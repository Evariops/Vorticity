using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Vorticity.Zstd.Internal;

/// <summary>libzstd's <c>SeqDef</c>: one sequence as a block stores it before encoding.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SequenceRecord
{
    /// <summary>A repeat code from 1 to 3, or the offset plus 3.</summary>
    public uint OffBase;

    /// <summary>The literal length, modulo 2^16 for the one long length a block may have.</summary>
    public ushort LitLength;

    /// <summary>The match length minus 3, modulo 2^16 for the one long length a block may have.</summary>
    public ushort MatchLengthBase;
}

/// <summary>libzstd's <c>ZSTD_longLengthType_e</c>: which length of the long sequence exceeds 16 bits.</summary>
internal enum LongLengthType
{
    None,
    LiteralLength,
    MatchLength,
}

/// <summary>
/// libzstd's <c>SeqStore_t</c>: a block's sequences as the match finder finds them, its literals
/// copied out, and the codes of the sequences once they are encoded.
/// </summary>
/// <remarks>The buffers are pinned for the compressor's lifetime.</remarks>
internal sealed unsafe class SequenceStore
{
    /// <summary>libzstd's <c>WILDCOPY_OVERLENGTH</c>: how far past their end the literal copies may write.</summary>
    public const int WildCopyOverlength = 32;

    /// <summary>libzstd's <c>ZSTD_maxNbSeq</c> for the largest block and the shortest match.</summary>
    public const int MaxSequences = FrameFormat.MaxBlockSize / 3;

    private readonly byte[] _literals = GC.AllocateUninitializedArray<byte>(FrameFormat.MaxBlockSize + WildCopyOverlength, pinned: true);
    private readonly SequenceRecord[] _sequences = GC.AllocateUninitializedArray<SequenceRecord>(MaxSequences + 1, pinned: true);
    private readonly byte[] _codes = GC.AllocateUninitializedArray<byte>(3 * (MaxSequences + 1), pinned: true);

    public SequenceStore()
    {
        LiteralsStart = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_literals));
        SequencesStart = (SequenceRecord*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_sequences));
        LiteralLengthCodes = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_codes));
        OffsetCodes = LiteralLengthCodes + MaxSequences + 1;
        MatchLengthCodes = OffsetCodes + MaxSequences + 1;
        Reset();
    }

    public byte* LiteralsStart { get; }

    /// <summary>Where the next literal goes.</summary>
    public byte* Literals;

    public SequenceRecord* SequencesStart { get; }

    /// <summary>Where the next sequence goes.</summary>
    public SequenceRecord* Sequences;

    public byte* LiteralLengthCodes { get; }

    public byte* OffsetCodes { get; }

    public byte* MatchLengthCodes { get; }

    public LongLengthType LongLengthType;

    /// <summary>The index of the sequence whose length <see cref="LongLengthType"/> designates.</summary>
    public uint LongLengthPosition;

    public nuint SequenceCount => (nuint)(Sequences - SequencesStart);

    public nuint LiteralCount => (nuint)(Literals - LiteralsStart);

    /// <summary>libzstd's <c>ZSTD_resetSeqStore</c>.</summary>
    public void Reset()
    {
        Literals = LiteralsStart;
        Sequences = SequencesStart;
        LongLengthType = LongLengthType.None;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_storeSeq</c>: the literals before the match copied out, 16 bytes at a time
    /// when they end 32 bytes or more before <paramref name="literalsLimit"/>, then the sequence.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(nuint litLength, byte* literals, byte* literalsLimit, uint offBase, nuint matchLength)
    {
        byte* limitWild = literalsLimit - WildCopyOverlength;
        byte* end = literals + litLength;
        byte* lit = Literals;
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

        Literals = lit + litLength;
        StoreOnly(litLength, offBase, matchLength);
    }

    /// <summary>libzstd's <c>ZSTD_storeSeqOnly</c>: the sequence, its literals copied already.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StoreOnly(nuint litLength, uint offBase, nuint matchLength)
    {
        Debug.Assert(SequenceCount < MaxSequences);
        Debug.Assert(matchLength >= 3);
        SequenceRecord* sequence = Sequences;
        if (litLength > 0xFFFF)
        {
            Debug.Assert(LongLengthType == LongLengthType.None);
            LongLengthType = LongLengthType.LiteralLength;
            LongLengthPosition = (uint)(sequence - SequencesStart);
        }

        nuint matchLengthBase = matchLength - 3;
        if (matchLengthBase > 0xFFFF)
        {
            Debug.Assert(LongLengthType == LongLengthType.None);
            LongLengthType = LongLengthType.MatchLength;
            LongLengthPosition = (uint)(sequence - SequencesStart);
        }

        sequence->LitLength = (ushort)litLength;
        sequence->OffBase = offBase;
        sequence->MatchLengthBase = (ushort)matchLengthBase;
        Sequences = sequence + 1;
    }

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
