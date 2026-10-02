using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Zstd.Internal;

/// <summary>
/// One state of a sequence decoding table, libzstd's <c>ZSTD_seqSymbol</c>: the FSE transition and the
/// code's meaning folded together, so that a lookup yields the value's base and how many bits to add.
/// </summary>
/// <remarks>
/// <para>
/// A 64-bit value, laid out per code for the fast loop, which loads it whole (see
/// <see cref="SeqEntry"/>): the next state on top (bits 48 to 63), where one shift extracts it, folded
/// into the add that completes the state; the base value below it (bits 16 to 47); and in the low byte
/// the count the next positions in the bitstream add up without extracting it first, the rest of the
/// entry only adding multiples of 256: the state's own bits for the two lengths, the extra bits for
/// the offset, whose other count is then the second byte.
/// </para>
/// <para>
/// <see cref="NextState"/> is an index into the whole <see cref="SequenceTableSet"/>, where every
/// state takes two entries: the table's slot included, doubled.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct SeqSymbol
{
    public readonly ulong Value;

    public SeqSymbol(ulong value) => Value = value;

    /// <summary>An entry of <paramref name="code"/>, from its fields.</summary>
    public SeqSymbol(SequenceCode code, int nextState, int nbAdditionalBits, int nbBits, uint baseValue)
    {
        ulong counts = code == SequenceCode.Offset
            ? (byte)nbAdditionalBits | ((ulong)(byte)nbBits << 8)
            : (byte)nbBits | ((ulong)(byte)nbAdditionalBits << 8);
        Value = counts | ((ulong)baseValue << 16) | ((ulong)(ushort)nextState << 48);
    }

    public int NextState => (int)(Value >> 48);

    public int NbAdditionalBits(SequenceCode code) => (int)SeqEntry.ExtraBits(code, Value);

    public int NbBits(SequenceCode code) => (int)SeqEntry.NbBits(code, Value);

    public uint BaseValue(SequenceCode code) => (uint)SeqEntry.BaseValue(code, Value);
}

/// <summary>
/// The fields of a <see cref="SeqSymbol"/> loaded whole, as a <see cref="ulong"/>: one load the table
/// index folds into, then one instruction a field.
/// </summary>
/// <remarks>
/// The fields are masked to the widths they need (extra bits up to 31, state bits up to 9, a base
/// value up to 17 bits for the lengths, 31 for the offsets), not to a byte or a half: the JIT turns a
/// shift and a mask into one <c>ubfx</c> except for widths of 8, 16 and 32, which it leaves to casts
/// that cost an instruction more. The results stay 64-bit, so that no conversion adds a move.
/// </remarks>
internal static class SeqEntry
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Load(ref SeqSymbol table, nint state) => Unsafe.As<SeqSymbol, ulong>(ref Unsafe.Add(ref table, state));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint NextState(ulong entry) => (nint)(entry >> 48);

    // ---- the two lengths: the state's bits in the low byte, then the extra bits

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint LengthNbBits(ulong entry) => (nint)(entry & 0xF);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint LengthExtraBits(ulong entry) => (nint)((entry >> 8) & 0x1F);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint LengthBase(ulong entry) => (nuint)((entry >> 16) & 0x1FFFF);

    // ---- the offset: the extra bits in the low byte, then the state's bits

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint OffsetExtraBits(ulong entry) => (nint)(entry & 0x1F);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint OffsetNbBits(ulong entry) => (nint)((entry >> 8) & 0xF);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint OffsetBase(ulong entry) => (nuint)((entry >> 16) & 0x7FFFFFFF);

    // ---- by code, for the paths that are not hot

    public static nint ExtraBits(SequenceCode code, ulong entry) =>
        code == SequenceCode.Offset ? OffsetExtraBits(entry) : LengthExtraBits(entry);

    public static nint NbBits(SequenceCode code, ulong entry) =>
        code == SequenceCode.Offset ? OffsetNbBits(entry) : LengthNbBits(entry);

    public static nuint BaseValue(SequenceCode code, ulong entry) =>
        code == SequenceCode.Offset ? OffsetBase(entry) : LengthBase(entry);
}

