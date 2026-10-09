using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Vorticity.Zstd.Internal;

/// <summary>
/// One sequence of a block, as its encoding wants it: libzstd's <c>SeqDef</c> and what
/// <c>ZSTD_seqToCodes</c> derives from it, computed when the match finder stores the sequence (see
/// <see cref="SequenceStore.StoreCoded(ref SequenceRecord*, uint*, nuint, uint, nuint)"/>), or on x64
/// once the block's sequences are found (<see cref="SequenceStore.ComputeCodes"/>).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SequenceRecord
{
    /// <summary>The extra bits, in the stream's order: the literal length's, the match length's, the offset's.</summary>
    public ulong Extras;

    /// <summary>
    /// The codes, at their places in the sequence tables' common arrays: the literal length code, the
    /// offset code plus <see cref="SequenceStore.OffsetCodes"/> and the match length code plus
    /// <see cref="SequenceStore.MatchLengthCodes"/>, a byte each; then the number of extra bits.
    /// </summary>
    public uint Codes;

    /// <summary>libzstd's <c>offBase</c>: a repeat code from 1 to 3, or the offset plus 3.</summary>
    public uint OffBase;
}

/// <summary>
/// libzstd's <c>SeqStore_t</c>: a block's sequences as the match finder finds them, with their codes
/// and the codes' counts, and its literals copied out.
/// </summary>
/// <remarks>
/// <para>
/// On Arm64 the codes are computed as each sequence is stored, rather than in passes of their own as
/// libzstd does: the match finders wait on their loads and mispredictions, and have the issue slots to
/// spare. On x64 they do not have the registers: the codes are computed in one pass once the block's
/// sequences are found (see <see cref="DefersCodes"/>).
/// Computed from the full lengths, a length over 16 bits gets the largest code and its low 16 bits
/// as extra bits, which is what libzstd's long-length fix-up gives.
/// </para>
/// <para>The buffers are pinned for the compressor's lifetime.</para>
/// </remarks>
internal sealed unsafe class SequenceStore
{
    /// <summary>libzstd's <c>WILDCOPY_OVERLENGTH</c>: how far past their end the literal copies may write.</summary>
    public const int WildCopyOverlength = 32;

    /// <summary>libzstd's <c>ZSTD_maxNbSeq</c> for the largest block and the shortest match.</summary>
    public const int MaxSequences = FrameFormat.MaxBlockSize / 3;

    /// <summary>Where the offset codes start in the common arrays: after the 36 literal length codes.</summary>
    public const int OffsetCodes = SequenceCodes.MaxLiteralLength + 1;

    /// <summary>Where the match length codes start in the common arrays: after the 32 offset codes.</summary>
    public const int MatchLengthCodes = OffsetCodes + SequenceCodes.MaxOffset + 1;

    /// <summary>The size of the common arrays: the 53 match length codes last.</summary>
    public const int AllCodes = MatchLengthCodes + SequenceCodes.MaxMatchLength + 1;

    private readonly byte[] _literals = GC.AllocateUninitializedArray<byte>(FrameFormat.MaxBlockSize + WildCopyOverlength, pinned: true);
    private readonly SequenceRecord[] _sequences = GC.AllocateUninitializedArray<SequenceRecord>(MaxSequences + 1, pinned: true);
    private readonly uint[] _counts = GC.AllocateArray<uint>(AllCodes, pinned: true);

