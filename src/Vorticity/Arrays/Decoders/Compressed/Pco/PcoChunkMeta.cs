using System;
using System.Buffers;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed.Pco;

/// <summary>How a pco chunk maps latents back to numbers.</summary>
internal enum PcoModeKind
{
    /// <summary>The latent is the number.</summary>
    Classic = 0,

    /// <summary>The number is <c>primary * base + secondary</c>.</summary>
    IntMult = 1,

    /// <summary>The float equivalent of <see cref="IntMult"/>.</summary>
    FloatMult = 2,

    /// <summary>Floats quantized to a fixed number of bits.</summary>
    FloatQuant = 3,

    /// <summary>The latent is an index into a dictionary the metadata holds.</summary>
    Dict = 4,
}

/// <summary>How a pco chunk delta-encodes its latents.</summary>
internal enum PcoDeltaKind
{
    /// <summary>No delta encoding.</summary>
    NoOp = 0,

    /// <summary>Consecutive differences, taken <c>Order</c> times.</summary>
    Consecutive = 1,

    /// <summary>Lookback (LZ-style) delta encoding.</summary>
    Lookback = 2,

    /// <summary>A linear prediction from the previous latents, the residual on the wire.</summary>
    Conv1 = 3,
}

/// <summary>What a pco chunk's numbers are.</summary>
internal enum PcoNumberKind : byte
{
    /// <summary>An unsigned integer: the latent is the number.</summary>
    Unsigned,

    /// <summary>A signed integer: the latent is the number shifted so that its minimum is zero.</summary>
    Signed,

    /// <summary>A float: the latent is its bits ordered so that the numbers' order is the latents'.</summary>
    Float,
}

/// <summary>A pco number type: the width of its latents and how they map back to it.</summary>
/// <param name="LatentBits">Bits per latent: 16, 32 or 64, the type's own width.</param>
/// <param name="Kind">How a latent maps to the number.</param>
internal readonly record struct PcoNumber(int LatentBits, PcoNumberKind Kind)
{
    /// <summary>The number type <c>vortex.pco</c> stores <paramref name="ptype"/> as, or null when it stores none.</summary>
    /// <remarks>
    /// Upstream's <c>number_type_from_ptype</c>: the nine types of 16 bits and more. pco itself has
    /// the two of eight bits, but Vortex never writes them.
    /// </remarks>
    internal static PcoNumber? Of(PType ptype) => ptype switch
    {
        PType.U16 => new PcoNumber(16, PcoNumberKind.Unsigned),
        PType.U32 => new PcoNumber(32, PcoNumberKind.Unsigned),
        PType.U64 => new PcoNumber(64, PcoNumberKind.Unsigned),
        PType.I16 => new PcoNumber(16, PcoNumberKind.Signed),
        PType.I32 => new PcoNumber(32, PcoNumberKind.Signed),
        PType.I64 => new PcoNumber(64, PcoNumberKind.Signed),
        PType.F16 => new PcoNumber(16, PcoNumberKind.Float),
        PType.F32 => new PcoNumber(32, PcoNumberKind.Float),
        PType.F64 => new PcoNumber(64, PcoNumberKind.Float),
        _ => null,
    };

    /// <summary>The latent's midpoint, <c>MID</c> upstream: its top bit.</summary>
    internal ulong Mid => 1UL << (LatentBits - 1);

    /// <summary>The latent's bits, all set.</summary>
    internal ulong Mask => LatentBits == 64 ? ulong.MaxValue : (1UL << LatentBits) - 1;

    /// <summary>A float's explicit mantissa bits: 10, 23 or 52.</summary>
    internal int PrecisionBits => LatentBits switch
    {
        16 => 10,
        32 => 23,
        _ => 52,
    };

    /// <summary>
    /// The number a latent stands for, as the bits of the type zero-extended: <c>from_latent_ordered</c>.
    /// </summary>
    /// <param name="latent">The latent; bits above the type's width are ignored.</param>
    /// <returns>The number's bits, zero-extended.</returns>
    internal ulong FromLatentOrdered(ulong latent) => Kind switch
    {
        PcoNumberKind.Signed => unchecked(latent + Mid) & Mask,
        PcoNumberKind.Float => ((latent & Mid) != 0 ? latent ^ Mid : ~latent) & Mask,
        _ => latent & Mask,
    };

    /// <summary>A float's bits as its ordered latent, <c>to_latent_ordered</c>; the inverse of <see cref="FromLatentOrdered"/>.</summary>
    /// <param name="bits">The float's bits, zero-extended.</param>
    /// <returns>The latent.</returns>
    internal ulong FloatToLatentOrdered(ulong bits) => ((bits & Mid) != 0 ? ~bits : bits ^ Mid) & Mask;
}

