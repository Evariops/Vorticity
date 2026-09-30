using System;
using System.Buffers;
using System.Numerics;

namespace Vorticity.Writing;

/// <summary>
/// One latent variable's bins trained as pco trains them, <c>train_infos</c>: an equal-count
/// histogram of its latents, merged by a dynamic program into the partition whose bits cost least,
/// and the bins' counts quantized into the weights of an ANS table.
/// </summary>
/// <remarks>
/// <para>
/// The histogram is upstream's: a quicksort that stops partitioning once a part fits in the bin
/// being filled, equal counts of the sorted order with a run of one value never split, in
/// O(n log bins) rather than a sort's O(n log n). Latents spanning a range no wider than their
/// count, which is what deltas leave, are counted instead, a pass and no partition, their runs
/// placed by the same rule.
/// </para>
/// <para>
/// Refilled variable after variable in the arrays it already has, which are sized by the bins, a
/// few hundred at most, and not by the latents.
/// </para>
/// </remarks>
internal sealed class PcoBinTrainer : IDisposable
{
    /// <summary>The widest range counted rather than sorted, and looked up rather than searched: a megabyte of counts.</summary>
    private const ulong CountingRange = 1 << 18;

    /// <summary>The largest ANS size log pco trains to, its highest compression level.</summary>
    private const int MaxCompressionLevel = 12;

    /// <summary>Bits a value a single bin must cost less than the best partition by, for the speed of no symbols.</summary>
    private const float SingleBinSpeedupBitsPerNum = 0.1f;

    /// <summary>Bits a value bins of one value each must cost less by, for the speed of no offsets.</summary>
    private const float TrivialOffsetSpeedupBitsPerNum = 0.1f;

    // The histogram's bins, then the partition's.
    private ulong[] _histogramLowers = new ulong[256];
    private ulong[] _histogramUppers = new ulong[256];
    private uint[] _histogramCounts = new uint[256];
    private int _histogramCount;

    private uint[] _prefix = new uint[257];
    private float[] _bestCosts = new float[257];
    private int[] _bestStarts = new int[256];
    private int[] _partition = new int[256];

    private ulong[] _lowers = new ulong[256];
    private ulong[] _uppers = new ulong[256];
    private int[] _offsetBits = new int[256];
    private uint[] _weights = new uint[256];
    private float[] _floatWeights = new float[256];

    // The histogram's cursor, and the bin the runs are filling with the count it ends at.
    private int _target;
    private long _targetCount;
    private long _total;
    private int _binsLog;
    private long _applied;
    private int _nextBin;
    private bool _open;
    private uint _openCount;
    private ulong _openLower;
    private ulong _openUpper;

    // The latents' least and span, and the bin of each value of the span when it is looked up.
    private ulong _minimum;
    private ulong _span;
    private ushort[]? _lookup;

    /// <summary>The bins trained, in the order of their lower bounds; zero for no latents.</summary>
    internal int Count { get; private set; }

    /// <summary>The ANS table's size log; zero for a single bin.</summary>
    internal int AnsSizeLog { get; private set; }

    /// <summary>Each bin's lower bound.</summary>
    internal ReadOnlySpan<ulong> Lowers => _lowers.AsSpan(0, Count);

    /// <summary>Each bin's offset width.</summary>
    internal ReadOnlySpan<int> OffsetBits => _offsetBits.AsSpan(0, Count);

    /// <summary>Each bin's ANS weight, summing to 2^<see cref="AnsSizeLog"/>.</summary>
    internal ReadOnlySpan<uint> Weights => _weights.AsSpan(0, Count);

    /// <summary>
    /// Whether a page's body holds nothing for this variable: no bin, or one whose values need no
    /// offset -- upstream's <c>are_trivial</c>.
    /// </summary>
    internal bool IsTrivial => Count == 0 || (Count == 1 && _offsetBits[0] == 0);

