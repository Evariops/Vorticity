// pco chunk metadata - pco-1.0.3/src/metadata/{chunk,mode,delta_encoding,chunk_latent_var,bin}.rs.
//
// One continuous bit stream, LSB first, with NO alignment between sections: upstream's
// `BitReaderBuilder::with_reader` hands out a fresh reader per section but carries the bit index
// across, so the format version's two aligned bytes are the only byte-aligned thing here.
//
// The shape is: a format version, a mode, a delta encoding, then one latent-variable table per
// latent the mode and delta encoding say exist - optionally delta, then primary, then optionally
// secondary. Each table is an ANS size and a list of bins, and each bin is a weight, a lower bound
// and a count of offset bits.
using System;

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
}

/// <summary>One bin of a latent variable's table.</summary>
/// <param name="Weight">ANS weight; always at least one.</param>
/// <param name="Lower">The bin's lower bound.</param>
/// <param name="OffsetBits">Bits of offset stored per value in this bin.</param>
internal readonly record struct PcoBin(uint Weight, ulong Lower, int OffsetBits);

/// <summary>One latent variable's table.</summary>
/// <param name="AnsSizeLog">Log2 of the ANS table size; zero when there is a single bin.</param>
/// <param name="Bins">The bins, in wire order.</param>
/// <param name="Table">
/// The tANS decoding table for these bins, built ONCE PER CHUNK.
/// </param>
internal readonly record struct PcoLatentVar(int AnsSizeLog, PcoBin[] Bins, PcoAnsTable Table);

