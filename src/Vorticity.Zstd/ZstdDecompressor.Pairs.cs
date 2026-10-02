using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Zstd.Internal;

namespace Vorticity.Zstd;

/// <summary>Two compressed blocks at once: their sequences decoded side by side, then executed in turn.</summary>
/// <remarks>
/// <para>
/// Within a block the sequences form one chain: each state indexes the table that gives the next,
/// at a position the states before it add up to, and the core waits on that chain, some 10 cycles
/// a sequence, with most of its units idle. Two blocks are two chains with nothing in common: so a
/// block and the next are read first (literals, tables), their sequences then decoded in one loop,
/// one of each a step, into records of lengths and offset, and each block finally executed from its
/// records, A then B, the way the fused loop would have, the rest of each bitstream included.
/// </para>
/// <para>
/// Only blocks with many short sequences pair: at least <see cref="MinPairSequences"/> each, with
/// lengths the tables expect short (<see cref="MaxPairLengths"/>), where the chains, not the copies,
/// take the time. A frame of blocks with few sequences is not even looked ahead in.
/// </para>
/// <para>
/// The result and the errors are those of decoding the blocks one after the other. B is read before
/// A executes, but whatever in B depends on A's output (the room left after it) is checked after A,
/// and if reading B fails, A runs alone, as if B had not been read: B then fails again in its turn,
/// the state B's failed reading left (its literals, its tables in the other set, the Huffman table)
/// being rebuilt by that second reading or not used by it.
/// </para>
/// </remarks>
public sealed partial class ZstdDecompressor
{
    /// <summary>The fewest sequences each of two blocks needs for them to be decoded side by side.</summary>
    private const int MinPairSequences = 64;

    /// <summary>
    /// The longest literal length plus match length, on average as the tables expect them, for which
    /// two blocks are decoded side by side: the pair pays for the chains of states, which the copies of
    /// longer sequences hide anyway, while their records only add to them.
    /// </summary>
    private const int MaxPairLengths = 24;

    /// <summary>The most sequences of a pair decoded side by side: the rest of each block runs alone.</summary>
    private const int MaxPairSequences = 1 << 15;

    /// <summary>The ulongs a step of the pair loop writes: A's record, then B's, each lengths then offset.</summary>
    private const int RecordStride = 4;

    /// <summary>Whether blocks are decoded in pairs; tests turn it off to compare with one at a time.</summary>
    internal bool PairsBlocks { get; set; } = true;

    /// <summary>The sequences decoded side by side so far, for the tests.</summary>
    internal long PairedSequences { get; private set; }

    /// <summary>
    /// The sequences of the last compressed block, from <see cref="MinPairSequences"/> at the start of
    /// a frame: a frame whose blocks have few sequences (many small blocks) is not looked ahead in.
    /// </summary>
    private int _lastBlockSequences;

    // ---- the second block of a pair: its literals, its short bitstream, the set of its tables (which
    // becomes the current one once it is decoded); and the records, as many as the largest pair
    // needed. Allocated with the first pair.
    private byte[]? _pairLiterals;
    private byte[]? _pairShortBitstream;
    private SequenceTableSet? _pairTables;
    private ulong[]? _records;

    /// <summary>
    /// Whether the block header at <paramref name="at"/> is a compressed block's, whole within
    /// <paramref name="source"/> and within the frame's block size: one that can pair.
    /// </summary>
    private static bool TryPeekCompressedBlock(ReadOnlySpan<byte> source, int at, int blockSizeMax, out int size, out bool last)
    {
        size = 0;
        last = false;
        if (source.Length - at < FrameFormat.BlockHeaderSize)
        {
            return false;
        }

        int header = source[at] | (source[at + 1] << 8) | (source[at + 2] << 16);
        if (((header >> 1) & 3) != 2)
        {
            return false;
        }

        size = header >> 3;
        last = (header & 1) != 0;
        return size <= blockSizeMax && size <= source.Length - (at + FrameFormat.BlockHeaderSize);
    }

