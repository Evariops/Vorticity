// FSST symbol-table training: Algorithm 3 of the FSST paper, as fsst-rs-0.6.0/src/builder.rs
// implements it.
//
// docs/01-scope.md §3 names this "by far the largest write kernel", and it is right for a reason
// worth stating: DECODING FSST is a flat walk over a code stream, while ENCODING it means deciding
// which 255 byte sequences of length 1..8 to spend the code space on. The algorithm is
// generational -- compress the sample with the table so far, count which codes and which ADJACENT
// PAIRS of codes occur, propose every pair as a merged symbol, keep the 255 best by gain, repeat
// -- and each generation sees a larger fraction of the sample than the last.
//
// WHERE THIS DELIBERATELY DIVERGES FROM THE REFERENCE, and why each divergence is safe:
//
//   * The reference matches symbols through a lossy hash table, whose insert can fail and silently
//     drop a symbol. We match through exact dictionaries keyed by length instead, so nothing we
//     choose is unreachable to us. What we may NOT do is keep a symbol that table could not hold:
//     a reader pushing a predicate down has to turn our table back into a matcher, and a symbol it
//     cannot place stops it dead. So the table is filtered to what the reference can hold before it
//     is settled, and the exact dictionaries buy speed rather than a larger table.
//   * The reference's compress loop reads 8 bytes past its cursor and lets a symbol match into the
//     zero padding of the final word. WE CAP EVERY MATCH AT THE BYTES REMAINING. A match into
//     padding would decode to MORE bytes than the row contained, and our own reader checks the
//     decoded length against the sum of uncompressed_lengths, so it would produce a file we could
//     not read back.
//
// None of this needs to be byte-identical to Rust's table: docs/05-benchmarks.md §3 sets the write
// target as a SIZE ratio precisely because "the compressor is a sampler, so two honest
// implementations of the same algorithm diverge on borderline data".
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace Vorticity.Writing;

/// <summary>A trained FSST symbol table, and the compression over it.</summary>
internal sealed class FsstSymbols
{
    /// <summary>Codes 0..254 name symbols; 255 is the escape.</summary>
    internal const int MaxSymbols = 255;

    /// <summary>The escape code: the next byte of the stream is a literal.</summary>
    internal const byte EscapeCode = 255;

    /// <summary>The longest symbol the format allows.</summary>
    internal const int MaxSymbolLength = 8;

    /// <summary>
    /// The extended code space of the reference: 0..255 are literal bytes, 256+i is symbol i.
    /// </summary>
    private const int CodeBase = 256;

    /// <summary>The "no previous code" marker, and the top of the extended space.</summary>
    private const int CodeMask = 511;

    /// <summary>The five generations of the FSST paper, as fractions of 128.</summary>
    private static readonly int[] Generations = [8, 38, 68, 98, 128];

    /// <summary><see cref="ulong"/>s per <c>count2</c> row: 512 bits of pair presence.</summary>
    private const int PairWordsPerRow = (CodeMask + 1) / 64;

    /// <summary>
    /// The trainer's working tables, owned by whoever owns the write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Count2</c> is 512 x 512 ints - ONE MEGABYTE - and <see cref="Train(System.ReadOnlySpan{byte},System.ReadOnlySpan{int},System.ReadOnlySpan{int})"/> runs ONCE PER
    /// COLUMN-CHUNK, so writing a 65 536-row file allocated and dropped about 64 MB of counting
    /// table. They are cleared at the top of every generation regardless, so reusing them cannot
    /// change a single symbol: the first <c>Array.Clear</c> of a freshly allocated array was always
    /// redundant work on memory the runtime had just zeroed.
    /// </para>
    /// <para>
    /// THEY WERE <c>[ThreadStatic]</c> AND THAT WAS THE WRONG ANSWER. A thread-static is correct
    /// only while no <c>await</c> separates taking the buffer from finishing with it, and
    /// <c>VortexFileWriter</c> is async throughout: the property held because training happens to
    /// be synchronous, which no test states and which an edit three layers up would break in
    /// silence.
    /// </para>
    /// <para>
    /// WHAT REPLACES IT IS AN EXPLICIT TRANSFER OF OWNERSHIP: one cached set, taken with an
    /// <see cref="System.Threading.Interlocked.Exchange(ref object, object)"/> that leaves null
    /// behind, put back in a <c>finally</c>. Whoever holds the reference is its only holder, which
    /// is the property a thread-static only PRETENDS to have -- a second trainer, on any thread,
    /// in any continuation, finds null and allocates its own rather than sharing. The process
    /// retains one set, not one per thread that has ever written a file.
    /// </para>
    /// <para>
    /// Per-write ownership was tried first and is the honest alternative; it costs 1.1 MB per file
    /// written, which `WriteAllocationTests` reported as 522 B/row on a 4096-row FSST file. The
    /// thread-static had been hiding that cost rather than removing it.
    /// </para>
    /// <para>
    /// The arrays inside are allocated ON FIRST USE, so the first write that reaches no FSST
    /// candidate pays nothing.
    /// </para>
    /// </remarks>
    internal sealed class TrainingTables
    {
        /// <summary>The one cached set; null while somebody holds it.</summary>
        private static TrainingTables? Cached;

