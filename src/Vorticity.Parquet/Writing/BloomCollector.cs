using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Indexes;
using Vorticity.Writing;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Thrift;

namespace Vorticity.Parquet.Writing;

/// <summary>
/// A column chunk's Bloom filter: each value of its pages hashed from its PLAIN bytes, a byte
/// array's without its length, into a filter sized for the most values the chunk can hold, folded
/// when the chunk closes to the size its values need at the rate the column asked.
/// </summary>
/// <remarks>
/// A filter of a power of two of blocks takes a value's block from the top bits of its hash, so
/// folding it is, bit for bit, the filter the same values give at the smaller size
/// (<see cref="SplitBlockBloom.Fold"/>): a value is inserted as its page closes, and none waits for
/// the chunk's count. A page of dictionary codes is not hashed: its values are the dictionary's
/// entries, hashed once each as the chunk closes, which counts them exactly; a value of a page
/// written otherwise counts as one. The filter is a block of the engine's pool kept from chunk to
/// chunk, 1 MiB at most, past which its rate is worse than asked rather than its size larger; the
/// row group writes it after its chunks and the chunk's next filter starts over.
/// </remarks>
internal sealed class BloomCollector : IDisposable
{
    /// <summary>The most blocks a filter takes: 1 MiB.</summary>
    private const int MaximumBlocks = (1 << 20) / SplitBlockBloom.BytesPerBlock;

    private readonly PooledBytes _words;
    private readonly PooledBytes _header;
    private readonly int _ppm;

    /// <summary>A fixed-width value's bytes; 0 for a byte array, whose length is before it.</summary>
    private readonly int _width;

    /// <summary>The blocks a chunk's filter starts with: enough for the most values it can hold.</summary>
    private readonly int _initialBlocks;

    /// <summary>The filter's blocks; 0 before the chunk's first value.</summary>
    private int _blocks;

    /// <summary>The values hashed from the chunk's pages.</summary>
    private long _values;

    /// <param name="column">The column.</param>
    /// <param name="rate">The false-positive rate asked, in (0, 1).</param>
    /// <param name="mostValues">The most values a chunk holds: a flat column's row group's rows.</param>
    /// <param name="pool">The engine's pool.</param>
    internal BloomCollector(WriteColumn column, double rate, long mostValues, AlignedBufferPool pool)
    {
        _ppm = (int)Math.Clamp(Math.Round(rate * 1_000_000), 1, 999_999);
        _width = column.Conversion == ValueConversion.ByteArray ? 0 : column.ValueWidth;
        _initialBlocks = SplitBlockBloom.BlocksFor(mostValues, _ppm, MaximumBlocks);
        _words = new PooledBytes(pool);
        _header = new PooledBytes(pool);
    }

    /// <summary>Whether the chunk closed with a filter that <see cref="WriteToAsync"/> has yet to write.</summary>
    internal bool Closed => _header.Length > 0;

    /// <summary>Hashes a page's <paramref name="values"/> PLAIN values into the filter.</summary>
    internal void AddPage(ReadOnlySpan<byte> plain, int values)
    {
        if (values > 0)
        {
            Add(plain, values);
            _values += values;
        }
    }

    /// <summary>
    /// Closes the chunk's filter: the dictionary's <paramref name="entryCount"/> PLAIN
    /// <paramref name="entries"/> hashed, the filter folded to the blocks its values need, and its
    /// header written before it.
    /// </summary>
    internal void Close(ReadOnlySpan<byte> entries, int entryCount)
    {
        if (entryCount > 0)
        {
            Add(entries, entryCount);
        }

        Span<uint> words = Words();
        int blocks = Math.Min(_blocks, SplitBlockBloom.BlocksFor(_values + entryCount, _ppm, MaximumBlocks));
        SplitBlockBloom.Fold(words, _blocks, blocks);
        _blocks = blocks;
        _words.Truncate(blocks * SplitBlockBloom.BytesPerBlock);
        ThriftCompactWriter writer = new(_header);
        BloomFilterHeader.Write(ref writer, _words.Length);
        writer.Flush();
    }

    /// <summary>Writes the closed filter, its header then its bitset, and starts the chunk's next.</summary>
    /// <returns>The bytes written.</returns>
    internal async ValueTask<int> WriteToAsync(ISegmentSink sink, CancellationToken cancellationToken)
    {
        int length = _header.Length + _words.Length;
        await sink.WriteAsync(_header.Written, cancellationToken).ConfigureAwait(false);
        await sink.WriteAsync(_words.Written, cancellationToken).ConfigureAwait(false);
        Reset();
        return length;
    }

    /// <summary>Forgets the chunk's filter, keeping the block.</summary>
    internal void Reset()
    {
        _header.Clear();
        _words.Clear();
        _blocks = 0;
        _values = 0;
    }

    public void Dispose()
    {
        _words.Dispose();
        _header.Dispose();
    }

    /// <summary>The filter, zeroed at the size of the most values the chunk holds when it has none yet.</summary>
    private Span<uint> Words()
    {
        if (_blocks == 0)
        {
            _blocks = _initialBlocks;
            _words.Clear();
            _words.Reserve(_blocks * SplitBlockBloom.BytesPerBlock).Clear();
        }

        return MemoryMarshal.Cast<byte, uint>(_words.WrittenSpan);
    }

    private void Add(ReadOnlySpan<byte> plain, int values)
    {
        Span<uint> words = Words();
        switch (_width)
        {
            case sizeof(uint):
                foreach (uint value in MemoryMarshal.Cast<byte, uint>(plain[..(values * sizeof(uint))]))
                {
                    SplitBlockBloom.Insert(words, SplitBlockBloom.XxHash64Of(value));
                }

                return;

            case sizeof(ulong):
                foreach (ulong value in MemoryMarshal.Cast<byte, ulong>(plain[..(values * sizeof(ulong))]))
                {
                    SplitBlockBloom.Insert(words, SplitBlockBloom.XxHash64Of(value));
                }

                return;

            case 0:
                int at = 0;
                for (int i = 0; i < values; i++)
                {
                    int length = BinaryPrimitives.ReadInt32LittleEndian(plain[at..]);
                    SplitBlockBloom.Insert(words, SplitBlockBloom.Hash(plain.Slice(at + sizeof(int), length), BloomHash.XxHash64));
                    at += sizeof(int) + length;
                }

                return;

            default:
                for (int i = 0; i < values; i++)
                {
                    SplitBlockBloom.Insert(words, SplitBlockBloom.Hash(plain.Slice(i * _width, _width), BloomHash.XxHash64));
                }

                return;
        }
    }
}