    /// <summary>
    /// The number of sequences a compressed block declares, read from its headers alone (the
    /// literals section's, then the sequences section's); -1 when they do not hold together, which
    /// the block's decoding then reports.
    /// </summary>
    private static int PeekSequenceCount(ReadOnlySpan<byte> source, int blockStart, int blockSize)
    {
        ReadOnlySpan<byte> block = source.Slice(blockStart, blockSize);
        if (block.Length < 5)
        {
            return -1;
        }

        int literalsType = block[0] & 3;
        int sizeFormat = (block[0] >> 2) & 3;
        int sectionSize;
        if (literalsType <= 1)
        {
            int headerSize = sizeFormat == 1 ? 2 : sizeFormat == 3 ? 3 : 1;
            int size = sizeFormat == 1 ? (block[0] | (block[1] << 8)) >> 4
                : sizeFormat == 3 ? (block[0] | (block[1] << 8) | (block[2] << 16)) >> 4
                : block[0] >> 3;
            sectionSize = headerSize + (literalsType == 0 ? size : 1);
        }
        else
        {
            uint lhc = (uint)(block[0] | (block[1] << 8) | (block[2] << 16) | (block[3] << 24));
            sectionSize = sizeFormat switch
            {
                2 => 4 + (int)(lhc >> 18),
                3 => 5 + (int)(lhc >> 22) + (block[4] << 10),
                _ => 3 + (int)((lhc >> 14) & 0x3FF),
            };
        }

        if (sectionSize >= block.Length)
        {
            return -1;
        }

        int nbSeq = block[sectionSize];
        if (nbSeq < 0x80)
        {
            return nbSeq;
        }

        if (nbSeq < 0xFF)
        {
            return sectionSize + 1 < block.Length ? ((nbSeq - 0x80) << 8) + block[sectionSize + 1] : -1;
        }

        return sectionSize + 2 < block.Length ? block[sectionSize + 1] + (block[sectionSize + 2] << 8) + 0x7F00 : -1;
    }

    /// <summary>
    /// Whether block A and block B after it, both compressed, have enough sequences each to be
    /// decoded side by side.
    /// </summary>
    private static bool WorthPairing(ReadOnlySpan<byte> source, int startA, int sizeA, int startB, int sizeB) =>
        PeekSequenceCount(source, startA, sizeA) >= MinPairSequences && PeekSequenceCount(source, startB, sizeB) >= MinPairSequences;