/// <summary>One bin of a latent variable's table.</summary>
/// <param name="Weight">ANS weight; always at least one.</param>
/// <param name="Lower">The bin's lower bound.</param>
/// <param name="OffsetBits">Bits of offset stored per value in this bin.</param>
internal readonly record struct PcoBin(uint Weight, ulong Lower, int OffsetBits);

/// <summary>One latent variable's table.</summary>
/// <param name="AnsSizeLog">Log2 of the ANS table size; zero when there is a single bin.</param>
/// <param name="BinStorage">The bins, in wire order, from the start of an array that may be longer.</param>
/// <param name="BinCount">How many bins there are.</param>
/// <param name="Table">
/// The tANS decoding table for these bins, built once per chunk.
/// </param>
/// <param name="LatentBits">
/// Bits per latent of this variable: the number's, but 32 for a lookback's delta and for a
/// dictionary's indices.
/// </param>
internal readonly record struct PcoLatentVar(int AnsSizeLog, PcoBin[] BinStorage, int BinCount, PcoAnsTable Table, int LatentBits)
{
    /// <summary>The bins, in wire order.</summary>
    internal ReadOnlySpan<PcoBin> Bins => BinStorage.AsSpan(0, BinCount);
}

/// <summary>
/// A pco chunk's metadata: a format version, a mode, a delta encoding, then one latent-variable
/// table for each latent the mode and delta encoding imply, each table an ANS size and a list of
/// bins. It is one continuous bit stream with no alignment between sections, so the format
/// version's leading bytes are the only byte-aligned part of it -- and a dictionary's, which starts
/// on a byte.
/// </summary>
/// <remarks>
/// <para>
/// Refilled chunk after chunk, in the bins and tables it already holds, rather than read into new
/// ones: a table runs to a few hundred kilobytes and every chunk of a column has one per latent
/// variable. What a refill gives replaces what the last one gave.
/// </para>
/// <para>
/// A dictionary is the exception: it is as long as the chunk has distinct numbers, up to millions,
/// so it is rented for the chunk and given back by the next refill or by <see cref="Release"/>,
/// rather than kept by metadata that outlives the decode.
/// </para>
/// </remarks>
internal sealed class PcoChunkMeta
{
    private const int ModeVariantBits = 4;
    private const int DeltaVariantBits = 4;
    private const int DeltaOrderBits = 3;
    private const int AnsSizeLogBits = 4;
    private const int BinCountBits = 15;
    private const int MaxAnsBits = 14;
    private const int LookbackWindowBits = 5;
    private const int LookbackStateBits = 4;
    private const int QuantizeKBits = 8;
    private const int DictLengthBits = 25;
    private const int Conv1QuantizationBits = 5;
    private const int Conv1WeightCountBits = 5;

    /// <summary>A lookback window's largest log: the most values a chunk holds, 2^24.</summary>
    private const int MaxLookbackWindowLog = 24;

    /// <summary>A convolution's most weights: five bits of count, plus one.</summary>
    internal const int MaxConv1Order = 32;

    /// <summary>One slot per latent variable, the delta's, the primary's and the secondary's.</summary>
    private readonly PcoBin[][] _bins = [[], [], []];

    private readonly PcoAnsTable[] _tables = [new PcoAnsTable(), new PcoAnsTable(), new PcoAnsTable()];

    private readonly long[] _conv1Weights = new long[MaxConv1Order];

    private ulong[]? _dictionary;

    /// <summary>pco format major version.</summary>
    internal int FormatMajor { get; private set; }

    /// <summary>pco format minor version; zero before major 4.</summary>
    internal int FormatMinor { get; private set; }

    /// <summary>The numbers the chunk holds.</summary>
    internal PcoNumber Number { get; private set; }

    /// <summary>The mode.</summary>
    internal PcoModeKind Mode { get; private set; }

