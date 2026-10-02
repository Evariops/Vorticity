using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Vorticity.Zstd.Internal;

namespace Vorticity.Zstd;

public sealed partial class ZstdDecompressor
{
    /// <summary>How far past their end the fast copies may write, and read literals.</summary>
    private const int WildCopyOverlength = 32;

    /// <summary>A bitstream shorter than eight bytes, copied where eight can be read.</summary>
    private readonly byte[] _shortBitstream = new byte[8];

    /// <summary>
    /// The decoder's whole state between two sequences, for the careful path to take over from the
    /// fast loop.
    /// </summary>
    private ref struct SequenceState
    {
        public ref byte Bits;
        public nint Ptr;
        public int Consumed;
        public ulong Container;
        public ref SeqSymbol LiteralLengths;
        public ref SeqSymbol MatchLengths;
        public ref SeqSymbol Offsets;
        public nint LiteralLengthState;
        public nint MatchLengthState;
        public nint OffsetState;
        public nuint Rep0;
        public nuint Rep1;
        public nuint Rep2;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_decompressSequences_body</c>: each sequence decoded and executed at once,
    /// with copies 16 bytes wide that may run past their end into room proven beforehand, then the
    /// literals after the last one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fast loop calls nothing, so that its state stays in registers: a sequence it cannot take
    /// hands the rest of the block to <see cref="ExecuteSequencesCareful"/>. Why no access of the
    /// fast loop leaves its buffer, established before it and kept by every iteration:
    /// </para>
    /// <list type="bullet">
    /// <item>A sequence runs here only if it ends <see cref="WildCopyOverlength"/> bytes before the
    /// destination does, and its literals before the last literal: a 16-byte copy that overruns stays
    /// inside the destination, and inside <paramref name="literals"/>, which has
    /// <see cref="LiteralsMargin"/> readable bytes after the last literal.</item>
    /// <item>Its match lies wholly within this frame's output, before the write position.</item>
    /// <item>The bitstream is read eight bytes at a time at a position that never goes below its
    /// start (see <see cref="Reload"/>). A stream under eight bytes is first copied into eight.</item>
    /// <item>States index their tables in range: a table built from a validated distribution maps
    /// every state and its low bits back into the table.</item>
    /// </list>
    /// </remarks>
    private int ExecuteSequences(
        ReadOnlySpan<byte> bitstream, int nbSeq, ReadOnlySpan<byte> literals, int literalCount,
        Span<byte> destination, int op, int blockSizeMax, ReadOnlySpan<byte> history)
    {
        Debug.Assert(literals.Length - literalCount >= LiteralsMargin);
        ref byte frameStart = ref MemoryMarshal.GetReference(destination);
        ref byte dst = ref Unsafe.Add(ref frameStart, op);
        ref byte lit = ref MemoryMarshal.GetReference(literals);
        ref byte litEnd = ref Unsafe.Add(ref lit, literalCount);
        nint fastRoom = Math.Min((nint)op + blockSizeMax, destination.Length - WildCopyOverlength);
        ref byte fastLimit = ref Unsafe.Add(ref frameStart, Math.Max(fastRoom, op));

        if (nbSeq > 0)
        {
            _sequenceEntropy = true;

            // ---- the bitstream, libzstd's BIT_initDStream
            if (bitstream.IsEmpty || bitstream[^1] == 0)
            {
                Throw.Error(ZstdError.SequenceBitstream);
            }

            int bc = 8 - BackwardBitReader.HighBit(bitstream[^1]);
            nint ptr;
            if (bitstream.Length >= 8)
            {
                ptr = bitstream.Length - 8;
            }
            else
            {
                Array.Clear(_shortBitstream);
                bitstream.CopyTo(_shortBitstream);
                bc += (8 - bitstream.Length) * 8;
                bitstream = _shortBitstream;
                ptr = 0;
            }

            ref byte bits = ref MemoryMarshal.GetReference(bitstream);
            ulong container = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bits, ptr));

            SeqTable llTableObject = _literalLengths;
            SeqTable ofTableObject = _offsets;
            SeqTable mlTableObject = _matchLengths;
            ref SeqSymbol llTable = ref MemoryMarshal.GetArrayDataReference(llTableObject.Entries);
            ref SeqSymbol ofTable = ref MemoryMarshal.GetArrayDataReference(ofTableObject.Entries);
            ref SeqSymbol mlTable = ref MemoryMarshal.GetArrayDataReference(mlTableObject.Entries);

            // ---- the initial states, each read followed by a reload
            nint llState = (nint)ReadBits(container, ref bc, llTableObject.TableLog);
            Reload(ref bits, ref ptr, ref bc, ref container);
            nint ofState = (nint)ReadBits(container, ref bc, ofTableObject.TableLog);
            Reload(ref bits, ref ptr, ref bc, ref container);
            nint mlState = (nint)ReadBits(container, ref bc, mlTableObject.TableLog);
            Reload(ref bits, ref ptr, ref bc, ref container);

            nuint rep0 = _rep0;
            nuint rep1 = _rep1;
            nuint rep2 = _rep2;
            nint litLength;
            nint matchLength;
            nuint offset;

            while (true)
            {
                // ---- decode: libzstd's ZSTD_decodeSequence, with the repeat offsets selected
                // without a branch
                ref SeqSymbol ll = ref Unsafe.Add(ref llTable, llState);
                ref SeqSymbol ml = ref Unsafe.Add(ref mlTable, mlState);
                ref SeqSymbol of = ref Unsafe.Add(ref ofTable, ofState);
                int llBits = ll.NbAdditionalBits;
                int mlBits = ml.NbAdditionalBits;
                int ofBits = of.NbAdditionalBits;
                uint litLengthBase = ll.BaseValue;

                // Extra bits are read only when there are some, as libzstd does: for most data the
                // literal and match lengths have none, and the branch is then always predicted.
                if (ofBits > 1)
                {
                    offset = of.BaseValue + (nuint)ReadBitsFast(container, ref bc, ofBits);
                    rep2 = rep1;
                    rep1 = rep0;
                    rep0 = offset;
                }
                else
                {
                    nuint raw = of.BaseValue + (ofBits == 0 ? 0 : (nuint)ReadBitsFast(container, ref bc, 1));
                    offset = ResolveOffset(raw, ofBits, litLengthBase == 0, ref rep0, ref rep1, ref rep2);
                }

                matchLength = (nint)ml.BaseValue;
                if (mlBits > 0)
                {
                    matchLength += (nint)ReadBitsFast(container, ref bc, mlBits);
                }

                if (llBits + mlBits + ofBits >= 57 - (SequenceCodes.LiteralLengthMaxLog + SequenceCodes.MatchLengthMaxLog + SequenceCodes.OffsetMaxLog))
                {
                    Reload(ref bits, ref ptr, ref bc, ref container);
                }

                litLength = (nint)litLengthBase;
                if (llBits > 0)
                {
                    litLength += (nint)ReadBitsFast(container, ref bc, llBits);
                }

                if (nbSeq != 1)
                {
                    llState = ll.NextState + (nint)ReadBits(container, ref bc, ll.NbBits);
                    mlState = ml.NextState + (nint)ReadBits(container, ref bc, ml.NbBits);
                    ofState = of.NextState + (nint)ReadBits(container, ref bc, of.NbBits);
                    Reload(ref bits, ref ptr, ref bc, ref container);
                }

                // ---- execute: libzstd's ZSTD_execSequence
                ref byte litAfter = ref Unsafe.Add(ref lit, litLength);
                ref byte matchStart = ref Unsafe.Add(ref dst, litLength);
                if (Unsafe.IsAddressGreaterThan(ref Unsafe.Add(ref matchStart, matchLength), ref fastLimit)
                    || Unsafe.IsAddressGreaterThan(ref litAfter, ref litEnd)
                    || offset > (nuint)Unsafe.ByteOffset(ref frameStart, ref matchStart))
                {
                    break;
                }

                // Literals: one 16-byte copy covers nearly all of them.
                Copy16(ref dst, ref lit);
                if (litLength > 16)
                {
                    WideCopy(ref Unsafe.Add(ref dst, 16), ref Unsafe.Add(ref lit, 16), litLength - 16);
                }

                lit = ref litAfter;
                ref byte match = ref Unsafe.Subtract(ref matchStart, offset);
                if (offset >= 16)
                {
                    // Source and destination at least 16 apart: plain 16-byte steps.
                    Copy16(ref matchStart, ref match);
                    if (matchLength > 16)
                    {
                        WideCopy(ref Unsafe.Add(ref matchStart, 16), ref Unsafe.Add(ref match, 16), matchLength - 16);
                    }
                }
                else
                {
                    OverlapCopy(ref matchStart, ref match, offset, matchLength);
                }

                dst = ref Unsafe.Add(ref matchStart, matchLength);
                if (--nbSeq == 0)
                {
                    goto Done;
                }
            }

            // ---- the careful path takes over, from the sequence just decoded to the last.
            var state = new SequenceState
            {
                Bits = ref bits,
                Ptr = ptr,
                Consumed = bc,
                Container = container,
                LiteralLengths = ref llTable,
                MatchLengths = ref mlTable,
                Offsets = ref ofTable,
                LiteralLengthState = llState,
                MatchLengthState = mlState,
                OffsetState = ofState,
                Rep0 = rep0,
                Rep1 = rep1,
                Rep2 = rep2,
            };
            int position = (int)Unsafe.ByteOffset(ref frameStart, ref dst);
            int literal = (int)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(literals), ref lit);
            return ExecuteSequencesCareful(
                ref state, nbSeq, litLength, matchLength, offset, literals, literal, literalCount,
                destination, op, position, blockSizeMax, history);