    /// <summary>The widest offset any bin stores.</summary>
    internal int MaxOffsetBits
    {
        get
        {
            int widest = 0;
            foreach (int bits in OffsetBits)
            {
                widest = Math.Max(widest, bits);
            }

            return widest;
        }
    }

    /// <summary>
    /// The average bits a latent costs, <c>avg_bits_per_latent</c>: each bin's ANS bits and offset
    /// bits, weighed by its share of the table.
    /// </summary>
    internal double AverageBitsPerLatent
    {
        get
        {
            double table = 1 << AnsSizeLog;
            double sum = 0;
            for (int i = 0; i < Count; i++)
            {
                double weight = _weights[i];
                sum += (AnsSizeLog - Math.Log2(weight) + _offsetBits[i]) * weight / table;
            }

            return sum;
        }
    }

    /// <summary>
    /// The bins of <paramref name="latents"/>, which are sorted here, in place: <c>train_infos</c>.
    /// </summary>
    /// <param name="latents">The variable's latents, no wider than <paramref name="latentBits"/>.</param>
    /// <param name="unoptimizedBinsLog">The histogram's size log, before the partition merges it.</param>
    /// <param name="latentBits">The variable's width.</param>
    internal void Train(Span<ulong> latents, int unoptimizedBinsLog, int latentBits)
    {
        ReleaseLookup();
        int n = latents.Length;
        if (n == 0)
        {
            Count = 0;
            AnsSizeLog = 0;
            return;
        }

        ulong minimum = ulong.MaxValue;
        ulong maximum = 0;
        foreach (ulong latent in latents)
        {
            minimum = Math.Min(minimum, latent);
            maximum = Math.Max(maximum, latent);
        }

        _minimum = minimum;
        _span = maximum - minimum;
        BeginHistogram(n, unoptimizedBinsLog);
        if (_span < Math.Min((ulong)Math.Max(n, 4096), CountingRange))
        {
            CountRuns(latents, minimum, (int)_span + 1);
        }
        else
        {
            int badPivots = 1 + BitOperations.Log2((uint)n + 1);
            PartitionRuns(latents, new Bound(0, Tight: false), new Bound(ulong.MaxValue, Tight: false), badPivots);
        }

        EndHistogram();

        int nLogCeiling = n <= 1 ? 0 : BitOperations.Log2((uint)(n - 1)) + 1;
        int estimatedSizeLog = Math.Min(Math.Min(unoptimizedBinsLog + 2, MaxCompressionLevel), nLogCeiling);
        Partition(estimatedSizeLog, latentBits);
        QuantizeWeights(n, estimatedSizeLog);
    }

    /// <summary>
    /// Makes <see cref="BinOf"/> a lookup rather than a search, when the latents trained span a
    /// range narrow enough to hold a bin a value: the pass that writes a chunk's pages asks it of
    /// every latent.
    /// </summary>
    internal void PrepareLookup()
    {
        ReleaseLookup();
        if (Count <= 1 || _span >= CountingRange)
        {
            return;
        }

        int span = (int)_span + 1;
        ushort[] lookup = ArrayPool<ushort>.Shared.Rent(span);
        ReadOnlySpan<ulong> lowers = Lowers;
        for (int bin = 0; bin < Count; bin++)
        {
            // A bin owns every value from its lower bound to the next bin's; the ones between its
            // upper bound and the next lower bound hold no latent and are never looked up.
            int from = (int)(lowers[bin] - _minimum);
            int to = bin + 1 < Count ? (int)(lowers[bin + 1] - _minimum) : span;
            lookup.AsSpan(from, to - from).Fill((ushort)bin);
        }

        _lookup = lookup;
    }

    /// <inheritdoc/>
    public void Dispose() => ReleaseLookup();

    private void ReleaseLookup()
    {
        if (_lookup is { } lookup)
        {
            _lookup = null;
            ArrayPool<ushort>.Shared.Return(lookup);
        }
    }