    /// <summary>
    /// Block A, at <paramref name="startA"/>, and block B after it, at <paramref name="startB"/>,
    /// both compressed and whole in <paramref name="source"/>: see the remarks of this part of the
    /// class. <paramref name="paired"/> tells whether B was decoded too; if not, only A was, and B is
    /// the next block. Returns the size of A's content, plus B's when paired.
    /// </summary>
    private int DecodeBlockPair(
        ReadOnlySpan<byte> source, int startA, int sizeA, int startB, int sizeB,
        Span<byte> destination, int op, int blockSizeMax, ReadOnlySpan<byte> history, out bool paired)
    {
        paired = false;

        // ---- A, read as DecodeCompressedBlock reads it
        int capacity = destination.Length - op;
        int literalsSizeA = DecodeLiterals(
            source, startA, sizeA, blockSizeMax, Math.Min(blockSizeMax, capacity), _literals, out ReadOnlySpan<byte> literalsA, out int literalCountA);
        ReadOnlySpan<byte> sequencesA = source.Slice(startA + literalsSizeA, sizeA - literalsSizeA);
        int nbSeqA = DecodeSequencesHeader(sequencesA, _sequenceTables, null, out int headerSizeA);
        if (nbSeqA > 0 && capacity == 0)
        {
            Throw.Error(ZstdError.DestinationTooSmall);
        }

        ReadOnlySpan<byte> bitstreamA = sequencesA.Slice(headerSizeA);
        _lastBlockSequences = nbSeqA;
        if (nbSeqA < MinPairSequences)
        {
            return ExecuteSequences(bitstreamA, nbSeqA, literalsA, literalCountA, destination, op, blockSizeMax, history);
        }

        // ---- B, read before A executes: with the room A leaves at most, rechecked once A is done
        _sequenceEntropy = true;
        _pairLiterals ??= new byte[FrameFormat.MaxBlockSize + LiteralsMargin];
        _pairShortBitstream ??= new byte[8];
        _pairTables ??= new SequenceTableSet();
        SequenceTableSet tablesB = _pairTables;
        int literalCountB;
        int nbSeqB;
        ReadOnlySpan<byte> literalsB;
        ReadOnlySpan<byte> bitstreamB;
        try
        {
            int literalsSizeB = DecodeLiterals(
                source, startB, sizeB, blockSizeMax, Math.Min(blockSizeMax, capacity), _pairLiterals, out literalsB, out literalCountB);
            ReadOnlySpan<byte> sequencesB = source.Slice(startB + literalsSizeB, sizeB - literalsSizeB);
            nbSeqB = DecodeSequencesHeader(sequencesB, tablesB, _sequenceTables, out int headerSizeB);
            bitstreamB = sequencesB.Slice(headerSizeB);
        }
        catch (ZstdException)
        {
            return ExecuteSequences(bitstreamA, nbSeqA, literalsA, literalCountA, destination, op, blockSizeMax, history);
        }

        paired = true;
        _lastBlockSequences = nbSeqB;
        if (!TryBeginSequences(bitstreamA, nbSeqA, _sequenceTables, _shortBitstream, out SequenceState a))
        {
            Throw.Error(ZstdError.SequenceBitstream);
        }

        // ---- the sequences of both, side by side, when B has enough and a valid bitstream
        SequenceState b = default;
        nint decoded = 0;
        ref ulong records = ref Unsafe.NullRef<ulong>();
        if (nbSeqB >= MinPairSequences
            && _sequenceTables.ExpectedLengthsTimes2 <= 2 * MaxPairLengths && tablesB.ExpectedLengthsTimes2 <= 2 * MaxPairLengths
            && TryBeginSequences(bitstreamB, nbSeqB, tablesB, _pairShortBitstream, out b))
        {
            // Every sequence but the last of each: the last reads no states, the careful path takes it.
            nint count = Math.Min(Math.Min(nbSeqA, nbSeqB) - 1, MaxPairSequences);
            if (_records is null || _records.Length < RecordStride * count)
            {
                _records = new ulong[RecordStride * (int)BitOperations.RoundUpToPowerOf2((uint)count)];
            }

            records = ref MemoryMarshal.GetArrayDataReference(_records);
            decoded = DecodeSequencePair(ref a, ref b, ref records, count);
            PairedSequences += 2 * decoded;
        }

        // ---- A: its records, then the rest of its bitstream
        ref byte frameStart = ref MemoryMarshal.GetReference(destination);
        a.Rep0 = _rep0;
        a.Rep1 = _rep1;
        a.Rep2 = _rep2;
        a.Dst = ref Unsafe.Add(ref frameStart, op);
        a.Lit = ref MemoryMarshal.GetReference(literalsA);
        if (decoded > 0)
        {
            ExecuteRecords(ref a, ref records, decoded, literalsA, literalCountA, destination, op, blockSizeMax, history);
        }

        int writtenA = RunSequences(ref a, literalsA, literalCountA, destination, op, blockSizeMax, history);
        int opB = op + writtenA;

        // ---- B, from the checks DecodeLiterals and DecodeCompressedBlock make on the room it has,
        // its tables now the current ones
        (_sequenceTables, _pairTables) = (tablesB, _sequenceTables);
        int capacityB = destination.Length - opB;
        if (Math.Min(blockSizeMax, capacityB) < literalCountB || (nbSeqB > 0 && capacityB == 0))
        {
            Throw.Error(ZstdError.DestinationTooSmall);
        }

        if (decoded == 0)
        {
            return writtenA + ExecuteSequences(bitstreamB, nbSeqB, literalsB, literalCountB, destination, opB, blockSizeMax, history);
        }

        b.Rep0 = _rep0;
        b.Rep1 = _rep1;
        b.Rep2 = _rep2;
        b.Dst = ref Unsafe.Add(ref frameStart, opB);
        b.Lit = ref MemoryMarshal.GetReference(literalsB);
        ExecuteRecords(ref b, ref Unsafe.Add(ref records, RecordStride / 2), decoded, literalsB, literalCountB, destination, opB, blockSizeMax, history);
        return writtenA + RunSequences(ref b, literalsB, literalCountB, destination, opB, blockSizeMax, history);
    }