/// <summary>The three sequence codes, in the order of their slots in a <see cref="SequenceTableSet"/>.</summary>
internal enum SequenceCode
{
    LiteralLength,
    Offset,
    MatchLength,
}

/// <summary>
/// A table shared between decoders, the predefined ones and a dictionary's: built for the slot of its
/// code, and copied into a decoder's <see cref="SequenceTableSet"/> when one uses it.
/// </summary>
internal sealed class SeqTable
{
    public readonly SeqSymbol[] Entries;
    public readonly int TableLog;
    public readonly SequenceCode Code;

    public SeqTable(SequenceCode code, ReadOnlySpan<short> norm, int tableLog)
    {
        Code = code;
        TableLog = tableLog;
        Entries = new SeqSymbol[2 << tableLog];
        SequenceCodes.BuildTable(code, Entries, norm, tableLog);
    }
}

/// <summary>
/// A decoder's three sequence tables in one array, each in its slot: literal lengths, then offsets,
/// then match lengths. One base serves the three lookups, and a state is an index into the whole.
/// </summary>
/// <remarks>
/// <para>
/// Every state takes two consecutive entries, alike: a state's index is twice its number, plus any
/// bit. A state then reads its <c>n</c> bits as <c>n + 1</c>, the bit after them landing on either
/// copy, with two shifts where <c>n</c> alone, which may be 0, takes three.
/// </para>
/// <para>
/// A slot holds the table its code currently decodes with: built there (FSE and RLE modes), or copied
/// there from a shared table (predefined mode, a dictionary's), which the repeat mode then keeps. A
/// shared table is copied only when the slot does not hold it already, and a dictionary's only once a
/// block repeats it.
/// </para>
/// </remarks>
internal sealed class SequenceTableSet
{
    public const int LiteralLengthSlot = 0;
    public const int OffsetSlot = LiteralLengthSlot + (2 << SequenceCodes.LiteralLengthMaxLog);
    public const int MatchLengthSlot = OffsetSlot + (2 << SequenceCodes.OffsetMaxLog);
    public const int Size = MatchLengthSlot + (2 << SequenceCodes.MatchLengthMaxLog);

    public readonly SeqSymbol[] Entries = new SeqSymbol[Size];

    /// <summary>Per code: the shared table in use, null when the slot's own build is.</summary>
    private readonly SeqTable?[] _current = new SeqTable?[3];

    /// <summary>Per code: the shared table the slot holds a copy of, null when it holds its own build.</summary>
    private readonly SeqTable?[] _held = new SeqTable?[3];

    private readonly int[] _tableLog = new int[3];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Slot(SequenceCode code) => code switch
    {
        SequenceCode.LiteralLength => LiteralLengthSlot,
        SequenceCode.Offset => OffsetSlot,
        _ => MatchLengthSlot,
    };

    public int TableLog(SequenceCode code) => _tableLog[(int)code];

    /// <summary>The state every frame starts from: a dictionary's tables, if it has them.</summary>
    public void BeginFrame(SeqTable? literalLengths, SeqTable? offsets, SeqTable? matchLengths)
    {
        Select(literalLengths, SequenceCode.LiteralLength);
        Select(offsets, SequenceCode.Offset);
        Select(matchLengths, SequenceCode.MatchLength);

        void Select(SeqTable? table, SequenceCode code)
        {
            if (table is not null)
            {
                _current[(int)code] = table;
                _tableLog[(int)code] = table.TableLog;
            }
        }
    }

    /// <summary>A shared table, copied into the slot unless it is there already.</summary>
    public void Use(SeqTable table)
    {
        int code = (int)table.Code;
        _current[code] = table;
        _tableLog[code] = table.TableLog;
        Materialize(table.Code);
    }

    /// <summary>The table the previous block used, for the repeat mode.</summary>
    public void Repeat(SequenceCode code) => Materialize(code);

    /// <summary>libzstd's <c>ZSTD_buildFSETable</c>, into the slot.</summary>
    public void Build(SequenceCode code, ReadOnlySpan<short> norm, int tableLog)
    {
        SequenceCodes.BuildTable(code, Entries.AsSpan(Slot(code)), norm, tableLog);
        Own(code, tableLog);
    }

