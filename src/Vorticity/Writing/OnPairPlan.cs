using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// A text column's OnPair encoding: a dictionary of at most 4 096 tokens of one to sixteen bytes --
/// the 256 single bytes and the adjacent pairs a sample of the column repeats -- and each row spelt
/// by the longest tokens that begin it, one twelve-bit code each. Built while choosing, as FSST's
/// plan is, since deciding that it pays means encoding the column.
/// </summary>
/// <remarks>
/// The training is the reference's: a scan of a random sixth of the column's bytes, each row cut
/// into its longest known tokens, where an adjacent pair seen often enough becomes a token of its
/// own, the bar rising and falling so that the dictionary fills as the scan budget runs out; the
/// tokens are then sorted by their bytes and numbered in that order. The sample is drawn by a
/// seeded generator of this library's own, so the bytes are the same from one run to the next,
/// though not the reference's: any dictionary holding the 256 single bytes decodes.
/// <para>
/// Every table is rented and handed back: the matcher's slots, the pair counts, the dictionary and
/// the code stream, so that a column priced and turned down leaves nothing behind.
/// </para>
/// </remarks>
internal sealed class OnPairPlan
{
    /// <summary>The longest token, in bytes; a reader copies this many from each token's start.</summary>
    internal const int MaxTokenSize = 16;

    /// <summary>The codes' width: 2^12 tokens, the reference's default dictionary.</summary>
    private const int DictionaryBits = 12;

    /// <summary>The share of the column's bytes the training scans.</summary>
    private const double SampleFraction = 0.15;

    private byte[] _dictionary;
    private int[] _dictionaryOffsets;
    private ushort[] _codes;
    private int[] _codeOffsets;
    private int[] _lengths;

    private OnPairPlan(
        byte[] dictionary, int dictionaryBytes, int[] dictionaryOffsets, int tokens, ushort[] codes, int codeCount,
        int[] codeOffsets, int[] lengths, int rows, long encodedSize)
    {
        _dictionary = dictionary;
        DictionaryBytes = dictionaryBytes;
        _dictionaryOffsets = dictionaryOffsets;
        Tokens = tokens;
        _codes = codes;
        CodeCount = codeCount;
        _codeOffsets = codeOffsets;
        _lengths = lengths;
        Rows = rows;
        EncodedSize = encodedSize;
    }

    /// <summary>The dictionary's tokens.</summary>
    internal int Tokens { get; }

    /// <summary>The dictionary's bytes, its trailing read padding included.</summary>
    internal int DictionaryBytes { get; }

    /// <summary>The codes of every row, end to end.</summary>
    internal int CodeCount { get; }

    /// <summary>The chunk's rows.</summary>
    internal int Rows { get; }

    /// <summary>The bytes this encoding will occupy, its dictionary and children included.</summary>
    internal long EncodedSize { get; }

    /// <summary>The tokens end to end, then zeros to sixteen bytes past the last token's start.</summary>
    internal ReadOnlySpan<byte> Dictionary => _dictionary.AsSpan(0, DictionaryBytes);

    /// <summary>Where each token starts, and the end of the last: <see cref="Tokens"/> + 1 entries.</summary>
    internal ReadOnlySpan<int> DictionaryOffsets => _dictionaryOffsets.AsSpan(0, Tokens + 1);

    /// <summary>The code stream.</summary>
    internal ReadOnlySpan<ushort> Codes => _codes.AsSpan(0, CodeCount);

    /// <summary>Where each row's codes begin, and the end of the last: <see cref="Rows"/> + 1 entries.</summary>
    internal ReadOnlySpan<int> CodeOffsets => _codeOffsets.AsSpan(0, Rows + 1);

    /// <summary>Each row's decoded length, zero for a null row.</summary>
    internal ReadOnlySpan<int> Lengths => _lengths.AsSpan(0, Rows);

    /// <summary>Hands every rental back, once; the plan is spent.</summary>
    internal void Release()
    {
        Return(ref _dictionary);
        Return(ref _dictionaryOffsets);
        Return(ref _codes);
        Return(ref _codeOffsets);
        Return(ref _lengths);
    }

