using System;

using Vorticity.Arrays.Decoders.Canonical;

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
/// <param name="BinStorage">The bins, in wire order, from the start of an array that may be longer.</param>
/// <param name="BinCount">How many bins there are.</param>
/// <param name="Table">
/// The tANS decoding table for these bins, built once per chunk.
/// </param>
internal readonly record struct PcoLatentVar(int AnsSizeLog, PcoBin[] BinStorage, int BinCount, PcoAnsTable Table)
{
    /// <summary>The bins, in wire order.</summary>
    internal ReadOnlySpan<PcoBin> Bins => BinStorage.AsSpan(0, BinCount);
}

/// <summary>
/// A pco chunk's metadata: a format version, a mode, a delta encoding, then one latent-variable
/// table for each latent the mode and delta encoding imply, each table an ANS size and a list of
/// bins. It is one continuous bit stream with no alignment between sections, so the format
/// version's leading bytes are the only byte-aligned part of it.
/// </summary>
/// <remarks>
/// Refilled chunk after chunk, in the bins and tables it already holds, rather than read into new
/// ones: a table runs to a few hundred kilobytes and every chunk of a column has one per latent
/// variable. What a refill gives replaces what the last one gave.
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

    /// <summary>One slot per latent variable, the delta's, the primary's and the secondary's.</summary>
    private readonly PcoBin[][] _bins = [[], [], []];

    private readonly PcoAnsTable[] _tables = [new PcoAnsTable(), new PcoAnsTable(), new PcoAnsTable()];

    /// <summary>pco format major version.</summary>
    internal int FormatMajor { get; private set; }

    /// <summary>pco format minor version; zero before major 4.</summary>
    internal int FormatMinor { get; private set; }

    /// <summary>The mode.</summary>
    internal PcoModeKind Mode { get; private set; }

    /// <summary>The mode's base, for the multiplying modes; zero otherwise.</summary>
    internal ulong ModeBase { get; private set; }

    /// <summary>The delta encoding.</summary>
    internal PcoDeltaKind Delta { get; private set; }

    /// <summary>Consecutive delta order; zero otherwise.</summary>
    internal int DeltaOrder { get; private set; }

    /// <summary>Whether the secondary latent is itself delta-encoded.</summary>
    internal bool SecondaryUsesDelta { get; private set; }

    /// <summary>The delta latent's table, present only for lookback deltas.</summary>
    internal PcoLatentVar? DeltaLatent { get; private set; }

    /// <summary>The primary latent's table.</summary>
    internal PcoLatentVar Primary { get; private set; }

    /// <summary>The secondary latent's table, absent in Classic mode.</summary>
    internal PcoLatentVar? Secondary { get; private set; }

    /// <summary>Reads a chunk's metadata, the pco header first, into new metadata.</summary>
    /// <param name="header">pco's file header bytes.</param>
    /// <param name="chunkMeta">The chunk's metadata bytes.</param>
    /// <param name="latentBits">Bits per latent: the width of the column's physical type.</param>
    /// <returns>The parsed metadata.</returns>
    internal static PcoChunkMeta Read(ReadOnlySpan<byte> header, ReadOnlySpan<byte> chunkMeta, int latentBits) =>
        new PcoChunkMeta().Refill(header, chunkMeta, latentBits);

    /// <summary>Reads a chunk's metadata, the pco header first, into this one.</summary>
    /// <param name="header">pco's file header bytes.</param>
    /// <param name="chunkMeta">The chunk's metadata bytes.</param>
    /// <param name="latentBits">Bits per latent: the width of the column's physical type.</param>
    /// <returns>This metadata.</returns>
    /// <remarks>
    /// The header and the chunk metadata are separate buffers in the Vortex node but one stream to
    /// pco, so they are concatenated here rather than read independently.
    /// </remarks>
    internal PcoChunkMeta Refill(ReadOnlySpan<byte> header, ReadOnlySpan<byte> chunkMeta, int latentBits)
    {
        int joinedLength = header.Length + chunkMeta.Length;
        Span<byte> stack = stackalloc byte[256];
        using Scratch<byte> joinedScratch = new Scratch<byte>(joinedLength, stack);
        Span<byte> joined = joinedScratch.Span[..joinedLength];
        header.CopyTo(joined);
        chunkMeta.CopyTo(joined[header.Length..]);

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
            ? ReadLatentVar(ref reader, 32, 0)
            : null;

        PcoLatentVar primary = ReadLatentVar(ref reader, latentBits, 1);

        // Classic maps one latent to the number; every multiplying mode needs a second.
        PcoLatentVar? secondary = mode == PcoModeKind.Classic
            ? null
            : ReadLatentVar(ref reader, latentBits, 2);

        FormatMajor = major;
        FormatMinor = minor;
        Mode = mode;
        ModeBase = modeBase;
        Delta = delta;
        DeltaOrder = deltaOrder;
        SecondaryUsesDelta = secondaryUsesDelta;
        DeltaLatent = deltaLatent;
        Primary = primary;
        Secondary = secondary;
        return this;
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
        return new PcoLatentVar(ansSizeLog, bins, binCount, _tables[slot].Refill(ansSizeLog, bins.AsSpan(0, binCount)));
    }

    /// <summary>Bits needed to encode a value up to <paramref name="latentBits"/>.</summary>
    /// <remarks><c>bits_to_encode_offset_bits</c>: 7 for a 64-bit latent, 6 for 32, and so on.</remarks>
    private static int BitsToEncodeOffsetBits(int latentBits) =>
        32 - System.Numerics.BitOperations.LeadingZeroCount((uint)latentBits);
}
