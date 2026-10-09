using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Zstd.Internal;

/// <summary>
/// Finite State Entropy: the table descriptions (normalized counts) shared by the Huffman weights
/// and the three sequence codes, and the byte-symbol decoder the Huffman weights are compressed with.
/// </summary>
internal static class Fse
{
    public const int MinTableLog = 5;
    public const int TableLogAbsoluteMax = 15;

    /// <summary>libzstd's FSE_MAX_TABLELOG, for the byte-symbol tables.</summary>
    public const int MaxTableLog = 12;

    /// <summary>The largest accuracy of the Huffman weights' table.</summary>
    public const int WeightsMaxTableLog = 6;

    /// <summary>
    /// libzstd's <c>FSE_readNCount</c>: reads a table description into <paramref name="normalized"/>,
    /// whose length must exceed the incoming <paramref name="maxSymbolValue"/>. On return
    /// <paramref name="maxSymbolValue"/> is the last symbol described.
    /// </summary>
    /// <returns>The bytes the description takes.</returns>
    public static int ReadNCount(
        Span<short> normalized, ref int maxSymbolValue, out int tableLog, ReadOnlySpan<byte> header, ZstdError error)
    {
        if (header.Length < 8)
        {
            // The reader works on at least eight bytes; a shorter header is read from a zero-padded
            // copy and must not have needed the padding.
            Span<byte> padded = stackalloc byte[8];
            padded.Clear();
            header.CopyTo(padded);
            int size = ReadNCount(normalized, ref maxSymbolValue, out tableLog, padded, error);
            if (size > header.Length)
            {
                Throw.Error(error);
            }

            return size;
        }

        int end = header.Length;
        int ip = 0;
        int maxSymbolPlusOne = maxSymbolValue + 1;
        normalized.Slice(0, maxSymbolPlusOne).Clear();

        uint bitStream = BinaryPrimitives.ReadUInt32LittleEndian(header);
        int nbBits = (int)(bitStream & 0xF) + MinTableLog;
        if (nbBits > TableLogAbsoluteMax)
        {
            Throw.Error(error);
        }

        bitStream >>= 4;
        int bitCount = 4;
        tableLog = nbBits;
        int remaining = (1 << nbBits) + 1;
        int threshold = 1 << nbBits;
        nbBits++;
        int symbol = 0;
        bool previousZero = false;

        while (true)
        {
            if (previousZero)
            {
                // Runs of zero-probability symbols: each 2-bit code 0b11 means three more and
                // another code follows.
                int repeats = BitOperations.TrailingZeroCount(~bitStream | 0x80000000u) >> 1;
                while (repeats >= 12)
                {
                    symbol += 3 * 12;
                    if (ip <= end - 7)
                    {
                        ip += 3;
                    }
                    else
                    {
                        bitCount -= 8 * (end - 7 - ip);
                        bitCount &= 31;
                        ip = end - 4;
                    }

                    bitStream = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(ip)) >> bitCount;
                    repeats = BitOperations.TrailingZeroCount(~bitStream | 0x80000000u) >> 1;
                }

                symbol += 3 * repeats;
                bitStream >>= 2 * repeats;
                bitCount += 2 * repeats;
                symbol += (int)(bitStream & 3);
                bitCount += 2;

                if (symbol >= maxSymbolPlusOne)
                {
                    break;
                }

                Advance(header, end, ref ip, ref bitCount, out bitStream);
            }

            int max = (2 * threshold) - 1 - remaining;
            int count;
            if ((bitStream & (uint)(threshold - 1)) < (uint)max)
            {
                count = (int)(bitStream & (uint)(threshold - 1));
                bitCount += nbBits - 1;
            }
            else
            {
                count = (int)(bitStream & (uint)((2 * threshold) - 1));
                if (count >= threshold)
                {
                    count -= max;
                }

                bitCount += nbBits;
            }

            count--; // -1 is a symbol below one unit of probability
            remaining -= count >= 0 ? count : -count;
            normalized[symbol++] = (short)count;
            previousZero = count == 0;

            if (remaining < threshold)
            {
                if (remaining <= 1)
                {
                    break;
                }

                nbBits = BackwardBitReader.HighBit((uint)remaining) + 1;
                threshold = 1 << (nbBits - 1);
            }

            if (symbol >= maxSymbolPlusOne)
            {
                break;
            }

            Advance(header, end, ref ip, ref bitCount, out bitStream);
        }

        if (remaining != 1 || symbol > maxSymbolPlusOne || bitCount > 32)
        {
            Throw.Error(error);
        }

        maxSymbolValue = symbol - 1;
        ip += (bitCount + 7) >> 3;
        return ip;

