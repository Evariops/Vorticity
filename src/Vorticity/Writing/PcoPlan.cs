using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed.Pco;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// A column chunk compressed as <c>vortex.pco</c>: its valid values as pco's latents, in chunks of
/// up to 2^18 and pages of up to 8 192, each chunk's mode, delta and bins chosen as pco's own
/// compressor chooses them at its default level, the bytes of every chunk's metadata and every
/// page written back to back into one rental.
/// </summary>
/// <remarks>
/// <para>
/// What is chosen is upstream's automatic choice, less what it rarely takes: an integer column's
/// mode is classic or, when pco's test on a sample finds a common divisor, its multiples
/// (<c>IntMult</c>); a float's is classic. The delta is none or a consecutive one of the order a
/// sample prices best, as pco's <c>choose_auto_delta_encoding</c> tries them; the lookback it also
/// tries is not written, and neither are the float modes, whose values ALP and ALP-RD already
/// take.
/// </para>
/// <para>
/// Everything is rented: the latents, a secondary, a sorting copy and a page's symbols, handed back
/// when the plan is built or refused; the bytes are the plan's until the writer takes them. The
/// trial on a sample reuses the same buffers, so pricing a column allocates nothing per column.
/// </para>
/// </remarks>
internal sealed class PcoPlan
{
    /// <summary>Values per pco chunk: upstream's <c>VALUES_PER_CHUNK</c>, pco's default page limit.</summary>
    internal const int ValuesPerChunk = 1 << 18;

    /// <summary>Values per page, as Vortex's compact compressor writes them.</summary>
    internal const int ValuesPerPage = 8192;

    /// <summary>pco's default compression level, which sets the histogram's size.</summary>
    private const int CompressionLevel = 8;

    /// <summary>A secondary latent's histogram is never finer than this: <c>LIMITED_UNOPTIMIZED_BINS_LOG</c>.</summary>
    private const int SecondaryBinsLog = 6;

    /// <summary>A delta encoding's metadata at its largest, in bits: <c>DeltaEncoding::MAX_BIT_SIZE</c>.</summary>
    private const int DeltaMaxBits = 4 + 5 + 5 + 64 + (32 * 32);

    private const int DeltaGroupSize = 200;
    private const int ValuesPerExtraDeltaGroup = 10_000;
    private const int MaxDeltaOrder = 7;

    /// <summary>pco's wrapped header: format version 4.1.</summary>
    internal static ReadOnlySpan<byte> Header => [4, 1];

    private byte[]? _data;
    private int[]? _buffers;
    private int[]? _pageValues;
    private int[]? _chunkPages;

    private PcoPlan(byte[] data, int[] buffers, int[] pageValues, int[] chunkPages, int chunkCount, int pageCount, long bytes)
    {
        _data = data;
        _buffers = buffers;
        _pageValues = pageValues;
        _chunkPages = chunkPages;
        ChunkCount = chunkCount;
        PageCount = pageCount;
        EncodedSize = bytes;
    }

    /// <summary>pco chunks, whose metadata lead the node's buffers.</summary>
    internal int ChunkCount { get; }

    /// <summary>Pages across every chunk, which follow the metadata.</summary>
    internal int PageCount { get; }

    /// <summary>The bytes of every buffer together.</summary>
    internal long EncodedSize { get; }

    /// <summary>
    /// What the plan costs the file: its buffers, and the framing a buffer a page brings -- each
    /// buffer's descriptor in the node, each page's count in the metadata. A trial is compared by
    /// its buffers, and pco's pages make many of them: a column that compresses to little has as
    /// much in the framing as in the pages.
    /// </summary>
    internal long Price => EncodedSize + Framing(ChunkCount, PageCount);

    /// <summary>The framing of <paramref name="chunks"/> chunks of <paramref name="pages"/> pages: an eight-byte descriptor a buffer, about five bytes of metadata a page and two a chunk, and the header.</summary>
    internal static long Framing(int chunks, int pages) => (8L * (chunks + pages)) + (5L * pages) + (2L * chunks) + 4;

    /// <summary>Each page's value count, in order.</summary>
    internal ReadOnlySpan<int> PageValues => _pageValues.AsSpan(0, PageCount);

