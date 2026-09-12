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
//   * The reference matches symbols through a LOSSY perfect hash table, whose insert can FAIL and
//     silently drop a symbol. That is a speed optimization with a correctness-visible side effect;
//     we use exact dictionaries keyed by length, so every chosen symbol is reachable. The result
//     is a table at least as good, never worse, and the output is still a legal FSST stream.
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
using System.Collections.Generic;

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

    /// <summary>
    /// The trainer's counting tables, kept per thread instead of allocated per call.
    /// </summary>
    /// <remarks>
    /// <c>count2</c> is 65 536 ints - 256 kB - and <see cref="Train"/> is called ONCE PER
    /// COLUMN-CHUNK, so writing a 65 536-row file allocated and dropped about 16 MB of counting
    /// table. They are cleared at the top of every generation regardless, so reusing them cannot
    /// change a single symbol: the first <c>Array.Clear</c> of a freshly allocated array was always
    /// redundant work on memory the runtime had just zeroed.
    ///
    /// THREAD-STATIC RATHER THAN POOLED, deliberately. <c>ArrayPool&lt;T&gt;.Shared</c> is
    /// process-global, and this repository has already measured what that costs a path that rents
    /// many blocks at once - a scattered take allocated a fresh owner per split once its rents moved
    /// into an exhausted size class. The price here is 260 kB retained per thread that has ever
    /// written a file, which is bounded, predictable, and does not interact with anything else.
    /// </remarks>
    [System.ThreadStatic]
    private static int[]? ScratchOne;

    /// <inheritdoc cref="ScratchOne"/>
    [System.ThreadStatic]
    private static int[]? ScratchTwo;

    /// <summary>Bytes of sample the trainer aims for before it stops drawing lines.</summary>
    private const int SampleTarget = 1 << 14;

    /// <summary>The longest run of one line the sampler takes.</summary>
    private const int SampleLine = 512;

    private readonly ulong[] _bits = new ulong[MaxSymbols];
    private readonly byte[] _lengths = new byte[MaxSymbols];
    private readonly short[] _oneByte = new short[256];
    private readonly short[] _twoByte = new short[65536];
    private readonly Dictionary<ulong, int>[] _byLength = new Dictionary<ulong, int>[MaxSymbolLength + 1];
    private int _count;

    private FsstSymbols()
    {
        for (int i = 3; i <= MaxSymbolLength; i++)
        {
            _byLength[i] = [];
        }

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
        FsstSymbols table = new FsstSymbols();
        List<ReadOnlyMemory<byte>> sample = MakeSample(rows, out bool sampled);
        if (sample.Count == 0)
        {
            return null;
        }

        int[] count1 = ScratchOne ??= new int[CodeMask + 1];
        int[] count2 = ScratchTwo ??= new int[(CodeMask + 1) * (CodeMask + 1)];

        foreach (int generation in Generations)
        {
            Array.Clear(count1);
            Array.Clear(count2);

            for (int i = 0; i < sample.Count; i++)
            {
                // The sample fraction is a HASH of the line index, not a prefix: taking the first
                // k lines would train generation after generation on the same head of the data.
                if (generation < 128 && (int)(Hash((ulong)i) & 127) > generation)
                {
                    continue;
                }

                table.CountLine(sample[i].Span, count1, count2);
            }

            table.Optimize(count1, count2, generation, prune: generation >= 128 && !sampled);
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
    private static List<ReadOnlyMemory<byte>> MakeSample(
        IReadOnlyList<ReadOnlyMemory<byte>> rows, out bool sampled)
    {
        long total = 0;
        int nonEmpty = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            total += rows[i].Length;
            if (rows[i].Length > 0)
            {
                nonEmpty++;
            }
        }

        List<ReadOnlyMemory<byte>> sample = [];
        if (nonEmpty == 0)
        {
            sampled = false;
            return sample;
        }

        if (total < SampleTarget)
        {
            sampled = false;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Length > 0)
                {
                    sample.Add(rows[i]);
                }
            }

            return sample;
        }

        sampled = true;
        ulong random = Hash(4637947);
        long drawn = 0;
        while (drawn < SampleTarget)
        {
            random = Hash(random);
            int start = (int)(random % (ulong)rows.Count);

            // The first non-empty row from `start`, wrapping. Without the wrap a corpus whose
            // tail is empty would draw nothing and loop forever.
            int found = -1;
            for (int offset = 0; offset < rows.Count; offset++)
            {
                int candidate = start + offset;
                if (candidate >= rows.Count)
                {
                    candidate -= rows.Count;
                }

                if (rows[candidate].Length > 0)
                {
                    found = candidate;
                    break;
                }
            }

            if (found < 0)
            {
                break;
            }

            ReadOnlyMemory<byte> line = rows[found];
            int chunks = 1 + ((line.Length - 1) / SampleLine);
            random = Hash(random);
            int chunk = SampleLine * (int)(random % (ulong)chunks);
            int length = Math.Min(SampleLine, line.Length - chunk);
            sample.Add(line.Slice(chunk, length));
            drawn += length;
        }

        return sample;
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
    private void CountLine(ReadOnlySpan<byte> line, int[] count1, int[] count2)
    {
        int read = 0;
        int previous = CodeMask;
        while (read < line.Length)
        {
            int code = FindLongest(Word(line, read), line.Length - read, out int length);
            count1[code]++;
            count2[(previous * (CodeMask + 1)) + code]++;

            if (length > 1)
            {
                int firstByte = (int)(SymbolBitsOf(code) & 0xFF);
                count1[firstByte]++;
                count2[(previous * (CodeMask + 1)) + firstByte]++;
            }

            read += length;
            previous = code;
        }
    }

    /// <summary>
    /// Rebuilds the table from the counts: every observed code is a candidate, every observed
    /// adjacent pair is a candidate for its concatenation, and the best 255 by gain win.
    /// </summary>
    /// <param name="count1">Per-code occurrence counts.</param>
    /// <param name="count2">Per-code-pair occurrence counts.</param>
    /// <param name="sampleFrac">This generation's sample fraction, out of 128.</param>
    /// <param name="prune">
    /// Whether to drop symbols that do not pay for themselves. Only on the last generation, and
    /// only when the counts are exact rather than sampled - pruning on estimates would discard
    /// symbols on the strength of noise.
    /// </param>
    private void Optimize(int[] count1, int[] count2, int sampleFrac, bool prune)
    {
        Dictionary<Candidate, long> candidates = new Dictionary<Candidate, long>(512);
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

            int row = code1 * (CodeMask + 1);
            for (int code2 = 0; code2 <= CodeMask; code2++)
            {
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

        List<KeyValuePair<Candidate, long>> ranked = [.. candidates];
        ranked.Sort(static (a, b) =>
        {
            int byGain = b.Value.CompareTo(a.Value);
            return byGain != 0 ? byGain : b.Key.Length.CompareTo(a.Key.Length);
        });

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
        int longest = Math.Min(MaxSymbolLength, remaining);
        for (int width = longest; width >= 3; width--)
        {
            if (_byLength[width].TryGetValue(word & Mask(width), out int code))
            {
                length = width;
                return CodeBase + code;
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
            // Exact, not lossy: the reference's perfect hash table can refuse an insert and drop
            // the symbol, which is a speed trade we have no reason to take in a writer.
            _byLength[length][bits] = code;
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
        for (int i = 3; i <= MaxSymbolLength; i++)
        {
            _byLength[i].Clear();
        }
    }

    /// <summary>A symbol proposed for the table: its bytes and its length.</summary>
    private readonly struct Candidate(ulong bits, byte length) : IEquatable<Candidate>
    {
        internal ulong Bits { get; } = bits;

        internal byte Length { get; } = length;

        public bool Equals(Candidate other) => Bits == other.Bits && Length == other.Length;

        public override bool Equals(object? obj) => obj is Candidate other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Bits, Length);
    }
}