    /// <summary>libzstd's <c>ZSTD_buildSeqTable_rle</c>: one state that every sequence takes.</summary>
    public void BuildRle(SequenceCode code, int symbol)
    {
        SequenceCodes.Symbol(code, symbol, out uint baseValue, out byte bits);
        int slot = Slot(code);
        Entries[slot] = new SeqSymbol(code, slot, bits, 0, baseValue);
        Entries[slot + 1] = Entries[slot];
        Own(code, 0);
    }

    private void Own(SequenceCode code, int tableLog)
    {
        _current[(int)code] = null;
        _held[(int)code] = null;
        _tableLog[(int)code] = tableLog;
    }

    private void Materialize(SequenceCode code)
    {
        SeqTable? table = _current[(int)code];
        if (!ReferenceEquals(table, _held[(int)code]))
        {
            table!.Entries.CopyTo(Entries.AsSpan(Slot(code)));
            _held[(int)code] = table;
        }
    }
}

/// <summary>The three sequence codes: their alphabets, their predefined distributions, their tables.</summary>
internal static class SequenceCodes
{
    public const int MaxLiteralLength = 35;
    public const int MaxMatchLength = 52;
    public const int MaxOffset = 31;
    public const int LiteralLengthMaxLog = 9;
    public const int MatchLengthMaxLog = 9;
    public const int OffsetMaxLog = 8;

    public static ReadOnlySpan<uint> LiteralLengthBase =>
    [
        0, 1, 2, 3, 4, 5, 6, 7,
        8, 9, 10, 11, 12, 13, 14, 15,
        16, 18, 20, 22, 24, 28, 32, 40,
        48, 64, 0x80, 0x100, 0x200, 0x400, 0x800, 0x1000,
        0x2000, 0x4000, 0x8000, 0x10000,
    ];

    public static ReadOnlySpan<byte> LiteralLengthBits =>
    [
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3,
        4, 6, 7, 8, 9, 10, 11, 12,
        13, 14, 15, 16,
    ];

    public static ReadOnlySpan<uint> MatchLengthBase =>
    [
        3, 4, 5, 6, 7, 8, 9, 10,
        11, 12, 13, 14, 15, 16, 17, 18,
        19, 20, 21, 22, 23, 24, 25, 26,
        27, 28, 29, 30, 31, 32, 33, 34,
        35, 37, 39, 41, 43, 47, 51, 59,
        67, 83, 99, 0x83, 0x103, 0x203, 0x403, 0x803,
        0x1003, 0x2003, 0x4003, 0x8003, 0x10003,
    ];

    public static ReadOnlySpan<byte> MatchLengthBits =>
    [
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3,
        4, 4, 5, 7, 8, 9, 10, 11,
        12, 13, 14, 15, 16,
    ];

    /// <summary>
    /// An offset code c stands for the value (1 &lt;&lt; c) + c extra bits; the base here is that minus 3,
    /// which turns a value above 3 into the offset itself. Values 1 to 3 are the repeat codes.
    /// </summary>
    public static ReadOnlySpan<uint> OffsetBase =>
    [
        0, 1, 1, 5, 0xD, 0x1D, 0x3D, 0x7D,
        0xFD, 0x1FD, 0x3FD, 0x7FD, 0xFFD, 0x1FFD, 0x3FFD, 0x7FFD,
        0xFFFD, 0x1FFFD, 0x3FFFD, 0x7FFFD, 0xFFFFD, 0x1FFFFD, 0x3FFFFD, 0x7FFFFD,
        0xFFFFFD, 0x1FFFFFD, 0x3FFFFFD, 0x7FFFFFD, 0xFFFFFFD, 0x1FFFFFFD, 0x3FFFFFFD, 0x7FFFFFFD,
    ];

    public static ReadOnlySpan<byte> OffsetBits =>
    [
        0, 1, 2, 3, 4, 5, 6, 7,
        8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23,
        24, 25, 26, 27, 28, 29, 30, 31,
    ];

