using System;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Indexes;

/// <summary>
/// The bytes of a chunk a decode of a range of its rows reads, from its tree and its marks alone: what a read of a few
/// granules fetches instead of the whole chunk, each a range of the chunk's region of buffers.
/// </summary>
/// <remarks>
/// <para>
/// Each encoding says where a range of its rows lies by its geometry: a packing's rows lie in blocks of 1 024, each
/// <c>128 × w</c> bytes; a plain column's rows <c>width</c> bytes each; a frame of reference, a zigzag, an extension, a
/// decimal's or a timestamp's parts hold their rows in their children; a dictionary's codes hold the rows, and its values
/// are read whole, as every row may name any of them; FSST's compressed bytes lie between the starts its marks give, and
/// its lengths and offsets are columns of integers of their own. Patches, validities, symbol tables, a constant's value
/// are small and read whole.
/// </para>
/// <para>
/// An encoding whose geometry is not known here makes the chunk be read whole: the plan is a hint for the reads, and a
/// decode that reads a byte the plan left out would read zeros, so the plan holds every byte the range decode touches or
/// it gives up.
/// </para>
/// </remarks>
internal sealed class GranuleBytes
{
    private readonly List<(long Start, long End)> _ranges = [];
    private (long Start, long Length)[] _layout = [];
    private ChunkGeometry _geometry;
    private int _granuleRows;

    /// <summary>The ranges of the region the last plan reads, sorted and merged.</summary>
    internal IReadOnlyList<(long Start, long End)> Ranges => _ranges;

    /// <summary>The bytes the last plan reads.</summary>
    internal long Bytes { get; private set; }

    /// <summary>
    /// Plans the read of rows <c>[start, start + count)</c> of a chunk of <paramref name="rows"/> rows whose tree
    /// <paramref name="root"/> was loaded over its region, its values <paramref name="width"/> bytes each where they are
    /// numbers; false when an encoding's geometry is not known, and the chunk is read whole.
    /// </summary>
    internal bool TryPlan(
        in ArrayNode root, int width, int rows, int start, int count, (long Start, long Length)[] layout, ChunkGeometry geometry,
        int granuleRows)
    {
        _ranges.Clear();
        _layout = layout;
        _geometry = geometry;
        _granuleRows = granuleRows;
        Bytes = 0;
        if (count <= 0 || start < 0 || (long)start + count > rows || !Walk(in root, width, rows, start, count))
        {
            return false;
        }

        Merge();
        return true;
    }

