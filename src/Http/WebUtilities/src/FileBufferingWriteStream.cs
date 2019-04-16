// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Internal;

namespace Microsoft.AspNetCore.WebUtilities
{
    /// <summary>
    /// A <see cref="Stream"/> that wraps another stream and buffers content to be written.
    /// <para>
    /// <see cref="FileBufferingWriteStream"/> is intended to provide a workaround for APIs that need to perform
    /// synchronous writes to the HTTP Response Stream while contending with a server that is configured to disallow synchronous writes.
    /// Synchronous writes are buffered to a memory backed stream up to the specified threshold, after which further writes are spooled to
    /// a temporary file on disk.
    /// </para>
    /// <para>
    /// Consumers of this API can invoke <see cref="M:Stream.CopyToAsync" /> to copy the results to the HTTP Response Stream.
    /// </para>
    /// </summary>
    public sealed class FileBufferingWriteStream : Stream
    {
        private const int DefaultMemoryThreshold = 32 * 1024; // 32k

        private readonly int _memoryThreshold;
        private readonly long? _bufferLimit;
        private readonly Func<string> _tempFileDirectoryAccessor;

        /// <summary>
        /// Initializes a new instance of <see cref="FileBufferingWriteStream"/>.
        /// </summary>
        /// <param name="memoryThreshold">
        /// The maximum amount of memory in bytes to allocate before switching to a file on disk.
        /// Defaults to 32kb.
        /// </param>
        /// <param name="bufferLimit">
        /// The maximum amouont of bytes that the <see cref="FileBufferingWriteStream"/> is allowed to buffer.
        /// </param>
        /// <param name="tempFileDirectoryAccessor">Provides the location of the directory to write buffered contents to.
        /// When unspecified, uses the value specified by the environment variable <c>ASPNETCORE_TEMP</c> if available, otherwise
        /// uses the value returned by <see cref="Path.GetTempPath"/>.
        /// </param>
        public FileBufferingWriteStream(
            int memoryThreshold = DefaultMemoryThreshold,
            long? bufferLimit = null,
            Func<string> tempFileDirectoryAccessor = null)
        {
            if (memoryThreshold < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(memoryThreshold));
            }

            if (bufferLimit != null && bufferLimit < memoryThreshold)
            {
                // We would expect a limit at least as much as memoryThreshold
                throw new ArgumentOutOfRangeException(nameof(bufferLimit), $"{nameof(bufferLimit)} must be larger than {nameof(memoryThreshold)}.");
            }

            _memoryThreshold = memoryThreshold;
            _bufferLimit = bufferLimit;
            _tempFileDirectoryAccessor = tempFileDirectoryAccessor ?? AspNetCoreTempDirectory.TempDirectoryFactory;
            PagedByteBuffer = new PagedByteBuffer(ArrayPool<byte>.Shared);
        }

        /// <inheritdoc />
        public override bool CanRead => false;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal long BufferedLength => PagedByteBuffer.Length + (FileStream?.Length ?? 0);

        internal PagedByteBuffer PagedByteBuffer { get; }

        internal FileStream FileStream { get; private set; }

        internal bool Disposed { get; private set; }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowArgumentException(buffer, offset, count);
            ThrowIfDisposed();

            if (_bufferLimit.HasValue && _bufferLimit - BufferedLength < count)
            {
                Dispose();
                throw new IOException("Buffer limit exceeded.");
            }