        /// <summary>Takes the cached set, or a fresh one when another trainer holds it.</summary>
        /// <returns>A set the caller owns outright until it calls <see cref="Give"/>.</returns>
        internal static TrainingTables Take() =>
            Interlocked.Exchange(ref Cached, null) ?? new TrainingTables();

        /// <summary>Gives a set back. Losing the race simply drops one set to the GC.</summary>
        /// <param name="tables">The set the caller is done with.</param>
        internal static void Give(TrainingTables tables) => Volatile.Write(ref Cached, tables);

        private int[]? _count1;
        private int[]? _count2;
        private ulong[]? _pairBits;
        private Line[]? _sample;
        private Dictionary<Candidate, long>? _candidates;
        private List<KeyValuePair<Candidate, long>>? _ranked;

        /// <summary>
        /// Room for the drawn sample: at most <see cref="SampleTarget"/> lines.
        /// </summary>
        /// <remarks>
        /// It lives HERE rather than in an <see cref="ArrayPool{T}"/> because a pool is per element
        /// type, and asking for one of <see cref="Line"/> creates a whole new
        /// <c>SharedArrayPool</c> - with its own Gen2 trimming callback, which showed up at 6.9% of
        /// a write profile doing nothing but reading memory pressure. This object is already the
        /// place the trainer keeps what it reuses across columns, and 128 kB next to
        /// <see cref="Count2"/>'s megabyte is not the line to economize on.
        ///
        /// The bound holds either way the sample is built: the unsampled branch takes rows whose
        /// lengths sum below <see cref="SampleTarget"/>, so there are fewer than that many
        /// non-empty ones; the sampled branch draws at least one byte per line until it reaches it.
        /// </remarks>
        internal Line[] Sample => _sample ??= new Line[SampleTarget];

        /// <summary>Per-code occurrence counts; 2 kB, cleared outright each generation.</summary>
        internal int[] Count1 => _count1 ??= new int[CodeMask + 1];

        /// <summary>Per-code-pair occurrence counts; 1 MB, cleared through <see cref="PairBits"/>.</summary>
        internal int[] Count2 => _count2 ??= new int[(CodeMask + 1) * (CodeMask + 1)];

        /// <summary>
        /// One bit per cell of <see cref="Count2"/>, set when that cell has been incremented.
        /// </summary>
        /// <remarks>
        /// <para>
        /// THE PAIR TABLE IS DENSE AND THE COUNTS IN IT ARE NOT. <c>count2</c> has 262 144 cells,
        /// and one generation can touch at most as many of them as the sample has tokens -
        /// <see cref="SampleTarget"/> is 16 kB, so a few thousand. The table was nevertheless
        /// CLEARED in full and SCANNED in full once per generation, five generations per
        /// column-chunk: 5 MB of <c>memset</c> and 1.3 M cell reads to look at a few thousand live
        /// counts. That is what put <c>Train</c> at a third of the write profile.
        /// </para>
        /// <para>
        /// <b>The visit order is unchanged, and that is load-bearing rather than incidental.</b>
        /// <c>Optimize</c> accumulates candidate gains into a dictionary, flattens it in INSERTION
        /// order and sorts it with an UNSTABLE sort, so two candidates of equal gain and equal
        /// length are separated by nothing but the order they were proposed in - and which of them
        /// wins the last slot of a full table changes the symbol table, hence the compressed bytes,
        /// hence the file. Walking each row's bitmap with <c>BitOperations.TrailingZeroCount</c>
        /// yields <c>code2</c> in ascending order, which is exactly the order the dense scan
        /// produced. <c>FsstSymbolsTests</c> and the byte-exact <c>WrittenSizeTests</c> are what
        /// hold that claim up.
        /// </para>
        /// </remarks>
        internal ulong[] PairBits => _pairBits ??= new ulong[(CodeMask + 1) * PairWordsPerRow];

