using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Vorticity.Zstd.Internal;

/// <summary>
/// A Huffman decoding table for the literals: one entry per value of the next <see cref="TableLog"/>
/// bits, holding the symbol they start with and the length of its code.
/// </summary>
/// <remarks>
/// An entry is libzstd's <c>HUF_DEltX1</c> as a little-endian <see cref="ushort"/>: the code length in
/// the low byte, the symbol in the high byte. The table keeps the tree's own width.
/// </remarks>
internal sealed class HuffmanTable
{
    /// <summary>The longest code a tree may describe; libzstd's <c>HUF_TABLELOG_MAX</c>.</summary>
    public const int MaxTableLog = 12;

    /// <summary>The widest table the fast four-stream loop takes: five codes of this length fit in 7 bytes.</summary>
    public const int FastTableLog = 11;

    public const int MaxSymbols = 256;

    /// <summary>The table, and four entries past the widest: the fill writes four entries at a time.</summary>
    public readonly ushort[] Entries = new ushort[(1 << MaxTableLog) + 4];

    /// <summary>The number of bits an entry is looked up with.</summary>
    public int TableLog;

    /// <summary>The width of the double-symbol table: the fast loops' widest tree.</summary>
    public const int DoubleLog = FastTableLog;

    /// <summary>
    /// The double-symbol table (libzstd's X2, derived from <see cref="Entries"/> when a literal section
    /// asks for it): for each value of the next 11 bits, the one or two symbols whose codes they start
    /// with, as <c>nbBits | symbols &lt;&lt; 8 | count &lt;&lt; 30</c>: the bits to consume where a shift
    /// takes them, the symbols where one store writes both, the count where an add takes it shifted.
    /// </summary>
    private uint[]? _double;

    /// <summary>Whether <see cref="_double"/> was built from the current tree.</summary>
    private bool _hasDouble;