    private bool Walk(in ArrayNode node, int width, int length, int start, int count)
    {
        switch (node.Encoding)
        {
            case ArrayEncodingId.FastLanesBitPacked:
            {
                BitPackedMetadata metadata = BitPackedMetadata.Read(node.Metadata);
                int bitWidth = (int)metadata.BitWidth;
                if (bitWidth > 0 && node.BufferCount >= 1)
                {
                    long blockBytes = 128L * bitWidth;
                    long first = ((long)metadata.Offset + start) / 1024;
                    long last = ((long)metadata.Offset + start + count - 1) / 1024;
                    Buffer(in node, 0, first * blockBytes, (last + 1) * blockBytes);
                }

                // Patches and validity, whole.
                WholeChildren(in node, 0);
                return true;
            }

            case ArrayEncodingId.FastLanesFor:
            case ArrayEncodingId.ZigZag:
                if (node.ChildCount != 1)
                {
                    return false;
                }

                WholeBuffers(in node);
                ArrayNode encoded = node.GetChild(0);
                return Walk(in encoded, width, length, start, count);

            case ArrayEncodingId.Extension:
                if (node.ChildCount != 1)
                {
                    return false;
                }

                ArrayNode storage = node.GetChild(0);
                return Walk(in storage, width, length, start, count);

            case ArrayEncodingId.Alp:
            {
                if (node.ChildCount < 1)
                {
                    return false;
                }

                ArrayNode integers = node.GetChild(0);
                WholeChildren(in node, 1);
                return Walk(in integers, width, length, start, count);
            }

            case ArrayEncodingId.Primitive:
                if (node.BufferCount >= 1 && width > 0)
                {
                    Buffer(in node, 0, (long)start * width, ((long)start + count) * width);
                }

                WholeChildren(in node, 0);
                return true;

            case ArrayEncodingId.Dict:
            {
                if (node.ChildCount != 2)
                {
                    return false;
                }

                DictMetadata metadata = DictMetadata.Read(node.Metadata);
                ArrayNode codes = node.GetChild(0);
                ArrayNode values = node.GetChild(1);
                Whole(in values);
                return Walk(in codes, metadata.CodesPType.ByteWidth(), length, start, count);
            }

            case ArrayEncodingId.DecimalByteParts:
            {
                if (node.ChildCount != 1)
                {
                    return false;
                }

                DecimalBytePartsMetadata metadata = DecimalBytePartsMetadata.Read(node.Metadata);
                ArrayNode part = node.GetChild(0);
                return Walk(in part, metadata.ZerothChildPType.ByteWidth(), length, start, count);
            }

            case ArrayEncodingId.DateTimeParts:
            {
                if (node.ChildCount != 3)
                {
                    return false;
                }

                DateTimePartsMetadata metadata = DateTimePartsMetadata.Read(node.Metadata);
                ArrayNode days = node.GetChild(0);
                ArrayNode seconds = node.GetChild(1);
                ArrayNode subseconds = node.GetChild(2);
                return Walk(in days, metadata.DaysPType.ByteWidth(), length, start, count)
                    && Walk(in seconds, metadata.SecondsPType.ByteWidth(), length, start, count)
                    && Walk(in subseconds, metadata.SubsecondsPType.ByteWidth(), length, start, count);
            }

            case ArrayEncodingId.Fsst:
            {
                if (node.BufferCount != 3 || node.ChildCount < 2)
                {
                    return false;
                }

                FsstMetadata metadata = FsstMetadata.Read(node.Metadata);

                // The symbol table, whole; the compressed bytes between the marks of the range's granules.
                Buffer(in node, 0, 0, long.MaxValue);
                Buffer(in node, 1, 0, long.MaxValue);
                if (!Marked(in node, 2, start, count))
                {
                    return false;
                }

                ArrayNode lengths = node.GetChild(0);
                ArrayNode offsets = node.GetChild(1);
                WholeChildren(in node, 2);
                return Walk(in lengths, metadata.UncompressedLengthsPType.ByteWidth(), length, start, count)
                    && Walk(in offsets, metadata.CodesOffsetsPType.ByteWidth(), length + 1, start, count + 1);
            }

            case ArrayEncodingId.Zstd:
                return Frames(in node, width, start, count);

            case ArrayEncodingId.Constant:
            case ArrayEncodingId.Sequence:
            case ArrayEncodingId.Null:
                Whole(in node);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// The frames of a zstd node that hold the range's values, each a buffer, and its dictionary: a node with no validity,
    /// whose rows are its values, counted frame by frame. A node with nulls holds the valid rows' values alone, which only
    /// its validity places, and is read whole.
    /// </summary>
    private bool Frames(in ArrayNode node, int width, int start, int count)
    {
        if (node.ChildCount != 0)
        {
            return false;
        }

        ZstdFrameMetadata[] table = System.Buffers.ArrayPool<ZstdFrameMetadata>.Shared.Rent(Math.Max(node.BufferCount, 1));
        try
        {
            ZstdMetadata metadata = ZstdMetadata.Read(node.Metadata, table.AsSpan(0, node.BufferCount));
            int first = metadata.DictionarySize != 0 ? 1 : 0;
            if (first + metadata.FrameCount != node.BufferCount)
            {
                return false;
            }

            if (first == 1)
            {
                Buffer(in node, 0, 0, long.MaxValue);
            }

            long covered = 0;
            long end = (long)start + count;
            for (int frame = 0; frame < metadata.FrameCount && covered < end; frame++)
            {
                long values = table[frame].ValueCount != 0
                    ? (long)table[frame].ValueCount
                    : width > 0 ? (long)table[frame].UncompressedSize / width : -1;
                if (values < 0)
                {
                    return false;
                }

                if (covered + values > start)
                {
                    Buffer(in node, first + frame, 0, long.MaxValue);
                }

                covered += values;
            }

            return true;
        }
        finally
        {
            System.Buffers.ArrayPool<ZstdFrameMetadata>.Shared.Return(table, clearArray: true);
        }
    }

    /// <summary>Every buffer of <paramref name="node"/> and of the nodes under it.</summary>
    private void Whole(in ArrayNode node)
    {
        WholeBuffers(in node);
        WholeChildren(in node, 0);
    }

    private void WholeBuffers(in ArrayNode node)
    {
        for (int b = 0; b < node.BufferCount; b++)
        {
            Buffer(in node, b, 0, long.MaxValue);
        }
    }

    private void WholeChildren(in ArrayNode node, int first)
    {
        for (int c = first; c < node.ChildCount; c++)
        {
            ArrayNode child = node.GetChild(c);
            Whole(in child);
        }
    }

    /// <summary>Bytes <c>[from, to)</c> of the node's buffer <paramref name="local"/>, cut to the buffer.</summary>
    private void Buffer(in ArrayNode node, int local, long from, long to)
    {
        int global = node.Arena.GlobalBufferIndex(node.Record.FirstBuffer + local);
        (long start, long length) = _layout[global];
        from = Math.Clamp(from, 0, length);
        to = Math.Clamp(to, from, length);
        if (to > from)
        {
            _ranges.Add((start + from, start + to));
        }
    }

    /// <summary>The bytes of the range's granules in a buffer of variable length, by its marks; false when it has none.</summary>
    private bool Marked(in ArrayNode node, int local, int start, int count)
    {
        int global = node.Arena.GlobalBufferIndex(node.Record.FirstBuffer + local);
        foreach (ChunkMark mark in _geometry.Marks)
        {
            if (mark.Buffer != global)
            {
                continue;
            }

            int first = start / _granuleRows;
            int last = (start + count - 1) / _granuleRows;
            if (first >= mark.Starts.Length)
            {
                return false;
            }

            long to = last + 1 < mark.Starts.Length ? mark.Starts[last + 1] : long.MaxValue;
            Buffer(in node, local, mark.Starts[first], to);
            return true;
        }

        return false;
    }

    /// <summary>The ranges sorted, and those that touch or overlap joined.</summary>
    private void Merge()
    {
        _ranges.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        int kept = 0;
        for (int i = 0; i < _ranges.Count; i++)
        {
            (long start, long end) = _ranges[i];
            if (kept > 0 && start <= _ranges[kept - 1].End)
            {
                _ranges[kept - 1] = (_ranges[kept - 1].Start, Math.Max(_ranges[kept - 1].End, end));
            }
            else
            {
                _ranges[kept++] = (start, end);
            }
        }

        _ranges.RemoveRange(kept, _ranges.Count - kept);
        long bytes = 0;
        foreach ((long start, long end) in _ranges)
        {
            bytes += end - start;
        }

        Bytes = bytes;
    }
}
