using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays;

namespace Vorticity.Aggregating;

/// <summary>
/// The values and the validity of a boolean block as words, copied into the caller's buffers. A
/// boolean node keeps one cached words buffer, which its validity and its values would share, so
/// neither is read through that cache here.
/// </summary>
internal static class BoolWords
{
    /// <summary>The values of a boolean block; a constant is filled, an encoded form decoded.</summary>
    internal static ReadOnlySpan<ulong> Values(CanonicalArena arena, scoped ref int node, ref ulong[] scratch)
    {
        if (arena.RecordRef(node).Kind != CanonicalKind.Constant)
        {
            node = EncodedForms.Canonical(arena, node);
        }

        ref readonly CanonicalRecord record = ref arena.RecordRef(node);
        return record.Kind == CanonicalKind.Constant
            ? Filled(record.BufferA.Span[0] != 0, record.Length, ref scratch)
            : Copied(record.BufferA.Span, record.BitOffset, record.Length, ref scratch);
    }

    /// <summary>The validity of a node; empty when every row holds a value.</summary>
    internal static ReadOnlySpan<ulong> Validity(CanonicalArena arena, int node, ref ulong[] scratch)
    {
        ref readonly CanonicalRecord record = ref arena.RecordRef(node);
        Validity validity = record.Validity;
        switch (validity.Kind)
        {
            case ValidityKind.NonNullable:
            case ValidityKind.AllValid:
                return default;
            case ValidityKind.AllInvalid:
                return Filled(false, record.Length, ref scratch);
            default:
                ref readonly CanonicalRecord bits = ref arena.RecordRef(validity.CanonicalNodeIndex);
                return Copied(bits.BufferA.Span, bits.BitOffset, record.Length, ref scratch);
        }
    }

    private static ReadOnlySpan<ulong> Filled(bool value, int length, ref ulong[] scratch)
    {
        int words = (length + 63) >> 6;
        Scratch.Grow(ref scratch, words);
        Span<ulong> into = scratch.AsSpan(0, words);
        into.Fill(value ? ulong.MaxValue : 0);
        ClearTail(into, length);
        return into;
    }

    private static ReadOnlySpan<ulong> Copied(ReadOnlySpan<byte> bits, int bitOffset, int length, ref ulong[] scratch)
    {
        int words = (length + 63) >> 6;
        Scratch.Grow(ref scratch, words);
        Span<ulong> into = scratch.AsSpan(0, words);
        int bytes = (bitOffset + length + 7) >> 3;
        if (bitOffset == 0)
        {
            Span<byte> target = MemoryMarshal.AsBytes(into);
            target.Clear();
            bits[..Math.Min(bytes, bits.Length)].CopyTo(target);
        }
        else
        {
            for (int w = 0; w < words; w++)
            {
                int startBit = bitOffset + (w << 6);
                int startByte = startBit >> 3;
                int shift = startBit & 7;
                int available = Math.Min(9, Math.Min(bytes, bits.Length) - startByte);
                ulong word = 0;
                for (int b = 0; b < available; b++)
                {
                    ulong value = bits[startByte + b];
                    word |= b == 0 ? value >> shift : value << ((b << 3) - shift);
                }

                into[w] = word;
            }
        }

        ClearTail(into, length);
        return into;
    }

    private static void ClearTail(Span<ulong> words, int length)
    {
        int tail = length & 63;
        if (tail != 0)
        {
            words[^1] &= (1UL << tail) - 1;
        }
    }
}

/// <summary>What a group of a boolean column saw: false, true, both or neither.</summary>
internal static class BoolFlags
{
    internal const byte SawFalse = 1;
    internal const byte SawTrue = 2;

    /// <summary>False before true.</summary>
    internal static bool? Min(byte flags) => (flags & SawFalse) != 0 ? false : (flags & SawTrue) != 0 ? true : null;

    /// <summary>True before false.</summary>
    internal static bool? Max(byte flags) => (flags & SawTrue) != 0 ? true : (flags & SawFalse) != 0 ? false : null;

    internal static long Distinct(byte flags) => ((flags & SawFalse) != 0 ? 1 : 0) + ((flags & SawTrue) != 0 ? 1 : 0);
}

/// <summary>The minimum, the maximum or the distinct count of a boolean column, by population count over the bits.</summary>
internal sealed class BoolSlot<TResult> : RecordSlot<byte, TResult>
{
    private readonly Func<byte, TResult> _finish;
    private MaskCache _rows;
    private MaskCache _trues;
    private ulong[] _bits = [];
    private ulong[] _validity = [];
    private long _loadedBatch;
    private int _loadedNode;
    private bool _allValid;

    internal BoolSlot(Func<byte, TResult> finish) => _finish = finish;

    internal override byte Seed => 0;

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        ReadOnlySpan<ulong> bits = Load(input, out ReadOnlySpan<ulong> valid);
        ReadOnlySpan<ulong> rows = _rows.And(input, input.Selection, valid);
        int all = RowMasks.Count(rows, start, end);
        if (all == 0)
        {
            return;
        }

        int trues = RowMasks.Count(_trues.And(input, rows, bits), start, end);
        ref byte flags = ref State(group);
        if (trues > 0)
        {
            flags |= BoolFlags.SawTrue;
        }

        if (all > trues)
        {
            flags |= BoolFlags.SawFalse;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        ReadOnlySpan<ulong> bits = Load(input, out ReadOnlySpan<ulong> valid);
        RowCursor rows = new RowCursor(_rows.And(input, input.Selection, valid), input.Start, input.End);
        StateView<byte> flags = States;
        while (rows.Next(out int row))
        {
            flags[groups[row]] |= ((bits[row >> 6] >> (row & 63)) & 1) != 0 ? BoolFlags.SawTrue : BoolFlags.SawFalse;
        }
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into)
    {
        StateView<byte> flags = States;
        StateView<byte> others = StatesOf(other);
        for (int i = 0; i < from.Length; i++)
        {
            flags[into[i]] |= others[from[i]];
        }
    }

    internal override TResult Result(int group) => _finish(State(group));

    /// <summary>The block's values and validity, copied once per batch whatever the number of ranges folded.</summary>
    private ReadOnlySpan<ulong> Load(in BatchInput input, out ReadOnlySpan<ulong> validity)
    {
        int words = (input.Rows + 63) >> 6;
        if (_loadedBatch != input.Batch || _loadedNode != input.Node)
        {
            int node = input.Node;
            BoolWords.Values(input.Arena, ref node, ref _bits);
            _allValid = BoolWords.Validity(input.Arena, node, ref _validity).IsEmpty;
            _loadedBatch = input.Batch;
            _loadedNode = input.Node;
        }

        validity = _allValid ? default : _validity.AsSpan(0, words);
        return _bits.AsSpan(0, words);
    }
}
