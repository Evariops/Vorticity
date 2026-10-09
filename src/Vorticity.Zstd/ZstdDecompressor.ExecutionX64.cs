using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Vorticity.Zstd.Internal;

namespace Vorticity.Zstd;

/// <summary>The fused sequence loop, as x64 runs it.</summary>
/// <remarks>
/// <para>
/// <see cref="ExecuteSequencesFast"/> keeps its whole state in registers, which Arm64's 31 hold:
/// x64 has 15, and the JIT, short of them, spills the states it has just computed and reloads them
/// at the top of the next iteration, putting a store and a load on the chain every sequence waits
/// on. This loop decodes and executes the same sequences, in the same order, with the same results
/// and the same handoff to the careful path, from fewer live values:
/// </para>
/// <list type="bullet">
/// <item>The bitstream is one moving reference, as in <see cref="DecodeSequencePair"/>, and its bound
/// is proven a batch of sequences at a time: a sequence reads at most 64 bits on the common paths,
/// so a batch of (position / 8) sequences never reloads below the start, and the sequences need no
/// test of their own. A sequence that reloads twice ends its batch.</item>
/// <item>What a sequence only reads or rarely writes stays in memory: the limits, the sequences left
/// outside the batch, the second and third repeat offsets. x64 compares with a memory operand at the
/// cost of a register compare.</item>
/// <item>Each test is a compare and a branch of its own, which x64 fuses into one operation: the
/// conditions the Arm64 loop combines with <c>&amp;</c> become, without conditional compares,
/// chains of <c>setcc</c>.</item>
/// </list>
/// </remarks>
public sealed partial class ZstdDecompressor
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ExecuteSequencesFastX64(ref SequenceState s, ref byte frameStart, ref byte fastLimit, ref byte litEnd)
    {
        ref byte stream = ref Unsafe.Add(ref s.Bits, s.Ptr);
        nint bc = s.Consumed;
        ulong container = s.Container;
        ref SeqSymbol tables = ref s.Tables;
        nint llState = s.LiteralLengthState;
        nint mlState = s.MatchLengthState;
        nint ofState = s.OffsetState;
        nuint rep0 = s.Rep0;
        ref byte dst = ref s.Dst;
        ref byte lit = ref s.Lit;

        // The second and third repeat offsets stay in s, and so do the sequences before the last one
        // (which the careful path takes) that are not in the batch: read and written there, in memory.
        s.NbSeq--;
        nint batch;
        ulong llEntry;
        ulong mlEntry;
        ulong ofEntry;
        nint litLength;
        nint matchLength;
        nuint offset;

    Batch:
        batch = Math.Min(s.NbSeq, Unsafe.ByteOffset(ref s.Bits, ref stream) >> 3);
        if (batch <= 0)
        {
            goto Edge;
        }

        s.NbSeq -= batch;

    Loop:
        llEntry = SeqEntry.Load(ref tables, llState);
        mlEntry = SeqEntry.Load(ref tables, mlState);
        ofEntry = SeqEntry.Load(ref tables, ofState);
        if (((llEntry | mlEntry) & 0x1F00) != 0)
        {
            goto Extra;
        }

        if ((ofEntry & 0x1E) == 0)
        {
            goto CommonRepeat;
        }

        // ---- the common sequence with a new offset: lengths without extra bits, the states first
        {
            nint bcBefore = bc;
            ulong containerBefore = container;
            nint consumed = CommonNextStates(llEntry, mlEntry, ofEntry, bc, container, ref llState, ref mlState, ref ofState);
            stream = ref Unsafe.Subtract(ref stream, (consumed >> 3) & 0x1F);
            bc = consumed & 7;
            container = Unsafe.ReadUnaligned<ulong>(ref stream);
            offset = SeqEntry.OffsetBase(ofEntry) + ReadBitsFast(containerBefore, bcBefore, (nint)ofEntry);
            s.Rep2 = s.Rep1;
            s.Rep1 = rep0;
            rep0 = offset;
            goto CommonCopy;
        }

    CommonRepeat:
        {
            nint bcBefore = bc;
            ulong containerBefore = container;
            nint consumed = CommonNextStates(llEntry, mlEntry, ofEntry, bc, container, ref llState, ref mlState, ref ofState);
            stream = ref Unsafe.Subtract(ref stream, (consumed >> 3) & 0x1F);
            bc = consumed & 7;
            container = Unsafe.ReadUnaligned<ulong>(ref stream);
            nuint raw = SeqEntry.OffsetBase(ofEntry) + ReadBits(containerBefore, bcBefore, SeqEntry.OffsetExtraBits(ofEntry));
            offset = ResolveOffsetX64(raw, SeqEntry.LengthBase(llEntry) == 0, ref rep0, ref s);
        }

    CommonCopy:
        {
            matchLength = (nint)SeqEntry.LengthBase(mlEntry);
            litLength = (nint)SeqEntry.LengthBase(llEntry);
            ref byte matchStart = ref Unsafe.Add(ref dst, litLength);

            // A literal length without extra bits is its code, 0 to 15: one 16-byte copy.
            if (Unsafe.IsAddressGreaterThan(ref Unsafe.Add(ref matchStart, matchLength), ref fastLimit))
            {
                goto Execute;
            }

            if (Unsafe.IsAddressGreaterThan(ref Unsafe.Add(ref lit, litLength), ref litEnd))
            {
                goto Execute;
            }

            if (offset > (nuint)Unsafe.ByteOffset(ref frameStart, ref matchStart))
            {
                goto Execute;
            }

            if ((offset < 16) | (matchLength > 16))
            {
                goto Execute;
            }

            Copy16(ref dst, ref lit);
            Copy16(ref matchStart, ref Unsafe.Subtract(ref matchStart, offset));
            dst = ref Unsafe.Add(ref matchStart, matchLength);
            lit = ref Unsafe.Add(ref lit, litLength);
            if (--batch != 0)
            {
                goto Loop;
            }

            goto Batch;
        }

    // ---- lengths with extra bits
    Extra:
        {
            nint ofBits = SeqEntry.OffsetExtraBits(ofEntry);
            nint mlBits = SeqEntry.LengthExtraBits(mlEntry);
            nint llBits = SeqEntry.LengthExtraBits(llEntry);
            if (ofBits + mlBits + llBits >= 57 - (SequenceCodes.LiteralLengthMaxLog + SequenceCodes.MatchLengthMaxLog + SequenceCodes.OffsetMaxLog))
            {
                // A reload inside the sequence, then another after it: up to 16 bytes back, which
                // the batch did not count. It ends with this sequence.
                s.NbSeq += batch - 1;
                batch = 1;
                goto General;
            }

            // Every position from the entries, the states first, as the common path; one reload.
            nint bcBefore = bc;
            ulong containerBefore = container;
            nint consumed = ExtraNextStates(llEntry, mlEntry, ofEntry, bc, container, ref llState, ref mlState, ref ofState, out nint mlAt);
            stream = ref Unsafe.Subtract(ref stream, (consumed >> 3) & 0x1F);
            bc = consumed & 7;
            container = Unsafe.ReadUnaligned<ulong>(ref stream);
            nuint raw = SeqEntry.OffsetBase(ofEntry) + ReadBits(containerBefore, bcBefore, (nint)ofEntry);
            matchLength = (nint)SeqEntry.LengthBase(mlEntry) + (nint)ReadBits(containerBefore, mlAt, (nint)(mlEntry >> 8));
            litLength = (nint)SeqEntry.LengthBase(llEntry) + (nint)ReadBits(containerBefore, mlAt + (nint)(mlEntry >> 8), (nint)(llEntry >> 8));
            if ((ofEntry & 0x1E) != 0)
            {
                offset = raw;
                s.Rep2 = s.Rep1;
                s.Rep1 = rep0;
                rep0 = raw;
                goto Execute;
            }

            offset = ResolveOffsetX64(raw, SeqEntry.LengthBase(llEntry) == 0, ref rep0, ref s);
            goto Execute;
        }

    // ---- the stream within eight bytes of its start: one sequence at a time, with clamped reloads
    Edge:
        if (s.NbSeq <= 0)
        {
            batch = 0;
            goto Stop;
        }

        s.NbSeq--;
        batch = 1;
        llEntry = SeqEntry.Load(ref tables, llState);
        mlEntry = SeqEntry.Load(ref tables, mlState);
        ofEntry = SeqEntry.Load(ref tables, ofState);

    // ---- libzstd's ZSTD_decodeSequence in full
    General:
        {
            nint ofBits = SeqEntry.OffsetExtraBits(ofEntry);
            nint mlBits = SeqEntry.LengthExtraBits(mlEntry);
            nint llBits = SeqEntry.LengthExtraBits(llEntry);
            nint ptr = Unsafe.ByteOffset(ref s.Bits, ref stream);
            nuint raw = SeqEntry.OffsetBase(ofEntry) + ReadBits(container, bc, ofBits);
            bc += ofBits;
            if (ofBits > 1)
            {
                offset = raw;
                s.Rep2 = s.Rep1;
                s.Rep1 = rep0;
                rep0 = raw;
            }
            else
            {
                offset = ResolveOffsetX64(raw, SeqEntry.LengthBase(llEntry) == 0, ref rep0, ref s);
            }

            matchLength = (nint)SeqEntry.LengthBase(mlEntry) + (nint)ReadBits(container, bc, mlBits);
            bc += mlBits;
            if (ofBits + mlBits + llBits >= 57 - (SequenceCodes.LiteralLengthMaxLog + SequenceCodes.MatchLengthMaxLog + SequenceCodes.OffsetMaxLog))
            {
                ReloadClamped(ref s.Bits, ref ptr, ref bc, ref container);
            }

            litLength = (nint)SeqEntry.LengthBase(llEntry) + (nint)ReadBits(container, bc, llBits);
            bc += llBits;
            NextStates(llEntry, mlEntry, ofEntry, container, ref bc, ref llState, ref mlState, ref ofState);
            ReloadClamped(ref s.Bits, ref ptr, ref bc, ref container);
            stream = ref Unsafe.Add(ref s.Bits, ptr);
        }

    // ---- any sequence, decoded: libzstd's ZSTD_execSequence
    Execute:
        {
            ref byte litAfter = ref Unsafe.Add(ref lit, litLength);
            ref byte matchStart = ref Unsafe.Add(ref dst, litLength);
            ref byte matchEnd = ref Unsafe.Add(ref matchStart, matchLength);
            if (Unsafe.IsAddressGreaterThan(ref matchEnd, ref fastLimit))
            {
                goto Pending;
            }

            if (Unsafe.IsAddressGreaterThan(ref litAfter, ref litEnd))
            {
                goto Pending;
            }

            nuint prefix = (nuint)Unsafe.ByteOffset(ref frameStart, ref matchStart);
            if (offset > prefix)
            {
                // A match that starts in the dictionary and ends there: see ExecuteSequencesFast.
                nuint back = offset - prefix;
                if (back > (nuint)s.HistoryLength)
                {
                    goto Pending;
                }

                if ((nint)back < matchLength)
                {
                    goto Pending;
                }

                WildCopyWide(ref dst, ref lit, litLength);
                WildCopyWide(ref matchStart, ref Unsafe.Subtract(ref s.HistoryEnd, back), matchLength);
            }
            else
            {
                // The literals from their own buffer, then the match 32 bytes at a time when it is 32
                // or more back, else 16 at a time, else as a pattern: a copy of up to 32 bytes is one
                // store, its loop not taken.
                ref byte match = ref Unsafe.Subtract(ref matchStart, offset);
                WildCopyWide(ref dst, ref lit, litLength);
                if (offset >= 32)
                {
                    WildCopyWide(ref matchStart, ref match, matchLength);
                }
                else if (offset >= 16)
                {
                    WildCopy16(ref matchStart, ref match, matchLength);
                }
                else
                {
                    OverlapCopy(ref matchStart, ref match, offset, matchLength);
                }
            }

            dst = ref matchEnd;
            lit = ref litAfter;
            if (--batch != 0)
            {
                goto Loop;
            }

            goto Batch;
        }

    Pending:
        s.Pending = true;
        s.LitLength = litLength;
        s.MatchLength = matchLength;
        s.Offset = offset;

    Stop:
        s.Ptr = Unsafe.ByteOffset(ref s.Bits, ref stream);
        s.Consumed = bc;
        s.Container = container;
        s.LiteralLengthState = llState;
        s.MatchLengthState = mlState;
        s.OffsetState = ofState;
        s.Rep0 = rep0;
        s.Dst = ref dst;
        s.Lit = ref lit;
        s.NbSeq += batch + 1;
    }

    /// <summary>
    /// Copies <paramref name="length"/> bytes 32 at a time, one block at least, between places 32 or
    /// more apart: writing, and reading, up to 31 past the end, as <see cref="WildCopy32"/>, which it
    /// is where 256-bit vectors are not accelerated. With AVX2, one load and one store a block.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WildCopyWide(ref byte dst, ref byte src, nint length)
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            WildCopy32(ref dst, ref src, length);
            return;
        }

        nint i = 0;
        do
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, i), Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref src, i)));
            i += 32;
        }
        while (i < length);
    }

    /// <summary>
    /// <see cref="ResolveOffset"/> with the second and third repeat offsets in <paramref name="s"/>,
    /// where the x64 loop keeps them.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint ResolveOffsetX64(nuint raw, bool noLiteral, ref nuint rep0, ref SequenceState s)
    {
        nuint index = raw + (noLiteral ? 1u : 0u);
        if (index == 0)
        {
            return rep0;
        }

        nuint rep1 = s.Rep1;
        nuint offset = index == 1 ? rep1 : index == 2 ? s.Rep2 : rep0 - 1;
        offset = offset == 0 ? nuint.MaxValue : offset;
        if (index != 1)
        {
            s.Rep2 = rep1;
        }

        s.Rep1 = rep0;
        rep0 = offset;
        return offset;
    }
}
