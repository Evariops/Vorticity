using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Vorticity.Arrays;

namespace Vorticity.Aggregating;

/// <summary>What a text aggregate does with one value of one group; a struct, so the walk calls it without a virtual call.</summary>
internal interface IBytesSink
{
    void Take(int group, ReadOnlySpan<byte> value);
}

/// <summary>The values of a text or binary block handed to a sink in the form the block arrives in: once per constant, per run, per distinct code, or per row.</summary>
internal static class BytesWalk
{
    internal static void Range<TSink>(ref TSink sink, in BatchInput input, int start, int end, int group, ref MaskCache mask, ref CodeSet distinct)
        where TSink : struct, IBytesSink
    {
        CanonicalArena arena = input.Arena;
        int node = input.Node;
        switch (EncodedForms.EncodingOf(arena, node))
        {
            case ColumnEncoding.Constant:
                if (RowMasks.Count(mask.And(input, input.Selection, ArenaWords.Validity(arena, node)), start, end) > 0)
                {
                    sink.Take(group, BytesBlock.Constant(arena, node));
                }

                return;

            case ColumnEncoding.RunEnd:
            {
                int runs = EncodedForms.RunEnd(arena, node, out ReadOnlySpan<uint> ends);
                BytesBlock values = BytesBlock.Canonical(arena, runs, out ReadOnlySpan<ulong> valid);
                ReadOnlySpan<ulong> rows = mask.And(input, input.Selection, ArenaWords.Validity(arena, node));
                int r = Runs.FirstEndingAfter(ends, start);
                int runStart = r == 0 ? 0 : (int)ends[r - 1];
                for (; r < ends.Length && runStart < end; r++)
                {
                    int runEnd = Math.Min((int)ends[r], input.Rows);
                    if (StorageValues.IsValid(valid, r) && RowMasks.Count(rows, Math.Max(runStart, start), Math.Min(runEnd, end)) > 0)
                    {
                        sink.Take(group, values[r]);
                    }

                    runStart = runEnd;
                }

                return;
            }

            case ColumnEncoding.Dictionary:
            {
                int entries = EncodedForms.Dictionary(arena, node, out ReadOnlySpan<uint> codes);
                BytesBlock dictionary = BytesBlock.Canonical(arena, entries, out ReadOnlySpan<ulong> valid);
                ReadOnlySpan<ulong> rows = mask.And(input, input.Selection, ArenaWords.Validity(arena, node));
                if (end - start < dictionary.Length)
                {
                    FewCodes(ref sink, ref distinct, codes, rows, start, end, group, dictionary, valid);
                }
                else
                {
                    ManyCodes(ref sink, ref distinct, codes, rows, start, end, group, dictionary, valid);
                }

                return;
            }

            default:
            {
                BytesBlock values = BytesBlock.Canonical(arena, node, out ReadOnlySpan<ulong> valid);
                RowCursor rows = new RowCursor(mask.And(input, input.Selection, valid), start, end);
                while (rows.Next(out int row))
                {
                    sink.Take(group, values[row]);
                }

                return;
            }
        }
    }

    /// <summary>The values of a range of fewer rows than its dictionary has codes, each once.</summary>
    /// <remarks>Each walk of a dictionary range is a method of its own, so that neither loop takes its shape from the other.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FewCodes<TSink>(
        ref TSink sink, ref CodeSet distinct, ReadOnlySpan<uint> codes, ReadOnlySpan<ulong> rows, int start, int end,
        int group, BytesBlock dictionary, ReadOnlySpan<ulong> valid)
        where TSink : struct, IBytesSink
    {
        foreach (int code in distinct.Few(codes, rows, start, end, dictionary.Length))
        {
            if (StorageValues.IsValid(valid, code))
            {
                sink.Take(group, dictionary[code]);
            }
        }
    }

    /// <summary>The values of a range as long as its dictionary or longer, each once, in code order.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ManyCodes<TSink>(
        ref TSink sink, ref CodeSet distinct, ReadOnlySpan<uint> codes, ReadOnlySpan<ulong> rows, int start, int end,
        int group, BytesBlock dictionary, ReadOnlySpan<ulong> valid)
        where TSink : struct, IBytesSink
    {
        Span<byte> seen = distinct.Table(dictionary.Length);
        RowCursor all = new RowCursor(rows, start, end);
        while (all.Next(out int row))
        {
            seen[(int)codes[row]] = 1;
        }

        for (int code = 0; code < seen.Length; code++)
        {
            if (seen[code] != 0 && StorageValues.IsValid(valid, code))
            {
                sink.Take(group, dictionary[code]);
            }
        }
    }

    internal static void Rows<TSink>(ref TSink sink, in BatchInput input, ReadOnlySpan<int> groups, ref MaskCache mask)
        where TSink : struct, IBytesSink
    {
        CanonicalArena arena = input.Arena;
        int node = input.Node;
        switch (EncodedForms.EncodingOf(arena, node))
        {
            case ColumnEncoding.Constant:
            {
                ReadOnlySpan<byte> value = BytesBlock.Constant(arena, node);
                RowCursor rows = new RowCursor(mask.And(input, input.Selection, ArenaWords.Validity(arena, node)), 0, input.Rows);
                while (rows.Next(out int row))
                {
                    sink.Take(groups[row], value);
                }

                return;
            }

            case ColumnEncoding.Dictionary:
            {
                int entries = EncodedForms.Dictionary(arena, node, out ReadOnlySpan<uint> codes);
                BytesBlock dictionary = BytesBlock.Canonical(arena, entries, out ReadOnlySpan<ulong> valid);
                RowCursor rows = new RowCursor(mask.And(input, input.Selection, ArenaWords.Validity(arena, node)), 0, input.Rows);
                while (rows.Next(out int row))
                {
                    int code = (int)codes[row];
                    if (StorageValues.IsValid(valid, code))
                    {
                        sink.Take(groups[row], dictionary[code]);
                    }
                }

                return;
            }

            default:
            {
                BytesBlock values = BytesBlock.Canonical(arena, node, out ReadOnlySpan<ulong> valid);
                RowCursor rows = new RowCursor(mask.And(input, input.Selection, valid), 0, input.Rows);
                while (rows.Next(out int row))
                {
                    sink.Take(groups[row], values[row]);
                }

                return;
            }
        }
    }
}