    /// <summary>Each chunk's page count, in order.</summary>
    internal ReadOnlySpan<int> ChunkPages => _chunkPages.AsSpan(0, ChunkCount);

    /// <summary>Buffer <paramref name="index"/>'s place in <see cref="TakeData"/>'s bytes: the chunk metadata first, then the pages.</summary>
    internal (int Start, int Length) BufferAt(int index) => (_buffers![2 * index], _buffers[(2 * index) + 1]);

    /// <summary>The bytes every buffer is cut from; the caller returns them to the pool, and the plan holds nothing after.</summary>
    internal byte[] TakeData()
    {
        byte[] data = _data!;
        _data = null;
        return data;
    }

    /// <summary>Gives back what the plan still holds.</summary>
    internal void Release()
    {
        if (_data is { } data)
        {
            _data = null;
            ArrayPool<byte>.Shared.Return(data);
        }

        if (_buffers is { } buffers)
        {
            _buffers = null;
            ArrayPool<int>.Shared.Return(buffers);
        }

        if (_pageValues is { } pages)
        {
            _pageValues = null;
            ArrayPool<int>.Shared.Return(pages);
        }

        if (_chunkPages is { } chunks)
        {
            _chunkPages = null;
            ArrayPool<int>.Shared.Return(chunks);
        }
    }

    /// <summary>Whether pco stores <paramref name="node"/>'s values: a primitive of 16 bits or more.</summary>
    internal static bool Stores(in CanonicalNode node) =>
        node.Kind == CanonicalKind.Primitive && PcoNumber.Of(node.PType) is not null;

    /// <summary>The chunk compressed, when its <see cref="Price"/> comes in under <paramref name="ceiling"/>.</summary>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="nodeIndex">A primitive node of 16 bits or more.</param>
    /// <param name="ceiling">The bytes to beat; <see cref="long.MaxValue"/> for a pin.</param>
    internal static PcoPlan? TryBuild(CanonicalArena arena, int nodeIndex, long ceiling)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        if (node.Kind != CanonicalKind.Primitive || PcoNumber.Of(node.PType) is not { } number)
        {
            return null;
        }

        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        int rows = node.Length;
        int valid = mask.AllValid ? rows : mask.AllInvalid ? 0 : BitmapKernels.CountSet(mask.Bits, mask.BitOffset, rows);