        // Inlined: as a call, it took the reader's position, count and bits by reference, which kept
        // them in memory for the whole description.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Advance(ReadOnlySpan<byte> header, int end, ref int ip, ref int bitCount, out uint bitStream)
        {
            if (ip <= end - 7 || ip + (bitCount >> 3) <= end - 4)
            {
                ip += bitCount >> 3;
                bitCount &= 7;
            }
            else
            {
                bitCount -= 8 * (end - 4 - ip);
                bitCount &= 31;
                ip = end - 4;
            }

            bitStream = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(ip)) >> bitCount;
        }
    }

    /// <summary>The step that spreads symbols over a table of <paramref name="tableSize"/> cells.</summary>
    public static int TableStep(int tableSize) => (tableSize >> 1) + (tableSize >> 3) + 3;

    /// <summary>
    /// libzstd's <c>FSE_decompress_wksp</c> with an accuracy cap of 6, which is how the weights of a
    /// Huffman tree are compressed: a table description, then a stream decoded by two interleaved
    /// states until it overflows.
    /// </summary>
    /// <returns>The number of weights decoded into <paramref name="weights"/>.</returns>
    public static int DecodeHuffmanWeights(ReadOnlySpan<byte> source, Span<byte> weights)
    {
        const ZstdError error = ZstdError.HuffmanTable;
        Span<short> normalized = stackalloc short[256];
        int maxSymbolValue = 255;
        int headerSize = ReadNCount(normalized, ref maxSymbolValue, out int tableLog, source, error);
        if (tableLog > WeightsMaxTableLog)
        {
            Throw.Error(error);
        }

        // libzstd decodes the weights in a fixed workspace sized for 6 bits and symbols up to 11;
        // what does not fit there is refused, and is refused here alike.
        if (DecompressWorkspaceSize(tableLog, maxSymbolValue) > DecompressWorkspaceSize(WeightsMaxTableLog, 11))
        {
            Throw.Error(error);
        }

        Span<FseEntry> table = stackalloc FseEntry[1 << WeightsMaxTableLog];
        BuildTable(table, normalized.Slice(0, maxSymbolValue + 1), tableLog, error);
        return DecodeTwoStates(source.Slice(headerSize), weights, table, tableLog);
    }

    /// <summary>libzstd's <c>FSE_DECOMPRESS_WKSP_SIZE</c>, in bytes.</summary>
    private static int DecompressWorkspaceSize(int tableLog, int maxSymbolValue)
    {
        int dtableU32 = 1 + (1 << tableLog);
        int buildBytes = (2 * (maxSymbolValue + 1)) + (1 << tableLog) + 8;
        int buildU32 = (buildBytes + 3) / 4;
        return (dtableU32 + 1 + buildU32 + 128 + 1) * 4;
    }

    /// <summary>
    /// libzstd's <c>FSE_buildDTable_internal</c> for byte symbols.
    /// </summary>
    /// <returns>Whether every state reads at least one bit (libzstd's fast mode).</returns>
    public static bool BuildTable(Span<FseEntry> table, ReadOnlySpan<short> normalized, int tableLog, ZstdError error)
    {
        int tableSize = 1 << tableLog;
        int highThreshold = tableSize - 1;
        Span<ushort> symbolNext = stackalloc ushort[256];
        bool fast = true;
        int largeLimit = 1 << (tableLog - 1);

        for (int s = 0; s < normalized.Length; s++)
        {
            if (normalized[s] == -1)
            {
                table[highThreshold--].Symbol = (byte)s;
                symbolNext[s] = 1;
            }
            else
            {
                if (normalized[s] >= largeLimit)
                {
                    fast = false;
                }

                symbolNext[s] = (ushort)normalized[s];
            }
        }

        int mask = tableSize - 1;
        int step = TableStep(tableSize);
        int position = 0;
        for (int s = 0; s < normalized.Length; s++)
        {
            for (int i = 0; i < normalized[s]; i++)
            {
                table[position].Symbol = (byte)s;
                do
                {
                    position = (position + step) & mask;
                }
                while (position > highThreshold);
            }
        }

        if (position != 0)
        {
            Throw.Error(error);
        }

        for (int u = 0; u < tableSize; u++)
        {
            int symbol = table[u].Symbol;
            int nextState = symbolNext[symbol]++;
            int nbBits = tableLog - BackwardBitReader.HighBit((uint)nextState);
            table[u].NbBits = (byte)nbBits;
            table[u].NewState = (ushort)((nextState << nbBits) - tableSize);
        }

        return fast;
    }

    /// <summary>libzstd's <c>FSE_decompress_usingDTable_generic</c> on a 64-bit container.</summary>
    /// <remarks>
    /// <para>
    /// The weights end where the stream overflows, so the statuses of libzstd's reload decide
    /// exactly as there (see <see cref="BackwardBitReader"/>), computed from the stream's position
    /// rather than returned: the reload itself is one clamped move, without branches. The stream's
    /// state lives in locals, an entry is one 32-bit load, and a state reads its bits with two shifts
    /// that give 0 for none (a state of a symbol above half the table reads no bit).
    /// </para>
    /// <para>
    /// A stream under eight bytes is read from a zero-padded copy, its bits below the stream counted
    /// as consumed: that is the container libzstd assembles byte by byte.
    /// </para>
    /// </remarks>
    private static int DecodeTwoStates(ReadOnlySpan<byte> source, Span<byte> output, ReadOnlySpan<FseEntry> table, int tableLog)
    {
        const ZstdError error = ZstdError.HuffmanTable;
        if (source.IsEmpty || source[^1] == 0)
        {
            Throw.Error(error);
        }

        nint bc = 8 - BackwardBitReader.HighBit(source[^1]);
        nint position;
        Span<byte> padded = stackalloc byte[8];
        scoped ref byte bits = ref MemoryMarshal.GetReference(source);
        if (source.Length >= 8)
        {
            position = source.Length - 8;
        }
        else
        {
            padded.Clear();
            source.CopyTo(padded);
            bc += (8 - source.Length) * 8;
            position = 0;
            bits = ref MemoryMarshal.GetReference(padded);
        }

        ulong container = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bits, position));
        ref uint entries = ref Unsafe.As<FseEntry, uint>(ref MemoryMarshal.GetReference(table));
        ref byte op = ref MemoryMarshal.GetReference(output);
        Debug.Assert(output.Length >= 256 && table.Length >= 1 << tableLog);

        // libzstd reloads after each initial state, then once more and refuses an overflow. An
        // overflowed stream (more than 64 bits consumed) stays as it is; any other reload is the
        // clamped one below, whatever its status.
        nint state1 = (nint)ReadBits(container, ref bc, tableLog);
        Reload(ref bits, ref position, ref bc, ref container);
        nint state2 = (nint)ReadBits(container, ref bc, tableLog);
        Reload(ref bits, ref position, ref bc, ref container);
        if (bc > 64)
        {
            Throw.Error(error);
        }

        // At most 255 weights: the last symbol's weight is implied.
        const int Max = 255;
        nint count = 0;

        // Four symbols a round while the reload before it finds the stream unfinished: not
        // overflowed, not at its start, and moving by all the bytes consumed. On a 64-bit container
        // libzstd reloads only between rounds.
        while (true)
        {
            bool unfinished = (bc <= 64) & (position > 0) & ((bc >> 3) <= position);
            Reload(ref bits, ref position, ref bc, ref container);
            if (!(unfinished & (count < Max - 3)))
            {
                break;
            }

            Unsafe.Add(ref op, count) = Symbol(ref entries, ref state1, container, ref bc);
            Unsafe.Add(ref op, count + 1) = Symbol(ref entries, ref state2, container, ref bc);
            Unsafe.Add(ref op, count + 2) = Symbol(ref entries, ref state1, container, ref bc);
            Unsafe.Add(ref op, count + 3) = Symbol(ref entries, ref state2, container, ref bc);
            count += 4;
        }

        // The tail: the stream ends when it overflows, and the other state then gives one more
        // symbol. Only the overflow matters here, which a reload reports before doing anything.
        while (true)
        {
            if (count > Max - 2)
            {
                Throw.Error(error);
            }

            Unsafe.Add(ref op, count++) = Symbol(ref entries, ref state1, container, ref bc);
            if (bc > 64)
            {
                Unsafe.Add(ref op, count++) = Symbol(ref entries, ref state2, container, ref bc);
                break;
            }

            Reload(ref bits, ref position, ref bc, ref container);
            if (count > Max - 2)
            {
                Throw.Error(error);
            }

            Unsafe.Add(ref op, count++) = Symbol(ref entries, ref state2, container, ref bc);
            if (bc > 64)
            {
                Unsafe.Add(ref op, count++) = Symbol(ref entries, ref state1, container, ref bc);
                break;
            }

            Reload(ref bits, ref position, ref bc, ref container);
        }

        return (int)count;

        // An entry as one 32-bit value: the next state's base, then the symbol, then the bits to read.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte Symbol(ref uint entries, ref nint state, ulong container, ref nint bc)
        {
            uint entry = Unsafe.Add(ref entries, state);
            state = (nint)(entry & 0xFFFF) + (nint)ReadBits(container, ref bc, (nint)(entry >> 24));
            return (byte)(entry >> 16);
        }
    }

    /// <summary>
    /// libzstd's <c>BIT_readBits</c>: 0 to 31 bits, as two shifts that give 0 for none. Past the
    /// container, the shifts take their counts modulo 64, as libzstd's do: the stream then fails.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadBits(ulong container, ref nint bc, nint count)
    {
        ulong value = ((container << (int)bc) >> 1) >> ((int)count ^ 63);
        bc += count;
        return value;
    }

    /// <summary>
    /// libzstd's <c>BIT_reloadDStream</c> without its status: back by the whole bytes consumed, but not
    /// below the start of the stream. An overflowed stream is at its start already: it stays.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Reload(ref byte bits, ref nint position, ref nint bc, ref ulong container)
    {
        nint bytes = Math.Min(bc >> 3, position);
        position -= bytes;
        bc -= bytes << 3;
        container = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bits, position));
    }
}

/// <summary>One state of a byte-symbol FSE decoding table.</summary>
internal struct FseEntry
{
    public ushort NewState;
    public byte Symbol;
    public byte NbBits;
}
