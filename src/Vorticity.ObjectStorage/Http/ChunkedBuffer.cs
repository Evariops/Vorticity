using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.ObjectStorage.Hashing;

namespace Vorticity.ObjectStorage.Http;

/// <summary>
/// A body held whole before it is sent, in pooled chunks of a mebibyte, so that its checksum is known
/// in time for a header and it can be sent again by a retry. A body never takes one array of its size.
/// </summary>
internal sealed class ChunkedBuffer : IDisposable
{
    internal const int ChunkBytes = 1 << 20;

    private readonly List<byte[]> _chunks = [];
    private long _length;

    /// <summary>The bytes held.</summary>
    internal long Length => _length;

    /// <summary>
    /// Reads <paramref name="content"/> into a new buffer up to <paramref name="limit"/> bytes, or to its
    /// end if that comes first, and says whether the end came.
    /// </summary>
    /// <param name="content">The content; advanced past what was read, never completed here.</param>
    /// <param name="limit">The most bytes the buffer takes.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The buffer and whether the content ended within it.</returns>
    internal static async ValueTask<(ChunkedBuffer Buffer, bool Ended)> FillAsync(PipeReader content, long limit, CancellationToken cancellationToken)
    {
        ChunkedBuffer buffer = new ChunkedBuffer();
        try
        {
            while (buffer._length < limit)
            {
                ReadResult result = await content.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> bytes = result.Buffer;
                long take = Math.Min(bytes.Length, limit - buffer._length);
                buffer.Append(bytes.Slice(0, take));
                content.AdvanceTo(bytes.GetPosition(take), bytes.End);
                if (result.IsCanceled)
                {
                    throw new OperationCanceledException("The content was cancelled.");
                }

                if (result.IsCompleted && take == bytes.Length)
                {
                    return (buffer, true);
                }
            }

            // The limit reached: the end may be next, which the next read says, awaited since a writer
            // completes its pipe after its last flush. What it finds is left for the next fill.
            ReadResult peek = await content.ReadAsync(cancellationToken).ConfigureAwait(false);
            bool ended = peek.IsCompleted && peek.Buffer.IsEmpty;
            content.AdvanceTo(peek.Buffer.Start);
            return (buffer, ended);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <summary>Takes <paramref name="next"/>'s bytes after this buffer's, its chunks moved when this one ends on a chunk, copied otherwise.</summary>
    internal void Absorb(ChunkedBuffer next)
    {
        if (_length % ChunkBytes == 0)
        {
            _chunks.AddRange(next._chunks);
            _length += next._length;
            next._chunks.Clear();
            next._length = 0;
            return;
        }

        Append(next.Sequence());
        next.Dispose();
    }

    /// <summary>The bytes as a sequence over the chunks, valid until disposal.</summary>
    internal ReadOnlySequence<byte> Sequence()
    {
        if (_chunks.Count == 0)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        if (_chunks.Count == 1)
        {
            return new ReadOnlySequence<byte>(_chunks[0], 0, (int)_length);
        }

        Segment first = new Segment(_chunks[0].AsMemory(0, ChunkBytes), 0);
        Segment last = first;
        for (int i = 1; i < _chunks.Count; i++)
        {
            int length = i == _chunks.Count - 1 ? (int)(_length - ((long)i * ChunkBytes)) : ChunkBytes;
            last = last.Append(_chunks[i].AsMemory(0, length));
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    /// <summary>The CRC-64/NVME of the bytes.</summary>
    internal ulong Crc64()
    {
        ulong crc = ulong.MaxValue;
        foreach (ReadOnlyMemory<byte> segment in Sequence())
        {
            crc = Crc64Nvme.Update(crc, segment.Span);
        }

        return ~crc;
    }

    /// <summary>The MD5 of the bytes, for a <c>Content-MD5</c> a service asks for; never for security.</summary>
    internal byte[] Md5()
    {
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        foreach (ReadOnlyMemory<byte> segment in Sequence())
        {
            md5.AppendData(segment.Span);
        }

        return md5.GetHashAndReset();
    }

    public void Dispose()
    {
        foreach (byte[] chunk in _chunks)
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        _chunks.Clear();
        _length = 0;
    }

    private void Append(ReadOnlySequence<byte> bytes)
    {
        foreach (ReadOnlyMemory<byte> segment in bytes)
        {
            ReadOnlySpan<byte> span = segment.Span;
            while (!span.IsEmpty)
            {
                int used = (int)(_length % ChunkBytes);
                if (used == 0 && _length / ChunkBytes == _chunks.Count)
                {
                    _chunks.Add(ArrayPool<byte>.Shared.Rent(ChunkBytes));
                }

                int take = Math.Min(span.Length, ChunkBytes - used);
                span[..take].CopyTo(_chunks[^1].AsSpan(used));
                span = span[take..];
                _length += take;
            }
        }
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        internal Segment Append(ReadOnlyMemory<byte> memory)
        {
            Segment next = new Segment(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}

/// <summary>A request body over a sequence the store holds, with its length declared; sent as many times as the request is.</summary>
internal sealed class SequenceContent(ReadOnlySequence<byte> bytes) : System.Net.Http.HttpContent
{
    protected override async Task SerializeToStreamAsync(System.IO.Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
    {
        foreach (ReadOnlyMemory<byte> segment in bytes)
        {
            await stream.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
        }

        StoreTelemetry.Written(bytes.Length);
    }

    protected override Task SerializeToStreamAsync(System.IO.Stream stream, System.Net.TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override bool TryComputeLength(out long length)
    {
        length = bytes.Length;
        return true;
    }
}
