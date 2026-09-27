using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using Vorticity.Compute;

namespace Vorticity.Writing;

/// <summary>
/// A trained FSST symbol table, and the compression over it. Every match is capped at the bytes a
/// value has left: a symbol matching into zero padding would decode to more bytes than the row held.
/// </summary>
internal sealed class FsstSymbols
{
    /// <summary>Codes 0..254 name symbols; 255 is the escape.</summary>
    internal const int MaxSymbols = 255;

    /// <summary>The escape code: the next byte of the stream is a literal.</summary>
    internal const byte EscapeCode = 255;

    /// <summary>The longest symbol the format allows.</summary>
    internal const int MaxSymbolLength = 8;

    /// <summary>The extended code space: 0..255 are literal bytes, 256+i is symbol i.</summary>
    private const int CodeBase = 256;

    /// <summary>The "no previous code" marker, and the top of the extended space.</summary>
    private const int CodeMask = 511;

    /// <summary>The generations, as fractions of 128 of the sample.</summary>
    private static readonly int[] Generations = [8, 38, 68, 98, 128];

    /// <summary><see cref="ulong"/>s per <c>count2</c> row: 512 bits of pair presence.</summary>
    private const int PairWordsPerRow = (CodeMask + 1) / 64;

    /// <summary>
    /// The trainer's working tables, a megabyte of counters reused across column-chunks. Ownership
    /// is explicit rather than thread-static, since a continuation may resume on another thread:
    /// one cached set is taken with an exchange that leaves null behind, and a second trainer
    /// allocates its own rather than sharing. Every table is cleared at the top of a generation, so
    /// reuse cannot change a symbol.
    /// </summary>
    internal sealed class TrainingTables
    {
        /// <summary>The one cached set; null while somebody holds it.</summary>
        private static TrainingTables? Cached;

        /// <summary>
        /// Takes the cached set, or a fresh one when another trainer holds it; the caller owns it
        /// outright until it calls <see cref="Give"/>.
        /// </summary>
        internal static TrainingTables Take() =>
            Interlocked.Exchange(ref Cached, null) ?? new TrainingTables();

        /// <summary>Gives a set back. Losing the race simply drops one set to the collector.</summary>
        internal static void Give(TrainingTables tables) => Volatile.Write(ref Cached, tables);

        private int[]? _count1;
        private int[]? _count2;
        private ulong[]? _pairBits;
        private Line[]? _sample;
        private Dictionary<Candidate, long>? _candidates;
        private List<KeyValuePair<Candidate, long>>? _ranked;

        /// <summary>
        /// Room for the drawn sample: at most <see cref="SampleTarget"/> lines, since either way of
        /// building the sample draws at least one byte per line. It lives here rather than in an
        /// <see cref="ArrayPool{T}"/> because a pool of <see cref="Line"/> would be a whole new
        /// shared pool with its own Gen2 trimming callback.
        /// </summary>
        internal Line[] Sample => _sample ??= new Line[SampleTarget];

        /// <summary>Per-code occurrence counts; 2 kB, cleared outright each generation.</summary>
        internal int[] Count1 => _count1 ??= new int[CodeMask + 1];

        /// <summary>Per-code-pair occurrence counts; 1 MB, cleared through <see cref="PairBits"/>.</summary>
        internal int[] Count2 => _count2 ??= new int[(CodeMask + 1) * (CodeMask + 1)];

        /// <summary>
        /// One bit per cell of <see cref="Count2"/>, set when that cell has been incremented, so
        /// that a generation clears and scans only the few thousand live cells of a dense table of
        /// 262 144. Walking a row's bitmap must keep yielding <c>code2</c> in ascending order: the
        /// candidates are sorted unstably, so the order they were proposed in decides which of two
        /// equal candidates wins the last slot, and that changes the written bytes.
        /// </summary>
        internal ulong[] PairBits => _pairBits ??= new ulong[(CodeMask + 1) * PairWordsPerRow];

        /// <summary>
        /// The candidate gain map, reused across generations and column-chunks. Reuse is
        /// order-neutral: a dictionary with no removals enumerates in insertion order, and clearing
        /// it puts it back in the state a fresh one is in.
        /// </summary>
        internal Dictionary<Candidate, long> Candidates =>
            _candidates ??= new Dictionary<Candidate, long>(512);