/// <summary>The smallest or largest text or binary value of each group, in byte order.</summary>
internal sealed class BytesExtremeSlot<TResult> : AggregateSlot<TResult>
{
    private readonly bool _max;
    private readonly ColumnShape _shape;
    private byte[][] _best = [];
    private int[] _lengths = [];
    private int _groups;
    private MaskCache _rows;
    private CodeSet _distinct;

    internal BytesExtremeSlot(ColumnShape shape, bool max)
    {
        _shape = shape;
        _max = max;
    }

    internal override void EnsureGroups(int groups)
    {
        if (groups > _best.Length)
        {
            int grown = Math.Max(groups, _best.Length * 2);
            Array.Resize(ref _best, grown);
            Array.Resize(ref _lengths, grown);
        }

        for (int g = _groups; g < groups; g++)
        {
            _best[g] = [];
            _lengths[g] = -1;
        }

        _groups = Math.Max(_groups, groups);
    }

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        Sink sink = new Sink(this);
        BytesWalk.Range(ref sink, input, start, end, group, ref _rows, ref _distinct);
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        Sink sink = new Sink(this);
        BytesWalk.Rows(ref sink, input, groups, ref _rows);
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> map)
    {
        BytesExtremeSlot<TResult> from = (BytesExtremeSlot<TResult>)other;
        for (int g = 0; g < from._groups; g++)
        {
            if (from._lengths[g] >= 0)
            {
                Offer(map[g], from._best[g].AsSpan(0, from._lengths[g]));
            }
        }
    }

    internal override TResult Result(int group) =>
        _lengths[group] < 0 ? default! : StorageValues.BytesToClr<TResult>(_best[group].AsSpan(0, _lengths[group]), _shape);

    private void Offer(int group, ReadOnlySpan<byte> value)
    {
        int length = _lengths[group];
        if (length >= 0)
        {
            int order = value.SequenceCompareTo(_best[group].AsSpan(0, length));
            if (_max ? order <= 0 : order >= 0)
            {
                return;
            }
        }

        byte[] buffer = _best[group];
        if (buffer.Length < value.Length)
        {
            _best[group] = buffer = new byte[Math.Max(value.Length, 16)];
        }

        value.CopyTo(buffer);
        _lengths[group] = value.Length;
    }

    private readonly struct Sink : IBytesSink
    {
        private readonly BytesExtremeSlot<TResult> _slot;

        internal Sink(BytesExtremeSlot<TResult> slot) => _slot = slot;

        public void Take(int group, ReadOnlySpan<byte> value) => _slot.Offer(group, value);
    }
}

/// <summary>The distinct non-null text or binary values of each group, keyed by group and value in one table.</summary>
internal sealed class BytesDistinctSlot : AggregateSlot<long>
{
    private readonly ByteKeyTable _seen = new ByteKeyTable();
    private long[] _counts = [];
    private int _groups;
    private byte[] _key = new byte[64];
    private MaskCache _rows;
    private CodeSet _distinct;

    internal override void EnsureGroups(int groups)
    {
        if (groups > _counts.Length)
        {
            Array.Resize(ref _counts, Math.Max(groups, _counts.Length * 2));
        }

        _groups = Math.Max(_groups, groups);
    }

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        Sink sink = new Sink(this);
        BytesWalk.Range(ref sink, input, start, end, group, ref _rows, ref _distinct);
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        Sink sink = new Sink(this);
        BytesWalk.Rows(ref sink, input, groups, ref _rows);
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> map)
    {
        ByteKeyTable from = ((BytesDistinctSlot)other)._seen;
        for (int i = 0; i < from.Count; i++)
        {
            ReadOnlySpan<byte> key = from.KeyOf(i);
            Add(map[BinaryPrimitives.ReadInt32LittleEndian(key)], key[4..]);
        }
    }

    internal override long Result(int group) => _counts[group];

    private void Add(int group, ReadOnlySpan<byte> value)
    {
        int length = 4 + value.Length;
        Scratch.Grow(ref _key, length);
        BinaryPrimitives.WriteInt32LittleEndian(_key, group);
        value.CopyTo(_key.AsSpan(4));
        _seen.GetOrAdd(_key.AsSpan(0, length), out bool added);
        if (added)
        {
            _counts[group]++;
        }
    }

    private readonly struct Sink : IBytesSink
    {
        private readonly BytesDistinctSlot _slot;

        internal Sink(BytesDistinctSlot slot) => _slot = slot;

        public void Take(int group, ReadOnlySpan<byte> value) => _slot.Add(group, value);
    }
}