            // Allow buffering in memory if we're below the memory threshold once the current buffer is written.
            var allowMemoryBuffer = (_memoryThreshold - count) >= PagedByteBuffer.Length;
            if (allowMemoryBuffer)
            {
                // Buffer content in the MemoryStream if it has capacity.
                PagedByteBuffer.Add(buffer, offset, count);
                Debug.Assert(PagedByteBuffer.Length <= _memoryThreshold);
            }
            else
            {
                // If the MemoryStream is incapable of accomodating the content to be written
                // spool to disk.
                EnsureFileStream();

                // Spool memory content to disk and clear in memory buffers. We no longer need to hold on to it
                PagedByteBuffer.CopyTo(FileStream, clearBuffers: true);

                FileStream.Write(buffer, offset, count);
            }
        }

        /// <inheritdoc />
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ThrowArgumentException(buffer, offset, count);
            ThrowIfDisposed();

            if (_bufferLimit.HasValue && _bufferLimit - BufferedLength < count)
            {
                Dispose();
                throw new IOException("Buffer limit exceeded.");
            }

            // Allow buffering in memory if we're below the memory threshold once the current buffer is written.
            var allowMemoryBuffer = (_memoryThreshold - count) >= PagedByteBuffer.Length;
            if (allowMemoryBuffer)
            {
                // Buffer content in the MemoryStream if it has capacity.
                PagedByteBuffer.Add(buffer, offset, count);
                Debug.Assert(PagedByteBuffer.Length <= _memoryThreshold);
            }
            else
            {
                // If the MemoryStream is incapable of accomodating the content to be written
                // spool to disk.
                EnsureFileStream();

                // Spool memory content to disk and clear in memory buffers. We no longer need to hold on to it
                await PagedByteBuffer.CopyToAsync(FileStream, clearBuffers: true, cancellationToken);

                await FileStream.WriteAsync(buffer, offset, count, cancellationToken);
            }
        }

        public override void Flush()
        {
            // Do nothing.
        }

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>
        /// Copies buffered content to <paramref name="destination"/>.
        /// </summary>
        /// <param name="destination">The <see cref="Stream" /> to copy to.</param>
        /// <param name="bufferSize">The size of the buffer.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken" />.</param>
        /// <returns>A <see cref="Task" /> that represents the asynchronous copy operation.</returns>
        /// <remarks>
        /// Users of this API do not need to reset the <see cref="Position" /> of this instance, prior to copying content.
        /// </remarks>
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            // When not null, FileStream always has "older" spooled content. The PagedByteBuffer always has "newer"
            // unspooled content. Copy the FileStream content first when available.
            if (FileStream != null)
            {
                FileStream.Position = 0;
                await FileStream.CopyToAsync(destination, bufferSize, cancellationToken);
            }

            // Copy memory content, but do not clear the buffers. We want multiple invocations of CopyTo \ CopyToAsync
            // on this instance to behave the same.
            await PagedByteBuffer.CopyToAsync(destination, clearBuffers: false, cancellationToken);
        }

        /// <summary>
        /// Copies buffered content to <paramref name="destination"/>.
        /// </summary>
        /// <param name="destination">The <see cref="Stream" /> to copy to.</param>
        /// <param name="bufferSize">The size of the buffer.</param>
        /// <remarks>
        /// Users of this API do not need to reset the <see cref="Position" /> of this instance, prior to copying content.
        /// </remarks>
        public override void CopyTo(Stream destination, int bufferSize)
        {
            // See comments under CopyToAsync for an explanation for the order of execution.
            if (FileStream != null)
            {
                FileStream.Position = 0;
                FileStream.CopyTo(destination, bufferSize);
            }

            // Copy memory content, but do not clear the buffers. We want multiple invocations of CopyTo \ CopyToAsync
            // on this instance to behave the same.
            PagedByteBuffer.CopyTo(destination, clearBuffers: false);
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (!Disposed)
            {
                Disposed = true;

                PagedByteBuffer.Dispose();
                FileStream?.Dispose();
            }
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            if (!Disposed)
            {
                Disposed = true;

                PagedByteBuffer.Dispose();
                await (FileStream?.DisposeAsync() ?? default);
            }
        }

        private void EnsureFileStream()
        {
            if (FileStream == null)
            {
                var tempFileDirectory = _tempFileDirectoryAccessor();
                var tempFileName = Path.Combine(tempFileDirectory, "ASPNETCORE_" + Guid.NewGuid() + ".tmp");
                FileStream = new FileStream(
                    tempFileName,
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.Delete,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            }
        }

        private void ThrowIfDisposed()
        {
            if (Disposed)
            {
                throw new ObjectDisposedException(nameof(FileBufferingReadStream));
            }
        }

        private static void ThrowArgumentException(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (buffer.Length - offset < count)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
        }
    }
}