        internal List<KeyValuePair<Candidate, long>> Ranked =>
            _ranked ??= new List<KeyValuePair<Candidate, long>>(512);
    }

    /// <summary>Bytes of sample the trainer aims for before it stops drawing lines.</summary>
    private const int SampleTarget = 1 << 14;

    /// <summary>The longest run of one line the sampler takes.</summary>
    private const int SampleLine = 512;

    /// <summary>Slots in the reference's matcher, which decides which symbols it can hold.</summary>
    private const int ReferenceSlotCount = 2048;

    private readonly ulong[] _bits = new ulong[MaxSymbols];
    private readonly byte[] _lengths = new byte[MaxSymbols];
    private readonly short[] _oneByte = new short[256];
    private readonly short[] _twoByte = new short[65536];

    /// <summary>
    /// The symbol of three bytes or more in each slot of the reference's matcher, or -1: at most
    /// one, because <see cref="Optimize"/> skips a candidate whose slot is taken. A symbol and the
    /// input it matches share their first three bytes, and so their slot, so one probe finds the
    /// only symbol of three bytes or more that can match.
    /// </summary>
    private readonly short[] _slots = new short[ReferenceSlotCount];

    private int _count;

    /// <summary>A table no plan reads any more, kept for the next training; null while none is.</summary>
    /// <remarks>
    /// A table's lookup of two-byte symbols alone is 128 KiB, an allocation of the large-object
    /// heap, and pricing FSST trains one per column-chunk whether FSST wins or not. One is kept,
    /// taken with an exchange that leaves null behind: a second trainer builds its own.
    /// </remarks>
    private static FsstSymbols? Spare;

    private FsstSymbols()
    {
        Clear();
    }

    /// <summary>Gives the table back for a later training, once nothing reads it.</summary>
    internal void Recycle() => Volatile.Write(ref Spare, this);

    /// <summary>A cleared table: the spare one, or a new one when there is none.</summary>
    private static FsstSymbols Fresh()
    {
        FsstSymbols? spare = Interlocked.Exchange(ref Spare, null);
        if (spare is null)
        {
            return new FsstSymbols();
        }

        spare.Clear();
        return spare;
    }

    /// <summary>How many symbols the table holds, 0..255.</summary>
    internal int Count => _count;

    /// <summary>Symbol <paramref name="index"/>'s bytes, little-endian in a <c>u64</c>.</summary>
    internal ulong SymbolBits(int index) => _bits[index];

    /// <summary>Symbol <paramref name="index"/>'s length in bytes, 1..8.</summary>
    internal byte SymbolLength(int index) => _lengths[index];