        Builder builder = new Builder(number, valid, ceiling);
        try
        {
            builder.Collect(node.Values.Span, in mask, rows);
            return builder.Encode();
        }
        finally
        {
            builder.Dispose();
        }
    }

    /// <summary>The rentals one build works in, and the steps that fill them.</summary>
    private sealed class Builder : IDisposable
    {
        private readonly PcoNumber _number;
        private readonly int _count;
        private readonly int _bits;
        private readonly ulong _mask;
        private readonly ulong _mid;
        private readonly ulong[] _latents;
        private readonly ulong[] _secondary;
        private readonly ulong[] _sorted;
        private readonly PcoBinTrainer _primaryBins = new PcoBinTrainer();
        private readonly PcoBinTrainer _secondaryBins = new PcoBinTrainer();
        private readonly PcoAnsEncoder _primaryEncoder = new PcoAnsEncoder();
        private readonly PcoAnsEncoder _secondaryEncoder = new PcoAnsEncoder();
        private readonly PageScratch _primaryPage = new PageScratch();
        private readonly PageScratch _secondaryPage = new PageScratch();
        private readonly PcoBitWriter _writer;
        private readonly long _ceiling;
        private int[]? _buffers;
        private int[]? _pageValues;
        private int[]? _chunkPages;
        private ulong[]? _moments;
        private long _abandonAbove = long.MaxValue;

        internal Builder(PcoNumber number, int count, long ceiling)
        {
            _number = number;
            _count = count;
            _bits = number.LatentBits;
            _mask = number.Mask;
            _mid = number.Mid;
            _latents = ArrayPool<ulong>.Shared.Rent(Math.Max(count, 1));
            _secondary = ArrayPool<ulong>.Shared.Rent(Math.Max(Math.Min(count, ValuesPerChunk), 1));
            _sorted = ArrayPool<ulong>.Shared.Rent(Math.Max(Math.Min(count, ValuesPerChunk), 1));

            // The bytes pco must come in under, when there is a ceiling, and never more than the
            // values themselves: the buffer grows past that only for a pin on values pco inflates.
            _ceiling = ceiling;
            long plain = ((long)count * number.LatentBits / 8) + 256;
            _writer = new PcoBitWriter((int)Math.Min(Math.Min(ceiling, plain), int.MaxValue / 2));
        }

        /// <summary>The valid rows' values as latents, <c>to_latent_ordered</c>, back to back.</summary>
        internal void Collect(ReadOnlySpan<byte> values, in ValidityMask mask, int rows)
        {
            Span<ulong> latents = _latents.AsSpan(0, _count);
            int at = 0;
            bool all = mask.AllValid;
            switch (_bits)
            {
                case 16:
                {
                    ReadOnlySpan<ushort> typed = MemoryMarshal.Cast<byte, ushort>(values)[..rows];
                    for (int row = 0; row < rows && at < latents.Length; row++)
                    {
                        if (all || mask.IsValid(row))
                        {
                            latents[at++] = Latent(typed[row]);
                        }
                    }

                    break;
                }

                case 32:
                {
                    ReadOnlySpan<uint> typed = MemoryMarshal.Cast<byte, uint>(values)[..rows];
                    for (int row = 0; row < rows && at < latents.Length; row++)
                    {
                        if (all || mask.IsValid(row))
                        {
                            latents[at++] = Latent(typed[row]);
                        }
                    }

                    break;
                }

                default:
                {
                    ReadOnlySpan<ulong> typed = MemoryMarshal.Cast<byte, ulong>(values)[..rows];
                    for (int row = 0; row < rows && at < latents.Length; row++)
                    {
                        if (all || mask.IsValid(row))
                        {
                            latents[at++] = Latent(typed[row]);
                        }
                    }

                    break;
                }
            }
        }

        /// <summary>A number's bits as its latent: unsigned as is, signed recentred, a float ordered.</summary>
        private ulong Latent(ulong bits) => _number.Kind switch
        {
            PcoNumberKind.Signed => (bits ^ _mid) & _mask,
            PcoNumberKind.Float => _number.FloatToLatentOrdered(bits),
            _ => bits,
        };

        /// <summary>Every chunk written, or null the moment the bytes pass the ceiling.</summary>
        internal PcoPlan? Encode()
        {
            long ceiling = _ceiling;
            int chunks = (_count + ValuesPerChunk - 1) / ValuesPerChunk;
            int pages = 0;
            for (int start = 0; start < _count; start += ValuesPerChunk)
            {
                pages += (Math.Min(ValuesPerChunk, _count - start) + ValuesPerPage - 1) / ValuesPerPage;
            }

            _buffers = ArrayPool<int>.Shared.Rent(Math.Max(2 * (chunks + pages), 1));
            _pageValues = ArrayPool<int>.Shared.Rent(Math.Max(pages, 1));
            _chunkPages = ArrayPool<int>.Shared.Rent(Math.Max(chunks, 1));
            _moments = ArrayPool<ulong>.Shared.Rent(MaxDeltaOrder * Math.Max(pages, 1));

            int page = 0;
            int chunk = 0;
            long framing = Framing(chunks, pages);

            // A quarter over: the sample's average is an estimate, and one that misses by a fifth
            // must not refuse a column pco would have won.
            _abandonAbove = ceiling == long.MaxValue ? long.MaxValue : ceiling + (ceiling / 4);
            for (int start = 0; start < _count; start += ValuesPerChunk)
            {
                int n = Math.Min(ValuesPerChunk, _count - start);
                page = EncodeChunk(_latents.AsSpan(start, n), chunk, chunks, page);
                chunk++;
                if (page < 0 || _writer.Length + framing >= ceiling)
                {
                    return null;
                }
            }

            int bytes = _writer.Length;
            PcoPlan plan = new PcoPlan(_writer.TakeBuffer(), _buffers, _pageValues, _chunkPages, chunks, pages, bytes);
            _buffers = null;
            _pageValues = null;
            _chunkPages = null;
            return plan;
        }

        /// <summary>One chunk: its mode, its delta, its bins, its metadata and its pages.</summary>
        /// <returns>The next page's index; -1 when the sample prices the chunk past any ceiling it could meet.</returns>
        private int EncodeChunk(Span<ulong> latents, int chunk, int chunks, int firstPage)
        {
            int n = latents.Length;

            // The mode, and the latents split by it: an integer's multiples of a common divisor and
            // what is left of each, or the latents as they are.
            ulong modeBase = _number.Kind == PcoNumberKind.Float ? 0 : ChooseBase(latents);
            Span<ulong> secondary = default;
            if (modeBase > 0)
            {
                secondary = _secondary.AsSpan(0, n);
                for (int i = 0; i < n; i++)
                {
                    ulong latent = latents[i];
                    latents[i] = latent / modeBase;
                    secondary[i] = latent % modeBase;
                }
            }

            int binsLog = PcoBinTrainer.UnoptimizedBinsLog(CompressionLevel, n);
            int order = ChooseDeltaOrder(latents, binsLog, out double bitsPerLatent);

            // What the sample says the primary will take, before any of it is written: a column pco
            // cannot win by a margin is refused here, the pass that bins and writes it spared.
            // Only a classic chunk is judged -- a divisor's second latent the sample does not price.
            if (modeBase == 0 && (long)(n * bitsPerLatent / 8) > _abandonAbove)
            {
                return -1;
            }

            // Pages of as equal a size as the limit allows, each delta-encoded on its own, its
            // moments kept for its metadata.
            int pageCount = (n + ValuesPerPage - 1) / ValuesPerPage;
            int low = n / pageCount;
            int longer = n % pageCount;
            Span<ulong> moments = _moments.AsSpan(firstPage * MaxDeltaOrder, pageCount * MaxDeltaOrder);
            int stored = 0;
            for (int p = 0, start = 0; p < pageCount; p++)
            {
                int pageN = low + (p < longer ? 1 : 0);
                _pageValues![firstPage + p] = pageN;
                Span<ulong> pageLatents = latents.Slice(start, pageN);
                DeltaEncode(pageLatents, order, moments.Slice(p * MaxDeltaOrder, MaxDeltaOrder));
                Span<ulong> kept = pageLatents[Math.Min(order, pageN)..];
                kept.CopyTo(_sorted.AsSpan(stored));
                stored += kept.Length;
                start += pageN;
            }

            _primaryBins.Train(_sorted.AsSpan(0, stored), binsLog, _bits);
            _primaryBins.PrepareLookup();
            _primaryEncoder.Refill(_primaryBins.AnsSizeLog, _primaryBins.Weights);
            if (modeBase > 0)
            {
                secondary.CopyTo(_sorted);
                _secondaryBins.Train(_sorted.AsSpan(0, n), Math.Min(binsLog, SecondaryBinsLog), _bits);
                _secondaryBins.PrepareLookup();
                _secondaryEncoder.Refill(_secondaryBins.AnsSizeLog, _secondaryBins.Weights);
            }

            // The chunk's metadata, then its pages.
            int metaStart = _writer.Length;
            WriteChunkMeta(modeBase, order);
            int metaEnd = _writer.Align();
            _buffers![2 * chunk] = metaStart;
            _buffers[(2 * chunk) + 1] = metaEnd - metaStart;
            _chunkPages![chunk] = pageCount;

            for (int p = 0, start = 0; p < pageCount; p++)
            {
                int pageN = _pageValues![firstPage + p];
                int pageStart = _writer.Length;
                WritePage(
                    latents.Slice(start, pageN), modeBase > 0 ? secondary.Slice(start, pageN) : default, order,
                    moments.Slice(p * MaxDeltaOrder, order), modeBase > 0);
                int pageEnd = _writer.Align();
                int buffer = chunks + firstPage + p;
                _buffers[2 * buffer] = pageStart;
                _buffers[(2 * buffer) + 1] = pageEnd - pageStart;
                start += pageN;
            }

            return firstPage + pageCount;
        }

        /// <summary>
        /// A consecutive delta of <paramref name="order"/> over one page, in place,
        /// <c>consecutive::encode_in_place</c>: each order keeps the first latent as its moment and
        /// replaces the rest by their differences, and the differences left are recentred.
        /// </summary>
        private void DeltaEncode(Span<ulong> latents, int order, Span<ulong> moments)
        {
            ulong mask = _mask;
            Span<ulong> rest = latents;
            for (int o = 0; o < order; o++)
            {
                moments[o] = rest.IsEmpty ? 0 : rest[0];
                for (int i = rest.Length - 1; i >= 1; i--)
                {
                    rest[i] = unchecked(rest[i] - rest[i - 1]) & mask;
                }

                rest = rest[Math.Min(1, rest.Length)..];
            }

            if (order > 0)
            {
                ulong mid = _mid;
                for (int i = 0; i < rest.Length; i++)
                {
                    rest[i] = unchecked(rest[i] + mid) & mask;
                }
            }
        }

        /// <summary>
        /// The consecutive order a sample of the primary prices best, <c>choose_auto_delta_encoding</c>:
        /// none, then each order in turn while the next is cheaper.
        /// </summary>
        private int ChooseDeltaOrder(ReadOnlySpan<ulong> primary, int binsLog, out double bitsPerLatent)
        {
            int n = primary.Length;
            int extraGroups = 1 + (n / ValuesPerExtraDeltaGroup);
            int nominal = (extraGroups + 1) * DeltaGroupSize;
            int padding = Math.Max(0, n - nominal) / extraGroups;

            // The sample and the copy each candidate delta-encodes, rented for the choice alone.
            ulong[] sampleArray = ArrayPool<ulong>.Shared.Rent(Math.Min(nominal, n) + 1);
            ulong[] work = ArrayPool<ulong>.Shared.Rent(Math.Min(nominal, n) + 1);
            try
            {
                int sampled = 0;
                int take = Math.Min(DeltaGroupSize, n);
                primary[..take].CopyTo(sampleArray);
                sampled += take;
                int i = DeltaGroupSize;
                for (int g = 0; g < extraGroups; g++)
                {
                    i += padding;
                    if (i < n)
                    {
                        int group = Math.Min(DeltaGroupSize, n - i);
                        primary.Slice(i, group).CopyTo(sampleArray.AsSpan(sampled));
                        sampled += group;
                    }

                    i += DeltaGroupSize;
                }

                ReadOnlySpan<ulong> sample = sampleArray.AsSpan(0, sampled);
                long best = SampleCost(sample, work, 0, binsLog);
                bitsPerLatent = _primaryBins.AverageBitsPerLatent;
                int bestOrder = 0;
                for (int order = 1; order <= MaxDeltaOrder; order++)
                {
                    long cost = SampleCost(sample, work, order, binsLog);
                    if (cost >= best)
                    {
                        break;
                    }

                    best = cost;
                    bestOrder = order;
                    bitsPerLatent = _primaryBins.AverageBitsPerLatent;
                }

                return bestOrder;
            }
            finally
            {
                ArrayPool<ulong>.Shared.Return(work);
                ArrayPool<ulong>.Shared.Return(sampleArray);
            }
        }

        /// <summary>
        /// A sample's bytes as one classic page under a delta of <paramref name="order"/>,
        /// <c>calculate_compressed_sample_size</c>: the chunk metadata at its largest, and the page's
        /// metadata and its body at the bins' average bits a latent.
        /// </summary>
        private long SampleCost(ReadOnlySpan<ulong> sample, ulong[] work, int order, int binsLog)
        {
            Span<ulong> latents = work.AsSpan(0, sample.Length);
            sample.CopyTo(latents);
            Span<ulong> moments = stackalloc ulong[MaxDeltaOrder];
            DeltaEncode(latents, order, moments);
            Span<ulong> stored = latents[Math.Min(order, latents.Length)..];
            _primaryBins.Train(stored, binsLog, _bits);

            int bins = _primaryBins.Count;
            int sizeLog = _primaryBins.AnsSizeLog;
            long metaBits = 4 + DeltaMaxBits + 4 + 15 + ((long)bins * (sizeLog + _bits + PcoBinTrainer.OffsetBitsBits(_bits)));
            long pageMeta = ((sizeLog * 4) + ((long)_bits * order) + 7) / 8;
            long body = ((long)Math.Ceiling(stored.Length * _primaryBins.AverageBitsPerLatent) + 7) / 8;
            return ((metaBits + 7) / 8) + pageMeta + body;
        }

        /// <summary>
        /// The common divisor an integer chunk's latents are multiples of, or zero:
        /// <c>int_mult::choose_base</c>, over pco's own sample of them.
        /// </summary>
        private ulong ChooseBase(ReadOnlySpan<ulong> latents)
        {
            int n = latents.Length;
            if (n < PcoIntMult.MinSample)
            {
                return 0;
            }

            int target = PcoIntMult.MinSample + ((n - PcoIntMult.MinSample) / PcoIntMult.SampleRatio);
            ulong[] sample = ArrayPool<ulong>.Shared.Rent(target);
            byte[] visited = ArrayPool<byte>.Shared.Rent((n + 7) / 8);
            try
            {
                int sampled = PcoIntMult.Sample(latents, sample.AsSpan(0, target), visited.AsSpan(0, (n + 7) / 8));
                return sampled < PcoIntMult.MinSample ? 0 : PcoIntMult.ChooseBase(sample.AsSpan(0, sampled));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(visited);
                ArrayPool<ulong>.Shared.Return(sample);
            }
        }

        /// <summary>
        /// The chunk's metadata, <c>ChunkMeta::write_to</c>: the mode and its base, the delta and its
        /// order, then each latent variable's ANS size log and bins.
        /// </summary>
        private void WriteChunkMeta(ulong modeBase, int order)
        {
            PcoBitWriter writer = _writer;
            if (modeBase > 0)
            {
                writer.Write(1, 4);
                writer.Write(modeBase, _bits);
            }
            else
            {
                writer.Write(0, 4);
            }

            if (order > 0)
            {
                writer.Write(1, 4);
                writer.Write((ulong)order, 3);
                writer.Write(0, 1);
            }
            else
            {
                writer.Write(0, 4);
            }

            WriteBins(_primaryBins);
            if (modeBase > 0)
            {
                WriteBins(_secondaryBins);
            }
        }

        private void WriteBins(PcoBinTrainer bins)
        {
            PcoBitWriter writer = _writer;
            int sizeLog = bins.AnsSizeLog;
            int offsetBitsBits = PcoBinTrainer.OffsetBitsBits(_bits);
            writer.Write((ulong)sizeLog, 4);
            writer.Write((ulong)bins.Count, 15);
            ReadOnlySpan<uint> weights = bins.Weights;
            ReadOnlySpan<ulong> lowers = bins.Lowers;
            ReadOnlySpan<int> offsetBits = bins.OffsetBits;
            for (int i = 0; i < bins.Count; i++)
            {
                writer.Write(weights[i] - 1, sizeLog);
                writer.Write(lowers[i], _bits);
                writer.Write((ulong)offsetBits[i], offsetBitsBits);
            }
        }

        /// <summary>
        /// One page, <c>write_page</c>: each variable's delta state and final ANS states, then batch
        /// by batch each variable's symbols' bits and its offsets.
        /// </summary>
        private void WritePage(Span<ulong> primary, Span<ulong> secondary, int order, ReadOnlySpan<ulong> moments, bool paired)
        {
            int pageN = primary.Length;
            Span<ulong> primaryStored = primary[Math.Min(order, pageN)..];
            _primaryPage.Dissect(primaryStored, _primaryBins, _primaryEncoder);
            if (paired)
            {
                _secondaryPage.Dissect(secondary, _secondaryBins, _secondaryEncoder);
            }

            PcoBitWriter writer = _writer;
            foreach (ulong moment in moments)
            {
                writer.Write(moment, _bits);
            }

            _primaryPage.WriteFinalStates(writer, _primaryBins.AnsSizeLog);
            if (paired)
            {
                _secondaryPage.WriteFinalStates(writer, _secondaryBins.AnsSizeLog);
            }

            writer.Align();
            for (int start = 0; start < pageN; start += PcoPageDecoder.BatchSize)
            {
                _primaryPage.WriteBatch(writer, _primaryBins, start);
                if (paired)
                {
                    _secondaryPage.WriteBatch(writer, _secondaryBins, start);
                }
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            ArrayPool<ulong>.Shared.Return(_latents);
            ArrayPool<ulong>.Shared.Return(_secondary);
            ArrayPool<ulong>.Shared.Return(_sorted);
            _primaryBins.Dispose();
            _secondaryBins.Dispose();
            _primaryPage.Dispose();
            _secondaryPage.Dispose();
            _writer.Dispose();
            if (_buffers is { } buffers)
            {
                ArrayPool<int>.Shared.Return(buffers);
            }

            if (_pageValues is { } pages)
            {
                ArrayPool<int>.Shared.Return(pages);
            }

            if (_chunkPages is { } chunks)
            {
                ArrayPool<int>.Shared.Return(chunks);
            }

            if (_moments is { } moments)
            {
                ArrayPool<ulong>.Shared.Return(moments);
            }
        }
    }

    /// <summary>
    /// One variable's page taken apart for writing, <c>dissect_page</c>: each latent's bin, its
    /// offset in it, and the bits the ANS encoder shed for its symbol, encoded last to first.
    /// </summary>
    private sealed class PageScratch : IDisposable
    {
        private readonly uint[] _states = new uint[4];
        private uint[] _ansValues = ArrayPool<uint>.Shared.Rent(ValuesPerPage);
        private byte[] _ansBits = ArrayPool<byte>.Shared.Rent(ValuesPerPage);
        private ulong[] _offsets = ArrayPool<ulong>.Shared.Rent(ValuesPerPage);
        private byte[] _offsetBits = ArrayPool<byte>.Shared.Rent(ValuesPerPage);
        private int _count;

        internal void Dissect(ReadOnlySpan<ulong> latents, PcoBinTrainer bins, PcoAnsEncoder encoder)
        {
            _count = latents.Length;
            uint initial = encoder.DefaultState;
            _states.AsSpan().Fill(initial);
            if (bins.IsTrivial)
            {
                return;
            }

            ReadOnlySpan<ulong> lowers = bins.Lowers;
            ReadOnlySpan<int> widths = bins.OffsetBits;
            bool single = bins.Count == 1;
            for (int i = latents.Length - 1; i >= 0; i--)
            {
                ulong latent = latents[i];
                int bin = single ? 0 : bins.BinOf(latent);
                _offsets[i] = latent - lowers[bin];
                _offsetBits[i] = (byte)widths[bin];
                if (!single)
                {
                    int lane = i & 3;
                    uint state = _states[lane];
                    uint next = encoder.Encode(state, bin, out int shed);
                    _ansValues[i] = state & ((1u << shed) - 1);
                    _ansBits[i] = (byte)shed;
                    _states[lane] = next;
                }
            }
        }

        internal void WriteFinalStates(PcoBitWriter writer, int sizeLog)
        {
            uint table = 1u << sizeLog;
            for (int lane = 0; lane < 4; lane++)
            {
                writer.Write(_states[lane] - table, sizeLog);
            }
        }

        internal void WriteBatch(PcoBitWriter writer, PcoBinTrainer bins, int start)
        {
            if (bins.IsTrivial || start >= _count)
            {
                return;
            }

            int end = Math.Min(start + PcoPageDecoder.BatchSize, _count);
            if (bins.Count > 1)
            {
                for (int i = start; i < end; i++)
                {
                    writer.Write(_ansValues[i], _ansBits[i]);
                }
            }

            for (int i = start; i < end; i++)
            {
                writer.Write(_offsets[i], _offsetBits[i]);
            }
        }

        public void Dispose()
        {
            ArrayPool<uint>.Shared.Return(_ansValues);
            ArrayPool<byte>.Shared.Return(_ansBits);
            ArrayPool<ulong>.Shared.Return(_offsets);
            ArrayPool<byte>.Shared.Return(_offsetBits);
            _ansValues = [];
            _ansBits = [];
            _offsets = [];
            _offsetBits = [];
        }
    }
}