    /// <summary>
    /// The mode's base latent, for the multiplying modes; the quantization's bit count for
    /// <see cref="PcoModeKind.FloatQuant"/>; zero otherwise.
    /// </summary>
    internal ulong ModeBase { get; private set; }

    /// <summary>The dictionary's entries, as the numbers' bits: <see cref="PcoModeKind.Dict"/> only.</summary>
    internal ReadOnlySpan<ulong> Dictionary => _dictionary.AsSpan(0, DictionaryLength);

    /// <summary>How many entries the dictionary has.</summary>
    internal int DictionaryLength { get; private set; }

    /// <summary>The delta encoding.</summary>
    internal PcoDeltaKind Delta { get; private set; }

    /// <summary>Consecutive delta order, or the convolution's weight count; zero otherwise.</summary>
    internal int DeltaOrder { get; private set; }

    /// <summary>Whether the secondary latent is itself delta-encoded.</summary>
    internal bool SecondaryUsesDelta { get; private set; }

    /// <summary>A lookback's window log: it looks back at most 2^this values.</summary>
    internal int LookbackWindowLog { get; private set; }

    /// <summary>A lookback's state log: each page opens with 2^this latents.</summary>
    internal int LookbackStateLog { get; private set; }

    /// <summary>The convolution's quantization: its sum is shifted right by this many bits.</summary>
    internal int Conv1Quantization { get; private set; }

    /// <summary>The convolution's bias.</summary>
    internal long Conv1Bias { get; private set; }

    /// <summary>The convolution's weights, the oldest latent's first.</summary>
    internal ReadOnlySpan<long> Conv1Weights => _conv1Weights.AsSpan(0, Delta == PcoDeltaKind.Conv1 ? DeltaOrder : 0);

    /// <summary>The delta latent's table, present only for lookback deltas.</summary>
    internal PcoLatentVar? DeltaLatent { get; private set; }

    /// <summary>The primary latent's table.</summary>
    internal PcoLatentVar Primary { get; private set; }

    /// <summary>The secondary latent's table, absent in the Classic and Dict modes.</summary>
    internal PcoLatentVar? Secondary { get; private set; }

    /// <summary>
    /// The latents each page opens with for the primary, <c>n_latents_per_state</c>: the values of
    /// a page's end that its body does not hold, and what a lookback's own latents stop short by.
    /// </summary>
    internal int PrimaryStateCount => StateCount(primary: true);

    /// <summary>The same for the secondary: the primary's when it shares the delta, zero otherwise.</summary>
    internal int SecondaryStateCount => StateCount(primary: false);

    /// <summary>Reads a chunk's metadata, the pco header first, into new metadata.</summary>
    /// <param name="header">pco's file header bytes.</param>
    /// <param name="chunkMeta">The chunk's metadata bytes.</param>
    /// <param name="number">The numbers the chunk holds, from the column's physical type.</param>
    /// <returns>The parsed metadata.</returns>
    internal static PcoChunkMeta Read(ReadOnlySpan<byte> header, ReadOnlySpan<byte> chunkMeta, PcoNumber number) =>
        new PcoChunkMeta().Refill(header, chunkMeta, number);