    public SequenceStore()
    {
        LiteralsStart = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_literals));
        SequencesStart = (SequenceRecord*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_sequences));
        Counts = (uint*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_counts));
        Reset();
    }

    /// <summary>The counts of the codes, at their places in the common arrays.</summary>
    public uint* Counts { get; }

    public byte* LiteralsStart { get; }

    /// <summary>Where the next literal goes.</summary>
    public byte* Literals;

    public SequenceRecord* SequencesStart { get; }

    /// <summary>Where the next sequence goes.</summary>
    public SequenceRecord* Sequences;


    public nuint SequenceCount => (nuint)(Sequences - SequencesStart);

    public nuint LiteralCount => (nuint)(Literals - LiteralsStart);

    /// <summary>libzstd's <c>ZSTD_resetSeqStore</c>, and the counts cleared.</summary>
    public void Reset()
    {
        Literals = LiteralsStart;
        Sequences = SequencesStart;
        Unsafe.InitBlockUnaligned(Counts, 0, AllCodes * sizeof(uint));
    }

    /// <summary>
    /// libzstd's <c>ZSTD_storeSeq</c>: the literals before the match copied out, 16 bytes at a time
    /// when they end 32 bytes or more before <paramref name="literalsLimit"/>, then the sequence.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(nuint litLength, byte* literals, byte* literalsLimit, uint offBase, nuint matchLength)
    {
        byte* lit = Literals;
        SequenceRecord* sequence = Sequences;
        Store(ref lit, ref sequence, Counts, litLength, literals, literalsLimit, offBase, matchLength);
        Literals = lit;
        Sequences = sequence;
    }

    /// <summary>
    /// <see cref="Store(nuint, byte*, byte*, uint, nuint)"/> on cursors a match finder holds (<see cref="Literals"/>,
    /// <see cref="Sequences"/>, written back once per block), and the store's <see cref="Counts"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store(
        ref byte* lit, ref SequenceRecord* sequence, uint* counts, nuint litLength, byte* literals, byte* literalsLimit,
        uint offBase, nuint matchLength)
    {
        byte* limitWild = literalsLimit - WildCopyOverlength;
        byte* end = literals + litLength;
        if (end <= limitWild)
        {
            Copy16(lit, literals);
            if (litLength > 16)
            {
                WildCopy(lit + 16, literals + 16, (nint)litLength - 16);
            }
        }
        else
        {
            SafeCopyLiterals(lit, literals, end, limitWild);
        }

        lit += litLength;
        StoreOnly(ref sequence, counts, litLength, offBase, matchLength);
    }

    /// <summary>
    /// Whether the match finders store a sequence as its two lengths and its offset alone, as libzstd
    /// does, its codes computed once the block is found (<see cref="ComputeCodes"/>): on x64. Computed
    /// as each sequence is stored, the codes take some forty instructions and a dozen values the
    /// match finders have no registers left for on x64, which spill them; in a pass of their own,
    /// they take a loop that holds them all. Arm64, with its 31 registers, computes them as it stores.
    /// </summary>
    public static bool DefersCodes => X86Base.IsSupported;

    /// <summary>
    /// libzstd's <c>ZSTD_storeSeqOnly</c>: with its codes (<see cref="StoreCoded"/>), or, where
    /// <see cref="DefersCodes"/>, the lengths in the place of the extra bits and the offset in its
    /// own, <see cref="ComputeCodes"/> to complete them.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreOnly(ref SequenceRecord* sequence, uint* counts, nuint litLength, uint offBase, nuint matchLength)
    {
        if (DefersCodes)
        {
            sequence->Extras = (ulong)litLength | ((ulong)matchLength << 32);
            sequence->OffBase = offBase;
            sequence++;
            return;
        }

        StoreCoded(ref sequence, counts, litLength, offBase, matchLength);
    }

    /// <summary>
    /// The codes of the sequences stored since the store was reset, where <see cref="DefersCodes"/>:
    /// each record completed from its lengths and offset, its codes counted. Called once the block's
    /// sequences are found, before anything reads them.
    /// </summary>
    public void ComputeCodes()
    {
        if (!DefersCodes)
        {
            return;
        }

        uint* counts = Counts;
        SequenceRecord* record = SequencesStart;
        SequenceRecord* end = Sequences;
        while (record < end)
        {
            ulong lengths = record->Extras;
            StoreCoded(ref record, counts, (nuint)(uint)lengths, record->OffBase, (nuint)(lengths >> 32));
        }
    }

    /// <summary>
    /// libzstd's <c>ZSTD_storeSeqOnly</c>, and its codes: those of the two lengths from tables below 64
    /// and 128, from their logarithms above (libzstd's <c>ZSTD_LLcode</c>, <c>ZSTD_MLcode</c>), the
    /// offset's from its logarithm; the extra bits gathered in the stream's order; the codes counted.
    /// </summary>
    /// <remarks>
    /// On x64 an entry is read as its two bytes, the code and the bits (an entry split took four
    /// instructions), the extra bits are kept with <c>bzhi</c> (a mask took three), and the offset's
    /// logarithm, of a value never 0, without the guard against 0.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreCoded(ref SequenceRecord* sequence, uint* counts, nuint litLength, uint offBase, nuint matchLength)
    {
        Debug.Assert(matchLength >= 3 && litLength <= FrameFormat.MaxBlockSize && offBase != 0);
        nuint matchLengthBase = matchLength - 3;
        nuint llCode;
        nuint llBits;
        if (litLength < 64)
        {
            ushort* table = (ushort*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(LiteralLengthCodeTable));
            if (X86Base.IsSupported)
            {
                llCode = ((byte*)(table + litLength))[0];
                llBits = ((byte*)(table + litLength))[1];
            }
            else
            {
                nuint entry = table[litLength];
                llCode = entry & 0x3F;
                llBits = entry >> 8;
            }
        }
        else
        {
            llBits = (nuint)(uint)BitOperations.Log2(litLength);
            llCode = llBits + 19;
        }

        nuint mlCode;
        nuint mlBits;
        if (matchLengthBase < 128)
        {
            ushort* table = (ushort*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(MatchLengthCodeTable));
            if (X86Base.IsSupported)
            {
                mlCode = ((byte*)(table + matchLengthBase))[0];
                mlBits = ((byte*)(table + matchLengthBase))[1];
            }
            else
            {
                nuint entry = table[matchLengthBase];
                mlCode = entry & 0x3F;
                mlBits = entry >> 8;
            }
        }
        else
        {
            mlBits = (nuint)(uint)BitOperations.Log2(matchLengthBase);
            mlCode = mlBits + 36;
        }

        nuint ofCode = X86Base.IsSupported
            ? (nuint)(uint)(31 ^ BitOperations.LeadingZeroCount(offBase))
            : (nuint)(uint)BitOperations.Log2(offBase);
        ulong extras = LowBits(litLength, llBits)
            | (LowBits(matchLengthBase, mlBits) << (int)llBits)
            | ((ulong)(offBase ^ (1u << (int)ofCode)) << (int)(llBits + mlBits));

        // The codes at their places in the common arrays, which index the counts as they are.
        nuint ofIndex = ofCode + OffsetCodes;
        nuint mlIndex = mlCode + MatchLengthCodes;
        nuint codes = llCode | (ofIndex << 8) | (mlIndex << 16) | ((llBits + mlBits + ofCode) << 24);
        sequence->Extras = extras;
        *(ulong*)&sequence->Codes = codes | ((ulong)offBase << 32);
        counts[llCode]++;
        counts[ofIndex]++;
        counts[mlIndex]++;
        sequence++;
    }

    /// <summary>The low <paramref name="count"/> bits of <paramref name="value"/>, below 64.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong LowBits(nuint value, nuint count) =>
        Bmi2.X64.IsSupported ? Bmi2.X64.ZeroHighBits(value, count) : value & ~(ulong.MaxValue << (int)count);

    /// <summary>The whole store, as a section the entropy coding takes.</summary>
    public SequenceSection Whole => new(LiteralsStart, LiteralCount, SequencesStart, SequenceCount, Counts);

    /// <summary>A record's literal length: its code's baseline, plus its extra bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint LiteralLengthOf(SequenceRecord* record)
    {
        int code = (int)(record->Codes & 0xFF);
        int bits = LiteralLengthBits[code];
        return LiteralLengthBaselines[code] + (uint)(record->Extras & ~(ulong.MaxValue << bits));
    }

    /// <summary>A record's match length: its code's baseline, plus its extra bits, plus 3.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint MatchLengthOf(SequenceRecord* record)
    {
        int literalLengthBits = LiteralLengthBits[(int)(record->Codes & 0xFF)];
        int code = (int)(((record->Codes >> 16) & 0xFF) - MatchLengthCodes);
        int bits = MatchLengthBits[code];
        return MatchLengthBaselines[code] + (uint)((record->Extras >> literalLengthBits) & ~(ulong.MaxValue << bits)) + 3;
    }

    /// <summary>
    /// A record's offset code replaced, its lengths kept: its codes and extra bits computed again. The
    /// counts are not changed: whoever replaces counts the codes again.
    /// </summary>
    public static void ReplaceOffBase(SequenceRecord* record, uint offBase)
    {
        nuint literalLength = LiteralLengthOf(record);
        nuint matchLength = MatchLengthOf(record);
        uint* scratch = stackalloc uint[AllCodes];
        SequenceRecord* sequence = record;
        StoreCoded(ref sequence, scratch, literalLength, offBase, matchLength);
    }

    /// <summary>The codes of <paramref name="count"/> records counted into <paramref name="counts"/>, cleared first.</summary>
    /// <remarks>
    /// Every code is below 128 (<see cref="AllCodes"/>): 7-bit fields, each one ubfx, where the JIT
    /// narrows an 8-bit one with uxtb before scaling it.
    /// </remarks>
    public static void CountCodes(SequenceRecord* records, nuint count, uint* counts)
    {
        new Span<uint>(counts, AllCodes).Clear();
        for (nuint n = 0; n < count; n++)
        {
            nuint codes = records[n].Codes;
            counts[codes & 0x7F]++;
            counts[(codes >> 8) & 0x7F]++;
            counts[(codes >> 16) & 0x7F]++;
        }
    }

    /// <summary>libzstd's <c>LL_bits</c>.</summary>
    public static ReadOnlySpan<byte> LiteralLengthBits =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12,
        13, 14, 15, 16,
    ];

    /// <summary>libzstd's <c>ML_bits</c>.</summary>
    public static ReadOnlySpan<byte> MatchLengthBits =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11,
        12, 13, 14, 15, 16,
    ];

    /// <summary>The literal length each code starts at.</summary>
    private static ReadOnlySpan<uint> LiteralLengthBaselines =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 18, 20, 22, 24, 28, 32, 40, 48, 64, 0x80, 0x100, 0x200, 0x400, 0x800, 0x1000,
        0x2000, 0x4000, 0x8000, 0x10000,
    ];

    /// <summary>The match length less 3 each code starts at.</summary>
    private static ReadOnlySpan<uint> MatchLengthBaselines =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
        32, 34, 36, 38, 40, 44, 48, 56, 64, 80, 96, 0x80, 0x100, 0x200, 0x400, 0x800,
        0x1000, 0x2000, 0x4000, 0x8000, 0x10000,
    ];

    /// <summary>libzstd's <c>LL_Code</c> and <c>LL_bits</c> below 64: <c>code | bits &lt;&lt; 8</c>.</summary>
    private static ReadOnlySpan<ushort> LiteralLengthCodeTable =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        0x110, 0x110, 0x111, 0x111, 0x112, 0x112, 0x113, 0x113,
        0x214, 0x214, 0x214, 0x214, 0x215, 0x215, 0x215, 0x215,
        0x316, 0x316, 0x316, 0x316, 0x316, 0x316, 0x316, 0x316,
        0x317, 0x317, 0x317, 0x317, 0x317, 0x317, 0x317, 0x317,
        0x418, 0x418, 0x418, 0x418, 0x418, 0x418, 0x418, 0x418,
        0x418, 0x418, 0x418, 0x418, 0x418, 0x418, 0x418, 0x418,
    ];

    /// <summary>libzstd's <c>ML_Code</c> and <c>ML_bits</c> below 128 (a match length minus 3): <c>code | bits &lt;&lt; 8</c>.</summary>
    private static ReadOnlySpan<ushort> MatchLengthCodeTable =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
        0x120, 0x120, 0x121, 0x121, 0x122, 0x122, 0x123, 0x123,
        0x224, 0x224, 0x224, 0x224, 0x225, 0x225, 0x225, 0x225,
        0x326, 0x326, 0x326, 0x326, 0x326, 0x326, 0x326, 0x326,
        0x327, 0x327, 0x327, 0x327, 0x327, 0x327, 0x327, 0x327,
        0x428, 0x428, 0x428, 0x428, 0x428, 0x428, 0x428, 0x428,
        0x428, 0x428, 0x428, 0x428, 0x428, 0x428, 0x428, 0x428,
        0x429, 0x429, 0x429, 0x429, 0x429, 0x429, 0x429, 0x429,
        0x429, 0x429, 0x429, 0x429, 0x429, 0x429, 0x429, 0x429,
        0x52A, 0x52A, 0x52A, 0x52A, 0x52A, 0x52A, 0x52A, 0x52A,
        0x52A, 0x52A, 0x52A, 0x52A, 0x52A, 0x52A, 0x52A, 0x52A,
        0x52A, 0x52A, 0x52A, 0x52A, 0x52A, 0x52A, 0x52A, 0x52A,
        0x52A, 0x52A, 0x52A, 0x52A, 0x52A, 0x52A, 0x52A, 0x52A,
    ];

    /// <summary>libzstd's <c>ZSTD_storeLastLiterals</c>: the literals after the last sequence.</summary>
    public void StoreLastLiterals(byte* anchor, nuint size)
    {
        Buffer.MemoryCopy(anchor, Literals, size, size);
        Literals += size;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Copy16(byte* destination, byte* source) =>
        Unsafe.WriteUnaligned(destination, Unsafe.ReadUnaligned<Vector128<byte>>(source));

    /// <summary>libzstd's <c>ZSTD_wildcopy</c> without overlap: 16 bytes, then 32 a step, up to 31 past the end.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WildCopy(byte* destination, byte* source, nint length)
    {
        byte* end = destination + length;
        Copy16(destination, source);
        if (length <= 16)
        {
            return;
        }

        destination += 16;
        source += 16;
        do
        {
            Copy16(destination, source);
            Copy16(destination + 16, source + 16);
            destination += 32;
            source += 32;
        }
        while (destination < end);
    }

    /// <summary>
    /// libzstd's <c>ZSTD_safecopyLiterals</c>: never reads past <paramref name="end"/>; wild copies up to
    /// <paramref name="limitWild"/>, then a byte at a time.
    /// </summary>
    /// <remarks>
    /// Inlined although rare: a call in a match finder makes the JIT keep what is live across it in
    /// the registers a call preserves, too few, and it spilled values of the search loop to the stack
    /// at every position.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SafeCopyLiterals(byte* destination, byte* source, byte* end, byte* limitWild)
    {
        if (source <= limitWild)
        {
            WildCopy(destination, source, (nint)(limitWild - source));
            destination += limitWild - source;
            source = limitWild;
        }

        while (source < end)
        {
            *destination++ = *source++;
        }
    }
}

/// <summary>
/// Sequences and their literals that the entropy coding takes as a block: a whole store, or a part
/// of it, which libzstd's post-block splitter emits as a block of its own.
/// </summary>
internal readonly unsafe struct SequenceSection
{
    public SequenceSection(byte* literalsStart, nuint literalCount, SequenceRecord* sequencesStart, nuint sequenceCount, uint* counts)
    {
        LiteralsStart = literalsStart;
        LiteralCount = literalCount;
        SequencesStart = sequencesStart;
        SequenceCount = sequenceCount;
        Counts = counts;
    }

    public byte* LiteralsStart { get; }

    public nuint LiteralCount { get; }

    public SequenceRecord* SequencesStart { get; }

    public nuint SequenceCount { get; }

    /// <summary>The codes of the sequences counted, at their places (<see cref="SequenceStore.Counts"/>).</summary>
    public uint* Counts { get; }
}
