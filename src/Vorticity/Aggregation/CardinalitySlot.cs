using System;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays;

namespace Vorticity.Aggregating;

/// <summary>
/// An approximate distinct count per group: a <see cref="CardinalitySketch"/> each, made at a group's
/// first value, over a fixed-seed XXH3-64 of the value's bytes. The sketches are counted by the query's
/// memory as they grow, and spill as their hashes or their set registers.
/// </summary>
internal abstract class CardinalitySlot : SketchSlot<CardinalitySketch, long>
{
    /// <summary>What a value may add at most: a register entry, or a hash kept exactly.</summary>
    private protected override long ValueBytes => sizeof(ulong);

    private protected override CardinalitySketch New() => new CardinalitySketch();

    private protected override CardinalitySketch Read(ref SpillReader reader) => CardinalitySketch.Read(ref reader);

    /// <summary>Adds a value's hash to a group's sketch, counting what it grows by.</summary>
    private protected void Add(int group, ulong hash)
    {
        CardinalitySketch sketch = Sketch(group);

        long before = sketch.Bytes;
        sketch.Add(hash);
        Grew(sketch.Bytes - before);
    }

    internal override long Result(int group) => Held(group) is { } sketch ? sketch.Estimate() : 0;

    /// <summary>The hash every distinct count takes of a value's bytes: XXH3-64 of seed 0, the same in every process.</summary>
    internal static ulong Hash(ReadOnlySpan<byte> value) => XxHash3.HashToUInt64(value);
}

/// <summary>
/// XXH3-64 of seed 0 over a value of 1, 2, 4, 8 or 16 bytes, bit for bit <see cref="XxHash3.HashToUInt64(ReadOnlySpan{byte}, long)"/>,
/// worked out inline: the library's general entry, its length dispatched and its secret read at each call, took a
/// value 10 to 20 ns where a value of these lengths is a few multiplications.
/// </summary>
/// <remarks>
/// XXH3's short inputs (xxhash.h, <c>XXH3_len_1to3_64b</c>, <c>XXH3_len_4to8_64b</c>, <c>XXH3_len_9to16_64b</c>) over its
/// default secret, whose first 56 bytes the constants below are folded from. Checked once against the library over every
/// length it serves: a disagreement leaves every value to the library.
/// </remarks>
internal static class ShortHash
{
    private const ulong Prime64Second = 0xC2B2AE3D27D4EB4FUL;
    private const ulong Prime64Third = 0x165667B19E3779F9UL;
    private const ulong PrimeMx1 = 0x165667919E3779F9UL;
    private const ulong PrimeMx2 = 0x9FB21C651E98DF25UL;

    // The bit flips of XXH3's short inputs at seed 0: secret words xor'd.
    private static readonly ulong Flip1To3 = Le32(Secret, 0) ^ Le32(Secret, 4);
    private static readonly ulong Flip4To8 = Le64(Secret, 8) ^ Le64(Secret, 16);
    private static readonly ulong FlipLow = Le64(Secret, 24) ^ Le64(Secret, 32);
    private static readonly ulong FlipHigh = Le64(Secret, 40) ^ Le64(Secret, 48);

    /// <summary>Whether the folds agree with the library: the inline hashes serve only then.</summary>
    internal static readonly bool Agrees = Check();

    /// <summary>The first 56 bytes of XXH3's default secret.</summary>
    private static ReadOnlySpan<byte> Secret =>
    [
        0xb8, 0xfe, 0x6c, 0x39, 0x23, 0xa4, 0x4b, 0xbe, 0x7c, 0x01, 0x81, 0x2c, 0xf7, 0x21, 0xad, 0x1c,
        0xde, 0xd4, 0x6d, 0xe9, 0x83, 0x90, 0x97, 0xdb, 0x72, 0x40, 0xa4, 0xa4, 0xb7, 0xb3, 0x67, 0x1f,
        0xcb, 0x79, 0xe6, 0x4e, 0xcc, 0xc0, 0xe5, 0x78, 0x82, 0x5a, 0xd0, 0x7d, 0xcc, 0xff, 0x72, 0x21,
        0xb8, 0x08, 0x46, 0x74, 0xf7, 0x43, 0x24, 0x8e,
    ];

    /// <summary>A value of 1 to 3 bytes: its first, middle and last byte, and its length.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Of1To3(byte first, byte middle, byte last, int length)
    {
        uint combined = ((uint)first << 16) | ((uint)middle << 24) | last | ((uint)length << 8);
        return Avalanche64(combined ^ Flip1To3);
    }