    /// <summary>
    /// Decodes up to <paramref name="count"/> sequences of each of two blocks, one of each a step,
    /// into <paramref name="records"/>: A's lengths and offset, then B's, each step. Both states move
    /// past the sequences decoded, whose number it returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A record holds the literal length in its low half and the match length in its high half,
    /// then the offset as libzstd's <c>offBase</c>: the offset plus 3, or 1 to 3 for the repeat codes,
    /// which only the execution can resolve. An offset code c stands for (1 &lt;&lt; c) plus c extra
    /// bits, which is that value: so a repeat code takes no branch here.
    /// </para>
    /// <para>
    /// The common sequences of both blocks (lengths without extra bits) take one straight path, the
    /// states of both first, as <see cref="CommonStates"/> explains. They reload without a clamp:
    /// each moves its stream back by eight bytes at most, so a batch of them is as long as the nearer
    /// stream's position divided by eight. Any other step decodes both sequences in full, as the
    /// general path of the fused loop does, then starts a new batch. The loop stops at the end of
    /// <paramref name="count"/>, or when a stream comes within eight bytes of its start: the fused
    /// loop and the careful path take the rest, from the same states.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint DecodeSequencePair(ref SequenceState a, ref SequenceState b, ref ulong records, nint count)
    {
        ref byte streamA = ref Unsafe.Add(ref a.Bits, a.Ptr);
        nint bcA = a.Consumed;
        ulong containerA = a.Container;
        ref SeqSymbol tablesA = ref a.Tables;
        nint llA = a.LiteralLengthState;
        nint mlA = a.MatchLengthState;
        nint ofA = a.OffsetState;
        ref byte streamB = ref Unsafe.Add(ref b.Bits, b.Ptr);
        nint bcB = b.Consumed;
        ulong containerB = b.Container;
        ref SeqSymbol tablesB = ref b.Tables;
        nint llB = b.LiteralLengthState;
        nint mlB = b.MatchLengthState;
        nint ofB = b.OffsetState;
        ref ulong output = ref records;
        nint left = count;
        nint batch;

    Batch:
        batch = Math.Min(left, Math.Min(Unsafe.ByteOffset(ref a.Bits, ref streamA), Unsafe.ByteOffset(ref b.Bits, ref streamB)) >> 3);
        if (batch <= 0)
        {
            goto Stop;
        }

        left -= batch;

    Loop:
        ulong llEntryA = SeqEntry.Load(ref tablesA, llA);
        ulong mlEntryA = SeqEntry.Load(ref tablesA, mlA);
        ulong ofEntryA = SeqEntry.Load(ref tablesA, ofA);
        ulong llEntryB = SeqEntry.Load(ref tablesB, llB);
        ulong mlEntryB = SeqEntry.Load(ref tablesB, mlB);
        ulong ofEntryB = SeqEntry.Load(ref tablesB, ofB);
        if ((((llEntryA | mlEntryA) | (llEntryB | mlEntryB)) & 0x1F00) == 0)
        {
            goto Common;
        }

        // ---- a length with extra bits in either block: that block's sequence decoded in full, as the
        // general path of the fused loop does, the other's as a common one; then a new batch, the
        // full decoding's reloads moving its stream back further than a common one's
        {
            ulong lengthsA;
            ulong offBaseA;
            if (((llEntryA | mlEntryA) & 0x1F00) == 0)
            {
                offBaseA = OffBase(containerA << (int)bcA, ofEntryA);
                nint consumedA = CommonNextStates(llEntryA, mlEntryA, ofEntryA, bcA, containerA, ref llA, ref mlA, ref ofA);
                lengthsA = (ulong)SeqEntry.LengthBase(llEntryA) | ((ulong)SeqEntry.LengthBase(mlEntryA) << 32);
                streamA = ref Unsafe.Subtract(ref streamA, (consumedA >> 3) & 0x1F);
                bcA = consumedA & 7;
                containerA = Unsafe.ReadUnaligned<ulong>(ref streamA);
            }
            else
            {
                nint ptrA = Unsafe.ByteOffset(ref a.Bits, ref streamA);
                DecodeFull(
                    llEntryA, mlEntryA, ofEntryA, ref a.Bits, ref ptrA, ref bcA, ref containerA, ref llA, ref mlA, ref ofA,
                    out lengthsA, out offBaseA);
                streamA = ref Unsafe.Add(ref a.Bits, ptrA);
            }

            ulong lengthsB;
            ulong offBaseB;
            if (((llEntryB | mlEntryB) & 0x1F00) == 0)
            {
                offBaseB = OffBase(containerB << (int)bcB, ofEntryB);
                nint consumedB = CommonNextStates(llEntryB, mlEntryB, ofEntryB, bcB, containerB, ref llB, ref mlB, ref ofB);
                lengthsB = (ulong)SeqEntry.LengthBase(llEntryB) | ((ulong)SeqEntry.LengthBase(mlEntryB) << 32);
                streamB = ref Unsafe.Subtract(ref streamB, (consumedB >> 3) & 0x1F);
                bcB = consumedB & 7;
                containerB = Unsafe.ReadUnaligned<ulong>(ref streamB);
            }
            else
            {
                nint ptrB = Unsafe.ByteOffset(ref b.Bits, ref streamB);
                DecodeFull(
                    llEntryB, mlEntryB, ofEntryB, ref b.Bits, ref ptrB, ref bcB, ref containerB, ref llB, ref mlB, ref ofB,
                    out lengthsB, out offBaseB);
                streamB = ref Unsafe.Add(ref b.Bits, ptrB);
            }

            output = lengthsA;
            Unsafe.Add(ref output, 1) = offBaseA;
            Unsafe.Add(ref output, 2) = lengthsB;
            Unsafe.Add(ref output, 3) = offBaseB;
            output = ref Unsafe.Add(ref output, RecordStride);
            left += batch - 1;
            goto Batch;
        }

    Common:
        {
            // The container as the offset reads it, taken first: the states then replace both.
            ulong offsetBitsA = containerA << (int)bcA;
            ulong offsetBitsB = containerB << (int)bcB;
            // A's states, then B's: interleaving them one instruction each loses 2-3% (the two chains
            // then compete for the same units in the same cycles).
            nint consumedA = CommonNextStates(llEntryA, mlEntryA, ofEntryA, bcA, containerA, ref llA, ref mlA, ref ofA);
            nint consumedB = CommonNextStates(llEntryB, mlEntryB, ofEntryB, bcB, containerB, ref llB, ref mlB, ref ofB);
            streamA = ref Unsafe.Subtract(ref streamA, (consumedA >> 3) & 0x1F);
            bcA = consumedA & 7;
            containerA = Unsafe.ReadUnaligned<ulong>(ref streamA);
            streamB = ref Unsafe.Subtract(ref streamB, (consumedB >> 3) & 0x1F);
            bcB = consumedB & 7;
            containerB = Unsafe.ReadUnaligned<ulong>(ref streamB);
            ulong lengthsA = (ulong)SeqEntry.LengthBase(llEntryA) | ((ulong)SeqEntry.LengthBase(mlEntryA) << 32);
            ulong offBaseA = OffBase(offsetBitsA, ofEntryA);
            ulong lengthsB = (ulong)SeqEntry.LengthBase(llEntryB) | ((ulong)SeqEntry.LengthBase(mlEntryB) << 32);
            ulong offBaseB = OffBase(offsetBitsB, ofEntryB);
            output = lengthsA;
            Unsafe.Add(ref output, 1) = offBaseA;
            Unsafe.Add(ref output, 2) = lengthsB;
            Unsafe.Add(ref output, 3) = offBaseB;
            output = ref Unsafe.Add(ref output, RecordStride);
            if (--batch != 0)
            {
                goto Loop;
            }

            goto Batch;
        }

    Stop:
        a.Ptr = Unsafe.ByteOffset(ref a.Bits, ref streamA);
        a.Consumed = bcA;
        a.Container = containerA;
        a.LiteralLengthState = llA;
        a.MatchLengthState = mlA;
        a.OffsetState = ofA;
        b.Ptr = Unsafe.ByteOffset(ref b.Bits, ref streamB);
        b.Consumed = bcB;
        b.Container = containerB;
        b.LiteralLengthState = llB;
        b.MatchLengthState = mlB;
        b.OffsetState = ofB;
        nint decoded = (nint)((nuint)Unsafe.ByteOffset(ref records, ref output) / (RecordStride * sizeof(ulong)));
        a.NbSeq -= decoded;
        b.NbSeq -= decoded;
        return decoded;
    }