        /// <summary>The candidate gain map, reused across generations and column-chunks.</summary>
        /// <remarks>
        /// Reuse is order-neutral. A <see cref="Dictionary{TKey, TValue}"/> with no removals
        /// enumerates in insertion order, <see cref="Dictionary{TKey, TValue}.Clear"/> puts it back
        /// in the state a fresh one is in, and growing it copies the entry array wholesale - so a
        /// pre-grown, cleared dictionary enumerates identically to one allocated per generation.
        /// See <see cref="PairBits"/> for why that identity is the property to protect.
        /// </remarks>
        internal Dictionary<Candidate, long> Candidates =>
            _candidates ??= new Dictionary<Candidate, long>(512);

        /// <inheritdoc cref="Candidates"/>
        internal List<KeyValuePair<Candidate, long>> Ranked =>
            _ranked ??= new List<KeyValuePair<Candidate, long>>(512);
    }

    /// <summary>Bytes of sample the trainer aims for before it stops drawing lines.</summary>
    private const int SampleTarget = 1 << 14;

    /// <summary>The longest run of one line the sampler takes.</summary>
    private const int SampleLine = 512;

    /// <summary>Slots in the symbol table; a power of two, four times <see cref="MaxSymbols"/>.</summary>
    private const int SlotCount = 1024;

    /// <summary>Slots in the reference's matcher, which decides which symbols it can hold.</summary>
    private const int ReferenceSlotCount = 2048;

    /// <summary>The multiplier of the multiply-shift hash; the reference's own constant.</summary>
    private const ulong HashPrime = 2971215073UL;

    private readonly ulong[] _bits = new ulong[MaxSymbols];
    private readonly byte[] _lengths = new byte[MaxSymbols];
    private readonly short[] _oneByte = new short[256];
    private readonly short[] _twoByte = new short[65536];

    /// <summary>
    /// The symbols of length 3..8, indexed by their first THREE bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This was six <c>Dictionary&lt;ulong, int&gt;</c>, one per length, probed longest-first -- so
    /// matching one byte of input cost up to SIX dictionary lookups, each with its own hashing,
    /// bucket walk and generic comparer call. The table is walked over the whole sample five times
    /// during training and again over the whole column during compression, and `dotnet-trace` puts
    /// `FsstSymbols.Train` at a third of the write path.
    /// </para>
    /// <para>
    /// EVERY SYMBOL OF LENGTH THREE OR MORE SHARES ITS FIRST THREE BYTES WITH ANY INPUT IT MATCHES,
    /// which is the observation the reference's table is built on. So one hash of those three bytes
    /// names a short chain of candidates, and the longest whose own bytes match is the answer --
    /// one probe and a walk of one or two entries instead of six probes.
    /// </para>
    /// <para>
    /// EXACT, NOT LOSSY, and that is where this parts company with fsst-rs: the reference keeps ONE
    /// symbol per slot and DROPS a symbol whose slot is taken, which is a speed trade a writer has
    /// no reason to make -- it would change which symbols are chosen, which changes the file. Here
    /// the chain holds every collision, the match test compares the candidate's full bytes at its
    /// own length, and the longest wins. `WrittenSizeTests` is byte-exact, and the whole argument
    /// for touching the writer at all is that this is neutral to the output.
    /// </para>
    /// </remarks>
    private readonly short[] _prefixHeads = new short[SlotCount];
    private readonly short[] _prefixChain = new short[MaxSymbols];

    private int _count;

    private FsstSymbols()
    {
        Clear();
    }

    /// <summary>How many symbols the table holds, 0..255.</summary>
    internal int Count => _count;

    /// <summary>Symbol <paramref name="index"/>'s bytes, little-endian in a <c>u64</c>.</summary>
    /// <param name="index">0-based, below <see cref="Count"/>.</param>
    /// <returns>The symbol's bits.</returns>
    internal ulong SymbolBits(int index) => _bits[index];

    /// <summary>Symbol <paramref name="index"/>'s length in bytes, 1..8.</summary>
    /// <param name="index">0-based, below <see cref="Count"/>.</param>
    /// <returns>The symbol's length.</returns>
    internal byte SymbolLength(int index) => _lengths[index];