    /// <summary>
    /// Trains a table on a corpus of rows, ignoring empty ones; null when nothing was learnable.
    /// </summary>
    internal static FsstSymbols? Train(IReadOnlyList<ReadOnlyMemory<byte>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        // An adapter for callers holding separate buffers; the write path calls the span form.
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
    /// Trains a table on rows held as slices of one contiguous heap; null when there is nothing to
    /// train on. A row is two ints rather than a memory, so a wide column costs no per-row object.
    /// </summary>
    internal static FsstSymbols? Train(
        ReadOnlySpan<byte> heap, ReadOnlySpan<int> starts, ReadOnlySpan<int> lengths)
    {
        FsstSymbols table = Fresh();

        TrainingTables tables = TrainingTables.Take();
        try
        {
            Span<Line> sample = tables.Sample;
            MakeSample(starts, lengths, sample, out int drawn, out bool sampled);
            FsstSymbols? trained = drawn == 0 ? null : TrainCore(table, heap, sample[..drawn], sampled, tables);
            if (trained is null)
            {
                table.Recycle();
            }

            return trained;
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
            Array.Clear(count1);
            ClearPairCounts(count2, pairBits);

            for (int i = 0; i < sample.Length; i++)
            {
                // The fraction is a hash of the line index, not a prefix: taking the first k lines
                // would train every generation on the same head of the data.
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

    /// <summary>
    /// Compresses one value, returning the code bytes written. The destination must hold at least
    /// <c>2 * value.Length</c> bytes, since every byte may need an escape.
    /// </summary>
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
                // stays representable, so the table never needs to be exhaustive.
                destination[written++] = EscapeCode;
                destination[written++] = (byte)code;
            }

            read += length;
        }

        return written;
    }

    /// <summary>
    /// Compresses every row, back to back, as <see cref="Compress"/> compresses one: each row's
    /// codes start where the one before ended, and <paramref name="offsets"/> records each start
    /// and the total.
    /// </summary>
    /// <param name="heap">
    /// The rows' bytes, followed by at least eight readable bytes of slack: every position is read
    /// as the next eight bytes in one load, however near the end of its row, and the bytes past the
    /// row never decide a symbol, whose width is capped at what the row has left.
    /// </param>
    /// <param name="starts">Each row's first byte in <paramref name="heap"/>.</param>
    /// <param name="lengths">Each row's length.</param>
    /// <param name="codes">Room for the worst case, two code bytes per input byte.</param>
    /// <param name="offsets">Room for a start per row and the total.</param>
    /// <param name="ceiling">Past this many code bytes the compression gives up.</param>
    /// <returns>The code bytes written, or -1 when they passed <paramref name="ceiling"/>.</returns>
    /// <remarks>
    /// One call for the column rather than one a row, with <see cref="FindLongest"/>'s probe written
    /// into the loop and every table read through a reference, so the width it finds stays in a
    /// register and the loop's code does not hang on whether the JIT inlines a call. The reads need
    /// no bounds: a slot is a hash masked to the slot count, a code is below
    /// <see cref="MaxSymbols"/>, the pair and byte tables are indexed by a pair and a byte, and the
    /// worst case sizes <paramref name="codes"/>, as it does for <see cref="Compress"/>.
    /// </remarks>
    internal int CompressAll(
        ReadOnlySpan<byte> heap, ReadOnlySpan<int> starts, ReadOnlySpan<int> lengths, Span<byte> codes,
        Span<int> offsets, long ceiling)
    {
        int rows = starts.Length;
        if (offsets.Length <= rows || lengths.Length < rows)
        {
            throw new ArgumentException("The offsets cannot hold a start per row and the total.", nameof(offsets));
        }

        long input = 0;
        for (int i = 0; i < rows; i++)
        {
            input += lengths[i];
            if ((uint)starts[i] > (uint)heap.Length || (long)starts[i] + lengths[i] + MaxSymbolLength > heap.Length)
            {
                throw new ArgumentException("A row with its eight bytes of slack lies outside the heap.", nameof(heap));
            }
        }

        if (codes.Length < 2 * input)
        {
            throw new ArgumentException("The codes cannot hold the worst case.", nameof(codes));
        }

        ref byte bytes = ref MemoryMarshal.GetReference(heap);
        ref byte into = ref MemoryMarshal.GetReference(codes);
        ref short slots = ref MemoryMarshal.GetArrayDataReference(_slots);
        ref byte widths = ref MemoryMarshal.GetArrayDataReference(_lengths);
        ref ulong symbols = ref MemoryMarshal.GetArrayDataReference(_bits);
        ref short pairs = ref MemoryMarshal.GetArrayDataReference(_twoByte);
        ref short singles = ref MemoryMarshal.GetArrayDataReference(_oneByte);
        nint written = 0;
        for (int row = 0; row < starts.Length; row++)
        {
            offsets[row] = (int)written;
            nint read = starts[row];
            nint end = read + lengths[row];
            while (read < end)
            {
                ulong word = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, read));
                nint left = end - read;
                if (left >= 3)
                {
                    int code = Unsafe.Add(ref slots, ReferenceSlot(word));
                    if (code >= 0)
                    {
                        // A symbol of three bytes or more: its mask keeps its 3..8 low bytes.
                        int width = Unsafe.Add(ref widths, code);
                        if (width <= left && (word & (ulong.MaxValue >> (64 - (width * 8)))) == Unsafe.Add(ref symbols, code))
                        {
                            Unsafe.Add(ref into, written++) = (byte)code;
                            read += width;
                            continue;
                        }
                    }
                }

                if (left >= 2)
                {
                    int two = Unsafe.Add(ref pairs, (nint)(ushort)word);
                    if (two >= 0)
                    {
                        Unsafe.Add(ref into, written++) = (byte)two;
                        read += 2;
                        continue;
                    }
                }

                int one = Unsafe.Add(ref singles, (nint)(byte)word);
                if (one >= 0)
                {
                    Unsafe.Add(ref into, written++) = (byte)one;
                }
                else
                {
                    Unsafe.Add(ref into, written++) = EscapeCode;
                    Unsafe.Add(ref into, written++) = (byte)word;
                }

                read++;
            }

            if (written > ceiling)
            {
                return -1;
            }
        }