    /// <summary>Reads a chunk's metadata, the pco header first, into this one.</summary>
    /// <param name="header">pco's file header bytes.</param>
    /// <param name="chunkMeta">The chunk's metadata bytes.</param>
    /// <param name="number">The numbers the chunk holds, from the column's physical type.</param>
    /// <returns>This metadata.</returns>
    /// <remarks>
    /// The header and the chunk metadata are separate buffers in the Vortex node but one stream to
    /// pco, so they are concatenated here rather than read independently.
    /// </remarks>
    internal PcoChunkMeta Refill(ReadOnlySpan<byte> header, ReadOnlySpan<byte> chunkMeta, PcoNumber number)
    {
        Release();
        int joinedLength = header.Length + chunkMeta.Length;
        Span<byte> stack = stackalloc byte[256];
        using Scratch<byte> joinedScratch = new Scratch<byte>(joinedLength, stack);
        Span<byte> joined = joinedScratch.Span[..joinedLength];
        header.CopyTo(joined);
        chunkMeta.CopyTo(joined[header.Length..]);

        PcoBitReader reader = new PcoBitReader(joined);

        int major = reader.ReadAlignedBytes(1)[0];
        int minor = major >= 4 ? reader.ReadAlignedBytes(1)[0] : 0;
        int latentBits = number.LatentBits;

        int modeVariant = (int)reader.ReadUInt(ModeVariantBits);
        ulong modeBase = 0;
        PcoModeKind mode = PcoModeKind.Classic;
        switch (modeVariant)
        {
            case 0:
                break;
            case 1:
                if (major == 0)
                {
                    CompressedThrow.Format("A pco chunk of the yanked format 0 encodes its IntMult base the old way.");
                }

                mode = PcoModeKind.IntMult;
                modeBase = reader.ReadUInt(latentBits);
                break;
            case 2:
                mode = PcoModeKind.FloatMult;
                modeBase = reader.ReadUInt(latentBits);
                break;
            case 3:
                mode = PcoModeKind.FloatQuant;
                modeBase = reader.ReadUInt(QuantizeKBits);
                break;
            case 4:
                mode = PcoModeKind.Dict;
                ReadDictionary(ref reader, number);
                break;
            default:
                CompressedThrow.Format($"A pco chunk declares mode variant {modeVariant}, which is not defined.");
                break;
        }

        RequireValidMode(mode, modeBase, number);

        PcoDeltaKind delta = PcoDeltaKind.NoOp;
        int deltaOrder = 0;
        bool secondaryUsesDelta = false;
        int windowLog = 0;
        int stateLog = 0;
        int quantization = 0;
        long bias = 0;
        if (major < 3)
        {
            // Before delta variants: an order alone, zero for none.
            deltaOrder = (int)reader.ReadUInt(DeltaOrderBits);
            delta = deltaOrder == 0 ? PcoDeltaKind.NoOp : PcoDeltaKind.Consecutive;
        }
        else
        {
            int deltaVariant = (int)reader.ReadUInt(DeltaVariantBits);
            switch (deltaVariant)
            {
                case 0:
                    break;
                case 1:
                    delta = PcoDeltaKind.Consecutive;
                    deltaOrder = (int)reader.ReadUInt(DeltaOrderBits);
                    if (deltaOrder == 0)
                    {
                        CompressedThrow.Format("A pco chunk declares a consecutive delta of order 0.");
                    }

                    secondaryUsesDelta = reader.ReadBool();
                    break;
                case 2:
                    delta = PcoDeltaKind.Lookback;
                    windowLog = 1 + (int)reader.ReadUInt(LookbackWindowBits);
                    stateLog = (int)reader.ReadUInt(LookbackStateBits);
                    if (windowLog > MaxLookbackWindowLog)
                    {
                        CompressedThrow.Format(
                            $"A pco lookback delta's window log {windowLog} exceeds the maximum {MaxLookbackWindowLog}.");
                    }

                    if (stateLog > windowLog)
                    {
                        CompressedThrow.Format(
                            $"A pco lookback delta's state log {stateLog} exceeds its window log {windowLog}.");
                    }

                    secondaryUsesDelta = reader.ReadBool();
                    break;
                case 3:
                    delta = PcoDeltaKind.Conv1;
                    quantization = (int)reader.ReadUInt(Conv1QuantizationBits);
                    bias = unchecked((long)(reader.ReadUInt(64) ^ (1UL << 63)));
                    deltaOrder = 1 + (int)reader.ReadUInt(Conv1WeightCountBits);
                    for (int i = 0; i < deltaOrder; i++)
                    {
                        _conv1Weights[i] = unchecked((int)(uint)(reader.ReadUInt(32) ^ (1UL << 31)));
                    }

                    break;
                default:
                    CompressedThrow.Format($"A pco chunk declares delta variant {deltaVariant}, which is not defined.");
                    break;
            }
        }

        // Only a lookback delta carries a latent variable of its own, and it is always u32.
        PcoLatentVar? deltaLatent = delta == PcoDeltaKind.Lookback
            ? ReadLatentVar(ref reader, 32, 0)
            : null;

        // A dictionary's latents are its indices, which are u32 whatever the numbers are.
        PcoLatentVar primary = ReadLatentVar(ref reader, mode == PcoModeKind.Dict ? 32 : latentBits, 1);

        // Classic and Dict map one latent to the number; every multiplying mode needs a second.
        PcoLatentVar? secondary = mode is PcoModeKind.Classic or PcoModeKind.Dict
            ? null
            : ReadLatentVar(ref reader, latentBits, 2);

        reader.DrainEmptyByte("chunk metadata");

        FormatMajor = major;
        FormatMinor = minor;
        Number = number;
        Mode = mode;
        ModeBase = modeBase;
        Delta = delta;
        DeltaOrder = deltaOrder;
        SecondaryUsesDelta = secondaryUsesDelta;
        LookbackWindowLog = windowLog;
        LookbackStateLog = stateLog;
        Conv1Quantization = quantization;
        Conv1Bias = bias;
        DeltaLatent = deltaLatent;
        Primary = primary;
        Secondary = secondary;

        if (deltaLatent is { } lookbacks)
        {
            RequireLookbacksInWindow(lookbacks, 1L << windowLog);
        }

        if (delta == PcoDeltaKind.Conv1)
        {
            RequireConv1Bounded(primary.LatentBits);
        }

        return this;
    }