    public static ReadOnlySpan<short> LiteralLengthDefaultNorm =>
    [
        4, 3, 2, 2, 2, 2, 2, 2,
        2, 2, 2, 2, 2, 1, 1, 1,
        2, 2, 2, 2, 2, 2, 2, 2,
        2, 3, 2, 1, 1, 1, 1, 1,
        -1, -1, -1, -1,
    ];

    public static ReadOnlySpan<short> MatchLengthDefaultNorm =>
    [
        1, 4, 3, 2, 2, 2, 2, 2,
        2, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, -1, -1,
        -1, -1, -1, -1, -1,
    ];

    public static ReadOnlySpan<short> OffsetDefaultNorm =>
    [
        1, 1, 1, 1, 1, 1, 2, 2,
        2, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1,
        -1, -1, -1, -1, -1,
    ];

    /// <summary>The predefined tables, built once from the default distributions.</summary>
    public static readonly SeqTable DefaultLiteralLengths = new(SequenceCode.LiteralLength, LiteralLengthDefaultNorm, 6);
    public static readonly SeqTable DefaultOffsets = new(SequenceCode.Offset, OffsetDefaultNorm, 5);
    public static readonly SeqTable DefaultMatchLengths = new(SequenceCode.MatchLength, MatchLengthDefaultNorm, 6);

    /// <summary>The largest symbol of a code.</summary>
    public static int MaxSymbol(SequenceCode code) => code switch
    {
        SequenceCode.LiteralLength => MaxLiteralLength,
        SequenceCode.Offset => MaxOffset,
        _ => MaxMatchLength,
    };

    /// <summary>The largest accuracy of a code's table.</summary>
    public static int MaxLog(SequenceCode code) => code switch
    {
        SequenceCode.LiteralLength => LiteralLengthMaxLog,
        SequenceCode.Offset => OffsetMaxLog,
        _ => MatchLengthMaxLog,
    };

    /// <summary>What a symbol of a code stands for: a base value and a number of extra bits.</summary>
    public static void Symbol(SequenceCode code, int symbol, out uint baseValue, out byte bits)
    {
        switch (code)
        {
            case SequenceCode.LiteralLength:
                baseValue = LiteralLengthBase[symbol];
                bits = LiteralLengthBits[symbol];
                break;
            case SequenceCode.Offset:
                baseValue = OffsetBase[symbol];
                bits = OffsetBits[symbol];
                break;
            default:
                baseValue = MatchLengthBase[symbol];
                bits = MatchLengthBits[symbol];
                break;
        }
    }