    /// <summary>A value of 4 to 8 bytes: its first four bytes and its last four, little-endian, and its length.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Of4To8(uint first, uint last, int length)
    {
        ulong keyed = (last + ((ulong)first << 32)) ^ Flip4To8;
        keyed ^= BitOperations.RotateLeft(keyed, 49) ^ BitOperations.RotateLeft(keyed, 24);
        keyed *= PrimeMx2;
        keyed ^= (keyed >> 35) + (ulong)length;
        keyed *= PrimeMx2;
        return keyed ^ (keyed >> 28);
    }

    /// <summary>A value of 16 bytes: its two words, little-endian.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Of16(ulong low, ulong high)
    {
        low ^= FlipLow;
        high ^= FlipHigh;
        ulong product = Math.BigMul(low, high, out ulong lowHalf) ^ lowHalf;
        ulong accumulated = 16 + BinaryPrimitives.ReverseEndianness(low) + high + product;
        accumulated ^= accumulated >> 37;
        accumulated *= PrimeMx1;
        return accumulated ^ (accumulated >> 32);
    }

    /// <summary>The hash of an 8-byte value, as its bytes little-endian.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Of8(ulong value) => Of4To8((uint)value, (uint)(value >> 32), 8);

    /// <summary>The hash of a 4-byte value, as its bytes little-endian.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Of4(uint value) => Of4To8(value, value, 4);

    /// <summary>The hash of a 2-byte value, as its bytes little-endian.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Of2(ushort value) => Of1To3((byte)value, (byte)(value >> 8), (byte)(value >> 8), 2);

    /// <summary>The hash of a 1-byte value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Of1(byte value) => Of1To3(value, value, value, 1);

    private static ulong Avalanche64(ulong hash)
    {
        hash ^= hash >> 33;
        hash *= Prime64Second;
        hash ^= hash >> 29;
        hash *= Prime64Third;
        return hash ^ (hash >> 32);
    }

    private static ulong Le32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);

    private static ulong Le64(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]);

    /// <summary>The inline hashes against the library's over values of every length they serve.</summary>
    private static bool Check()
    {
        Span<byte> bytes = stackalloc byte[16];
        ulong state = 0x9E3779B97F4A7C15UL;
        for (int round = 0; round < 64; round++)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                state = (state ^ (state >> 31)) * 0xBF58476D1CE4E5B9UL;
                bytes[i] = (byte)(state >> 56);
            }

            if (Of1(bytes[0]) != XxHash3.HashToUInt64(bytes[..1])
                || Of2(BinaryPrimitives.ReadUInt16LittleEndian(bytes)) != XxHash3.HashToUInt64(bytes[..2])
                || Of4(BinaryPrimitives.ReadUInt32LittleEndian(bytes)) != XxHash3.HashToUInt64(bytes[..4])
                || Of8(BinaryPrimitives.ReadUInt64LittleEndian(bytes)) != XxHash3.HashToUInt64(bytes[..8])
                || Of16(BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..])) != XxHash3.HashToUInt64(bytes))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>An approximate distinct count of a column of fixed-width values: numbers, decimals, uuids.</summary>
/// <typeparam name="TValue">The column's storage type.</typeparam>
internal sealed class FixedCardinalitySlot<TValue>(StorageKind kind) : CardinalitySlot
    where TValue : unmanaged
{
    private TValue[] _values = [];
    private MaskCache _rows;
    private ulong[] _clip = [];

    /// <summary>The sketches, then the values widened, the masks joined and the window's words, back to the process's shelf.</summary>
    internal override void Release()
    {
        base.Release();
        Scratch.Return(ref _values);
        _rows.Release();
        Scratch.Return(ref _clip);
    }

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        ReadOnlySpan<TValue> values = FixedReader.Values(input.Arena, input.Node, kind, ref _values, out ReadOnlySpan<ulong> valid);
        bool whole = start == 0 && end == input.Rows && input.Selection.IsEmpty;
        Selection rows = whole ? new Selection(input.Rows) : RowMasks.Window(input.Selection, input.Rows, start, end, ref _clip);
        foreach (int row in rows)
        {
            if (valid.IsEmpty || (valid[row >> 6] & (1UL << row)) != 0)
            {
                Add(group, HashOf(values[row]));
            }
        }

        if (!whole)
        {
            RowMasks.Unclip(_clip, start, end);
        }
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        ReadOnlySpan<TValue> values = FixedReader.Values(input.Arena, input.Node, kind, ref _values, out ReadOnlySpan<ulong> valid);
        RowCursor rows = new RowCursor(_rows.And(input, input.Selection, valid), input.Start, input.End);
        while (rows.Next(out int row))
        {
            Add(groups[row], HashOf(values[row]));
        }
    }

    /// <summary>
    /// A value's hash, a float's zeros and NaNs each taken as one value, as the exact distinct count
    /// takes them equal.
    /// </summary>
    private static ulong HashOf(TValue value)
    {
        if (typeof(TValue) == typeof(double))
        {
            double d = FloatKey.Normal(Unsafe.As<TValue, double>(ref value));
            value = Unsafe.As<double, TValue>(ref d);
        }
        else if (typeof(TValue) == typeof(float))
        {
            float f = FloatKey.Normal(Unsafe.As<TValue, float>(ref value));
            value = Unsafe.As<float, TValue>(ref f);
        }
        else if (typeof(TValue) == typeof(Half))
        {
            Half h = FloatKey.Normal(Unsafe.As<TValue, Half>(ref value));
            value = Unsafe.As<Half, TValue>(ref h);
        }

        // The value's bytes as they lie, little-endian: XXH3 of its length worked out inline (ShortHash).
        if (BitConverter.IsLittleEndian && ShortHash.Agrees)
        {
            switch (Unsafe.SizeOf<TValue>())
            {
                case 8:
                    return ShortHash.Of8(Unsafe.As<TValue, ulong>(ref value));
                case 4:
                    return ShortHash.Of4(Unsafe.As<TValue, uint>(ref value));
                case 2:
                    return ShortHash.Of2(Unsafe.As<TValue, ushort>(ref value));
                case 1:
                    return ShortHash.Of1(Unsafe.As<TValue, byte>(ref value));
                case 16:
                    return ShortHash.Of16(Unsafe.As<TValue, ulong>(ref value), Unsafe.Add(ref Unsafe.As<TValue, ulong>(ref value), 1));
            }
        }

        return Hash(MemoryMarshal.AsBytes(new ReadOnlySpan<TValue>(ref value)));
    }
}