    /// <summary>Gives back the rented dictionary, if any; the metadata reads as empty until refilled.</summary>
    internal void Release()
    {
        if (_dictionary is { } dictionary)
        {
            _dictionary = null;
            DictionaryLength = 0;
            ArrayPool<ulong>.Shared.Return(dictionary);
        }
    }

    private int StateCount(bool primary)
    {
        if (!primary && !SecondaryUsesDelta)
        {
            return 0;
        }

        return Delta switch
        {
            PcoDeltaKind.Consecutive => DeltaOrder,
            PcoDeltaKind.Lookback => 1 << LookbackStateLog,
            PcoDeltaKind.Conv1 => primary ? DeltaOrder : 0,
            _ => 0,
        };
    }

    /// <summary>
    /// A dictionary: its length, zeros to the next byte, then its latents uncompressed, each kept as
    /// the number it stands for so that a page's join is one lookup a value.
    /// </summary>
    private void ReadDictionary(ref PcoBitReader reader, PcoNumber number)
    {
        int length = (int)reader.ReadUInt(DictLengthBits);
        reader.DrainEmptyByte("dictionary length");

        // The latents are there before anything is rented for them, so a length the bytes cannot
        // hold is refused rather than allocated.
        if ((long)length * number.LatentBits > reader.BitsRemaining)
        {
            CompressedThrow.Format(
                $"A pco dictionary declares {length} entries, more than its {reader.BitsRemaining} bits hold.");
        }

        ulong[] dictionary = ArrayPool<ulong>.Shared.Rent(Math.Max(length, 1));
        for (int i = 0; i < length; i++)
        {
            dictionary[i] = number.FromLatentOrdered(reader.ReadUInt(number.LatentBits));
        }

        _dictionary = dictionary;
        DictionaryLength = length;
    }

    /// <summary>The modes a number type can hold, as upstream's <c>mode_is_valid</c> checks before any page.</summary>
    private static void RequireValidMode(PcoModeKind mode, ulong modeBase, PcoNumber number)
    {
        bool valid = mode switch
        {
            PcoModeKind.IntMult => number.Kind != PcoNumberKind.Float && modeBase > 0,
            PcoModeKind.FloatMult => number.Kind == PcoNumberKind.Float && IsFiniteNonZero(number.FromLatentOrdered(modeBase), number),
            PcoModeKind.FloatQuant => number.Kind == PcoNumberKind.Float && modeBase > 0 && modeBase <= (ulong)number.PrecisionBits,
            _ => true,
        };

        if (!valid)
        {
            CompressedThrow.Format(
                $"A pco chunk of {number.LatentBits}-bit {number.Kind} numbers declares mode {mode} with base {modeBase}, which it cannot hold.");
        }
    }

    /// <summary>A float's bits, other than a zero, an infinity or a NaN: its exponent short of all ones and its magnitude not zero.</summary>
    private static bool IsFiniteNonZero(ulong bits, PcoNumber number)
    {
        ulong magnitude = bits & (number.Mid - 1);
        ulong infinity = (number.Mid - 1) & ~((1UL << number.PrecisionBits) - 1);
        return magnitude != 0 && (magnitude & infinity) != infinity;
    }

