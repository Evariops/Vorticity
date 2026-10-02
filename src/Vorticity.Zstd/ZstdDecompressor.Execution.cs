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
    /// The decoder's whole state between two sequences: what the fast loop starts from, and hands to
    /// the careful path when it stops.
    /// </summary>
    private ref struct SequenceState
    {
        public ref byte Bits;
        public nint Ptr;
        public nint Consumed;
        public ulong Container;

        /// <summary>The three tables, one array: the states index it whole.</summary>
        public ref SeqSymbol Tables;
        public nint LiteralLengthState;
        public nint MatchLengthState;
        public nint OffsetState;
        public nuint Rep0;
        public nuint Rep1;
        public nuint Rep2;

        /// <summary>Where the next sequence writes, and its literals start.</summary>
        public ref byte Dst;
        public ref byte Lit;

        /// <summary>The sequences left, a pending one included.</summary>
        public nint NbSeq;

        /// <summary>A sequence the fast loop decoded but did not execute.</summary>
        public bool Pending;
        public nint LitLength;
        public nint MatchLength;
        public nuint Offset;
    }

    /// <summary>
    /// The lowest bitstream position the common path of the fast loop takes a sequence from: its
    /// reload, which steps back by up to 8 bytes, then needs no clamp.
    /// </summary>
    private const int FastLoopMinPtr = 8;

    /// <summary>
    /// libzstd's <c>ZSTD_decompressSequences_body</c>: each sequence decoded and executed at once,
    /// with copies 16 bytes wide that may run past their end into room proven beforehand, then the
    /// literals after the last one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ExecuteSequencesFast"/> runs the bulk of the sequences; the last one, the end of the
    /// bitstream and any sequence it cannot take go to <see cref="ExecuteSequencesCareful"/>. Why no
    /// access of the fast loop leaves its buffer, established here and kept by every iteration:
    /// </para>
    /// <list type="bullet">
    /// <item>A sequence runs there only if it ends <see cref="WildCopyOverlength"/> bytes before the
    /// destination does, and its literals before the last literal: a 16-byte copy that overruns stays
    /// inside the destination, and inside <paramref name="literals"/>, which has
    /// <see cref="LiteralsMargin"/> readable bytes after the last literal.</item>
    /// <item>Its match lies wholly within this frame's output, before the write position.</item>
    /// <item>The bitstream is read eight bytes at a time at a position that never goes below its
    /// start: the common path reloads only from <see cref="FastLoopMinPtr"/> bytes, the general path
    /// clamps. A stream under eight bytes is first copied into eight.</item>
    /// <item>States index their tables in range: a table built from a validated distribution maps
    /// every state and its low bits back into the table.</item>
    /// </list>
    /// </remarks>
    private int ExecuteSequences(
        ReadOnlySpan<byte> bitstream, int nbSeq, ReadOnlySpan<byte> literals, int literalCount,
        Span<byte> destination, int op, int blockSizeMax, ReadOnlySpan<byte> history)
    {
        Debug.Assert(literals.Length - literalCount >= LiteralsMargin);
        if (nbSeq == 0)
        {
            return FinishBlock(literals, 0, literalCount, destination, op, op, blockSizeMax);
        }

        _sequenceEntropy = true;
        if (!TryBeginSequences(bitstream, nbSeq, _sequenceTables, _shortBitstream, out SequenceState state))
        {
            Throw.Error(ZstdError.SequenceBitstream);
        }

        state.Rep0 = _rep0;
        state.Rep1 = _rep1;
        state.Rep2 = _rep2;
        state.Dst = ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), op);
        state.Lit = ref MemoryMarshal.GetReference(literals);
        return RunSequences(ref state, literals, literalCount, destination, op, blockSizeMax, history);
    }

    /// <summary>
    /// libzstd's <c>BIT_initDStream</c>, then the initial states, each read followed by a reload: the
    /// state of a block's sequences before the first, but for the repeat offsets and the positions
    /// in the output and the literals. False when the bitstream does not end with its marker bit. A
    /// bitstream shorter than eight bytes is copied into <paramref name="shortBitstream"/>, eight bytes
    /// the state then reads.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryBeginSequences(
        ReadOnlySpan<byte> bitstream, int nbSeq, SequenceTableSet tables, byte[] shortBitstream, out SequenceState state)
    {
        state = default;
        if (bitstream.IsEmpty || bitstream[^1] == 0)
        {
            return false;
        }

        nint bc = 8 - BackwardBitReader.HighBit(bitstream[^1]);
        nint ptr;
        if (bitstream.Length >= 8)
        {
            ptr = bitstream.Length - 8;
        }
        else
        {
            Array.Clear(shortBitstream);
            bitstream.CopyTo(shortBitstream);
            bc += (8 - bitstream.Length) * 8;
            bitstream = shortBitstream;
            ptr = 0;
        }

        ref byte bits = ref MemoryMarshal.GetReference(bitstream);
        ulong container = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bits, ptr));

        // Indices into the whole table set.
        int llLog = tables.TableLog(SequenceCode.LiteralLength);
        int ofLog = tables.TableLog(SequenceCode.Offset);
        int mlLog = tables.TableLog(SequenceCode.MatchLength);
        nint llState = SequenceTableSet.LiteralLengthSlot + ((nint)ReadBits(container, bc, llLog) << 1);
        bc += llLog;
        Reload(ref bits, ref ptr, ref bc, ref container);
        nint ofState = SequenceTableSet.OffsetSlot + ((nint)ReadBits(container, bc, ofLog) << 1);
        bc += ofLog;
        Reload(ref bits, ref ptr, ref bc, ref container);
        nint mlState = SequenceTableSet.MatchLengthSlot + ((nint)ReadBits(container, bc, mlLog) << 1);
        bc += mlLog;
        Reload(ref bits, ref ptr, ref bc, ref container);

        state.Bits = ref bits;
        state.Ptr = ptr;
        state.Consumed = bc;
        state.Container = container;
        state.Tables = ref MemoryMarshal.GetArrayDataReference(tables.Entries);
        state.LiteralLengthState = llState;
        state.MatchLengthState = mlState;
        state.OffsetState = ofState;
        state.NbSeq = nbSeq;
        return true;
    }

    /// <summary>
    /// The sequences of a block left in <paramref name="state"/>, from where it is in the output and
    /// the literals, then the literals after the last: the fast loop while it can, then the careful
    /// path, which ends the block, whose output starts at <paramref name="blockStart"/>.
    /// </summary>
    /// <returns>The size of the block's content.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RunSequences(
        ref SequenceState state, ReadOnlySpan<byte> literals, int literalCount,
        Span<byte> destination, int blockStart, int blockSizeMax, ReadOnlySpan<byte> history)
    {
        if (state.NbSeq > 1 && !state.Pending)
        {
            ref byte frameStart = ref MemoryMarshal.GetReference(destination);
            nint fastRoom = Math.Min((nint)blockStart + blockSizeMax, destination.Length - WildCopyOverlength);
            ExecuteSequencesFast(
                ref state, ref frameStart, ref Unsafe.Add(ref frameStart, Math.Max(fastRoom, Unsafe.ByteOffset(ref frameStart, ref state.Dst))),
                ref Unsafe.Add(ref MemoryMarshal.GetReference(literals), literalCount));
        }

        return ExecuteSequencesCareful(ref state, literals, literalCount, destination, blockStart, blockSizeMax, history);
    }

    /// <summary>
    /// The fast loop: decodes and executes every sequence but the last, while each fits the room
    /// proven for copies that run past their end; otherwise it stops, with the sequence it could not
    /// execute pending.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It calls nothing, so that its state stays in registers, and its common paths are straight: an
    /// offset without extra length bits, eight bytes or more of bitstream before the reload, literals
    /// and a match of up to 16 bytes, 16 or more apart; a new offset on one path, a repeat code on
    /// another. Any other sequence takes the general path, still in this loop.
    /// </para>
    /// <para>
    /// The layout is written for the JIT, which without profile data places the target of
    /// <c>if (c) goto L;</c> right after the branch, and a block that rejoins further down between the
    /// branch and the join. So each test of the common path is one condition, combined with
    /// <c>&amp;</c> rather than <c>&amp;&amp;</c> (a chain would be weighed as unlikely), jumping to its
    /// common continuation; and the general path runs to the end of its iteration on its own, back to
    /// the loop head. The rare blocks then land after the loop, and the common path takes no branch
    /// but the loop's.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ExecuteSequencesFast(ref SequenceState s, ref byte frameStart, ref byte fastLimit, ref byte litEnd)
    {
        ref byte bits = ref s.Bits;
        nint ptr = s.Ptr;
        nint bc = s.Consumed;
        ulong container = s.Container;
        ref SeqSymbol tables = ref s.Tables;
        nint llState = s.LiteralLengthState;
        nint mlState = s.MatchLengthState;
        nint ofState = s.OffsetState;
        nuint rep0 = s.Rep0;
        nuint rep1 = s.Rep1;
        nuint rep2 = s.Rep2;
        ref byte dst = ref s.Dst;
        ref byte lit = ref s.Lit;
        // The sequences left before the last one, which the careful path takes: counted down to 0,
        // one subtraction and one branch a sequence.
        nint beforeLast = s.NbSeq - 1;
        nint litLength;
        nint matchLength;
        nuint offset;

    Loop:
        // Each entry whole, in one load the table index folds into; its fields by shifts.
        ulong llEntry = SeqEntry.Load(ref tables, llState);
        ulong mlEntry = SeqEntry.Load(ref tables, mlState);
        ulong ofEntry = SeqEntry.Load(ref tables, ofState);
        // The common sequence: lengths without extra bits (the second byte of both their entries),
        // and the reload after it eight bytes or more from the start. Any offset: with no extra
        // length bits, a sequence reads at most 7 + 31 + 9 + 9 + 8 = 64 bits, which the container
        // holds whole, so the reload libzstd makes inside a sequence of 31 bits or more changes
        // nothing here.
        if ((((llEntry | mlEntry) & 0x1F00) == 0) & (ptr >= FastLoopMinPtr))
        {
            goto CommonDecode;
        }

        // ---- the general path: libzstd's ZSTD_decodeSequence in full
        {
            nint ofBits = SeqEntry.OffsetExtraBits(ofEntry);
            nint mlBits = SeqEntry.LengthExtraBits(mlEntry);
            nint llBits = SeqEntry.LengthExtraBits(llEntry);
            nuint raw = SeqEntry.OffsetBase(ofEntry) + ReadBits(container, bc, ofBits);
            bc += ofBits;
            if (ofBits > 1)
            {
                offset = raw;
                rep2 = rep1;
                rep1 = rep0;
                rep0 = raw;
            }
            else
            {
                offset = ResolveOffset(raw, SeqEntry.LengthBase(llEntry) == 0, ref rep0, ref rep1, ref rep2);
            }

            matchLength = (nint)SeqEntry.LengthBase(mlEntry) + (nint)ReadBits(container, bc, mlBits);
            bc += mlBits;
            if (ofBits + mlBits + llBits >= 57 - (SequenceCodes.LiteralLengthMaxLog + SequenceCodes.MatchLengthMaxLog + SequenceCodes.OffsetMaxLog))
            {
                ReloadClamped(ref bits, ref ptr, ref bc, ref container);
            }

            litLength = (nint)SeqEntry.LengthBase(llEntry) + (nint)ReadBits(container, bc, llBits);
            bc += llBits;
            NextStates(llEntry, mlEntry, ofEntry, container, ref bc, ref llState, ref mlState, ref ofState);
            ReloadClamped(ref bits, ref ptr, ref bc, ref container);
            goto Execute;
        }

    CommonDecode:
        // A new offset reads more than one extra bit: bits 1 to 4 of the entry's low byte.
        if ((ofEntry & 0x1E) != 0)
        {
            goto CommonNewOffset;
        }

        // ---- a repeat offset, branched on as libzstd does; the rest as for a new one, written twice
        // so that each path runs straight to its own end, the states first
        {
            nint bcBefore = bc;
            ulong containerBefore = container;
            CommonStates(llEntry, mlEntry, ofEntry, ref bits, ref ptr, ref bc, ref container, ref llState, ref mlState, ref ofState);
            nuint raw = SeqEntry.OffsetBase(ofEntry) + ReadBits(containerBefore, bcBefore, SeqEntry.OffsetExtraBits(ofEntry));
            offset = ResolveOffset(raw, SeqEntry.LengthBase(llEntry) == 0, ref rep0, ref rep1, ref rep2);
            matchLength = (nint)SeqEntry.LengthBase(mlEntry);
            litLength = (nint)SeqEntry.LengthBase(llEntry);

            ref byte litAfter = ref Unsafe.Add(ref lit, litLength);
            ref byte matchStart = ref Unsafe.Add(ref dst, litLength);
            ref byte matchEnd = ref Unsafe.Add(ref matchStart, matchLength);
            if (FitsShortCopies(ref frameStart, ref fastLimit, ref litEnd, ref litAfter, ref matchStart, ref matchEnd, litLength, matchLength, offset))
            {
                Copy16(ref dst, ref lit);
                Copy16(ref matchStart, ref Unsafe.Subtract(ref matchStart, offset));
                dst = ref matchEnd;
                lit = ref litAfter;
                if (--beforeLast != 0)
                {
                    goto Loop;
                }

                goto Stop;
            }

            goto Execute;
        }

    CommonNewOffset:
        {
            // The states first, the offset after them from the container they replace: see CommonStates.
            nint bcBefore = bc;
            ulong containerBefore = container;
            CommonStates(llEntry, mlEntry, ofEntry, ref bits, ref ptr, ref bc, ref container, ref llState, ref mlState, ref ofState);
            offset = SeqEntry.OffsetBase(ofEntry) + ReadBitsFast(containerBefore, bcBefore, (nint)ofEntry);
            rep2 = rep1;
            rep1 = rep0;
            rep0 = offset;
            matchLength = (nint)SeqEntry.LengthBase(mlEntry);
            litLength = (nint)SeqEntry.LengthBase(llEntry);

            // One test for the room and the shape of the copies: then one 16-byte copy each.
            ref byte litAfter = ref Unsafe.Add(ref lit, litLength);
            ref byte matchStart = ref Unsafe.Add(ref dst, litLength);
            ref byte matchEnd = ref Unsafe.Add(ref matchStart, matchLength);
            if (FitsShortCopies(ref frameStart, ref fastLimit, ref litEnd, ref litAfter, ref matchStart, ref matchEnd, litLength, matchLength, offset))
            {
                Copy16(ref dst, ref lit);
                Copy16(ref matchStart, ref Unsafe.Subtract(ref matchStart, offset));
                dst = ref matchEnd;
                lit = ref litAfter;
                if (--beforeLast != 0)
                {
                    goto Loop;
                }

                goto Stop;
            }

            goto Execute;
        }

    // ---- any sequence, decoded: libzstd's ZSTD_execSequence
    Execute:
        {
            ref byte litAfter = ref Unsafe.Add(ref lit, litLength);
            ref byte matchStart = ref Unsafe.Add(ref dst, litLength);
            ref byte matchEnd = ref Unsafe.Add(ref matchStart, matchLength);
            if (Unsafe.IsAddressGreaterThan(ref matchEnd, ref fastLimit)
                | Unsafe.IsAddressGreaterThan(ref litAfter, ref litEnd)
                | (offset > (nuint)Unsafe.ByteOffset(ref frameStart, ref matchStart)))
            {
                goto Pending;
            }

            ref byte match = ref Unsafe.Subtract(ref matchStart, offset);
            // Unsigned compares, unlike the short path's: shared with it, the JIT would compute them
            // once, as booleans, and the short path's compares would no longer chain.
            if (((nuint)litLength < 17) & ((nuint)matchLength < 33) & (offset > 15))
            {
                // A match of up to 32 bytes: two copies straight, no loop whose exit mispredicts.
                Copy16(ref dst, ref lit);
                Copy16(ref matchStart, ref match);
                Copy16(ref Unsafe.Add(ref matchStart, 16), ref Unsafe.Add(ref match, 16));
            }
            else
            {
                WildCopy16(ref dst, ref lit, litLength);
                if (offset >= 16)
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
            if (--beforeLast != 0)
            {
                goto Loop;
            }

            goto Stop;
        }

    Pending:
        s.Pending = true;
        s.LitLength = litLength;
        s.MatchLength = matchLength;
        s.Offset = offset;

    Stop:
        s.Ptr = ptr;
        s.Consumed = bc;
        s.Container = container;
        s.LiteralLengthState = llState;
        s.MatchLengthState = mlState;
        s.OffsetState = ofState;
        s.Rep0 = rep0;
        s.Rep1 = rep1;
        s.Rep2 = rep2;
        s.Dst = ref dst;
        s.Lit = ref lit;
        s.NbSeq = beforeLast + 1;
    }

    /// <summary>
    /// The three states after a common sequence, whose only extra bits are the offset's, and the
    /// reload after it, eight bytes or more from the start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These sums are the loop's critical path: each state is read at a position the counts of the
    /// three entries just loaded add up to. So the counts are added as the entries come, without
    /// first extracting them, which costs two cycles after a load: the low byte of an entry is the
    /// count each position needs (the offset's extra bits, then each length's state bits, see
    /// <see cref="SeqSymbol"/>), and the rest of the entry only adds multiples of 256 to the sum.
    /// A shift takes its count modulo 64, and every position here is below 64, so the shifts read
    /// the right bits; the reload takes the low byte of the total, at most 64. The shift counts of
    /// the length reads come from the raw entries too: 63 - n is n ^ 63 in the low six bits.
    /// </para>
    /// <para>
    /// The positions are added as a tree, and a state reads its bits and the next one (see
    /// <see cref="ReadStateBits"/>).
    /// </para>
    /// <para>
    /// The JIT emits the statements in their order, and the core issues the oldest of the ready
    /// instructions first: whatever comes before the states in the loop and is ready with the entries
    /// (the offset's bits, the lengths, the copies' addresses) takes the cycles the states need. So
    /// the loop calls this before anything else is done with the entries, and this computes the
    /// offset's state first, the longest chain (its bits come after both lengths'), then the match
    /// length's, then the literal length's: 7% fewer cycles a sequence on the common path alone.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CommonStates(
        ulong llEntry, ulong mlEntry, ulong ofEntry, ref byte bits, ref nint ptr, ref nint bc, ref ulong container,
        ref nint llState, ref nint mlState, ref nint ofState)
    {
        nint consumed = CommonNextStates(llEntry, mlEntry, ofEntry, bc, container, ref llState, ref mlState, ref ofState);
        Debug.Assert(ptr >= 8);
        ptr -= (consumed >> 3) & 0x1F;
        bc = consumed & 7;
        container = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bits, ptr));
    }

    /// <summary>
    /// The three states after a common sequence (see <see cref="CommonStates"/>), and the position in
    /// the container after it, in the low byte: at most 64, the reload the caller's.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint CommonNextStates(
        ulong llEntry, ulong mlEntry, ulong ofEntry, nint bc, ulong container, ref nint llState, ref nint mlState, ref nint ofState)
    {
        nint llAt = bc + (nint)ofEntry;
        nint llMlNb = (nint)llEntry + (nint)mlEntry;
        nint mlAt = llAt + (nint)llEntry;
        nint ofAt = llAt + llMlNb;
        nint ofNb = SeqEntry.OffsetNbBits(ofEntry);
        ofState = SeqEntry.NextState(ofEntry) + (nint)ReadStateBits(container, ofAt, ofNb);
        mlState = SeqEntry.NextState(mlEntry) + (nint)ReadStateBits(container, mlAt, (nint)mlEntry);
        llState = SeqEntry.NextState(llEntry) + (nint)ReadStateBits(container, llAt, (nint)llEntry);
        nint consumed = ofAt + ofNb;
        Debug.Assert((consumed & 0xFF) <= 64);
        return consumed;
    }

    /// <summary>
    /// Whether a common sequence fits the room proven for copies past their end, and takes one
    /// 16-byte copy of literals and one of match, 16 or more apart: one condition, the comparisons
    /// combined with <c>&amp;</c>, which the JIT turns into one chain of conditional compares and one
    /// branch.
    /// </summary>
    /// <remarks>
    /// The literals need no test of their own: a literal length without extra bits, as in a common
    /// sequence, is a code from 0 to 15 (code 16 is the first with an extra bit), which is its value.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool FitsShortCopies(
        ref byte frameStart, ref byte fastLimit, ref byte litEnd, ref byte litAfter, ref byte matchStart, ref byte matchEnd,
        nint litLength, nint matchLength, nuint offset)
    {
        Debug.Assert(litLength <= 15);
        return !Unsafe.IsAddressGreaterThan(ref matchEnd, ref fastLimit)
            & !Unsafe.IsAddressGreaterThan(ref litAfter, ref litEnd)
            & (offset <= (nuint)Unsafe.ByteOffset(ref frameStart, ref matchStart))
            & (matchLength <= 16)
            & (offset >= 16);
    }

    /// <summary>The three states after a sequence: libzstd's ZSTD_updateFseStateWithDInfo, in order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void NextStates(
        ulong llEntry, ulong mlEntry, ulong ofEntry, ulong container, ref nint bc,
        ref nint llState, ref nint mlState, ref nint ofState)
    {
        nint llNb = SeqEntry.LengthNbBits(llEntry);
        nint mlNb = SeqEntry.LengthNbBits(mlEntry);
        nint ofNb = SeqEntry.OffsetNbBits(ofEntry);
        nint mlAt = bc + llNb;
        nint ofAt = mlAt + mlNb;
        llState = SeqEntry.NextState(llEntry) + (nint)ReadStateBits(container, bc, llNb);
        mlState = SeqEntry.NextState(mlEntry) + (nint)ReadStateBits(container, mlAt, mlNb);
        ofState = SeqEntry.NextState(ofEntry) + (nint)ReadStateBits(container, ofAt, ofNb);
        bc = ofAt + ofNb;
    }

    /// <summary>
    /// libzstd's <c>BIT_reloadDStream</c> without its status: back by the whole bytes consumed, but
    /// not below the start of the stream. An overflowed stream is then at the start already, and stays.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReloadClamped(ref byte bits, ref nint ptr, ref nint bc, ref ulong container)
    {
        nint bytes = Math.Min(bc >> 3, ptr);
        ptr -= bytes;
        bc -= bytes << 3;
        container = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bits, ptr));
    }

    /// <summary>
    /// Copies <paramref name="length"/> bytes 16 at a time, one block at least: libzstd's
    /// <c>ZSTD_wildcopy</c>, writing up to 15 past the end. Source and destination are 16 or more
    /// apart. A loop that always runs once, so that a short copy takes no branch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WildCopy16(ref byte dst, ref byte src, nint length)
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
    /// Executes the sequence the fast loop left pending, if any, then decodes and executes the
    /// remaining ones, checking each copy in libzstd's order (<c>ZSTD_execSequenceEnd</c>), and ends
    /// the block.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ExecuteSequencesCareful(
        ref SequenceState state, ReadOnlySpan<byte> literals, int literalCount,
        Span<byte> destination, int blockStart, int blockSizeMax, ReadOnlySpan<byte> history)
    {
        ref byte frameStart = ref MemoryMarshal.GetReference(destination);
        ref byte lit = ref MemoryMarshal.GetReference(literals);
        nint o = Unsafe.ByteOffset(ref frameStart, ref state.Dst);
        nint litPtr = Unsafe.ByteOffset(ref lit, ref state.Lit);
        nint blockEnd = (nint)blockStart + blockSizeMax;
        nint oend = Math.Min(blockEnd, destination.Length);
        nint nbSeq = state.NbSeq;
        nint litLength;
        nint matchLength;
        nuint offset;

        if (state.Pending)
        {
            litLength = state.LitLength;
            matchLength = state.MatchLength;
            offset = state.Offset;
        }
        else
        {
            Debug.Assert(nbSeq > 0);
            DecodeSequence(ref state, nbSeq, out litLength, out matchLength, out offset);
        }

        while (true)
        {
            ExecuteCarefully(ref frameStart, ref o, ref lit, ref litPtr, litLength, matchLength, offset, oend, blockEnd, literalCount, history);
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

    /// <summary>
    /// One sequence, each copy checked in libzstd's order (<c>ZSTD_execSequenceEnd</c>): from
    /// <paramref name="o"/> in the output and <paramref name="litPtr"/> in the literals, both moved past it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ExecuteCarefully(
        ref byte frameStart, ref nint o, ref byte lit, ref nint litPtr, nint litLength, nint matchLength, nuint offset,
        nint oend, nint blockEnd, int literalCount, ReadOnlySpan<byte> history)
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
    }

    /// <summary>The fast loop's decoding, on the state the careful path carries.</summary>
    private static void DecodeSequence(ref SequenceState s, nint nbSeq, out nint litLength, out nint matchLength, out nuint offset)
    {
        ulong llEntry = SeqEntry.Load(ref s.Tables, s.LiteralLengthState);
        ulong mlEntry = SeqEntry.Load(ref s.Tables, s.MatchLengthState);
        ulong ofEntry = SeqEntry.Load(ref s.Tables, s.OffsetState);
        nint llBits = SeqEntry.LengthExtraBits(llEntry);
        nint mlBits = SeqEntry.LengthExtraBits(mlEntry);
        nint ofBits = SeqEntry.OffsetExtraBits(ofEntry);
        nuint raw = SeqEntry.OffsetBase(ofEntry) + ReadBits(s.Container, s.Consumed, ofBits);
        s.Consumed += ofBits;
        if (ofBits > 1)
        {
            offset = raw;
            s.Rep2 = s.Rep1;
            s.Rep1 = s.Rep0;
            s.Rep0 = raw;
        }
        else
        {
            offset = ResolveOffset(raw, SeqEntry.LengthBase(llEntry) == 0, ref s.Rep0, ref s.Rep1, ref s.Rep2);
        }

        matchLength = (nint)(SeqEntry.LengthBase(mlEntry) + ReadBits(s.Container, s.Consumed, mlBits));
        s.Consumed += mlBits;
        if (llBits + mlBits + ofBits >= 57 - (SequenceCodes.LiteralLengthMaxLog + SequenceCodes.MatchLengthMaxLog + SequenceCodes.OffsetMaxLog))
        {
            Reload(ref s.Bits, ref s.Ptr, ref s.Consumed, ref s.Container);
        }

        litLength = (nint)(SeqEntry.LengthBase(llEntry) + ReadBits(s.Container, s.Consumed, llBits));
        s.Consumed += llBits;
        if (nbSeq != 1)
        {
            NextStates(llEntry, mlEntry, ofEntry, s.Container, ref s.Consumed, ref s.LiteralLengthState, ref s.MatchLengthState, ref s.OffsetState);
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
    /// The offset a repeat code designates, and the repeat offsets after it (RFC 8878, 3.1.1.5),
    /// branched on as libzstd does. <paramref name="raw"/> is the code's base plus its extra bit, 0 to
    /// 2, which with no literal before the match shifts up by one to index Repeated_Offset1 to 3, the
    /// last meaning Repeated_Offset1 - 1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint ResolveOffset(nuint raw, bool noLiteral, ref nuint rep0, ref nuint rep1, ref nuint rep2)
    {
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
    /// Copies <paramref name="length"/> bytes forward, exactly: nothing is written past the end. From
    /// another buffer, or from far enough back, it is one block copy; a match that overlaps its own
    /// output repeats a pattern, which is copied in blocks that double, each from the start of the copy.
    /// </summary>
    private static void CopyForward(ref byte dst, nint d, ref byte src, nint s, nint length)
    {
        nint distance = d - s;
        if (!Unsafe.AreSame(ref dst, ref src) || distance >= length)
        {
            Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref dst, d), ref Unsafe.Add(ref src, s), (uint)length);
            return;
        }

        // The first period, then blocks of what is already written: a whole number of periods each,
        // so that every block continues the pattern in phase.
        Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref dst, d), ref Unsafe.Add(ref dst, s), (uint)distance);
        nint done = distance;
        while (done < length)
        {
            nint block = Math.Min(done, length - done);
            Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref dst, d + done), ref Unsafe.Add(ref dst, d), (uint)block);
            done += block;
        }
    }

    /// <summary>
    /// A match closer than 16 bytes, which repeats a pattern of <paramref name="offset"/> bytes: its
    /// first 16 bytes are one byte shuffle of the bytes before the match, then stored as many times as
    /// it takes, each store moving on by the largest multiple of the period that fits in 16 bytes, so
    /// that the pattern stays in phase. No load waits on a store, where libzstd's
    /// <c>ZSTD_overlapCopy8</c> reloads each 8 bytes it has just written; up to 15 bytes are written
    /// past the end.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void OverlapCopy(ref byte dst, ref byte match, nuint offset, nint length)
    {
        Debug.Assert(offset is > 0 and < 16);
        Vector128<byte> source = Unsafe.ReadUnaligned<Vector128<byte>>(ref match);
        Vector128<byte> indices = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(PatternIndices), (nint)offset * 16));
        Vector128<byte> pattern = Vector128.Shuffle(source, indices);
        nint step = PatternStep[(int)offset];
        nint i = 0;
        do
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, i), pattern);
            i += step;
        }
        while (i < length);
    }

    /// <summary>For each period from 1 to 15, the byte of the pattern each of 16 positions repeats: j mod period.</summary>
    private static ReadOnlySpan<byte> PatternIndices =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1,
        0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0,
        0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3,
        0, 1, 2, 3, 4, 0, 1, 2, 3, 4, 0, 1, 2, 3, 4, 0,
        0, 1, 2, 3, 4, 5, 0, 1, 2, 3, 4, 5, 0, 1, 2, 3,
        0, 1, 2, 3, 4, 5, 6, 0, 1, 2, 3, 4, 5, 6, 0, 1,
        0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 0, 1, 2, 3, 4, 5, 6,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 0, 1, 2, 3, 4, 5,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 0, 1, 2, 3, 4,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 0, 1, 2, 3,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 0, 1, 2,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 0, 1,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 0,
    ];

    /// <summary>For each period, the largest multiple of it that fits in 16 bytes: how far a store moves on.</summary>
    private static ReadOnlySpan<byte> PatternStep => [16, 16, 16, 15, 16, 15, 12, 14, 16, 9, 10, 11, 12, 13, 14, 15];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Copy16(ref byte dst, ref byte src) =>
        Unsafe.WriteUnaligned(ref dst, Unsafe.ReadUnaligned<Vector128<byte>>(ref src));

    /// <summary>
    /// libzstd's <c>BIT_readBitsFast</c>: 1 to 31 bits from bit <paramref name="at"/> of the container.
    /// Only the low six bits of <paramref name="count"/> matter: a raw entry whose low byte is the
    /// count serves as it is.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint ReadBitsFast(ulong container, nint at, nint count) =>
        // A shift takes its count modulo 64, so 64 - count is just -count: one negation.
        (nuint)((container << (int)at) >> (int)-count);

    /// <summary>
    /// A state's <paramref name="count"/> bits, 0 to 9, and the bit after them: the offset of its next
    /// state in the doubled <see cref="SequenceTableSet"/>, where the extra bit lands on either copy.
    /// Two shifts, where the bits alone would take three to give 0 for none. Only the low six bits
    /// of <paramref name="count"/> and <paramref name="at"/> matter, as for any shift.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint ReadStateBits(ulong container, nint at, nint count) =>
        // 63 - count is count ^ 63 for a count up to 63: one instruction, off the dependency chain.
        (nuint)((container << (int)at) >> ((int)count ^ 63));

    /// <summary>libzstd's <c>BIT_readBits</c>: 0 to 31 bits, as two shifts that give 0 for none.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint ReadBits(ulong container, nint at, nint count) =>
        // 63 - count is count ^ 63 for a count up to 63: one instruction.
        (nuint)(((container << (int)at) >> 1) >> ((int)count ^ 63));

    /// <summary>
    /// libzstd's <c>BIT_reloadDStream</c>. From eight bytes up, the position moves back by the whole
    /// bytes consumed, at most seven since a reload follows every 57 bits at most; below eight it
    /// moves by what is left, and an overflowed stream stays as it is.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Reload(ref byte bits, ref nint ptr, ref nint bc, ref ulong container)
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
            bc -= bytes << 3;
        }
        else
        {
            return;
        }

        container = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bits, ptr));
    }
}