/// <summary>An approximate distinct count of a text or binary column, each value hashed as its bytes.</summary>
internal sealed class BytesCardinalitySlot : CardinalitySlot
{
    private MaskCache _rows;
    private CodeSet _distinct;

    // A batch's dictionary entries hashed at their first row: each code's hash, and the batch it was hashed in.
    private ulong[] _hashes = [];
    private int[] _hashedIn = [];
    private int _batch;

    /// <summary>The sketches, then the masks joined, the codes met and the entries' hashes, back to the process's shelf.</summary>
    internal override void Release()
    {
        base.Release();
        _rows.Release();
        _distinct.Release();
        Scratch.Return(ref _hashes);
        Scratch.Return(ref _hashedIn);
    }

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        Sink sink = new Sink(this);
        BytesWalk.Range(ref sink, input, start, end, group, ref _rows, ref _distinct);
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        if (EncodedForms.EncodingOf(input.Arena, input.Node) == ColumnEncoding.Dictionary)
        {
            StepCodes(input, groups);
            return;
        }

        Sink sink = new Sink(this);
        BytesWalk.Rows(ref sink, input, groups, ref _rows);
    }

    /// <summary>
    /// The rows of a dictionary's text into their groups' sketches, each entry hashed once a batch, at its first row: each
    /// row hashed its entry's bytes again, 54 ms for 4M rows of a thousand texts over 64 groups.
    /// </summary>
    private void StepCodes(in BatchInput input, ReadOnlySpan<int> groups)
    {
        CanonicalArena arena = input.Arena;
        int entries = EncodedForms.Dictionary(arena, input.Node, out ReadOnlySpan<uint> codes);
        BytesBlock dictionary = BytesBlock.Canonical(arena, entries, out ReadOnlySpan<ulong> valid);

        // A batch's number marks the entries it hashed, so that no table is cleared between batches.
        if (_hashedIn.Length < dictionary.Length)
        {
            Scratch.Grow(ref _hashes, dictionary.Length);
            Scratch.Grow(ref _hashedIn, dictionary.Length);
            _batch = 0;
        }
        else if (_batch == int.MaxValue)
        {
            Array.Clear(_hashedIn);
            _batch = 0;
        }

        int batch = ++_batch;
        ulong[] hashes = _hashes;
        int[] hashedIn = _hashedIn;
        RowCursor rows = new RowCursor(_rows.And(input, input.Selection, ArenaWords.Validity(arena, input.Node)), input.Start, input.End);
        while (rows.Next(out int row))
        {
            int code = (int)codes[row];
            if (!StorageValues.IsValid(valid, code))
            {
                continue;
            }

            if (hashedIn[code] != batch)
            {
                hashes[code] = Hash(dictionary[code]);
                hashedIn[code] = batch;
            }

            Add(groups[row], hashes[code]);
        }
    }

    private readonly struct Sink(BytesCardinalitySlot slot) : IBytesSink
    {
        public void Take(int group, ReadOnlySpan<byte> value) => slot.Add(group, Hash(value));
    }
}