    /// <summary>Upstream's <c>ChunkMeta::new</c> check: every lookback bin starts inside the window.</summary>
    private static void RequireLookbacksInWindow(PcoLatentVar lookbacks, long window)
    {
        foreach (PcoBin bin in lookbacks.Bins)
        {
            if (bin.Lower < 1 || bin.Lower > (ulong)window)
            {
                CompressedThrow.Format(
                    $"A pco lookback bin starts at {bin.Lower}, outside the window [1, {window}].");
            }
        }
    }

    /// <summary>
    /// Upstream's <c>ChunkMeta::new</c> check on a convolution: latents of 32 bits at most, and
    /// weights and bias that cannot overflow the signed type a prediction is summed in, twice the
    /// latent's width -- which is what lets a prediction be summed in 64 bits here whatever the width.
    /// </summary>
    private void RequireConv1Bounded(int latentBits)
    {
        if (latentBits > 32)
        {
            CompressedThrow.Format("A pco convolution delta is declared over latents wider than 32 bits.");
        }

        int convBits = latentBits * 2;
        if (Conv1Quantization > Math.Min(31, convBits - 1))
        {
            CompressedThrow.Format($"A pco convolution's quantization {Conv1Quantization} exceeds the maximum.");
        }

        double weightSum = 0;
        foreach (long weight in Conv1Weights)
        {
            weightSum += Math.Abs((double)weight);
        }

        double maxPrediction = Math.Abs((double)Conv1Bias) + (Math.Pow(2, latentBits) * weightSum);
        if (maxPrediction >= Math.Pow(2, convBits - 1))
        {
            CompressedThrow.Format(
                $"A pco convolution's weights and bias risk overflowing as high as {maxPrediction}.");
        }
    }

    private PcoLatentVar ReadLatentVar(ref PcoBitReader reader, int latentBits, int slot)
    {
        int ansSizeLog = (int)reader.ReadUInt(AnsSizeLogBits);
        int binCount = (int)reader.ReadUInt(BinCountBits);

        // Upstream's three checks, kept because each one bounds something a later stage indexes.
        if ((1L << ansSizeLog) < binCount)
        {
            CompressedThrow.Format(
                $"A pco latent variable has ANS size log {ansSizeLog}, too small for {binCount} bins.");
        }

        if (binCount == 1 && ansSizeLog > 0)
        {
            CompressedThrow.Format(
                $"A pco latent variable has one bin but ANS size log {ansSizeLog}; zero was required.");
        }

        if (ansSizeLog > MaxAnsBits)
        {
            CompressedThrow.Format(
                $"A pco latent variable has ANS size log {ansSizeLog}, above the maximum {MaxAnsBits}.");
        }

        int offsetBitsBits = BitsToEncodeOffsetBits(latentBits);
        PcoBin[] bins = _bins[slot];
        if (bins.Length < binCount)
        {
            _bins[slot] = bins = new PcoBin[binCount];
        }

        for (int i = 0; i < binCount; i++)
        {
            uint weight = (uint)reader.ReadUInt(ansSizeLog) + 1;
            ulong lower = reader.ReadUInt(latentBits);
            int offsetBits = (int)reader.ReadUInt(offsetBitsBits);
            if (offsetBits > latentBits)
            {
                CompressedThrow.Format(
                    $"A pco bin declares {offsetBits} offset bits, above the latent's {latentBits}.");
            }

            bins[i] = new PcoBin(weight, lower, offsetBits);
        }

        // The table is a property of the chunk and not of a page, so it is built here, with the
        // metadata it depends on. Building it spreads every symbol across the table and walks
        // every slot, a cost that does not shrink with the page, so rebuilding it per page would
        // dominate a scan for no change in the result. The pages only read it, so they share it.
        return new PcoLatentVar(
            ansSizeLog, bins, binCount, _tables[slot].Refill(ansSizeLog, bins.AsSpan(0, binCount)), latentBits);
    }

    /// <summary>Bits needed to encode a value up to <paramref name="latentBits"/>.</summary>
    /// <remarks><c>bits_to_encode_offset_bits</c>: 7 for a 64-bit latent, 6 for 32, and so on.</remarks>
    private static int BitsToEncodeOffsetBits(int latentBits) =>
        32 - System.Numerics.BitOperations.LeadingZeroCount((uint)latentBits);
}