    /// <summary>
    /// libzstd's <c>HUF_readDTableX1_wksp</c>: reads a tree description and builds the table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No widening to 11 bits, unlike libzstd: it widens every table so that its fast loop shifts by
    /// a constant, but on Arm64 a shift by a register costs the same, and a narrower table is that
    /// much less to fill (a 9-bit tree fills 512 entries instead of 2048).
    /// </para>
    /// <para>
    /// Codes are assigned by increasing weight, then by symbol: the symbols of weight w take
    /// 2^(w-1) consecutive entries each, from the lowest weight up. As libzstd does, the symbols are
    /// first sorted by weight (those of weight 0 left out), and the table is then written in order,
    /// one weight at a time, so that
    /// the length of a symbol's entries is the same all through its inner loop. Up to four entries, a
    /// symbol takes one 8-byte store, whose excess the next symbol overwrites (the last one's falls
    /// in the four entries past the table); from eight, 16-byte stores.
    /// </para>
    /// </remarks>
    /// <returns>The bytes the description takes.</returns>
    public int Read(ReadOnlySpan<byte> source)
    {
        Span<byte> weights = stackalloc byte[MaxSymbols];
        Span<int> rankCount = stackalloc int[MaxTableLog + 1];
        int size = ReadWeights(source, weights, rankCount, out int symbols, out int log);
        nint symbolCount = symbols;
        int tableLog = log;

        // ---- the symbols of the table sorted by weight, stable. Weight 0 has no entry: its symbols,
        // which come in long runs, are skipped rather than sorted, since each would wait on the
        // store of the one before through their shared counter.
        Span<int> nextSlot = stackalloc int[MaxTableLog + 1];
        int slot = 0;
        for (int w = 1; w <= tableLog; w++)
        {
            nextSlot[w] = slot;
            slot += rankCount[w];
        }

        Span<byte> sorted = stackalloc byte[MaxSymbols];
        ref byte weight = ref MemoryMarshal.GetReference(weights);
        ref int next = ref MemoryMarshal.GetReference(nextSlot);
        ref byte order = ref MemoryMarshal.GetReference(sorted);
        for (nint s = 0; s < symbolCount; s++)
        {
            nint w = Unsafe.Add(ref weight, s);
            if (w == 0)
            {
                continue;
            }

            ref int at = ref Unsafe.Add(ref next, w);
            int a = at;
            Unsafe.Add(ref order, a) = (byte)s;
            at = a + 1;
        }

        // ---- the entries, in table order
        ref ushort table = ref MemoryMarshal.GetArrayDataReference(Entries);
        nint symbol = 0;
        nint position = 0;
        for (int w = 1; w <= tableLog; w++)
        {
            nint count = rankCount[w];
            nint length = (nint)1 << (w - 1);
            nint end = symbol + count;
            ulong nbBits = (ulong)(tableLog + 1 - w);
            if (length <= 4)
            {
                for (; symbol < end; symbol++)
                {
                    ulong entry = ((ulong)Unsafe.Add(ref order, symbol) << 8) | nbBits;
                    Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref table, position)), entry * 0x0001000100010001UL);
                    position += length;
                }
            }
            else
            {
                for (; symbol < end; symbol++)
                {
                    var entries = Vector128.Create((ushort)((Unsafe.Add(ref order, symbol) << 8) | (int)nbBits));
                    for (nint i = 0; i < length; i += 8)
                    {
                        Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref table, position + i)), entries);
                    }

                    position += length;
                }
            }
        }

        Debug.Assert(position == 1 << tableLog);
        TableLog = tableLog;
        _hasDouble = false;
        return size;
    }

    /// <summary>
    /// libzstd's <c>HUF_readStats</c>: the weights of a tree, the last one implied.
    /// </summary>
    /// <remarks>
    /// The weights are counted in four histograms, one for each weight of a group of four: equal
    /// weights come in runs, and one counter would make each count wait on the store of the last.
    /// </remarks>
    /// <returns>The bytes the description takes.</returns>
    internal static int ReadWeights(ReadOnlySpan<byte> source, Span<byte> weights, Span<int> rankCount, out int symbolCount, out int tableLog)
    {
        const ZstdError error = ZstdError.HuffmanTable;
        if (source.IsEmpty)
        {
            Throw.Error(error);
        }

        int headerByte = source[0];
        int inputSize;
        int count;
        if (headerByte >= 128)
        {
            // Direct representation: 4 bits a weight, two a byte, the first in the high nibble.
            count = headerByte - 127;
            inputSize = (count + 1) / 2;
            if (inputSize + 1 > source.Length)
            {
                Throw.Error(error);
            }

            for (int n = 0; n < count; n += 2)
            {
                byte b = source[1 + (n / 2)];
                weights[n] = (byte)(b >> 4);
                weights[n + 1] = (byte)(b & 15);
            }
        }
        else
        {
            inputSize = headerByte;
            if (inputSize + 1 > source.Length)
            {
                Throw.Error(error);
            }

            count = Fse.DecodeHuffmanWeights(source.Slice(1, inputSize), weights);
        }

        // Four histograms of 16 counters; a weight above 12 is refused below, and counted modulo 16.
        Span<int> histograms = stackalloc int[4 * 16];
        histograms.Clear();
        ref int h = ref MemoryMarshal.GetReference(histograms);
        ref byte weight = ref MemoryMarshal.GetReference(weights);
        uint weightTotal = 0;
        nint above = 0;
        nint n4 = 0;
        for (; n4 + 4 <= count; n4 += 4)
        {
            nint w0 = Unsafe.Add(ref weight, n4);
            nint w1 = Unsafe.Add(ref weight, n4 + 1);
            nint w2 = Unsafe.Add(ref weight, n4 + 2);
            nint w3 = Unsafe.Add(ref weight, n4 + 3);
            above |= (MaxTableLog - w0) | (MaxTableLog - w1) | (MaxTableLog - w2) | (MaxTableLog - w3);
            Unsafe.Add(ref h, w0 & 15)++;
            Unsafe.Add(ref h, 16 + (w1 & 15))++;
            Unsafe.Add(ref h, 32 + (w2 & 15))++;
            Unsafe.Add(ref h, 48 + (w3 & 15))++;
            weightTotal += ((1u << (int)w0) >> 1) + ((1u << (int)w1) >> 1) + ((1u << (int)w2) >> 1) + ((1u << (int)w3) >> 1);
        }

        for (; n4 < count; n4++)
        {
            nint w = Unsafe.Add(ref weight, n4);
            above |= MaxTableLog - w;
            Unsafe.Add(ref h, w & 15)++;
            weightTotal += (1u << (int)w) >> 1;
        }

        if (above < 0)
        {
            Throw.Error(error);
        }

        for (int w = 0; w <= MaxTableLog; w++)
        {
            rankCount[w] = Unsafe.Add(ref h, w) + Unsafe.Add(ref h, 16 + w) + Unsafe.Add(ref h, 32 + w) + Unsafe.Add(ref h, 48 + w);
        }

        if (weightTotal == 0)
        {
            Throw.Error(error);
        }

        tableLog = BackwardBitReader.HighBit(weightTotal) + 1;
        if (tableLog > MaxTableLog)
        {
            Throw.Error(error);
        }

        // The last weight completes the total to a power of two, which only one weight can do.
        uint rest = (1u << tableLog) - weightTotal;
        int restBit = BackwardBitReader.HighBit(rest);
        if ((1u << restBit) != rest)
        {
            Throw.Error(error);
        }

        int lastWeight = restBit + 1;
        weights[count] = (byte)lastWeight;
        rankCount[lastWeight]++;

        // A valid tree has an even number of codes of the longest length, at least two.
        if (rankCount[1] < 2 || (rankCount[1] & 1) != 0)
        {
            Throw.Error(error);
        }

        symbolCount = count + 1;
        return inputSize + 1;
    }

    /// <summary>
    /// libzstd's <c>HUF_decompress1X1_usingDTable_internal</c>: one stream filling all of
    /// <paramref name="output"/>.
    /// </summary>
    public void DecodeSingleStream(ReadOnlySpan<byte> source, Span<byte> output)
    {
        var bits = new BackwardBitReader(source, ZstdError.HuffmanStream);
        DecodeStream(ref bits, output);
        if (!bits.IsEndOfStream)
        {
            Throw.Error(ZstdError.HuffmanStream);
        }
    }

    /// <summary>
    /// libzstd's <c>HUF_decompress4X1_usingDTable_internal</c>: a six-byte jump table, then four
    /// streams that each fill a quarter of <paramref name="output"/>, rounded up, the last one taking
    /// what remains. The fast loop takes them when the table is 11 bits wide and every stream holds
    /// eight bytes; otherwise, and to finish, one symbol at a time.
    /// </summary>
    public void DecodeFourStreams(ReadOnlySpan<byte> source, Span<byte> output, bool preferDouble = false)
    {
        const ZstdError error = ZstdError.HuffmanStream;
        if (source.Length < 10 || output.Length < 6)
        {
            Throw.Error(error);
        }

        int length1 = BinaryPrimitives.ReadUInt16LittleEndian(source);
        int length2 = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(2));
        int length3 = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(4));
        int length4 = source.Length - 6 - length1 - length2 - length3;
        if (length4 < 0)
        {
            Throw.Error(error);
        }

        int segment = (output.Length + 3) / 4;
        if (TableLog <= FastTableLog && length1 >= 8 && length2 >= 8 && length3 >= 8 && length4 >= 8
            && 3 * segment < output.Length)
        {
            if (preferDouble && !_hasDouble)
            {
                BuildDouble();
            }

            DecodeFourStreamsFast(source, output, length1, length2, length3, segment, preferDouble);
            return;
        }

        int start2 = 6 + length1;
        int start3 = start2 + length2;
        int start4 = start3 + length3;

        var bits1 = new BackwardBitReader(source.Slice(6, length1), error);
        var bits2 = new BackwardBitReader(source.Slice(start2, length2), error);
        var bits3 = new BackwardBitReader(source.Slice(start3, length3), error);
        var bits4 = new BackwardBitReader(source.Slice(start4, length4), error);

        DecodeStream(ref bits1, output.Slice(0, segment));
        DecodeStream(ref bits2, output.Slice(segment, segment));
        DecodeStream(ref bits3, output.Slice(2 * segment, segment));
        DecodeStream(ref bits4, output.Slice(3 * segment));

        if (!(bits1.IsEndOfStream & bits2.IsEndOfStream & bits3.IsEndOfStream & bits4.IsEndOfStream))
        {
            Throw.Error(error);
        }
    }

    /// <summary>
    /// libzstd's <c>HUF_decompress4X1_usingDTable_internal_fast_c_loop</c>: the four streams decoded
    /// together, five symbols each a round, then each stream finished by <see cref="DecodeStream"/>.
    /// Tables of up to 11 bits take it, looked up with a shift by a register.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A container holds a stream's next bits from its top, with a 1 set just below the last valid
    /// one: the count of trailing zeros is then the number of bits consumed since the container was
    /// loaded, and a reload steps back by its whole bytes. Every entry is looked up with the top
    /// <see cref="TableLog"/> bits.
    /// </para>
    /// <para>
    /// Bounds: a round takes at most 5 x 11 bits, under 7 bytes, from each stream, and writes 5
    /// symbols to each. Before the rounds, their number is capped by what the last stream's quarter
    /// can take and by what lies between the lowest stream position and the start of the section;
    /// the stream positions are checked to be in order, the lowest first, so that none can go below
    /// the section. The careful finish then validates each stream exactly, as the reference decoder.
    /// </para>
    /// </remarks>
    private void DecodeFourStreamsFast(ReadOnlySpan<byte> source, Span<byte> output, int length1, int length2, int length3, int segment, bool useDouble)
    {
        const ZstdError error = ZstdError.HuffmanStream;
        int start1 = 6;
        int start2 = start1 + length1;
        int start3 = start2 + length2;
        int start4 = start3 + length3;
        if (source[start2 - 1] == 0 || source[start3 - 1] == 0 || source[start4 - 1] == 0 || source[^1] == 0)
        {
            Throw.Error(error);
        }

        ref byte input = ref MemoryMarshal.GetReference(source);
        ref byte first = ref MemoryMarshal.GetReference(output);
        var streams = new FourStreams
        {
            Ip0 = ref Unsafe.Add(ref input, start2 - 8),
            Ip1 = ref Unsafe.Add(ref input, start3 - 8),
            Ip2 = ref Unsafe.Add(ref input, start4 - 8),
            Ip3 = ref Unsafe.Add(ref input, source.Length - 8),
            Op0 = ref first,
            Op1 = ref Unsafe.Add(ref first, segment),
            Op2 = ref Unsafe.Add(ref first, 2 * segment),
            Op3 = ref Unsafe.Add(ref first, 3 * segment),
        };
        streams.Bits0 = InitFastStream(ref streams.Ip0);
        streams.Bits1 = InitFastStream(ref streams.Ip1);
        streams.Bits2 = InitFastStream(ref streams.Ip2);
        streams.Bits3 = InitFastStream(ref streams.Ip3);
        if (useDouble)
        {
            RunFourStreamsDouble(
                ref streams, ref MemoryMarshal.GetArrayDataReference(_double!), ref input,
                ref Unsafe.Add(ref first, segment), ref Unsafe.Add(ref first, 2 * segment),
                ref Unsafe.Add(ref first, 3 * segment), ref Unsafe.Add(ref first, output.Length));
        }
        else
        {
            RunFourStreams(
                ref streams, ref MemoryMarshal.GetArrayDataReference(Entries), 64 - TableLog, ref input,
                ref Unsafe.Add(ref first, output.Length));
        }

        // Each stream finished and checked on its own, as libzstd's HUF_initRemainingDStream.
        Finish(source, start1, start2, ref input, ref streams.Ip0, streams.Bits0, output, ref first, ref streams.Op0, 0, segment);
        Finish(source, start2, start3, ref input, ref streams.Ip1, streams.Bits1, output, ref first, ref streams.Op1, segment, segment);
        Finish(source, start3, start4, ref input, ref streams.Ip2, streams.Bits2, output, ref first, ref streams.Op2, 2 * segment, segment);
        Finish(source, start4, source.Length, ref input, ref streams.Ip3, streams.Bits3, output, ref first, ref streams.Op3, 3 * segment, output.Length - (3 * segment));

        void Finish(
            ReadOnlySpan<byte> source, int start, int end, ref byte input, ref byte ip, ulong bits,
            Span<byte> output, ref byte first, ref byte op, int segmentStart, int segmentLength)
        {
            int written = (int)Unsafe.ByteOffset(ref first, ref op) - segmentStart;
            if ((uint)written > (uint)segmentLength)
            {
                Throw.Error(error);
            }

            // The container may reach below the stream's first byte once the stream is nearly
            // consumed; the reference reader then sits at the first byte with the bits below counted
            // as consumed, and more than 64 means the stream was overrun.
            int position = (int)Unsafe.ByteOffset(ref input, ref ip) - start;
            int consumed = BitOperations.TrailingZeroCount(bits);
            if (position < 0)
            {
                consumed -= position * 8;
                position = 0;
            }

            if (consumed > 64)
            {
                Throw.Error(error);
            }

            var reader = BackwardBitReader.Resume(source.Slice(start, end - start), position, consumed);
            DecodeStream(ref reader, output.Slice(segmentStart + written, segmentLength - written));
            if (!reader.IsEndOfStream)
            {
                Throw.Error(error);
            }
        }
    }

    /// <summary>The four streams between the fast loop and their finish: containers, input and output positions.</summary>
    private ref struct FourStreams
    {
        public ulong Bits0;
        public ulong Bits1;
        public ulong Bits2;
        public ulong Bits3;
        public ref byte Ip0;
        public ref byte Ip1;
        public ref byte Ip2;
        public ref byte Ip3;
        public ref byte Op0;
        public ref byte Op1;
        public ref byte Op2;
        public ref byte Op3;
    }

    /// <summary>
    /// The rounds of <see cref="DecodeFourStreamsFast"/>: a method of its own that calls nothing, so
    /// that the twelve values of the four streams stay in registers, none of them living past it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunFourStreams(ref FourStreams s, ref ushort table, int shift, ref byte input, ref byte oend)
    {
        ulong bits0 = s.Bits0;
        ulong bits1 = s.Bits1;
        ulong bits2 = s.Bits2;
        ulong bits3 = s.Bits3;
        ref byte ip0 = ref s.Ip0;
        ref byte ip1 = ref s.Ip1;
        ref byte ip2 = ref s.Ip2;
        ref byte ip3 = ref s.Ip3;
        ref byte op0 = ref s.Op0;
        ref byte op1 = ref s.Op1;
        ref byte op2 = ref s.Op2;
        ref byte op3 = ref s.Op3;

        while (true)
        {
            nint outputRounds = Unsafe.ByteOffset(ref op3, ref oend) / 5;
            nint inputRounds = Unsafe.ByteOffset(ref input, ref ip0) / 7;
            nint rounds = Math.Min(outputRounds, inputRounds);
            if (rounds == 0
                || Unsafe.IsAddressLessThan(ref ip1, ref ip0)
                || Unsafe.IsAddressLessThan(ref ip2, ref ip1)
                || Unsafe.IsAddressLessThan(ref ip3, ref ip2))
            {
                break;
            }

            ref byte olimit = ref Unsafe.Add(ref op3, rounds * 5);
            do
            {
                Symbol(ref table, shift, ref bits0, ref op0, 0);
                Symbol(ref table, shift, ref bits1, ref op1, 0);
                Symbol(ref table, shift, ref bits2, ref op2, 0);
                Symbol(ref table, shift, ref bits3, ref op3, 0);
                Symbol(ref table, shift, ref bits0, ref op0, 1);
                Symbol(ref table, shift, ref bits1, ref op1, 1);
                Symbol(ref table, shift, ref bits2, ref op2, 1);
                Symbol(ref table, shift, ref bits3, ref op3, 1);
                Symbol(ref table, shift, ref bits0, ref op0, 2);
                Symbol(ref table, shift, ref bits1, ref op1, 2);
                Symbol(ref table, shift, ref bits2, ref op2, 2);
                Symbol(ref table, shift, ref bits3, ref op3, 2);
                Symbol(ref table, shift, ref bits0, ref op0, 3);
                Symbol(ref table, shift, ref bits1, ref op1, 3);
                Symbol(ref table, shift, ref bits2, ref op2, 3);
                Symbol(ref table, shift, ref bits3, ref op3, 3);
                Symbol(ref table, shift, ref bits0, ref op0, 4);
                Symbol(ref table, shift, ref bits1, ref op1, 4);
                Symbol(ref table, shift, ref bits2, ref op2, 4);
                Symbol(ref table, shift, ref bits3, ref op3, 4);
                ip0 = ref ReloadFast(ref bits0, ref ip0);
                op0 = ref Unsafe.Add(ref op0, 5);
                ip1 = ref ReloadFast(ref bits1, ref ip1);
                op1 = ref Unsafe.Add(ref op1, 5);
                ip2 = ref ReloadFast(ref bits2, ref ip2);
                op2 = ref Unsafe.Add(ref op2, 5);
                ip3 = ref ReloadFast(ref bits3, ref ip3);
                op3 = ref Unsafe.Add(ref op3, 5);
            }
            while (Unsafe.IsAddressLessThan(ref op3, ref olimit));
        }

        s.Bits0 = bits0;
        s.Bits1 = bits1;
        s.Bits2 = bits2;
        s.Bits3 = bits3;
        s.Ip0 = ref ip0;
        s.Ip1 = ref ip1;
        s.Ip2 = ref ip2;
        s.Ip3 = ref ip3;
        s.Op0 = ref op0;
        s.Op1 = ref op1;
        s.Op2 = ref op2;
        s.Op3 = ref op3;
    }

    /// <summary>
    /// The rounds of <see cref="DecodeFourStreamsFast"/> on the double-symbol table: each lookup writes
    /// two bytes and moves on by the one or two symbols it decoded, five lookups a stream a round.
    /// </summary>
    /// <remarks>
    /// A round takes at most 5 x 11 bits from each stream, as the single-symbol rounds, and writes at
    /// most ten bytes to each: the rounds are capped by what the most filled quarter has left, and
    /// recounted when they run out.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunFourStreamsDouble(
        ref FourStreams s, ref uint table, ref byte input, ref byte end0, ref byte end1, ref byte end2, ref byte end3)
    {
        ulong bits0 = s.Bits0;
        ulong bits1 = s.Bits1;
        ulong bits2 = s.Bits2;
        ulong bits3 = s.Bits3;
        ref byte ip0 = ref s.Ip0;
        ref byte ip1 = ref s.Ip1;
        ref byte ip2 = ref s.Ip2;
        ref byte ip3 = ref s.Ip3;
        ref byte op0 = ref s.Op0;
        ref byte op1 = ref s.Op1;
        ref byte op2 = ref s.Op2;
        ref byte op3 = ref s.Op3;

        while (true)
        {
            nint room = Math.Min(
                Math.Min(Unsafe.ByteOffset(ref op0, ref end0), Unsafe.ByteOffset(ref op1, ref end1)),
                Math.Min(Unsafe.ByteOffset(ref op2, ref end2), Unsafe.ByteOffset(ref op3, ref end3)));
            nint rounds = Math.Min(room / 10, Unsafe.ByteOffset(ref input, ref ip0) / 7);
            if (rounds <= 0
                || Unsafe.IsAddressLessThan(ref ip1, ref ip0)
                || Unsafe.IsAddressLessThan(ref ip2, ref ip1)
                || Unsafe.IsAddressLessThan(ref ip3, ref ip2))
            {
                break;
            }

            do
            {
                op0 = ref Pair(ref table, ref bits0, ref op0);
                op1 = ref Pair(ref table, ref bits1, ref op1);
                op2 = ref Pair(ref table, ref bits2, ref op2);
                op3 = ref Pair(ref table, ref bits3, ref op3);
                op0 = ref Pair(ref table, ref bits0, ref op0);
                op1 = ref Pair(ref table, ref bits1, ref op1);
                op2 = ref Pair(ref table, ref bits2, ref op2);
                op3 = ref Pair(ref table, ref bits3, ref op3);
                op0 = ref Pair(ref table, ref bits0, ref op0);
                op1 = ref Pair(ref table, ref bits1, ref op1);
                op2 = ref Pair(ref table, ref bits2, ref op2);
                op3 = ref Pair(ref table, ref bits3, ref op3);
                op0 = ref Pair(ref table, ref bits0, ref op0);
                op1 = ref Pair(ref table, ref bits1, ref op1);
                op2 = ref Pair(ref table, ref bits2, ref op2);
                op3 = ref Pair(ref table, ref bits3, ref op3);
                op0 = ref Pair(ref table, ref bits0, ref op0);
                op1 = ref Pair(ref table, ref bits1, ref op1);
                op2 = ref Pair(ref table, ref bits2, ref op2);
                op3 = ref Pair(ref table, ref bits3, ref op3);
                ip0 = ref ReloadFast(ref bits0, ref ip0);
                ip1 = ref ReloadFast(ref bits1, ref ip1);
                ip2 = ref ReloadFast(ref bits2, ref ip2);
                ip3 = ref ReloadFast(ref bits3, ref ip3);
            }
            while (--rounds > 0);
        }

        s.Bits0 = bits0;
        s.Bits1 = bits1;
        s.Bits2 = bits2;
        s.Bits3 = bits3;
        s.Ip0 = ref ip0;
        s.Ip1 = ref ip1;
        s.Ip2 = ref ip2;
        s.Ip3 = ref ip3;
        s.Op0 = ref op0;
        s.Op1 = ref op1;
        s.Op2 = ref op2;
        s.Op3 = ref op3;
    }

    /// <summary>One lookup of the double-symbol table: both bytes written, the output moved by the count.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref byte Pair(scoped ref uint table, scoped ref ulong bits, ref byte op)
    {
        ulong entry = Unsafe.Add(ref table, (nint)(bits >> (64 - DoubleLog)));
        Unsafe.WriteUnaligned(ref op, (ushort)(entry >> 8));
        bits <<= (int)entry;
        return ref Unsafe.Add(ref op, (nint)(entry >> 30));
    }

    /// <summary>
    /// libzstd's <c>HUF_selectDecoder</c>, with costs measured here: whether four streams of
    /// <paramref name="compressedSize"/> bytes decode <paramref name="symbols"/> symbols faster with
    /// the double-symbol table.
    /// </summary>
    /// <remarks>
    /// Two symbols share a lookup when their codes fit in 11 bits together, which is common below six
    /// bits a symbol on average, and rare above. There, a symbol costs 1.4 cycles instead of 2.4 (the
    /// URL frame's literals), and building the table some 2,200 cycles: worth it from about 2,000
    /// symbols, and always once built, which a section reusing the previous tree finds.
    /// </remarks>
    public bool PrefersDouble(int compressedSize, int symbols) =>
        compressedSize * 4 < symbols * 3 && (_hasDouble || symbols >= 2048);

    /// <summary>Builds the double-symbol table of the current tree, for a micro-benchmark.</summary>
    internal void BuildDoubleForBenchmark() => BuildDouble();

    /// <summary>
    /// libzstd's <c>HUF_readDTableX2</c>, derived from the single-symbol table: the code each value of
    /// the next 11 bits starts with, and the next code too when it ends within those bits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Filled by runs rather than value by value. A symbol of code length <c>n</c> owns a run of
    /// 2^(11 - n) values, and the single-symbol table lists the runs from the longest codes to the
    /// shortest, each aligned on its size (a complete code, which the weights were checked to make,
    /// sorted by length, has that property). Within the run of a first symbol of length <c>n1</c>, the
    /// values go through the 11 - n1 bits that follow its code, and the second symbol of a value is
    /// the run of the whole table those bits fall in, scaled down by 2^n1. The codes too long to fit
    /// come first: a prefix of the run that keeps one symbol; then each code that fits, a run of
    /// 2^(11 - n1 - n2) values that keep two.
    /// </para>
    /// </remarks>
    private void BuildDouble()
    {
        // Four entries past the table: a fill writes four at a time.
        uint[] table = _double ??= new uint[(1 << DoubleLog) + 4];
        int scale = DoubleLog - TableLog;
        ref ushort single = ref MemoryMarshal.GetArrayDataReference(Entries);
        ref uint output = ref MemoryMarshal.GetArrayDataReference(table);

        // ---- the runs of the single-symbol table, in its order: symbol, code length, start in 11 bits
        Span<byte> runSymbol = stackalloc byte[MaxSymbols];
        Span<byte> runBits = stackalloc byte[MaxSymbols];
        Span<short> runStart = stackalloc short[MaxSymbols];
        int runs = 0;
        for (int i = 0; i < 1 << TableLog;)
        {
            ushort entry = Unsafe.Add(ref single, i);
            int bits = entry & 0xFF;
            runSymbol[runs] = (byte)(entry >> 8);
            runBits[runs] = (byte)bits;
            runStart[runs] = (short)(i << scale);
            runs++;
            i += 1 << (TableLog - bits);
        }

        // The first run whose code is at most r bits long, for every r: the runs that fit after a
        // first code of 11 - r bits, all at the end.
        Span<short> firstFitting = stackalloc short[DoubleLog + 1];
        int k = 0;
        for (int r = DoubleLog; r >= 0; r--)
        {
            while (k < runs && runBits[k] > r)
            {
                k++;
            }

            firstFitting[r] = (short)k;
        }

        // ---- each first symbol's run: the values that keep it alone, then one run a second symbol
        for (int j = 0; j < runs; j++)
        {
            int firstBits = runBits[j];
            int room = DoubleLog - firstBits;
            ref uint row = ref Unsafe.Add(ref output, runStart[j]);
            uint firstSymbol = (uint)runSymbol[j] << 8;
            int fitting = firstFitting[room];
            int alone = (fitting < runs ? runStart[fitting] : 1 << DoubleLog) >> firstBits;
            Fill(ref row, alone, (uint)firstBits | firstSymbol | (1u << 30));
            for (int f = fitting; f < runs; f++)
            {
                int secondBits = runBits[f];
                uint pair = (uint)(firstBits + secondBits) | firstSymbol | ((uint)runSymbol[f] << 16) | (2u << 30);
                Fill(ref Unsafe.Add(ref row, runStart[f] >> firstBits), 1 << (room - secondBits), pair);
            }
        }

        _hasDouble = true;

        // Four entries a store, up to three past the end: the fills go in increasing order, so the
        // next one writes over the excess, and the last one's falls in the four entries past the table.
        static void Fill(ref uint at, nint count, uint value)
        {
            var four = Vector128.Create(value);
            nint i = 0;
            do
            {
                Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref at, i)), four);
                i += 4;
            }
            while (i < count);
        }
    }

    /// <summary>libzstd's <c>HUF_initFastDStream</c>: the last eight bytes, the marker made the sentinel.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong InitFastStream(ref byte ip)
    {
        int consumed = 8 - BackwardBitReader.HighBit(Unsafe.Add(ref ip, 7));
        return (Unsafe.ReadUnaligned<ulong>(ref ip) | 1) << consumed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Symbol(ref ushort table, int shift, ref ulong bits, ref byte op, int k)
    {
        int entry = Unsafe.Add(ref table, (nint)(bits >> shift));
        bits <<= entry;
        Unsafe.Add(ref op, k) = (byte)(entry >> 8);
    }

    /// <summary>
    /// Steps a stream back by the whole bytes its container has consumed and reloads it. A ref
    /// parameter cannot be repointed for the caller, so the new position is returned.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref byte ReloadFast(scoped ref ulong bits, ref byte ip)
    {
        int consumed = BitOperations.TrailingZeroCount(bits);
        ref byte next = ref Unsafe.Subtract(ref ip, consumed >> 3);
        bits = (Unsafe.ReadUnaligned<ulong>(ref next) | 1) << (consumed & 7);
        return ref next;
    }

    /// <summary>Decodes one symbol per entry until <paramref name="output"/> is full.</summary>
    private void DecodeStream(ref BackwardBitReader bits, Span<byte> output)
    {
        ushort[] entries = Entries;
        int tableLog = TableLog;
        for (int i = 0; i < output.Length; i++)
        {
            // A reload leaves at least 57 bits while the stream has them; a code takes at most 12.
            if (bits.BitsConsumed > 64 - MaxTableLog)
            {
                bits.Reload();
            }

            ushort entry = entries[bits.PeekBitsFast(tableLog)];
            output[i] = (byte)(entry >> 8);
            bits.SkipBits(entry & 0xFF);
        }
    }
}