        offsets[rows] = (int)written;
        return (int)written;
    }

    /// <summary>
    /// Draws up to <see cref="SampleTarget"/> bytes of sample, in runs of at most
    /// <see cref="SampleLine"/>. Runs rather than prefixes or whole rows: prefixes would train on
    /// headers, and whole rows would let a single long row dominate.
    /// </summary>
    /// <remarks>
    /// A draw takes the first non-empty row from a random one. The draws walk there while all they
    /// have walked costs less than a pass listing the non-empty rows; past that, the rows are
    /// listed once, and every draw finds its row by a binary search of the list. A column whose
    /// values sit together behind a long empty stretch would otherwise have every draw walk the
    /// stretch, and one whose values are merely apart pays no pass it does not need. Either way
    /// the row found is the same, so is the sample.
    /// </remarks>
    internal static void MakeSample(
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
        int walked = 0;
        int[]? filled = null;
        try
        {
            while (bytes < SampleTarget)
            {
                random = Hash(random);
                int start = (int)(random % (ulong)rows);

                // The first non-empty row from `start`, wrapping: without the wrap a corpus whose
                // tail is empty would draw nothing and loop forever. The row drawn is tried first,
                // which on a column with no empty rows is the whole draw.
                int found = lengths[start] > 0 ? start : Find(lengths, start, nonEmpty, ref walked, ref filled);

                int lineLength = lengths[found];
                int chunks = 1 + ((lineLength - 1) / SampleLine);
                random = Hash(random);
                int chunk = SampleLine * (int)(random % (ulong)chunks);
                int length = Math.Min(SampleLine, lineLength - chunk);
                into[drawn++] = new Line(starts[found] + chunk, length);
                bytes += length;
            }
        }
        finally
        {
            if (filled is not null)
            {
                ArrayPool<int>.Shared.Return(filled);
            }
        }
    }

    /// <summary>
    /// The first non-empty row after the empty <paramref name="start"/>, wrapping: walked while the
    /// rows walked by every draw, <paramref name="walked"/>, stay under a pass over all of them,
    /// found in the list of non-empty rows, <paramref name="filled"/>, made at the first draw past it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Find(ReadOnlySpan<int> lengths, int start, int nonEmpty, ref int walked, ref int[]? filled)
    {
        if (filled is null)
        {
            // The walk counts its steps by offset, a loop whose only carried value is that offset.
            int rows = lengths.Length;
            int budget = rows - walked;
            for (int offset = 1; offset < budget; offset++)
            {
                int candidate = start + offset;
                if (candidate >= rows)
                {
                    candidate -= rows;
                }

                if (lengths[candidate] > 0)
                {
                    walked += offset;
                    return candidate;
                }
            }

            filled = Filled(lengths, nonEmpty);
        }

        return First(filled.AsSpan(0, nonEmpty), start);
    }

    /// <summary>The first of the ascending rows <paramref name="filled"/> from <paramref name="start"/>, or the first of all past the last.</summary>
    private static int First(ReadOnlySpan<int> filled, int start)
    {
        int at = filled.BinarySearch(start);
        if (at < 0)
        {
            at = ~at;
        }

        return at < filled.Length ? filled[at] : filled[0];
    }

    /// <summary>The <paramref name="nonEmpty"/> non-empty rows in ascending order, in an array rented from the shared pool.</summary>
    private static int[] Filled(ReadOnlySpan<int> lengths, int nonEmpty)
    {
        int[] filled = ArrayPool<int>.Shared.Rent(nonEmpty);
        int count = 0;
        for (int row = 0; row < lengths.Length; row++)
        {
            if (lengths[row] > 0)
            {
                filled[count++] = row;
            }
        }

        return filled;
    }

    private static ulong Hash(ulong value) => (value * 2971215073UL) ^ (value >> 15);

    /// <summary>
    /// Compresses one sample line with the current table, counting codes and adjacent code pairs.
    /// A merged symbol's gain is how often its two halves occur next to each other, which is what
    /// the next generation proposes as a candidate; the extra single-byte count keeps the option of
    /// a shorter symbol alive.
    /// </summary>
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
    /// Increments one pair count and records that the cell is live. The bit is set unconditionally,
    /// since a non-zero cell with no bit would leave <see cref="ClearPairCounts"/> a stale count
    /// for the next generation to read as the sample's own.
    /// </summary>
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
    /// <remarks>Pruning is only safe on exact counts: on estimates it discards symbols on noise.</remarks>
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

            // Single bytes are boosted to keep the common ones in the table: an escape costs two
            // bytes, which is the one way this encoding can make data bigger.
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

            // Ascending code2: the ranking sort is unstable, so the proposal order is load-bearing.
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

        // One bit per slot of the reader's matcher. A symbol it could not hold is skipped here
        // rather than removed later, so the next candidate takes the code and the table still
        // fills; dropping after the table is full would spend code space on nothing.
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
        // Summed rather than maximized: the same symbol is reachable through several codes, and its
        // worth is how often it occurs in total.
        candidates[candidate] = candidates.TryGetValue(candidate, out long existing)
            ? existing + gain
            : gain;
    }

    /// <summary>
    /// The longest symbol matching the next bytes -- <c>256 + i</c> for symbol <c>i</c> -- or the
    /// byte's own value in <c>0..255</c> when none does. <paramref name="remaining"/> bounds the
    /// match so that no symbol reaches into the zero padding of <paramref name="word"/>.
    /// </summary>
    private int FindLongest(ulong word, int remaining, out int length)
    {
        int limit = Math.Min(MaxSymbolLength, remaining);
        if (limit >= 3)
        {
            int code = _slots[ReferenceSlot(word)];
            if (code >= 0)
            {
                int width = _lengths[code];
                if (width <= limit && (word & Mask(width)) == _bits[code])
                {
                    length = width;
                    return CodeBase + code;
                }
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
    /// Reorders the table so the symbol lengths read 2, 3, ... 8, then 1. Not cosmetic and not
    /// visible to this reader: other implementations validate that order and refuse an interleaved
    /// table outright. Called once, after the last generation and before anything is compressed,
    /// because the reordering changes every code.
    /// </summary>
    private void OrderByLength()
    {
        // A table is 255 symbols at most, so its reordering fits the stack.
        int count = _count;
        Span<int> order = stackalloc int[MaxSymbols];
        order = order[..count];
        for (int i = 0; i < count; i++)
        {
            order[i] = i;
        }

        // One-byte symbols sort last, the rest by ascending length; stable within a length, which
        // keeps the gain order the optimizer chose.
        SpanSort.Sort(order, new ByLength(_lengths));

        Span<ulong> bits = stackalloc ulong[MaxSymbols];
        Span<byte> lengths = stackalloc byte[MaxSymbols];
        for (int i = 0; i < count; i++)
        {
            bits[i] = _bits[order[i]];
            lengths[i] = _lengths[order[i]];
        }

        Clear();
        for (int i = 0; i < count; i++)
        {
            Insert(bits[i], lengths[i]);
        }
    }

    /// <summary>Symbols by length, one-byte ones last, then by their place in the table.</summary>
    private readonly struct ByLength : IComparer<int>
    {
        private readonly byte[] _lengths;

        internal ByLength(byte[] lengths) => _lengths = lengths;

        public int Compare(int a, int b)
        {
            int keyA = _lengths[a] == 1 ? MaxSymbolLength + 1 : _lengths[a];
            int keyB = _lengths[b] == 1 ? MaxSymbolLength + 1 : _lengths[b];
            return keyA != keyB ? keyA.CompareTo(keyB) : a.CompareTo(b);
        }
    }

    /// <summary>
    /// The slot a one-symbol-per-slot matcher gives a symbol of three bytes or more, so that a
    /// symbol such a matcher could not hold is never chosen: a reader pushing a predicate down has
    /// to rebuild the table as a matcher, and a symbol it cannot place stops it dead. This table is
    /// that matcher too: <see cref="CompressAll"/> and <see cref="FindLongest"/> probe the one slot.
    /// </summary>
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
            int slot = ReferenceSlot(bits);
            Debug.Assert(_slots[slot] < 0, "Optimize admits one symbol of three bytes or more a slot.");
            _slots[slot] = (short)code;
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
        Array.Fill(_slots, (short)-1);
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