/// <summary>A pco chunk's metadata.</summary>
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

    private PcoChunkMeta(
        int formatMajor,
        int formatMinor,
        PcoModeKind mode,
        ulong modeBase,
        PcoDeltaKind delta,
        int deltaOrder,
        bool secondaryUsesDelta,
        PcoLatentVar? deltaLatent,
        PcoLatentVar primary,
        PcoLatentVar? secondary)
    {
        FormatMajor = formatMajor;
        FormatMinor = formatMinor;
        Mode = mode;
        ModeBase = modeBase;
        Delta = delta;
        DeltaOrder = deltaOrder;
        SecondaryUsesDelta = secondaryUsesDelta;
        DeltaLatent = deltaLatent;
        Primary = primary;
        Secondary = secondary;
    }

    /// <summary>pco format major version.</summary>
    internal int FormatMajor { get; }

    /// <summary>pco format minor version; zero before major 4.</summary>
    internal int FormatMinor { get; }

    /// <summary>The mode.</summary>
    internal PcoModeKind Mode { get; }

    /// <summary>The mode's base, for the multiplying modes; zero otherwise.</summary>
    internal ulong ModeBase { get; }

    /// <summary>The delta encoding.</summary>
    internal PcoDeltaKind Delta { get; }

    /// <summary>Consecutive delta order; zero otherwise.</summary>
    internal int DeltaOrder { get; }

    /// <summary>Whether the secondary latent is itself delta-encoded.</summary>
    internal bool SecondaryUsesDelta { get; }

    /// <summary>The delta latent's table, present only for lookback deltas.</summary>
    internal PcoLatentVar? DeltaLatent { get; }

    /// <summary>The primary latent's table.</summary>
    internal PcoLatentVar Primary { get; }

    /// <summary>The secondary latent's table, absent in Classic mode.</summary>
    internal PcoLatentVar? Secondary { get; }

    /// <summary>Reads a chunk's metadata, the pco header first.</summary>
    /// <param name="header">pco's file header bytes.</param>
    /// <param name="chunkMeta">The chunk's metadata bytes.</param>
    /// <param name="latentBits">Bits per latent: the width of the column's physical type.</param>
    /// <returns>The parsed metadata.</returns>
    /// <remarks>
    /// The header and the chunk metadata are separate buffers in the Vortex node but one stream to
    /// pco, so they are concatenated here rather than read independently.
    /// </remarks>
    internal static PcoChunkMeta Read(ReadOnlySpan<byte> header, ReadOnlySpan<byte> chunkMeta, int latentBits)
    {
        byte[] joined = new byte[header.Length + chunkMeta.Length];
        header.CopyTo(joined);
        chunkMeta.CopyTo(joined.AsSpan(header.Length));

        PcoBitReader reader = new PcoBitReader(joined);

        int major = reader.ReadAlignedBytes(1)[0];
        int minor = major >= 4 ? reader.ReadAlignedBytes(1)[0] : 0;

        int modeVariant = (int)reader.ReadUInt(ModeVariantBits);
        ulong modeBase = 0;
        PcoModeKind mode;
        switch (modeVariant)
        {
            case 0:
                mode = PcoModeKind.Classic;
                break;
            case 1:
                mode = PcoModeKind.IntMult;
                modeBase = reader.ReadUInt(latentBits);
                break;
            case 2:
                mode = PcoModeKind.FloatMult;
                modeBase = reader.ReadUInt(latentBits);
                break;
            case 3:
                mode = PcoModeKind.FloatQuant;
                modeBase = reader.ReadUInt(BitsToEncodeOffsetBits(latentBits));
                break;
            default:
                mode = PcoModeKind.Classic;
                CompressedThrow.Format($"A pco chunk declares mode variant {modeVariant}, which is not defined.");
                break;
        }

        int deltaVariant = (int)reader.ReadUInt(DeltaVariantBits);
        PcoDeltaKind delta;
        int deltaOrder = 0;
        bool secondaryUsesDelta = false;
        switch (deltaVariant)
        {
            case 0:
                delta = PcoDeltaKind.NoOp;
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
                int windowLog = 1 + (int)reader.ReadUInt(LookbackWindowBits);
                int stateLog = (int)reader.ReadUInt(LookbackStateBits);
                if (stateLog > windowLog)
                {
                    CompressedThrow.Format(
                        $"A pco lookback delta's state log {stateLog} exceeds its window log {windowLog}.");
                }

                break;
            default:
                delta = PcoDeltaKind.NoOp;
                CompressedThrow.Format($"A pco chunk declares delta variant {deltaVariant}, which is not defined.");
                break;
        }

        // Only a lookback delta carries a latent variable of its own, and it is always u32.
        PcoLatentVar? deltaLatent = delta == PcoDeltaKind.Lookback
            ? ReadLatentVar(ref reader, 32)
            : null;

        PcoLatentVar primary = ReadLatentVar(ref reader, latentBits);

        // Classic maps one latent to the number; every multiplying mode needs a second.
        PcoLatentVar? secondary = mode == PcoModeKind.Classic
            ? null
            : ReadLatentVar(ref reader, latentBits);

        return new PcoChunkMeta(
            major, minor, mode, modeBase, delta, deltaOrder, secondaryUsesDelta,
            deltaLatent, primary, secondary);
    }

    private static PcoLatentVar ReadLatentVar(ref PcoBitReader reader, int latentBits)
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
        PcoBin[] bins = new PcoBin[binCount];
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

        // THE TABLE IS A PROPERTY OF THE CHUNK, NOT OF A PAGE, and it used to be rebuilt for every
        // page of every latent variable: `dotnet-trace` put `PcoAnsTable.Build` at 83.7% of a
        // 1M-row `vortex.pco` scan. It spreads up to 16 384 symbols across the table and walks
        // every slot, which is a fixed cost per build and therefore entirely wasted when the bins
        // it is built from have not changed. Building it here ties it to the metadata it actually
        // depends on. The table is immutable, so sharing it across pages needs no copy.
        return new PcoLatentVar(ansSizeLog, bins, PcoAnsTable.Build(ansSizeLog, bins));
    }

    /// <summary>Bits needed to encode a value up to <paramref name="latentBits"/>.</summary>
    /// <remarks><c>bits_to_encode_offset_bits</c>: 7 for a 64-bit latent, 6 for 32, and so on.</remarks>
    private static int BitsToEncodeOffsetBits(int latentBits) =>
        32 - System.Numerics.BitOperations.LeadingZeroCount((uint)latentBits);
}