        Done:
            if (!(ptr == 0 && bc == 64))
            {
                Throw.Error(ZstdError.SequenceBitstream);
            }

            _rep0 = (uint)rep0;
            _rep1 = (uint)rep1;
            _rep2 = (uint)rep2;
        }

        int written = (int)Unsafe.ByteOffset(ref frameStart, ref dst);
        int literalsDone = (int)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(literals), ref lit);
        return FinishBlock(literals, literalsDone, literalCount, destination, op, written, blockSizeMax);
    }

    /// <summary>
    /// Executes the sequence the fast loop could not, then decodes and executes the remaining ones,
    /// checking each copy in libzstd's order (<c>ZSTD_execSequenceEnd</c>).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ExecuteSequencesCareful(
        ref SequenceState state, int nbSeq, nint litLength, nint matchLength, nuint offset,
        ReadOnlySpan<byte> literals, int literal, int literalCount,
        Span<byte> destination, int blockStart, int position, int blockSizeMax, ReadOnlySpan<byte> history)
    {
        ref byte frameStart = ref MemoryMarshal.GetReference(destination);
        ref byte lit = ref MemoryMarshal.GetReference(literals);
        nint o = position;
        nint litPtr = literal;
        nint blockEnd = (nint)blockStart + blockSizeMax;
        nint oend = Math.Min(blockEnd, destination.Length);

        while (true)
        {
            nint seqEnd = o + litLength + matchLength;
            if (seqEnd > oend)
            {
                Throw.Error(seqEnd > blockEnd ? ZstdError.BlockTooLarge : ZstdError.DestinationTooSmall);
            }

            if (litPtr + litLength > literalCount)
            {
                Throw.Error(ZstdError.LiteralsOverrun);
            }

            CopyForward(ref frameStart, o, ref lit, litPtr, litLength);
            o += litLength;
            litPtr += litLength;

            nint matchFrom;
            if (offset > (nuint)o)
            {
                // Before the frame: into the dictionary, then on into the frame's own output.
                if (offset > (nuint)(o + history.Length))
                {
                    Throw.Error(ZstdError.OffsetTooLarge);
                }

                nint h = history.Length - (nint)(offset - (nuint)o);
                nint fromHistory = Math.Min(matchLength, history.Length - h);
                CopyForward(ref frameStart, o, ref MemoryMarshal.GetReference(history), h, fromHistory);
                o += fromHistory;
                matchLength -= fromHistory;
                matchFrom = 0;
            }
            else
            {
                matchFrom = o - (nint)offset;
            }

            CopyForward(ref frameStart, o, ref frameStart, matchFrom, matchLength);
            o += matchLength;

            if (--nbSeq == 0)
            {
                break;
            }

            DecodeSequence(ref state, nbSeq, out litLength, out matchLength, out offset);
        }

        if (!(state.Ptr == 0 && state.Consumed == 64))
        {
            Throw.Error(ZstdError.SequenceBitstream);
        }

        _rep0 = (uint)state.Rep0;
        _rep1 = (uint)state.Rep1;
        _rep2 = (uint)state.Rep2;
        return FinishBlock(literals, (int)litPtr, literalCount, destination, blockStart, (int)o, blockSizeMax);
    }

    /// <summary>The fast loop's decoding, on the state the careful path carries.</summary>
    private static void DecodeSequence(ref SequenceState s, int nbSeq, out nint litLength, out nint matchLength, out nuint offset)
    {
        ref SeqSymbol ll = ref Unsafe.Add(ref s.LiteralLengths, s.LiteralLengthState);
        ref SeqSymbol ml = ref Unsafe.Add(ref s.MatchLengths, s.MatchLengthState);
        ref SeqSymbol of = ref Unsafe.Add(ref s.Offsets, s.OffsetState);
        int llBits = ll.NbAdditionalBits;
        int mlBits = ml.NbAdditionalBits;
        int ofBits = of.NbAdditionalBits;
        nuint raw = of.BaseValue + (nuint)ReadBits(s.Container, ref s.Consumed, ofBits);
        offset = ResolveOffset(raw, ofBits, ll.BaseValue == 0, ref s.Rep0, ref s.Rep1, ref s.Rep2);
        matchLength = (nint)(ml.BaseValue + ReadBits(s.Container, ref s.Consumed, mlBits));
        if (llBits + mlBits + ofBits >= 57 - (SequenceCodes.LiteralLengthMaxLog + SequenceCodes.MatchLengthMaxLog + SequenceCodes.OffsetMaxLog))
        {
            Reload(ref s.Bits, ref s.Ptr, ref s.Consumed, ref s.Container);
        }

        litLength = (nint)(ll.BaseValue + ReadBits(s.Container, ref s.Consumed, llBits));
        if (nbSeq != 1)
        {
            s.LiteralLengthState = ll.NextState + (nint)ReadBits(s.Container, ref s.Consumed, ll.NbBits);
            s.MatchLengthState = ml.NextState + (nint)ReadBits(s.Container, ref s.Consumed, ml.NbBits);
            s.OffsetState = of.NextState + (nint)ReadBits(s.Container, ref s.Consumed, of.NbBits);
            Reload(ref s.Bits, ref s.Ptr, ref s.Consumed, ref s.Container);
        }
    }

    /// <summary>The literals after the last sequence: the end of every compressed block.</summary>
    private static int FinishBlock(
        ReadOnlySpan<byte> literals, int literal, int literalCount, Span<byte> destination, int blockStart, int position, int blockSizeMax)
    {
        int last = literalCount - literal;
        long blockEnd = (long)blockStart + blockSizeMax;
        long oend = Math.Min(blockEnd, destination.Length);
        if (last > oend - position)
        {
            Throw.Error((long)position + last > blockEnd ? ZstdError.BlockTooLarge : ZstdError.DestinationTooSmall);
        }

        literals.Slice(literal, last).CopyTo(destination.Slice(position));
        return position + last - blockStart;
    }

    /// <summary>
    /// The offset an offset code's value designates, and the repeat offsets after it (RFC 8878,
    /// 3.1.1.5), branched on as libzstd does. <paramref name="raw"/> is the code's base plus its
    /// extra bits: a new offset when the code has more than one extra bit, otherwise 0 to 2, which
    /// with no literal before the match shifts up by one to index Repeated_Offset1 to 3, the last
    /// meaning Repeated_Offset1 - 1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint ResolveOffset(nuint raw, int ofBits, bool noLiteral, ref nuint rep0, ref nuint rep1, ref nuint rep2)
    {
        if (ofBits > 1)
        {
            rep2 = rep1;
            rep1 = rep0;
            rep0 = raw;
            return raw;
        }

        nuint index = raw + (noLiteral ? 1u : 0u);
        if (index == 0)
        {
            return rep0;
        }

        nuint offset = index == 1 ? rep1 : index == 2 ? rep2 : rep0 - 1;
        offset = offset == 0 ? nuint.MaxValue : offset;
        if (index != 1)
        {
            rep2 = rep1;
        }

        rep1 = rep0;
        rep0 = offset;
        return offset;
    }

    /// <summary>
    /// Copies <paramref name="length"/> bytes forward, overlapping or not: 16 at a time while source
    /// and destination are 16 apart, then byte by byte. It writes nothing past its end.
    /// </summary>
    private static void CopyForward(ref byte dst, nint d, ref byte src, nint s, nint length)
    {
        nint end = d + length;
        if (Unsafe.AreSame(ref dst, ref src) && d - s < 16)
        {
            for (; d < end; d++, s++)
            {
                Unsafe.Add(ref dst, d) = Unsafe.Add(ref src, s);
            }

            return;
        }

        for (; d + 16 <= end; d += 16, s += 16)
        {
            Copy16(ref Unsafe.Add(ref dst, d), ref Unsafe.Add(ref src, s));
        }

        for (; d < end; d++, s++)
        {
            Unsafe.Add(ref dst, d) = Unsafe.Add(ref src, s);
        }
    }

    /// <summary>
    /// Copies at least <paramref name="length"/> bytes 16 at a time, writing up to 15 past the end:
    /// libzstd's <c>ZSTD_wildcopy</c>. Source and destination are at least 16 apart.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WideCopy(ref byte dst, ref byte src, nint length)
    {
        nint i = 0;
        do
        {
            Copy16(ref Unsafe.Add(ref dst, i), ref Unsafe.Add(ref src, i));
            i += 16;
        }
        while (i < length);
    }

    /// <summary>
    /// A match closer than 16 bytes, which repeats a pattern: libzstd's <c>ZSTD_overlapCopy8</c>
    /// spreads it to at least 8 apart, then 8 bytes a step, writing up to 7 past the end.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void OverlapCopy(ref byte dst, ref byte match, nuint offset, nint length)
    {
        if (offset < 8)
        {
            dst = match;
            Unsafe.Add(ref dst, 1) = Unsafe.Add(ref match, 1);
            Unsafe.Add(ref dst, 2) = Unsafe.Add(ref match, 2);
            Unsafe.Add(ref dst, 3) = Unsafe.Add(ref match, 3);
            match = ref Unsafe.Add(ref match, Spread32[(int)offset]);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 4), Unsafe.ReadUnaligned<uint>(ref match));
            match = ref Unsafe.Subtract(ref match, Spread64[(int)offset]);
        }
        else
        {
            Unsafe.WriteUnaligned(ref dst, Unsafe.ReadUnaligned<ulong>(ref match));
        }

        if (length > 8)
        {
            nint i = 8;
            do
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, i), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref match, i)));
                i += 8;
            }
            while (i < length);
        }
    }

    /// <summary>libzstd's <c>dec32table</c>: how far the source moves to spread a short offset.</summary>
    private static ReadOnlySpan<byte> Spread32 => [0, 1, 2, 1, 4, 4, 4, 4];

    /// <summary>libzstd's <c>dec64table</c>: then the source lies 8 or more behind.</summary>
    private static ReadOnlySpan<byte> Spread64 => [8, 8, 8, 7, 8, 9, 10, 11];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Copy16(ref byte dst, ref byte src) =>
        Unsafe.WriteUnaligned(ref dst, Unsafe.ReadUnaligned<Vector128<byte>>(ref src));

    /// <summary>libzstd's <c>BIT_readBitsFast</c>: 1 to 31 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadBitsFast(ulong container, ref int bc, int count)
    {
        // A shift takes its count modulo 64, so 64 - count is just -count: one negation.
        uint value = (uint)((container << bc) >> -count);
        bc += count;
        return value;
    }

    /// <summary>libzstd's <c>BIT_readBits</c>: 0 to 31 bits, as two shifts that give 0 for none.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadBits(ulong container, ref int bc, int count)
    {
        // 63 - count is count ^ 63 for a count up to 63: one instruction.
        uint value = (uint)(((container << bc) >> 1) >> (count ^ 63));
        bc += count;
        return value;
    }

    /// <summary>
    /// libzstd's <c>BIT_reloadDStream</c>. From eight bytes up, the position moves back by the whole
    /// bytes consumed, at most seven since a reload follows every 57 bits at most; below eight it
    /// moves by what is left, and an overflowed stream stays as it is.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Reload(ref byte bits, ref nint ptr, ref int bc, ref ulong container)
    {
        if (ptr >= 8)
        {
            Debug.Assert(bc <= 64);
            ptr -= bc >> 3;
            bc &= 7;
        }
        else if (bc <= 64)
        {
            nint bytes = Math.Min(bc >> 3, ptr);
            ptr -= bytes;
            bc -= (int)bytes << 3;
        }
        else
        {
            return;
        }

        container = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bits, ptr));
    }
}