    /// <summary>Trains a table on a corpus of rows.</summary>
    /// <param name="rows">The values to learn from; null and empty rows are ignored.</param>
    /// <returns>The trained table, holding at least one symbol, or null when nothing was learnable.</returns>
    internal static FsstSymbols? Train(IReadOnlyList<ReadOnlyMemory<byte>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        // THE ADAPTER, not the entry point. `FsstPlan` already holds its column as one contiguous
        // heap and calls the span form directly; this shape exists for callers that hold a list of
        // separate buffers, which in this repository is the tests.
        long total = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            total += rows[i].Length;
        }

        byte[] heap = new byte[Math.Max(total, 1)];
        int[] starts = new int[Math.Max(rows.Count, 1)];
        int[] lengths = new int[Math.Max(rows.Count, 1)];
        int at = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            ReadOnlySpan<byte> value = rows[i].Span;
            value.CopyTo(heap.AsSpan(at));
            starts[i] = at;
            lengths[i] = value.Length;
            at += value.Length;
        }

        return Train(heap, starts.AsSpan(0, rows.Count), lengths.AsSpan(0, rows.Count));
    }

    /// <summary>
    /// Trains a table on rows held as slices of ONE contiguous heap.
    /// </summary>
    /// <param name="heap">The concatenated row bytes.</param>
    /// <param name="starts">Each row's offset into <paramref name="heap"/>.</param>
    /// <param name="lengths">Each row's length; zero for a row that contributes nothing.</param>
    /// <returns>The trained table, or null when there is nothing to train on.</returns>
    /// <remarks>
    /// A ROW IS TWO INTS, NOT A `ReadOnlyMemory&lt;byte&gt;`. The list form allocated one 16-byte
    /// memory per row and a `List` to hold them -- on a 65 536-row column that is a megabyte on the
    /// large object heap, built to be read once and dropped, and it is a large part of why the
    /// write path spent 14% of its profile in the pool trimmer the Gen2 collector runs.
    /// </remarks>
    internal static FsstSymbols? Train(
        ReadOnlySpan<byte> heap, ReadOnlySpan<int> starts, ReadOnlySpan<int> lengths)
    {
        FsstSymbols table = new FsstSymbols();

        // The sample buffer comes from the reused training set, not from a pool: see
        // TrainingTables.Sample for why a pool of `Line` is the wrong tool.
        TrainingTables tables = TrainingTables.Take();
        try
        {
            Span<Line> sample = tables.Sample;
            MakeSample(starts, lengths, sample, out int drawn, out bool sampled);
            return drawn == 0 ? null : TrainCore(table, heap, sample[..drawn], sampled, tables);
        }
        finally
        {
            TrainingTables.Give(tables);
        }
    }

    /// <summary>One row, or one drawn run of one row, as a slice of the shared heap.</summary>
    internal readonly struct Line
    {
        internal Line(int start, int length)
        {
            Start = start;
            Length = length;
        }

        internal int Start { get; }

        internal int Length { get; }
    }

    private static FsstSymbols? TrainCore(
        FsstSymbols table, ReadOnlySpan<byte> heap, ReadOnlySpan<Line> sample, bool sampled,
        TrainingTables tables)
    {
        int[] count1 = tables.Count1;
        int[] count2 = tables.Count2;
        ulong[] pairBits = tables.PairBits;

        foreach (int generation in Generations)
        {
            // count1 is 2 kB and is cleared outright; count2 is 1 MB and is cleared through the
            // bitmap, which touches only the cells a previous generation actually incremented.
            Array.Clear(count1);
            ClearPairCounts(count2, pairBits);

            for (int i = 0; i < sample.Length; i++)
            {
                // The sample fraction is a HASH of the line index, not a prefix: taking the first
                // k lines would train generation after generation on the same head of the data.
                if (generation < 128 && (int)(Hash((ulong)i) & 127) > generation)
                {
                    continue;
                }

                Line line = sample[i];
                table.CountLine(heap.Slice(line.Start, line.Length), count1, count2, pairBits);
            }

            table.Optimize(tables, count1, count2, pairBits, generation, prune: generation >= 128 && !sampled);
        }

        if (table._count == 0)
        {
            return null;
        }

        table.OrderByLength();
        return table;
    }

    /// <summary>Compresses one value into <paramref name="destination"/>.</summary>
    /// <param name="value">The bytes to compress.</param>
    /// <param name="destination">At least <c>2 * value.Length</c> bytes.</param>
    /// <returns>How many code bytes were written.</returns>
    internal int Compress(ReadOnlySpan<byte> value, Span<byte> destination)
    {
        int read = 0;
        int written = 0;
        while (read < value.Length)
        {
            int code = FindLongest(Word(value, read), value.Length - read, out int length);
            if (code >= CodeBase)
            {
                destination[written++] = (byte)(code - CodeBase);
            }
            else
            {
                // No symbol covers this byte, so it costs two: the escape and itself. Every byte
                // remains representable, which is why the table never needs to be exhaustive.
                destination[written++] = EscapeCode;
                destination[written++] = (byte)code;
            }

            read += length;
        }

        return written;
    }

    // --------------------------------------------------------------------------------- training

    /// <summary>
    /// Draws up to <see cref="SampleTarget"/> bytes of sample, in runs of at most
    /// <see cref="SampleLine"/>.
    /// </summary>
    /// <remarks>
    /// Reproduced from the reference rather than replaced with something simpler, because the
    /// sample is what the whole algorithm sees: a sampler that took prefixes would train on
    /// headers, and one that took whole rows would let a single long row dominate.
    /// </remarks>
    private static void MakeSample(
        ReadOnlySpan<int> starts, ReadOnlySpan<int> lengths, Span<Line> into, out int drawn,
        out bool sampled)
    {
        long total = 0;
        int nonEmpty = 0;
        int rows = lengths.Length;
        for (int i = 0; i < rows; i++)
        {
            total += lengths[i];
            if (lengths[i] > 0)
            {
                nonEmpty++;
            }
        }

        drawn = 0;
        if (nonEmpty == 0)
        {
            sampled = false;
            return;
        }

        if (total < SampleTarget)
        {
            sampled = false;
            for (int i = 0; i < rows; i++)
            {
                if (lengths[i] > 0)
                {
                    into[drawn++] = new Line(starts[i], lengths[i]);
                }
            }

            return;
        }

        sampled = true;
        ulong random = Hash(4637947);
        long bytes = 0;
        while (bytes < SampleTarget)
        {
            random = Hash(random);
            int start = (int)(random % (ulong)rows);

            // The first non-empty row from `start`, wrapping. Without the wrap a corpus whose
            // tail is empty would draw nothing and loop forever.
            int found = -1;
            for (int offset = 0; offset < rows; offset++)
            {
                int candidate = start + offset;
                if (candidate >= rows)
                {
                    candidate -= rows;
                }

                if (lengths[candidate] > 0)
                {
                    found = candidate;
                    break;
                }
            }

            if (found < 0)
            {
                break;
            }

            int lineLength = lengths[found];
            int chunks = 1 + ((lineLength - 1) / SampleLine);
            random = Hash(random);
            int chunk = SampleLine * (int)(random % (ulong)chunks);
            int length = Math.Min(SampleLine, lineLength - chunk);
            into[drawn++] = new Line(starts[found] + chunk, length);
            bytes += length;
        }
    }

    /// <summary>The FSST hash, as the C++ implementation's <c>FSST_HASH</c> macro defines it.</summary>
    private static ulong Hash(ulong value) => (value * 2971215073UL) ^ (value >> 15);

    /// <summary>
    /// Compresses one sample line with the current table, counting codes and adjacent code PAIRS.
    /// </summary>
    /// <remarks>
    /// The pair counts are the whole point: a merged symbol's gain is how often its two halves
    /// occur next to each other, which is exactly what the next generation proposes as a candidate.
    /// The single-byte extension count - recorded when a matched symbol is longer than one byte -
    /// is the reference's way of keeping the option of a shorter symbol alive.
    /// </remarks>
    private void CountLine(ReadOnlySpan<byte> line, int[] count1, int[] count2, ulong[] pairBits)
    {
        int read = 0;
        int previous = CodeMask;
        while (read < line.Length)
        {
            int code = FindLongest(Word(line, read), line.Length - read, out int length);
            count1[code]++;
            CountPair(count2, pairBits, previous, code);

            if (length > 1)
            {
                int firstByte = (int)(SymbolBitsOf(code) & 0xFF);
                count1[firstByte]++;
                CountPair(count2, pairBits, previous, firstByte);
            }

            read += length;
            previous = code;
        }
    }

    /// <summary>
    /// Increments one pair count and records that the cell is live.
    /// </summary>
    /// <remarks>
    /// The bit is set unconditionally rather than only on the 0 -&gt; 1 transition: a branch here
    /// would be mispredicted about as often as it is taken, and a bit that is already set costs a
    /// redundant OR. What must never happen is the reverse - a non-zero cell with no bit - because
    /// <see cref="ClearPairCounts"/> would then leave a stale count for the next generation to
    /// read as if the sample had produced it.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CountPair(int[] count2, ulong[] pairBits, int previous, int code)
    {
        count2[(previous * (CodeMask + 1)) + code]++;
        pairBits[(previous * PairWordsPerRow) + (code >> 6)] |= 1UL << (code & 63);
    }

    /// <summary>Zeroes every pair count a previous generation touched, and the bitmap with it.</summary>
    private static void ClearPairCounts(int[] count2, ulong[] pairBits)
    {
        for (int row = 0; row <= CodeMask; row++)
        {
            int bitBase = row * PairWordsPerRow;
            int rowBase = row * (CodeMask + 1);
            for (int w = 0; w < PairWordsPerRow; w++)
            {
                ulong word = pairBits[bitBase + w];
                if (word == 0)
                {
                    continue;
                }

                pairBits[bitBase + w] = 0;
                int codeBase = w << 6;
                while (word != 0)
                {
                    count2[rowBase + codeBase + BitOperations.TrailingZeroCount(word)] = 0;
                    word &= word - 1;
                }
            }
        }
    }

    /// <summary>
    /// Rebuilds the table from the counts: every observed code is a candidate, every observed
    /// adjacent pair is a candidate for its concatenation, and the best 255 by gain win.
    /// </summary>
    /// <param name="tables">The write's working tables.</param>
    /// <param name="count1">Per-code occurrence counts.</param>
    /// <param name="count2">Per-code-pair occurrence counts.</param>
    /// <param name="pairBits">Which cells of <paramref name="count2"/> are live.</param>
    /// <param name="sampleFrac">This generation's sample fraction, out of 128.</param>
    /// <param name="prune">
    /// Whether to drop symbols that do not pay for themselves. Only on the last generation, and
    /// only when the counts are exact rather than sampled - pruning on estimates would discard
    /// symbols on the strength of noise.
    /// </param>
    private void Optimize(
        TrainingTables tables, int[] count1, int[] count2, ulong[] pairBits, int sampleFrac,
        bool prune)
    {
        Dictionary<Candidate, long> candidates = tables.Candidates;
        candidates.Clear();
        int minimum = prune ? 1 : 5 * sampleFrac / 128;

        for (int code1 = 0; code1 <= CodeMask; code1++)
        {
            int count = count1[code1];
            if (count == 0 || count < minimum)
            {
                continue;
            }

            ulong bits1 = SymbolBitsOf(code1);
            int length1 = SymbolLengthOf(code1);

            // The 8x boost on single bytes is the reference's, and its purpose is to keep common
            // bytes in the table so they never need an escape - an escape costs two bytes, which
            // is the one way FSST can make data BIGGER.
            long gain = (long)count * length1;
            if (length1 == 1)
            {
                gain *= 8;
            }

            Add(candidates, new Candidate(bits1, (byte)length1), gain);

            if (sampleFrac >= 128 || length1 == MaxSymbolLength)
            {
                continue;
            }

            // Ascending code2, exactly as the dense 512-cell scan this replaces produced it. The
            // bitmap holds a set bit for every cell CountPair incremented and for no other, so the
            // cells visited are the same ones the dense scan would not have skipped.
            int row = code1 * (CodeMask + 1);
            int bitBase = code1 * PairWordsPerRow;
            for (int w = 0; w < PairWordsPerRow; w++)
            {
                ulong word = pairBits[bitBase + w];
                int codeBase = w << 6;
                while (word != 0)
                {
                    int code2 = codeBase + BitOperations.TrailingZeroCount(word);
                    word &= word - 1;

                    int pair = count2[row + code2];
                    if (pair == 0)
                    {
                        continue;
                    }

                    int length2 = SymbolLengthOf(code2);
                    int merged = length1 + length2;
                    if (merged > MaxSymbolLength)
                    {
                        continue;
                    }

                    ulong bits = bits1 | (SymbolBitsOf(code2) << (length1 * 8));
                    Add(candidates, new Candidate(bits, (byte)merged), (long)pair * merged);
                }
            }
        }

        List<KeyValuePair<Candidate, long>> ranked = tables.Ranked;
        ranked.Clear();
        foreach (KeyValuePair<Candidate, long> entry in candidates)
        {
            ranked.Add(entry);
        }

        ranked.Sort(static (a, b) =>
        {
            int byGain = b.Value.CompareTo(a.Value);
            return byGain != 0 ? byGain : b.Key.Length.CompareTo(a.Key.Length);
        });

        // One bit per slot of the reference's matcher. A symbol it could not hold is skipped here
        // rather than removed later, so the next candidate down the ranking takes the code instead
        // and the table still fills: dropping after the table is full would spend code space on
        // nothing.
        Span<ulong> taken = stackalloc ulong[ReferenceSlotCount / 64];
        taken.Clear();

        Clear();
        for (int i = 0; i < ranked.Count && _count < MaxSymbols; i++)
        {
            Candidate candidate = ranked[i].Key;
            if (prune)
            {
                long saves = candidate.Length == 1 ? ranked[i].Value / 8 : ranked[i].Value;
                if (saves <= candidate.Length + 1)
                {
                    continue;
                }
            }

            if (candidate.Length >= 3)
            {
                int slot = ReferenceSlot(candidate.Bits);
                ref ulong word = ref taken[slot >> 6];
                ulong mask = 1UL << (slot & 63);
                if ((word & mask) != 0)
                {
                    continue;
                }

                word |= mask;
            }

            Insert(candidate.Bits, candidate.Length);
        }
    }

    private static void Add(Dictionary<Candidate, long> candidates, Candidate candidate, long gain)
    {
        // Summed rather than maximized: the same symbol can be reached through several codes, and
        // its worth is how often it occurs in total.
        candidates[candidate] = candidates.TryGetValue(candidate, out long existing)
            ? existing + gain
            : gain;
    }

    // ---------------------------------------------------------------------------------- matching

    /// <summary>
    /// The longest symbol matching the next bytes, or the literal byte when none does.
    /// </summary>
    /// <param name="word">The next up-to-8 bytes, little-endian, zero-padded.</param>
    /// <param name="remaining">How many bytes of the value are actually left.</param>
    /// <param name="length">Receives how many input bytes the match consumes.</param>
    /// <returns>
    /// <c>256 + i</c> for symbol <c>i</c>, or the byte's own value in <c>0..255</c> for a literal.
    /// </returns>
    private int FindLongest(ulong word, int remaining, out int length)
    {
        int limit = Math.Min(MaxSymbolLength, remaining);
        if (limit >= 3)
        {
            int best = -1;
            int bestLength = 2;
            for (int code = _prefixHeads[Bucket(word)]; code >= 0; code = _prefixChain[code])
            {
                int width = _lengths[code];
                if (width > limit || width <= bestLength)
                {
                    continue;
                }

                if ((word & Mask(width)) == _bits[code])
                {
                    best = code;
                    bestLength = width;
                }
            }

            if (best >= 0)
            {
                length = bestLength;
                return CodeBase + best;
            }
        }

        if (remaining >= 2)
        {
            short two = _twoByte[(int)(ushort)word];
            if (two >= 0)
            {
                length = 2;
                return CodeBase + two;
            }
        }

        short one = _oneByte[(int)(byte)word];
        length = 1;
        return one >= 0 ? CodeBase + one : (int)(byte)word;
    }

    private static ulong Mask(int width) =>
        width >= MaxSymbolLength ? ulong.MaxValue : (1UL << (width * 8)) - 1;

    /// <summary>The next up-to-8 bytes as a little-endian word, zero-padded past the end.</summary>
    private static ulong Word(ReadOnlySpan<byte> data, int at)
    {
        // One load when the bytes are there, which is every position but the last seven of a value.
        // Assembling it a byte at a time cost eight loads, eight shifts and eight ORs on the
        // innermost loop of both training and compression.
        if (at + MaxSymbolLength <= data.Length)
        {
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                data.Slice(at, MaxSymbolLength));
        }

        ulong word = 0;
        int available = Math.Min(MaxSymbolLength, data.Length - at);
        for (int i = 0; i < available; i++)
        {
            word |= (ulong)data[at + i] << (i * 8);
        }

        return word;
    }

    /// <summary>The bits of an extended code: the byte itself below 256, else the symbol.</summary>
    private ulong SymbolBitsOf(int code) => code < CodeBase ? (ulong)code : _bits[code - CodeBase];

    /// <summary>The length of an extended code: one below 256, else the symbol's.</summary>
    private int SymbolLengthOf(int code) => code < CodeBase ? 1 : _lengths[code - CodeBase];

    /// <summary>
    /// Reorders the table so the symbol lengths read 2, 3, ... 8, then 1.
    /// </summary>
    /// <remarks>
    /// NOT cosmetic, and not something the round trip through our own decoder can see: the
    /// reference VALIDATES this ordering and refuses the array outright
    /// (`vortex-fsst-0.86.1/src/array.rs`, `validate_symbol_lengths` - lengths must be
    /// non-decreasing from 2, and once a one-byte symbol appears every later symbol must be one
    /// byte). Our own reader does not check it, which is the safe direction to differ in and is
    /// exactly why this was invisible until the Rust cross-check ran: 35 of 774 written files were
    /// rejected, every one of them with an interleaved table.
    ///
    /// The reference reaches the same order for its own reasons - it splits the two-byte range by
    /// whether a symbol has a longer suffix, which its decompressor uses - but that split is
    /// invisible to the validator, so reproducing the LENGTH order is the whole requirement.
    ///
    /// Called once, after the last generation and before anything is compressed, because the
    /// reordering changes every code.
    /// </remarks>
    private void OrderByLength()
    {
        int[] order = new int[_count];
        for (int i = 0; i < _count; i++)
        {
            order[i] = i;
        }

        // One-byte symbols sort last; everything else by ascending length. Stable within a length,
        // which keeps the gain order the optimizer chose.
        Array.Sort(order, (a, b) =>
        {
            int keyA = _lengths[a] == 1 ? MaxSymbolLength + 1 : _lengths[a];
            int keyB = _lengths[b] == 1 ? MaxSymbolLength + 1 : _lengths[b];
            return keyA != keyB ? keyA.CompareTo(keyB) : a.CompareTo(b);
        });

        ulong[] bits = new ulong[_count];
        byte[] lengths = new byte[_count];
        for (int i = 0; i < _count; i++)
        {
            bits[i] = _bits[order[i]];
            lengths[i] = _lengths[order[i]];
        }

        Clear();
        for (int i = 0; i < bits.Length; i++)
        {
            Insert(bits[i], lengths[i]);
        }
    }

    /// <summary>
    /// The slot the reference's matcher gives a symbol of three bytes or more.
    /// </summary>
    /// <remarks>
    /// Its table holds one symbol per slot and refuses a second, so a symbol that lands on a taken
    /// slot is one the reference cannot hold. It drops such a symbol while training, which is why
    /// its own tables always fit; ours are built with exact dictionaries and would keep it. Keeping
    /// it costs nothing to read -- the decoder walks codes and never hashes -- but a reader that
    /// wants to push a predicate down has to turn the table back into a matcher, and a symbol it
    /// cannot place aborts that. The few bytes a dropped symbol would have saved are not worth a
    /// column no one else can filter on.
    ///
    /// The mixing is the reference's, and it is NOT <see cref="Bucket"/>'s: that one hashes the
    /// product where this hashes the value, which is a different function and would predict the
    /// wrong slot.
    /// </remarks>
    /// <param name="bits">The symbol's bytes, little-endian.</param>
    private static int ReferenceSlot(ulong bits) =>
        (int)(Hash(bits & 0xFFFFFFUL) & (ReferenceSlotCount - 1));

    private void Insert(ulong bits, byte length)
    {
        int code = _count;
        if (length == 1)
        {
            _oneByte[(int)(byte)bits] = (short)code;
        }
        else if (length == 2)
        {
            _twoByte[(int)(ushort)bits] = (short)code;
        }
        else
        {
            int bucket = Bucket(bits);
            _prefixChain[code] = _prefixHeads[bucket];
            _prefixHeads[bucket] = (short)code;
        }

        _bits[code] = bits;
        _lengths[code] = length;
        _count = code + 1;
    }

    private void Clear()
    {
        _count = 0;
        Array.Fill(_oneByte, (short)-1);
        Array.Fill(_twoByte, (short)-1);
        Array.Fill(_prefixHeads, (short)-1);
    }

    /// <summary>
    /// The bucket a symbol's first three bytes fall in.
    /// </summary>
    /// <param name="bits">The symbol's bytes, or the next eight bytes of input.</param>
    /// <remarks>
    /// Three bytes because that is the shortest symbol this table indexes, so a symbol and the
    /// input it matches always agree on them. The multiplier is the reference's own hash constant.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Bucket(ulong bits)
    {
        ulong mixed = (bits & 0xFFFFFFUL) * HashPrime;
        return (int)((mixed ^ (mixed >> 15)) & (SlotCount - 1));
    }

    /// <summary>A symbol proposed for the table: its bytes and its length.</summary>
    internal readonly struct Candidate(ulong bits, byte length) : IEquatable<Candidate>
    {
        internal ulong Bits { get; } = bits;

        internal byte Length { get; } = length;

        public bool Equals(Candidate other) => Bits == other.Bits && Length == other.Length;

        public override bool Equals(object? obj) => obj is Candidate other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Bits, Length);
    }
}
