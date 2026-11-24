using System;
using System.IO;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>
/// A stream that hashes what is written to it and keeps nothing: an object hashed as a store copies it once
/// (<see cref="Vorticity.IO.IObjectStore.CopyToAsync"/>), one request a store that streams answers, where a read of a
/// mebibyte at a time cost 256 requests an object of 256 MiB.
/// </summary>
internal sealed class HashingStream(XxHash128 hash) : Stream
{
    /// <summary>The bytes hashed.</summary>
    internal long Written { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        hash.Append(buffer);
        Written += buffer.Length;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer.AsSpan(offset, count));
        return Task.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