    /// <summary>
    /// libzstd's <c>ZSTD_buildFSETable</c>: spreads the symbols of a normalized distribution over the
    /// table and folds each symbol's base value and extra bits into its states, each written twice,
    /// whose next states are indices into the <see cref="SequenceTableSet"/> slot of <paramref name="code"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distribution comes from <see cref="Fse.ReadNCount"/>, which has validated it: every symbol
    /// is within the code's alphabet, and the counts, a symbol below one unit of probability counting
    /// one, sum to the table's size. No index here needs a bounds check.
    /// </para>
    /// <para>
    /// As libzstd's fast spread, without a branch that depends on the counts: the symbols are first
    /// laid down in order, eight copies a write, then scattered over the table. The cells k x step
    /// (modulo the size) for k from 0 run over the whole table once, and the laid-down symbols go to
    /// those below the threshold, in order: every cell is written, the index into the laid-down
    /// symbols moving only on those, and the top cells, written with whatever comes next, are then
    /// given to the symbols below one unit. With no chain from one cell to the next, four cells a
    /// step. Each state is then built whole from its symbol's base value and extra bits, and written
    /// twice in one store.
    /// </para>
    /// </remarks>
    public static void BuildTable(SequenceCode code, Span<SeqSymbol> entries, ReadOnlySpan<short> norm, int tableLog)
    {
        ReadOnlySpan<uint> baseValue = code switch
        {
            SequenceCode.LiteralLength => LiteralLengthBase,
            SequenceCode.Offset => OffsetBase,
            _ => MatchLengthBase,
        };
        ReadOnlySpan<byte> bits = code switch
        {
            SequenceCode.LiteralLength => LiteralLengthBits,
            SequenceCode.Offset => OffsetBits,
            _ => MatchLengthBits,
        };
        Debug.Assert(norm.Length <= MaxMatchLength + 1 && entries.Length >= 2 << tableLog);
        Debug.Assert(baseValue.Length >= norm.Length && bits.Length >= norm.Length);

        nint tableSize = (nint)1 << tableLog;
        nint symbolCount = norm.Length;
        Span<ushort> symbolNext = stackalloc ushort[MaxMatchLength + 1];
        Span<ulong> template = stackalloc ulong[MaxMatchLength + 1];
        Span<byte> spread = stackalloc byte[(1 << LiteralLengthMaxLog) + 8];
        Span<byte> symbols = stackalloc byte[1 << LiteralLengthMaxLog];
        Span<byte> lowSymbols = stackalloc byte[MaxMatchLength + 1];
        ref ushort next = ref MemoryMarshal.GetReference(symbolNext);
        ref ulong templates = ref MemoryMarshal.GetReference(template);
        ref byte spreadStart = ref MemoryMarshal.GetReference(spread);
        ref byte cells = ref MemoryMarshal.GetReference(symbols);
        ref byte low = ref MemoryMarshal.GetReference(lowSymbols);
        ref short counts = ref MemoryMarshal.GetReference(norm);
        ref uint bases = ref MemoryMarshal.GetReference(baseValue);
        ref byte extraBits = ref MemoryMarshal.GetReference(bits);

        // ---- per symbol: its template, its first state, and its place in the laid-down order. The
        // template holds the base value and the extra bits where the code's layout puts them, less
        // the 1 that nbBits + 1 doubles by (see the states below).
        int extraShift = code == SequenceCode.Offset ? 0 : 8;
        int nbBitsShift = code == SequenceCode.Offset ? 8 : 0;
        nint lowCount = 0;
        nint laid = 0;
        ulong copies = 0;
        for (nint s = 0; s < symbolCount; s++, copies += 0x0101010101010101UL)
        {
            // A count is -1 (below one unit) or positive: its absolute value is the symbol's first
            // state, its sign moves the threshold, its positive part is how many cells it lays down.
            // All three by arithmetic on the sign mask: Math.Abs and Math.Max become branches.
            nint count = Unsafe.Add(ref counts, s);
            nint sign = count >> 63;
            Unsafe.Add(ref templates, s) = ((ulong)Unsafe.Add(ref bases, s) << 16) + ((ulong)Unsafe.Add(ref extraBits, s) << extraShift) - (1UL << nbBitsShift);
            Unsafe.Add(ref next, s) = (ushort)((count ^ sign) - sign);

            // Below one unit: next in the list of the top cells, by a store whose index only moves
            // for such a symbol.
            Unsafe.Add(ref low, lowCount) = (byte)s;
            lowCount -= sign;

            // Eight copies of the symbol a write: nearly every count fits in one.
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref spreadStart, laid), copies);
            for (nint i = 8; i < count; i += 8)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref spreadStart, laid + i), copies);
            }

            laid += count & ~sign;
        }

        nint highThreshold = tableSize - 1 - lowCount;
        Debug.Assert(laid == highThreshold + 1);

        // ---- the scatter, four cells a step: see the remarks
        nint mask = tableSize - 1;
        nint step = Fse.TableStep((int)tableSize);
        nint at = 0;
        nint taken = 0;
        for (nint k = 0; k < tableSize; k += 4, at += 4 * step)
        {
            nint p0 = at & mask;
            nint p1 = (at + step) & mask;
            nint p2 = (at + (2 * step)) & mask;
            nint p3 = (at + (3 * step)) & mask;
            Unsafe.Add(ref cells, p0) = Unsafe.Add(ref spreadStart, taken);
            taken += p0 <= highThreshold ? 1 : 0;
            Unsafe.Add(ref cells, p1) = Unsafe.Add(ref spreadStart, taken);
            taken += p1 <= highThreshold ? 1 : 0;
            Unsafe.Add(ref cells, p2) = Unsafe.Add(ref spreadStart, taken);
            taken += p2 <= highThreshold ? 1 : 0;
            Unsafe.Add(ref cells, p3) = Unsafe.Add(ref spreadStart, taken);
            taken += p3 <= highThreshold ? 1 : 0;
        }

        Debug.Assert(taken == laid);
        for (nint i = 0; i < lowCount; i++)
        {
            Unsafe.Add(ref cells, tableSize - 1 - i) = Unsafe.Add(ref low, i);
        }

        // ---- the states, in table order: a symbol's k-th cell decodes the state count + k, which
        // reads nbBits = tableLog - highbit(count + k) bits and continues in the doubled slot at
        // slot + 2 x ((count + k) << nbBits) - 2 x size. The entry is summed, not or-ed, from a
        // template that already takes the 1 off nbBits + 1, the shift that doubles.
        //
        // A counter a symbol, read, incremented and written back each cell, makes a chain through
        // memory whenever a frequent symbol comes back within a few cells: each read waits on the
        // write before. So the two halves of the table are built side by side, each with its own
        // counters, those of the second half starting where the first half leaves them: the count
        // of each symbol in the first half comes from four histograms, each taking every fourth
        // cell, in which no chain forms either.
        nint stateBase = SequenceTableSet.Slot(code) - (2 * tableSize);
        nint bitsOffset = 63 - tableLog - 1;
        nint half = tableSize >> 1;
        Span<ushort> histograms = stackalloc ushort[4 * 64];
        histograms.Clear();
        ref ushort h = ref MemoryMarshal.GetReference(histograms);
        for (nint u = 0; u < half; u += 4)
        {
            Unsafe.Add(ref h, Unsafe.Add(ref cells, u))++;
            Unsafe.Add(ref h, 64 + Unsafe.Add(ref cells, u + 1))++;
            Unsafe.Add(ref h, 128 + Unsafe.Add(ref cells, u + 2))++;
            Unsafe.Add(ref h, 192 + Unsafe.Add(ref cells, u + 3))++;
        }

        Span<ushort> secondNext = stackalloc ushort[MaxMatchLength + 1];
        ref ushort next2 = ref MemoryMarshal.GetReference(secondNext);
        for (nint s = 0; s < symbolCount; s++)
        {
            Unsafe.Add(ref next2, s) = (ushort)(Unsafe.Add(ref next, s) + Unsafe.Add(ref h, s) + Unsafe.Add(ref h, 64 + s)
                + Unsafe.Add(ref h, 128 + s) + Unsafe.Add(ref h, 192 + s));
        }

        ref ulong output = ref Unsafe.As<SeqSymbol, ulong>(ref MemoryMarshal.GetReference(entries));
        ref ulong output2 = ref Unsafe.Add(ref output, 2 * half);
        for (nint u = 0; u < half; u++)
        {
            ulong first = Entry(ref cells, ref next, ref templates, u, bitsOffset, stateBase, nbBitsShift);
            ulong second = Entry(ref cells, ref next2, ref templates, u + half, bitsOffset, stateBase, nbBitsShift);
            output = first;
            Unsafe.Add(ref output, 1) = first;
            output2 = second;
            Unsafe.Add(ref output2, 1) = second;
            output = ref Unsafe.Add(ref output, 2);
            output2 = ref Unsafe.Add(ref output2, 2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong Entry(ref byte cells, ref ushort next, ref ulong templates, nint u, nint bitsOffset, nint stateBase, int nbBitsShift)
        {
            nint symbol = Unsafe.Add(ref cells, u);
            nint nextState = Unsafe.Add(ref next, symbol);
            Unsafe.Add(ref next, symbol) = (ushort)(nextState + 1);
            nint doubling = (nint)BitOperations.LeadingZeroCount((ulong)nextState) - bitsOffset;
            nint state = (nextState << (int)doubling) + stateBase;
            return Unsafe.Add(ref templates, symbol) + ((ulong)doubling << nbBitsShift) + ((ulong)state << 48);
        }
    }
}