    /// <summary>
    /// The histogram's size log for a chunk of <paramref name="n"/> latents at a compression level,
    /// <c>choose_unoptimized_bins_log</c>: the level itself while the chunk is large enough to fill
    /// that many bins sixteen deep, half the rest past that.
    /// </summary>
    internal static int UnoptimizedBinsLog(int compressionLevel, int n)
    {
        int logN = n <= 0 ? 0 : BitOperations.Log2((uint)n);
        int fast = Math.Max(0, logN - 4);
        return compressionLevel <= fast ? compressionLevel : fast + ((compressionLevel - fast) / 2);
    }

    /// <summary>Bits to encode an offset up to <paramref name="range"/>: <c>bits_to_encode_offset</c>.</summary>
    internal static int BitsOf(ulong range) => 64 - BitOperations.LeadingZeroCount(range);

    /// <summary>Bits a bin's offset width is written in: <c>bits_to_encode_offset_bits</c>.</summary>
    internal static int OffsetBitsBits(int latentBits) => 32 - BitOperations.LeadingZeroCount((uint)latentBits);

    /// <summary>The bin holding <paramref name="latent"/>: the last whose lower bound it reaches.</summary>
    internal int BinOf(ulong latent)
    {
        if (_lookup is { } lookup)
        {
            return lookup[(int)(latent - _minimum)];
        }

        ReadOnlySpan<ulong> lowers = Lowers;
        int low = 0;
        int high = lowers.Length - 1;
        while (low < high)
        {
            int middle = (low + high + 1) >> 1;
            if (lowers[middle] <= latent)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    /// <summary>Starts an equal-count histogram of <paramref name="total"/> latents, <c>histogram</c>.</summary>
    private void BeginHistogram(int total, int binsLog)
    {
        // One more than the bins: the last run can close a bin past the count the target gives.
        int capacity = (1 << binsLog) + 1;
        if (_histogramLowers.Length < capacity)
        {
            _histogramLowers = new ulong[capacity];
            _histogramUppers = new ulong[capacity];
            _histogramCounts = new uint[capacity];
        }

        _total = total;
        _binsLog = binsLog;
        _applied = 0;
        _nextBin = 0;
        _open = false;
        _histogramCount = 0;
        _target = 0;
        _targetCount = 0;
    }

    private void EndHistogram()
    {
        if (_open)
        {
            Complete(_nextBin);
        }
    }

    /// <summary>The latents' runs by counting each value of their span, in order.</summary>
    private void CountRuns(ReadOnlySpan<ulong> latents, ulong minimum, int span)
    {
        int[] counts = ArrayPool<int>.Shared.Rent(span);
        try
        {
            Span<int> tally = counts.AsSpan(0, span);
            tally.Clear();
            foreach (ulong latent in latents)
            {
                tally[(int)(latent - minimum)]++;
            }

            for (int i = 0; i < span; i++)
            {
                if (tally[i] > 0)
                {
                    ApplyRun(minimum + (ulong)i, tally[i]);
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(counts);
        }
    }

    /// <summary>A bound of a part: its least or greatest latent when tight, a value past them when loose.</summary>
    private readonly record struct Bound(ulong Value, bool Tight);

    /// <summary>
    /// Upstream's <c>apply_quicksort_recurse</c>: a part that fits in the bin being filled goes into
    /// it whole, one of a single value is placed as a run, and any other is partitioned around a
    /// pivot and each side taken in turn, the smaller first -- so the parts are met in the sorted
    /// order without the order inside a part ever being settled. Parts the pivots keep splitting
    /// badly are sorted, as upstream heap-sorts them, and walked.
    /// </summary>
    private void PartitionRuns(Span<ulong> latents, Bound lower, Bound upper, int badPivots)
    {
        while (!latents.IsEmpty)
        {
            int target = BinIndex(_applied);
            long targetCount = CumulativeCount(target);
            long end = _applied + latents.Length;
            if (end <= targetCount)
            {
                ApplyBounded(latents, lower, upper);
                if (end == targetCount)
                {
                    Complete(target);
                }

                return;
            }

            if (lower.Value == upper.Value || latents.Length == 1)
            {
                ApplyConstantRun(latents.Length, latents[0]);
                return;
            }

            ulong tentative = ChoosePivot(latents);
            ulong pivot;
            Bound leftUpper;
            Bound rightLower;
            if (tentative > lower.Value)
            {
                pivot = tentative;
                leftUpper = new Bound(tentative - 1, Tight: false);
                rightLower = new Bound(tentative, Tight: true);
            }
            else
            {
                pivot = tentative + 1;
                leftUpper = new Bound(tentative, Tight: true);
                rightLower = new Bound(tentative + 1, Tight: false);
            }

            int left = Partition(latents, pivot);
            Span<ulong> lesser = latents[..left];
            Span<ulong> greater = latents[left..];
            if (1 + Math.Min(left, latents.Length - left) < latents.Length / 8)
            {
                if (--badPivots == 0)
                {
                    lesser.Sort();
                    greater.Sort();
                    SortedRuns(latents);
                    return;
                }

                BreakPatterns(lesser);
                BreakPatterns(greater);
            }

            PartitionRuns(lesser, lower, leftUpper, badPivots);
            latents = greater;
            lower = rightLower;
        }
    }

    /// <summary>A part that fits in the bin being filled, its bounds taken from the part where they are loose.</summary>
    private void ApplyBounded(ReadOnlySpan<ulong> latents, Bound lower, Bound upper)
    {
        ulong least = lower.Value;
        ulong greatest = upper.Value;
        if (!lower.Tight || !upper.Tight)
        {
            ulong min = ulong.MaxValue;
            ulong max = 0;
            foreach (ulong latent in latents)
            {
                min = Math.Min(min, latent);
                max = Math.Max(max, latent);
            }

            least = lower.Tight ? least : min;
            greatest = upper.Tight ? greatest : max;
        }

        ApplyIncomplete(latents.Length, least, greatest);
    }

    /// <summary>Upstream's pivot, <c>choose_pivot</c>: the median of three latents, each the median of its neighbours past fifty.</summary>
    private static ulong ChoosePivot(ReadOnlySpan<ulong> latents)
    {
        int length = latents.Length;
        int a = length / 4;
        int b = length / 2;
        int c = (length * 3) / 4;
        if (length >= 8)
        {
            if (length >= 50)
            {
                a = MedianOf3(latents, a - 1, a, a + 1);
                b = MedianOf3(latents, b - 1, b, b + 1);
                c = MedianOf3(latents, c - 1, c, c + 1);
            }

            b = MedianOf3(latents, a, b, c);
        }

        return latents[b];
    }

    /// <summary>Upstream's <c>sort3</c> on indices: which of the three holds the median, ties to the later.</summary>
    private static int MedianOf3(ReadOnlySpan<ulong> latents, int a, int b, int c)
    {
        if (latents[b] < latents[a])
        {
            (a, b) = (b, a);
        }

        if (latents[c] < latents[b])
        {
            (b, c) = (c, b);
        }

        if (latents[b] < latents[a])
        {
            (a, b) = (b, a);
        }

        return b;
    }

    /// <summary>Upstream's branchless partition: every latent under the pivot moved to the front, how many returned.</summary>
    private static int Partition(Span<ulong> latents, ulong pivot)
    {
        int left = 0;
        for (int i = 0; i < latents.Length; i++)
        {
            ulong value = latents[i];
            latents[i] = latents[left];
            latents[left] = value;
            left += value < pivot ? 1 : 0;
        }

        return left;
    }

    /// <summary>Upstream's <c>break_patterns</c>: three latents swapped to places a xorshift picks, against pivots an input defeats.</summary>
    private static void BreakPatterns(Span<ulong> latents)
    {
        int length = latents.Length;
        if (length < 8)
        {
            return;
        }

        ulong seed = (ulong)length;
        ulong modulus = BitOperations.RoundUpToPowerOf2((ulong)length);
        int at = length / 4 * 2;
        for (int i = 0; i < 3; i++)
        {
            seed ^= seed << 13;
            seed ^= seed >> 7;
            seed ^= seed << 17;
            int other = (int)(seed & (modulus - 1));
            if (other >= length)
            {
                other -= length;
            }

            (latents[at - 1 + i], latents[other]) = (latents[other], latents[at - 1 + i]);
        }
    }

    /// <summary>
    /// The sorted latents, a bin at a time, <c>apply_sorted</c>: straight to the latent each bin
    /// would end on, whose run is placed whole, everything before it open in the bin.
    /// </summary>
    private void SortedRuns(ReadOnlySpan<ulong> sorted)
    {
        ReadOnlySpan<ulong> rest = sorted;
        while (!rest.IsEmpty)
        {
            int target = BinIndex(_applied);
            long targetCount = CumulativeCount(target);
            long targetIndex = targetCount - _applied;
            if (targetIndex >= rest.Length)
            {
                ApplyIncomplete(rest.Length, rest[0], rest[^1]);
                if (targetIndex == rest.Length)
                {
                    Complete(target);
                }

                break;
            }

            // The value the bin would end on, and the whole run of it, which goes to one bin.
            int left = (int)targetIndex - 1;
            int right = (int)targetIndex;
            ulong value = rest[left];
            while (left > 0 && rest[left - 1] == value)
            {
                left--;
            }

            while (right < rest.Length && rest[right] == value)
            {
                right++;
            }

            if (left > 0)
            {
                ApplyIncomplete(left, rest[0], rest[left - 1]);
            }

            ApplyConstantRun(right - left, value);
            rest = rest[right..];
        }
    }

    /// <summary>
    /// One run of <paramref name="count"/> latents of <paramref name="value"/>, the next in order,
    /// as the partition would meet it: in the bin being filled when it ends at or before the bin's
    /// count, and otherwise, crossing the bin's end, placed whole as upstream's constant run is.
    /// </summary>
    /// <remarks>
    /// The bin's target is kept between runs: while fewer latents than its count are placed, the
    /// bin the next latent belongs to is the same one, so a run short of it costs no division.
    /// </remarks>
    private void ApplyRun(ulong value, int count)
    {
        if (_applied >= _targetCount)
        {
            _target = BinIndex(_applied);
            _targetCount = CumulativeCount(_target);
        }

        if (_applied + count <= _targetCount)
        {
            ApplyIncomplete(count, value, value);
            if (_applied == _targetCount)
            {
                Complete(_target);
            }

            return;
        }

        ApplyConstantRun(count, value);
    }

    private int BinIndex(long cumulative) => (int)(((ulong)cumulative << _binsLog) / (ulong)_total);

    private long CumulativeCount(int bin) =>
        (long)(((((ulong)bin + 1) * (ulong)_total) + (1UL << _binsLog) - 1) >> _binsLog);

    private void ApplyIncomplete(int count, ulong lower, ulong upper)
    {
        if (count == 0)
        {
            return;
        }

        if (_open)
        {
            _openUpper = upper;
            _openCount += (uint)count;
        }
        else
        {
            _open = true;
            _openCount = (uint)count;
            _openLower = lower;
            _openUpper = upper;
        }

        _applied += count;
    }

    private void ApplyConstantRun(int count, ulong value)
    {
        long start = _applied;
        long end = start + count;
        int bin = BinIndex(start + (count / 2));
        if (bin > _nextBin)
        {
            // The run centres past the next bin: what is open closes before it, or the run takes
            // the spare bin itself.
            int spare = bin - 1;
            if (!Complete(spare))
            {
                bin = spare;
            }
        }

        ApplyIncomplete(count, value, value);
        if (end >= CumulativeCount(bin))
        {
            Complete(bin);
        }
    }

    private bool Complete(int bin)
    {
        if (!_open)
        {
            return false;
        }

        _nextBin = bin + 1;
        int at = _histogramCount++;
        _histogramLowers[at] = _openLower;
        _histogramUppers[at] = _openUpper;
        _histogramCounts[at] = _openCount;
        _open = false;
        return true;
    }

    /// <summary>
    /// The histogram's bins merged into the partition whose estimated bits cost least,
    /// <c>optimize_bins</c>: a bin's cost is its metadata plus, per value, its ANS bits and its offset's.
    /// </summary>
    private void Partition(int sizeLog, int latentBits)
    {
        int bins = _histogramCount;
        if (_prefix.Length < bins + 1)
        {
            _prefix = new uint[bins + 1];
            _bestCosts = new float[bins + 1];
            _bestStarts = new int[bins];
            _partition = new int[bins];
            _lowers = new ulong[bins];
            _uppers = new ulong[bins];
            _offsetBits = new int[bins];
            _weights = new uint[bins];
            _floatWeights = new float[bins];
        }

        ReadOnlySpan<ulong> lowers = _histogramLowers.AsSpan(0, bins);
        ReadOnlySpan<ulong> uppers = _histogramUppers.AsSpan(0, bins);
        ReadOnlySpan<uint> counts = _histogramCounts.AsSpan(0, bins);
        _prefix[0] = 0;
        for (int i = 0; i < bins; i++)
        {
            _prefix[i + 1] = _prefix[i] + counts[i];
        }

        uint total = _prefix[bins];
        float totalLog2 = Log2Approx(total);
        float metaCost = sizeLog + latentBits + OffsetBitsBits(latentBits);
        _bestCosts[0] = 0;
        for (int i = 0; i < bins; i++)
        {
            float best = float.MaxValue;
            int bestStart = -1;
            ulong upper = uppers[i];
            uint through = _prefix[i + 1];
            for (int j = i; j >= 0; j--)
            {
                float cost = _bestCosts[j] + BinCost(metaCost, lowers[j], upper, through - _prefix[j], totalLog2);
                if (cost < best)
                {
                    best = cost;
                    bestStart = j;
                }
            }

            _bestCosts[i + 1] = best;
            _bestStarts[i] = bestStart;
        }

        float bestCost = _bestCosts[bins];
        float single = BinCost(metaCost, lowers[0], uppers[bins - 1], total, totalLog2);
        if (single < bestCost + (SingleBinSpeedupBitsPerNum * total))
        {
            Emit(0, bins - 1, 0);
            Count = 1;
            return;
        }

        bool everyBinOneValue = true;
        float trivial = 0;
        for (int i = 0; i < bins; i++)
        {
            everyBinOneValue &= lowers[i] == uppers[i];
            trivial += BinCost(metaCost, lowers[i], uppers[i], counts[i], totalLog2);
        }

        if (everyBinOneValue && trivial < bestCost + (TrivialOffsetSpeedupBitsPerNum * total))
        {
            for (int i = 0; i < bins; i++)
            {
                Emit(i, i, i);
            }

            Count = bins;
            return;
        }

        // Rewound from the last bin: each best start is where the bin ending before it ends.
        int parts = 0;
        for (int end = bins - 1; ;)
        {
            int start = _bestStarts[end];
            _partition[parts++] = start;
            if (start == 0)
            {
                break;
            }

            end = start - 1;
        }

        int endOf = bins - 1;
        for (int k = 0; k < parts; k++)
        {
            // Stored last first: the part at k ends where the one before it starts.
            int start = _partition[k];
            Emit(start, endOf, parts - 1 - k);
            endOf = start - 1;
        }

        Count = parts;
    }

    /// <summary>The histogram's bins <paramref name="first"/> to <paramref name="last"/> as bin <paramref name="at"/>, its count its weight for now.</summary>
    private void Emit(int first, int last, int at)
    {
        ulong lower = _histogramLowers[first];
        ulong upper = _histogramUppers[last];
        _lowers[at] = lower;
        _uppers[at] = upper;
        _offsetBits[at] = BitsOf(upper - lower);
        _weights[at] = _prefix[last + 1] - _prefix[first];
    }

    /// <summary>
    /// The bins' counts quantized into weights summing to a power of two, <c>quantize_weights</c>:
    /// each count's share of the table, every bin at least one, rounded and then nudged until the
    /// sum is exact, the table shrunk by the powers of two every weight shares.
    /// </summary>
    private void QuantizeWeights(int total, int maxSizeLog)
    {
        int bins = Count;
        if (bins == 1)
        {
            AnsSizeLog = 0;
            _weights[0] = 1;
            return;
        }

        int minimumSizeLog = 64 - BitOperations.LeadingZeroCount((ulong)(bins - 1));
        int sizeLog = Math.Max(minimumSizeLog, maxSizeLog);
        Span<uint> weights = _weights.AsSpan(0, bins);
        Span<float> floats = _floatWeights.AsSpan(0, bins);

        uint required = 1u << sizeLog;
        float multiplier = required / (float)total;
        float desired = 0;
        for (int i = 0; i < bins; i++)
        {
            floats[i] = MathF.Max((weights[i] * multiplier) - 1f, 0f);
            desired += floats[i];
        }

        uint requiredSurplus = required - (uint)bins;
        float surplusMultiplier = desired == 0f ? 0f : requiredSurplus / desired;
        uint sum = 0;
        for (int i = 0; i < bins; i++)
        {
            floats[i] = 1f + (floats[i] * surplusMultiplier);
            weights[i] = (uint)MathF.Round(floats[i], MidpointRounding.AwayFromZero);
            sum += weights[i];
        }

        // Upstream walks each correction once from the first bin; the walk here wraps, which only a
        // sum that one walk cannot fix, where upstream would index past its bins, ever reaches.
        for (int i = 0; sum > required; i = (i + 1) % bins)
        {
            if (weights[i] > 1 && weights[i] > floats[i])
            {
                weights[i]--;
                sum--;
            }
        }

        for (int i = 0; sum < required; i = (i + 1) % bins)
        {
            if (weights[i] < floats[i])
            {
                weights[i]++;
                sum++;
            }
        }

        int shared = 32;
        foreach (uint weight in weights)
        {
            shared = Math.Min(shared, BitOperations.TrailingZeroCount(weight));
        }

        for (int i = 0; i < bins; i++)
        {
            weights[i] >>= shared;
        }

        AnsSizeLog = sizeLog - shared;
    }

    private static float BinCost(float metaCost, ulong lower, ulong upper, uint count, float totalLog2)
    {
        float values = count;
        float ansCost = totalLog2 - Log2Approx(count);
        float offsetCost = BitsOf(upper - lower);
        return metaCost + ((ansCost + offsetCost) * values);
    }

    /// <summary>
    /// pco's quick log2, <c>log2_approx</c>: the exponent, plus a quadratic fitted to the
    /// significand on [0.674, 1.348). The partition compares costs made of it, so it is upstream's
    /// to the bit rather than <see cref="MathF.Log2"/>.
    /// </summary>
    internal static float Log2Approx(float x)
    {
        const float Z = 0.674f;
        const uint SignificandMask = 0x7FFFFF;
        uint zSignificand = BitConverter.SingleToUInt32Bits(Z) & SignificandMask;
        const float B = 2.0f / Z;
        const float C = -B / (6.0f * Z);
        const float A = -B - C;

        uint bits = BitConverter.SingleToUInt32Bits(x);
        uint exponent = bits >> 23;
        uint significand = bits & SignificandMask;
        uint highBit = significand > zSignificand ? 1u : 0u;
        uint logInt = unchecked(exponent + highBit - 127);
        uint normalizedBits = ((0x7Fu ^ highBit) << 23) | significand;
        float normalized = BitConverter.UInt32BitsToSingle(normalizedBits);
        return (float)logInt + A + (normalized * (B + (C * normalized)));
    }
}