    /// <summary>
    /// Any sequence, decoded as the general path of the fused loop decodes it, into a record's
    /// lengths and <c>offBase</c> (see <see cref="DecodeSequencePair"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecodeFull(
        ulong llEntry, ulong mlEntry, ulong ofEntry, ref byte bits, ref nint ptr, ref nint bc, ref ulong container,
        ref nint llState, ref nint mlState, ref nint ofState, out ulong lengths, out ulong offBase)
    {
        nint ofBits = SeqEntry.OffsetExtraBits(ofEntry);
        nint mlBits = SeqEntry.LengthExtraBits(mlEntry);
        nint llBits = SeqEntry.LengthExtraBits(llEntry);
        offBase = OffBase(container << (int)bc, ofEntry);
        bc += ofBits;
        nuint matchLength = SeqEntry.LengthBase(mlEntry) + ReadBits(container, bc, mlBits);
        bc += mlBits;
        if (ofBits + mlBits + llBits >= 57 - (SequenceCodes.LiteralLengthMaxLog + SequenceCodes.MatchLengthMaxLog + SequenceCodes.OffsetMaxLog))
        {
            ReloadClamped(ref bits, ref ptr, ref bc, ref container);
        }

        nuint litLength = SeqEntry.LengthBase(llEntry) + ReadBits(container, bc, llBits);
        bc += llBits;
        NextStates(llEntry, mlEntry, ofEntry, container, ref bc, ref llState, ref mlState, ref ofState);
        ReloadClamped(ref bits, ref ptr, ref bc, ref container);
        lengths = (ulong)litLength | ((ulong)matchLength << 32);
    }

    /// <summary>
    /// The <c>offBase</c> of an offset code c, (1 &lt;&lt; c) plus its c extra bits, from the container
    /// shifted to them (<paramref name="bits"/>) and the raw entry, whose low six bits are c: the bits
    /// shifted down by one, a 1 set above them, then all shifted down to c bits below that 1. Two
    /// instructions less than adding the power of two to the bits read, which a code of 0 makes take
    /// three shifts anyway.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong OffBase(ulong bits, ulong ofEntry) => ((bits >> 1) | (1UL << 63)) >> ((int)ofEntry ^ 63);

    /// <summary>
    /// Executes <paramref name="count"/> records of a block, every <see cref="RecordStride"/> ulongs
    /// from <paramref name="records"/>, from where <paramref name="state"/> is in the output and the
    /// literals, with its repeat offsets: the fast loop while the copies fit, then the careful path.
    /// </summary>
    private static void ExecuteRecords(
        ref SequenceState state, ref ulong records, nint count, ReadOnlySpan<byte> literals, int literalCount,
        Span<byte> destination, int blockStart, int blockSizeMax, ReadOnlySpan<byte> history)
    {
        ref byte frameStart = ref MemoryMarshal.GetReference(destination);
        nint fastRoom = Math.Min((nint)blockStart + blockSizeMax, destination.Length - WildCopyOverlength);
        nint done = ExecuteRecordsFast(
            ref state, ref records, count, ref frameStart,
            ref Unsafe.Add(ref frameStart, Math.Max(fastRoom, Unsafe.ByteOffset(ref frameStart, ref state.Dst))),
            ref Unsafe.Add(ref MemoryMarshal.GetReference(literals), literalCount));
        if (done < count || state.Pending)
        {
            ExecuteRecordsCareful(
                ref state, ref Unsafe.Add(ref records, RecordStride * done), count - done, literals, literalCount,
                destination, blockStart, blockSizeMax, history);
        }
    }

    /// <summary>
    /// The execution half of the fused loop, on records: while a sequence fits the room proven for
    /// copies past their end. It returns the records it took, the last one left pending in
    /// <paramref name="s"/> if it could not execute it.
    /// </summary>
    /// <remarks>
    /// The room is proven a batch of records at a time, as the pair loop proves its bitstream: a
    /// record of 16 literals and a 16-byte match at most moves the output by 32 bytes and the
    /// literals by 16, so the next (room / 32) such records fit, and each only checks its own shape.
    /// Any other record takes the checked path and is charged to the batch for the room it took, a
    /// new batch being proven only once it is spent.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint ExecuteRecordsFast(
        ref SequenceState s, ref ulong records, nint count, ref byte frameStart, ref byte fastLimit, ref byte litEnd)
    {
        Debug.Assert(count > 0);
        nuint rep0 = s.Rep0;
        nuint rep1 = s.Rep1;
        nuint rep2 = s.Rep2;
        ref byte dst = ref s.Dst;
        ref byte lit = ref s.Lit;
        ref ulong record = ref records;
        ref ulong end = ref Unsafe.Add(ref records, RecordStride * count);
        nint batch;
        nint litLength;
        nint matchLength;
        nuint offset;
        nuint offBase;

    Batch:
        batch = Math.Min(
            Unsafe.ByteOffset(ref record, ref end) >> 5,
            Math.Min(Unsafe.ByteOffset(ref dst, ref fastLimit) >> 5, Unsafe.ByteOffset(ref lit, ref litEnd) >> 4));
        if (batch <= 0)
        {
            goto Stop;
        }

    Loop:
        // The lengths as the two halves of their word: no instruction to separate them.
        litLength = (nint)Unsafe.As<ulong, uint>(ref record);
        matchLength = (nint)Unsafe.Add(ref Unsafe.As<ulong, uint>(ref record), 1);
        offBase = (nuint)Unsafe.Add(ref record, 1);
        record = ref Unsafe.Add(ref record, RecordStride);
        if (offBase > 3)
        {
            goto NewOffset;
        }

        // ---- a repeat offset; the rest as for a new one, written twice so that each path runs
        // straight to its own end
        {
            offset = ResolveOffset(offBase - 1, litLength == 0, ref rep0, ref rep1, ref rep2);
            ref byte matchStart = ref Unsafe.Add(ref dst, litLength);
            if (IsShortRecord(ref frameStart, ref matchStart, litLength, matchLength, offset))
            {
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

            goto Execute;
        }

    NewOffset:
        {
            offset = offBase - 3;
            rep2 = rep1;
            rep1 = rep0;
            rep0 = offset;
            ref byte matchStart = ref Unsafe.Add(ref dst, litLength);
            if (IsShortRecord(ref frameStart, ref matchStart, litLength, matchLength, offset))
            {
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

            goto Execute;
        }

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

            // The room it took, in records of 32 bytes of output and 16 literals: at least one.
            batch -= Max((litLength + matchLength + 31) >> 5, (litLength + 15) >> 4);
            if (batch > 0)
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
        s.Rep0 = rep0;
        s.Rep1 = rep1;
        s.Rep2 = rep2;
        s.Dst = ref dst;
        s.Lit = ref lit;
        return (nint)((nuint)Unsafe.ByteOffset(ref records, ref record) / (RecordStride * sizeof(ulong)));
    }

    /// <summary>The larger of two values, by their difference's sign: no branch.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint Max(nint a, nint b)
    {
        nint difference = a - b;
        return a - (difference & (difference >> 63));
    }

    /// <summary>
    /// Whether a record within a batch (see <see cref="ExecuteRecordsFast"/>) takes one 16-byte copy of
    /// literals and one of match, 16 or more apart, the match within the output: one condition.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsShortRecord(ref byte frameStart, ref byte matchStart, nint litLength, nint matchLength, nuint offset) =>
        (offset <= (nuint)Unsafe.ByteOffset(ref frameStart, ref matchStart))
        & (litLength <= 16)
        & (matchLength <= 16)
        & (offset >= 16);

    /// <summary>
    /// The pending sequence <see cref="ExecuteRecordsFast"/> left, if any, then <paramref name="count"/>
    /// records from <paramref name="record"/>, each checked as the careful path checks a sequence.
    /// </summary>
    private static void ExecuteRecordsCareful(
        ref SequenceState s, ref ulong record, nint count, ReadOnlySpan<byte> literals, int literalCount,
        Span<byte> destination, int blockStart, int blockSizeMax, ReadOnlySpan<byte> history)
    {
        ref byte frameStart = ref MemoryMarshal.GetReference(destination);
        ref byte lit = ref MemoryMarshal.GetReference(literals);
        nint o = Unsafe.ByteOffset(ref frameStart, ref s.Dst);
        nint litPtr = Unsafe.ByteOffset(ref lit, ref s.Lit);
        nint blockEnd = (nint)blockStart + blockSizeMax;
        nint oend = Math.Min(blockEnd, destination.Length);
        nuint rep0 = s.Rep0;
        nuint rep1 = s.Rep1;
        nuint rep2 = s.Rep2;
        if (s.Pending)
        {
            s.Pending = false;
            ExecuteCarefully(ref frameStart, ref o, ref lit, ref litPtr, s.LitLength, s.MatchLength, s.Offset, oend, blockEnd, literalCount, history);
        }

        while (count-- > 0)
        {
            nint litLength = (nint)(uint)record;
            nint matchLength = (nint)(record >> 32);
            nuint offBase = (nuint)Unsafe.Add(ref record, 1);
            record = ref Unsafe.Add(ref record, RecordStride);
            nuint offset;
            if (offBase > 3)
            {
                offset = offBase - 3;
                rep2 = rep1;
                rep1 = rep0;
                rep0 = offset;
            }
            else
            {
                offset = ResolveOffset(offBase - 1, litLength == 0, ref rep0, ref rep1, ref rep2);
            }

            ExecuteCarefully(ref frameStart, ref o, ref lit, ref litPtr, litLength, matchLength, offset, oend, blockEnd, literalCount, history);
        }

        s.Rep0 = rep0;
        s.Rep1 = rep1;
        s.Rep2 = rep2;
        s.Dst = ref Unsafe.Add(ref frameStart, o);
        s.Lit = ref Unsafe.Add(ref lit, litPtr);
    }
}