    /// <summary>
    /// Trains a dictionary on the column and encodes it, or returns null when the encoding does not
    /// come in under <paramref name="sizeCeiling"/>. The training's own sample is encoded first and
    /// its size scaled to the column: a column that would miss the ceiling by a twentieth is turned
    /// down on that, before the pass over every row.
    /// </summary>
    internal static OnPairPlan? TryBuild(CanonicalArena arena, int nodeIndex, long sizeCeiling)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        int rows = node.Length;

        // Each row costs a code offset and a length of a byte at least: a floor, not an estimate.
        if (sizeCeiling <= 0 || rows == 0 || (2L * rows) + 1 > sizeCeiling)
        {
            return null;
        }

        ValidityReader valid = ValidityReader.Of(arena, node.Validity);
        int[] lengths = ArrayPool<int>.Shared.Rent(rows);
        long plain = ViewHeap.Lengths(node, valid, lengths.AsSpan(0, rows));
        if (plain == 0 || plain > int.MaxValue - ViewHeap.Slack)
        {
            ArrayPool<int>.Shared.Return(lengths);
            return null;
        }

        int total = (int)plain;
        byte[] heap = ArrayPool<byte>.Shared.Rent(total + ViewHeap.Slack);
        int[] starts = ArrayPool<int>.Shared.Rent(rows);
        Trainer? trainer = null;
        ushort[]? codes = null;
        int[]? codeOffsets = null;
        bool kept = false;
        try
        {
            ViewHeap.Gather(node, lengths.AsSpan(0, rows), heap, starts.AsSpan(0, rows));
            trainer = Trainer.Rent();
            int sampled = trainer.Train(heap, starts.AsSpan(0, rows), lengths.AsSpan(0, rows), total, out int sampleCodes);

            // The sample's codes, scaled to the column's bytes: a bound it has to clear with room.
            int width = CodeWidth(trainer.Tokens);
            long fixedBytes = FixedBytes(trainer, rows, lengths.AsSpan(0, rows));
            if (sampled > 0 && sizeCeiling < long.MaxValue / 2)
            {
                long estimated = fixedBytes + ((long)sampleCodes * total / sampled * width / 8);
                if (estimated > sizeCeiling + (sizeCeiling / 20))
                {
                    return null;
                }
            }

            codes = ArrayPool<ushort>.Shared.Rent(total);
            codeOffsets = ArrayPool<int>.Shared.Rent(rows + 1);

            // The codes the ceiling leaves room for; a pin's ceiling leaves room for any.
            long room = sizeCeiling - fixedBytes;
            long budget = room > long.MaxValue / 8 ? long.MaxValue : room * 8 / width;
            int count = trainer.Parse(heap, starts.AsSpan(0, rows), lengths.AsSpan(0, rows), codes, codeOffsets, budget);
            if (count < 0)
            {
                return null;
            }

            long encoded = fixedBytes + (((long)count * width) + 7) / 8 + FsstPlan.RowTableBytes(rows + 1, count);
            if (encoded > sizeCeiling)
            {
                return null;
            }

            (byte[] dictionary, int dictionaryBytes, int[] dictionaryOffsets) = trainer.TakeDictionary();
            OnPairPlan plan = new OnPairPlan(
                dictionary, dictionaryBytes, dictionaryOffsets, trainer.Tokens, codes, count, codeOffsets, lengths, rows, encoded);
            kept = true;
            return plan;
        }
        finally
        {
            trainer?.Recycle();
            if (!kept)
            {
                if (codes is not null)
                {
                    ArrayPool<ushort>.Shared.Return(codes);
                }

                if (codeOffsets is not null)
                {
                    ArrayPool<int>.Shared.Return(codeOffsets);
                }

                ArrayPool<int>.Shared.Return(lengths);
            }

            ArrayPool<int>.Shared.Return(starts);
            ArrayPool<byte>.Shared.Return(heap);
        }
    }

    /// <summary>The bits a code takes for a dictionary of <paramref name="tokens"/>.</summary>
    private static int CodeWidth(int tokens) => Math.Max(1, 32 - BitOperations.LeadingZeroCount((uint)Math.Max(tokens - 1, 1)));

    /// <summary>What the encoding costs besides its codes and code offsets: the dictionary, its offsets, the lengths.</summary>
    private static long FixedBytes(Trainer trainer, int rows, ReadOnlySpan<int> lengths) =>
        trainer.PaddedBytes
        + FsstPlan.RowTableBytes(trainer.Tokens + 1, trainer.PaddedBytes)
        + FsstPlan.RowTableBytes(rows, FsstPlan.MaxOf(lengths));

    private static void Return<T>(ref T[] rental)
    {
        T[] array = rental;
        rental = [];
        if (array.Length > 0)
        {
            ArrayPool<T>.Shared.Return(array);
        }
    }

    /// <summary>
    /// The training and the parse, over tables rented once and kept by the writer's thread between
    /// columns: the matcher's slots, the pair counts, the dictionary being built.
    /// </summary>
    private sealed class Trainer
    {
        private const int Capacity = 1 << DictionaryBits;

        /// <summary>Slots of the token table, four to a token so that a probe almost always ends on its first.</summary>
        private const int TokenSlots = Capacity * 4;

        /// <summary>Slots of the pair table: the reference's cap of eight pairs a token, at half load.</summary>
        private const int PairSlots = Capacity * 16;

        /// <summary>A trainer no plan uses any more, kept for the next column; null while none is.</summary>
        /// <remarks>
        /// Its tables are most of a megabyte, and pricing OnPair trains once per text chunk FSST
        /// could take, whether OnPair wins or not. One is kept, taken with an exchange that leaves
        /// null behind, as FSST keeps its table: a second writer builds its own.
        /// </remarks>
        private static Trainer? Spare;

        // The token table: each token's bytes as two little-endian words masked to its length, the
        // length (zero for an empty slot) and the code.
        private readonly ulong[] _low = new ulong[TokenSlots];
        private readonly ulong[] _high = new ulong[TokenSlots];
        private readonly byte[] _length = new byte[TokenSlots];
        private readonly ushort[] _code = new ushort[TokenSlots];

        // The eight-byte prefixes of the tokens longer than eight bytes, which a probe for those
        // lengths is spared when the input does not start with one.
        private readonly ulong[] _prefix = new ulong[TokenSlots];
        private readonly bool[] _prefixUsed = new bool[TokenSlots];

        // The pair counts: a pair of codes, and how often the scan met it, saturating at 255.
        private readonly uint[] _pairKey = new uint[PairSlots];
        private readonly byte[] _pairCount = new byte[PairSlots];

        // The dictionary, in the order its tokens were found.
        private readonly byte[] _bytes = new byte[(Capacity * MaxTokenSize) + MaxTokenSize];
        private readonly int[] _offsets = new int[Capacity + 1];
        private readonly int[] _order = new int[Capacity];

        private int _tokens;
        private int _pairs;
        private int _longTokens;
        private uint _presentLengths;
        private int _paddedBytes;

        /// <summary>The tokens the dictionary holds.</summary>
        internal int Tokens => _tokens;

        /// <summary>The sorted dictionary's bytes, its read padding included.</summary>
        internal int PaddedBytes => _paddedBytes;

        internal static Trainer Rent()
        {
            Trainer trainer = Interlocked.Exchange(ref Spare, null) ?? new Trainer();
            trainer.Reset();
            return trainer;
        }

        internal void Recycle() => Volatile.Write(ref Spare, this);

        private void Reset()
        {
            Array.Clear(_length);
            Array.Clear(_prefixUsed);
            Array.Clear(_pairCount);
            _pairs = 0;
            _tokens = 0;
            _longTokens = 0;
            _presentLengths = 0;
            _offsets[0] = 0;
            for (int b = 0; b < 256; b++)
            {
                _bytes[b] = (byte)b;
                _offsets[b + 1] = b + 1;
                Insert((ulong)b, 0, 1, (ushort)b);
            }

            _tokens = 256;
        }

        /// <summary>
        /// Builds the dictionary from a sample of the rows and sorts it, returning the sample's bytes
        /// and, through <paramref name="sampleCodes"/>, the codes it takes under the final dictionary.
        /// </summary>
        internal int Train(byte[] heap, ReadOnlySpan<int> starts, ReadOnlySpan<int> lengths, int total, out int sampleCodes)
        {
            int rows = starts.Length;
            long budget = (long)(total * SampleFraction);
            int[] order = ArrayPool<int>.Shared.Rent(rows);
            try
            {
                int selected = SampleOrder(order.AsSpan(0, rows), lengths, budget);
                Discover(heap, starts, lengths, order.AsSpan(0, selected), budget, total);
                Sort();

                // The sample again, under the sorted dictionary: what the column's codes are scaled from.
                int sampled = 0;
                sampleCodes = 0;
                for (int i = 0; i < selected && sampled <= budget; i++)
                {
                    int row = order[i];
                    sampled += lengths[row];
                    sampleCodes += CountCodes(heap, starts[row], lengths[row]);
                }

                return sampled;
            }
            finally
            {
                ArrayPool<int>.Shared.Return(order);
            }
        }

        /// <summary>
        /// Encodes every row, returning the codes written, or -1 once they pass
        /// <paramref name="budget"/>.
        /// </summary>
        internal int Parse(byte[] heap, ReadOnlySpan<int> starts, ReadOnlySpan<int> lengths, ushort[] codes, int[] offsets, long budget)
        {
            int count = 0;
            offsets[0] = 0;
            for (int row = 0; row < starts.Length; row++)
            {
                int at = starts[row];
                int end = at + lengths[row];
                while (at < end)
                {
                    int match = Longest(heap, at, end - at, out ushort code);
                    codes[count++] = code;
                    at += match;
                }

                if (count > budget)
                {
                    return -1;
                }

                offsets[row + 1] = count;
            }

            return count;
        }

        /// <summary>The sorted dictionary's rentals, handed to the plan.</summary>
        internal (byte[] Bytes, int Length, int[] Offsets) TakeDictionary()
        {
            byte[] bytes = ArrayPool<byte>.Shared.Rent(_paddedBytes);
            int[] offsets = ArrayPool<int>.Shared.Rent(_tokens + 1);
            _bytes.AsSpan(0, _paddedBytes).CopyTo(bytes);
            _offsets.AsSpan(0, _tokens + 1).CopyTo(offsets);
            return (bytes, _paddedBytes, offsets);
        }

        /// <summary>
        /// The rows the training scans, in the order it scans them: a partial shuffle of every row,
        /// three tenths of them and a thousand more, doubled until their bytes pass the budget.
        /// </summary>
        private static int SampleOrder(Span<int> order, ReadOnlySpan<int> lengths, long budget)
        {
            int rows = order.Length;
            for (int i = 0; i < rows; i++)
            {
                order[i] = i;
            }

            ulong state = 42;
            int shuffled = 0;
            int wanted = (int)Math.Min(rows, (long)(Math.Min(SampleFraction * 2, 1.0) * rows) + 1024);
            long bytes = 0;
            while (true)
            {
                for (; shuffled < wanted; shuffled++)
                {
                    int pick = shuffled + (int)(Next(ref state) % (ulong)(rows - shuffled));
                    (order[shuffled], order[pick]) = (order[pick], order[shuffled]);
                    bytes += lengths[order[shuffled]];
                }

                if (bytes > budget || wanted == rows)
                {
                    return wanted;
                }

                wanted = (int)Math.Min(rows, (long)wanted * 2);
            }
        }

        private static ulong Next(ref ulong state)
        {
            ulong z = state += 0x9E37_79B9_7F4A_7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>
        /// The reference's scan: each sampled row cut into its longest tokens, a pair of adjacent
        /// tokens counted, and a pair counted as often as the bar becomes a token of its own, which
        /// the scan then carries on from as one token.
        /// </summary>
        private void Discover(byte[] heap, ReadOnlySpan<int> starts, ReadOnlySpan<int> lengths, ReadOnlySpan<int> order, long budget, int total)
        {
            Threshold bar = new Threshold(Capacity - 256, budget);
            for (int i = 0; i < order.Length; i++)
            {
                int row = order[i];
                int at = starts[row];
                int end = at + lengths[row];
                if (at == end)
                {
                    continue;
                }

                int previousLength = Longest(heap, at, end - at, out ushort previous);
                bar.Scanned(previousLength);
                if (bar.Exhausted)
                {
                    return;
                }

                int position = at + previousLength;
                while (position < end)
                {
                    int currentLength = Longest(heap, position, end - position, out ushort current);
                    bar.Scanned(currentLength);
                    if (bar.Exhausted)
                    {
                        return;
                    }

                    int pairLength = previousLength + currentLength;
                    if (pairLength <= MaxTokenSize)
                    {
                        uint key = ((uint)previous << 16) | current;
                        if (Count(key) >= bar.Value)
                        {
                            ushort code = Add(heap.AsSpan(position - previousLength, pairLength));
                            if (_tokens == Capacity)
                            {
                                return;
                            }

                            bar.Created();
                            Forget(key);
                            previous = code;
                            previousLength = pairLength;
                            position += currentLength;
                            continue;
                        }
                    }

                    previous = current;
                    previousLength = currentLength;
                    position += currentLength;
                }
            }
        }

        /// <summary>Appends a token found by the scan and makes it matchable, returning its code.</summary>
        private ushort Add(ReadOnlySpan<byte> token)
        {
            ushort code = (ushort)_tokens;
            int start = _offsets[_tokens];
            token.CopyTo(_bytes.AsSpan(start));
            _offsets[_tokens + 1] = start + token.Length;
            _tokens++;
            (ulong low, ulong high) = Words(token);
            Insert(low, high, token.Length, code);
            return code;
        }

        /// <summary>
        /// Sorts the tokens by their bytes, renumbers them in that order, pads the dictionary for
        /// its readers and rebuilds the matcher over the new numbers.
        /// </summary>
        private void Sort()
        {
            int tokens = _tokens;
            Span<int> order = _order.AsSpan(0, tokens);
            for (int i = 0; i < tokens; i++)
            {
                order[i] = i;
            }

            order.Sort(new ByBytes(_bytes, _offsets));

            // Laid out afresh in sorted order; the old layout is read through a copy.
            int used = _offsets[tokens];
            byte[] old = ArrayPool<byte>.Shared.Rent(used);
            int[] oldOffsets = ArrayPool<int>.Shared.Rent(tokens + 1);
            try
            {
                _bytes.AsSpan(0, used).CopyTo(old);
                _offsets.AsSpan(0, tokens + 1).CopyTo(oldOffsets);
                Array.Clear(_length);
                Array.Clear(_prefixUsed);
                _longTokens = 0;
                _presentLengths = 0;
                int at = 0;
                for (int i = 0; i < tokens; i++)
                {
                    int from = oldOffsets[order[i]];
                    int length = oldOffsets[order[i] + 1] - from;
                    ReadOnlySpan<byte> token = old.AsSpan(from, length);
                    token.CopyTo(_bytes.AsSpan(at));
                    _offsets[i] = at;
                    at += length;
                    (ulong low, ulong high) = Words(token);
                    Insert(low, high, length, (ushort)i);
                }

                _offsets[tokens] = at;

                // A reader copies sixteen bytes from every token's start, the last one's included.
                int padded = Math.Max(at, _offsets[tokens - 1] + MaxTokenSize);
                _bytes.AsSpan(at, padded - at).Clear();
                _paddedBytes = padded;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(old);
                ArrayPool<int>.Shared.Return(oldOffsets);
            }
        }

        /// <summary>The codes a row takes under the current dictionary, without writing them.</summary>
        private int CountCodes(byte[] heap, int at, int length)
        {
            int end = at + length;
            int count = 0;
            while (at < end)
            {
                at += Longest(heap, at, end - at, out _);
                count++;
            }

            return count;
        }

        // -------------------------------------------------------------------- the token table

        /// <summary>
        /// The longest token the <paramref name="remaining"/> bytes at <paramref name="at"/> begin
        /// with, and its code. Every single byte is a token, so there always is one.
        /// </summary>
        /// <remarks>
        /// Sixteen bytes are read whatever remains, which the heap's slack keeps in bounds, and each
        /// probe masks them to the length it asks about, so the bytes past the row never count.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int Longest(byte[] heap, int at, int remaining, out ushort code)
        {
            ref byte start = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(heap), at);
            ulong low = Unsafe.ReadUnaligned<ulong>(ref start);
            ulong high = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, 8));
            int window = Math.Min(remaining, MaxTokenSize);
            uint lengths = _presentLengths & ((2u << window) - 1);

            // The long tokens only when one starts with these eight bytes.
            if (window > 8 && _longTokens > 0 && !HasPrefix(low))
            {
                lengths &= 0x1FF;
            }

            while (lengths > 1)
            {
                int length = 31 - BitOperations.LeadingZeroCount(lengths);
                lengths &= ~(1u << length);
                ulong l = length >= 8 ? low : low & ((1UL << (length * 8)) - 1);
                ulong h = length > 8 ? high & Mask(length - 8) : 0;
                if (Find(l, h, length, out code))
                {
                    return length;
                }
            }

            // Every single byte is a token.
            return Find(low & 0xFF, 0, 1, out code) ? 1 : throw new InvalidOperationException("OnPair's dictionary lost a single byte.");
        }

        private static ulong Mask(int bytes) => bytes >= 8 ? ulong.MaxValue : (1UL << (bytes * 8)) - 1;

        private static (ulong Low, ulong High) Words(ReadOnlySpan<byte> token)
        {
            Span<byte> buffer = stackalloc byte[MaxTokenSize];
            buffer.Clear();
            token.CopyTo(buffer);
            return (BinaryPrimitives.ReadUInt64LittleEndian(buffer), BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..]));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Slot(ulong low, ulong high, int length, int slots)
        {
            ulong hash = (low * 0x9E37_79B9_7F4A_7C15UL) ^ (high * 0xC2B2_AE3D_27D4_EB4FUL) ^ ((ulong)length * 0x1656_67B1_9E37_79F9UL);
            return (int)((hash ^ (hash >> 32)) & (uint)(slots - 1));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool Find(ulong low, ulong high, int length, out ushort code)
        {
            int slot = Slot(low, high, length, TokenSlots);
            while (true)
            {
                byte stored = _length[slot];
                if (stored == 0)
                {
                    code = 0;
                    return false;
                }

                if (stored == length && _low[slot] == low && _high[slot] == high)
                {
                    code = _code[slot];
                    return true;
                }

                slot = (slot + 1) & (TokenSlots - 1);
            }
        }

        /// <summary>Makes a token matchable, a token of the same bytes already there taking the new code.</summary>
        private void Insert(ulong low, ulong high, int length, ushort code)
        {
            int slot = Slot(low, high, length, TokenSlots);
            while (_length[slot] != 0 && !(_length[slot] == length && _low[slot] == low && _high[slot] == high))
            {
                slot = (slot + 1) & (TokenSlots - 1);
            }

            _low[slot] = low;
            _high[slot] = high;
            _length[slot] = (byte)length;
            _code[slot] = code;
            _presentLengths |= 1u << length;
            if (length > 8)
            {
                AddPrefix(low);
                _longTokens++;
            }
        }

        private bool HasPrefix(ulong prefix)
        {
            int slot = Slot(prefix, 0, 0, TokenSlots);
            while (_prefixUsed[slot])
            {
                if (_prefix[slot] == prefix)
                {
                    return true;
                }

                slot = (slot + 1) & (TokenSlots - 1);
            }

            return false;
        }

        private void AddPrefix(ulong prefix)
        {
            int slot = Slot(prefix, 0, 0, TokenSlots);
            while (_prefixUsed[slot])
            {
                if (_prefix[slot] == prefix)
                {
                    return;
                }

                slot = (slot + 1) & (TokenSlots - 1);
            }

            _prefix[slot] = prefix;
            _prefixUsed[slot] = true;
        }

        // -------------------------------------------------------------------- the pair counts

        /// <summary>Counts one more sighting of a pair, returning how often it has been seen.</summary>
        /// <remarks>
        /// The table is not grown: once three quarters full, a pair it has not seen is counted once
        /// and not kept. A sample in random order meets the frequent pairs early, which are the
        /// ones that become tokens, and the table's memory stays what it is.
        /// </remarks>
        private int Count(uint key)
        {
            int slot = PairSlot(key);
            while (_pairCount[slot] != 0)
            {
                if (_pairKey[slot] == key)
                {
                    if (_pairCount[slot] < byte.MaxValue)
                    {
                        _pairCount[slot]++;
                    }

                    return _pairCount[slot];
                }

                slot = (slot + 1) & (PairSlots - 1);
            }

            if (_pairs < PairSlots / 4 * 3)
            {
                _pairKey[slot] = key;
                _pairCount[slot] = 1;
                _pairs++;
            }

            return 1;
        }

        /// <summary>Drops a pair that became a token, shifting back the pairs its slot displaced.</summary>
        private void Forget(uint key)
        {
            int slot = PairSlot(key);
            while (_pairCount[slot] != 0 && _pairKey[slot] != key)
            {
                slot = (slot + 1) & (PairSlots - 1);
            }

            if (_pairCount[slot] == 0)
            {
                return;
            }

            _pairs--;

            // Backward-shift deletion: every later entry of the run that could sit earlier moves up.
            int hole = slot;
            int next = (hole + 1) & (PairSlots - 1);
            while (_pairCount[next] != 0)
            {
                int home = PairSlot(_pairKey[next]);
                if (((next - home) & (PairSlots - 1)) >= ((next - hole) & (PairSlots - 1)))
                {
                    _pairKey[hole] = _pairKey[next];
                    _pairCount[hole] = _pairCount[next];
                    hole = next;
                }

                next = (next + 1) & (PairSlots - 1);
            }

            _pairCount[hole] = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PairSlot(uint key)
        {
            ulong hash = key * 0x9E37_79B9_7F4A_7C15UL;
            return (int)((hash ^ (hash >> 32)) & (PairSlots - 1));
        }

        /// <summary>Orders token numbers by their bytes, a prefix before the tokens it begins.</summary>
        private readonly struct ByBytes(byte[] bytes, int[] offsets) : IComparer<int>
        {
            public int Compare(int x, int y) =>
                bytes.AsSpan(offsets[x], offsets[x + 1] - offsets[x]).SequenceCompareTo(
                    bytes.AsSpan(offsets[y], offsets[y + 1] - offsets[y]));
        }

        /// <summary>
        /// The reference's adaptive bar: raised when tokens come faster than the dictionary's room
        /// over the budget's bytes allows, lowered when they come slower, checked every so many
        /// tokens.
        /// </summary>
        private struct Threshold
        {
            private readonly int _capacity;
            private readonly long _budget;
            private readonly int _interval;
            private int _created;
            private long _scanned;
            private int _createdAtCheck;
            private long _scannedAtCheck;
            private int _checkpoint;

            internal Threshold(int capacity, long budget)
            {
                _capacity = capacity;
                _budget = budget;
                _interval = Math.Max(capacity / 128, 64);
                _checkpoint = _interval;
                Value = 2;
                _created = 0;
                _scanned = 0;
                _createdAtCheck = 0;
                _scannedAtCheck = 0;
            }

            internal int Value { get; private set; }

            internal readonly bool Exhausted => _scanned > _budget;

            internal void Scanned(int bytes) => _scanned += bytes;

            internal void Created()
            {
                _created++;
                if (_created >= _checkpoint)
                {
                    Rebalance();
                }
            }

            private void Rebalance()
            {
                long createdSince = _created - _createdAtCheck;
                long scannedSince = _scanned - _scannedAtCheck;
                double recent = scannedSince > 0 ? (double)createdSince / scannedSince : 1e9;
                long entriesLeft = _capacity > _created ? _capacity - _created : 1;
                long bytesLeft = _budget > _scanned ? _budget - _scanned : 1;
                double target = (double)entriesLeft / bytesLeft;
                double ratio = target > 0 ? recent / target : 1e9;
                if (ratio > 2.0 && Value < 255)
                {
                    Value++;
                }
                else if (ratio < 0.5 && Value > 2)
                {
                    Value--;
                }

                _createdAtCheck = _created;
                _scannedAtCheck = _scanned;
                _checkpoint = _created + _interval;
            }
        }
    }
}
