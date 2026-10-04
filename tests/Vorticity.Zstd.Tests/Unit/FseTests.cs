using System;
using System.Collections.Generic;
using System.Linq;
using Vorticity.Zstd.Internal;
using Xunit;

namespace Vorticity.Zstd.Tests.Unit;

/// <summary>
/// Table descriptions (normalized counts): random valid distributions written by a transcription of
/// libzstd's <c>FSE_writeNCount</c> must read back exactly, and invalid ones must be refused.
/// </summary>
public sealed class FseTests
{
    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        for (int i = 0; i < 400; i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_distributions_read_back(int seed)
    {
        var random = new Random(seed);
        int maxSymbol = random.Next(4) switch { 0 => 35, 1 => 52, 2 => 31, _ => 255 };
        int tableLog = Fse.MinTableLog + random.Next(maxSymbol == 255 ? 8 : 5);
        short[] norm = RandomDistribution(random, maxSymbol, tableLog);
        byte[] header = WriteNCount(norm, tableLog);

        // Followed by other bytes, as in a block: the reader must stop where the description ends.
        byte[] followed = [.. header, 0xA5, 0x5A, 0xFF];
        short[] read = new short[maxSymbol + 1];
        int maxSymbolValue = maxSymbol;
        int size = Fse.ReadNCount(read, ref maxSymbolValue, out int readLog, followed, ZstdError.FseTable);

        int lastNonZero = Array.FindLastIndex(norm, c => c != 0);
        Assert.Equal(header.Length, size);
        Assert.Equal(tableLog, readLog);
        Assert.Equal(lastNonZero, maxSymbolValue);
        Assert.Equal(norm.AsSpan(0, lastNonZero + 1).ToArray(), read.AsSpan(0, lastNonZero + 1).ToArray());
    }

    [Fact]
    public void Short_headers_are_read_from_a_padded_copy()
    {
        // tableLog 5, two symbols of 16: fits in under eight bytes.
        short[] norm = [16, 16];
        byte[] header = WriteNCount(norm, 5);
        Assert.True(header.Length < 8);
        short[] read = new short[36];
        int max = 35;
        Assert.Equal(header.Length, Fse.ReadNCount(read, ref max, out int log, header, ZstdError.FseTable));
        Assert.Equal(5, log);
        Assert.Equal(1, max);
        Assert.Equal(16, read[0]);
        Assert.Equal(16, read[1]);

        // One byte less and the description runs past its end.
        Assert.Throws<ZstdException>(() => Fse.ReadNCount(new short[36], ref max, out _, header.AsSpan(0, header.Length - 1), ZstdError.FseTable));
    }

    [Fact]
    public void An_accuracy_above_15_is_refused()
    {
        int max = 35;
        // The low four bits store tableLog - 5: 0xB is 16.
        Assert.Throws<ZstdException>(() => Fse.ReadNCount(new short[36], ref max, out _, new byte[] { 0x0B, 0, 0, 0, 0, 0, 0, 0 }, ZstdError.FseTable));
    }

    [Fact]
    public void A_distribution_that_does_not_sum_to_the_table_is_refused()
    {
        short[] norm = [16, 16];
        byte[] header = WriteNCount(norm, 5);

        // Read with a symbol budget of one: the second symbol's count is missing.
        int max = 0;
        Assert.Throws<ZstdException>(() => Fse.ReadNCount(new short[36], ref max, out _, header, ZstdError.FseTable));
    }

    [Fact]
    public void Byte_symbol_tables_spread_like_libzstd()
    {
        // FSE_buildDTable on libzstd's predefined literal length distribution, rebuilt as byte
        // symbols, gives the same spread as the sequence table builder.
        short[] norm = SequenceCodes.LiteralLengthDefaultNorm.ToArray();
        var table = new FseEntry[64];
        Fse.BuildTable(table, norm, 6, ZstdError.FseTable);
        for (int i = 0; i < 64; i++)
        {
            SeqSymbol expected = SequenceCodes.DefaultLiteralLengths.Entries[2 * i];
            Assert.Equal(expected.NbBits(SequenceCode.LiteralLength), table[i].NbBits);
            Assert.Equal(expected.NextState / 2, table[i].NewState);
            Assert.Equal(expected.BaseValue(SequenceCode.LiteralLength), SequenceCodes.LiteralLengthBase[table[i].Symbol]);
        }
    }

    /// <summary>A random valid distribution: low-probability symbols (-1), zeros in runs, positive counts.</summary>
    private static short[] RandomDistribution(Random random, int maxSymbol, int tableLog)
    {
        int total = 1 << tableLog;
        int symbols = 2 + random.Next(Math.Min(maxSymbol, total - 1));
        short[] norm = new short[maxSymbol + 1];
        var present = new List<int>();

        // Runs of absent symbols, some longer than 24 to exercise the repeat codes.
        int s = 0;
        while (present.Count < symbols && s <= maxSymbol)
        {
            if (random.Next(5) == 0)
            {
                s += random.Next(4) == 0 ? 24 + random.Next(30) : 1 + random.Next(4);
                continue;
            }

            present.Add(s++);
        }

        if (present.Count < 2)
        {
            present = [0, 1];
        }

        int remaining = total;
        foreach (int symbol in present)
        {
            norm[symbol] = 1;
            remaining--;
        }

        // Some symbols below one unit of probability.
        foreach (int symbol in present.Where(_ => random.Next(6) == 0).Take(present.Count - 1))
        {
            norm[symbol] = -1;
        }

        while (remaining > 0)
        {
            int symbol = present[random.Next(present.Count)];
            if (norm[symbol] == -1)
            {
                continue;
            }

            int add = Math.Min(remaining, 1 + random.Next(Math.Max(1, remaining / 3)));
            norm[symbol] += (short)add;
            remaining -= add;
        }

        return norm;
    }

    /// <summary>libzstd's <c>FSE_writeNCount_generic</c>.</summary>
    internal static byte[] WriteNCount(short[] norm, int tableLog)
    {
        var output = new List<byte>();
        int tableSize = 1 << tableLog;
        uint bitStream = (uint)(tableLog - Fse.MinTableLog);
        int bitCount = 4;
        int remaining = tableSize + 1;
        int threshold = tableSize;
        int nbBits = tableLog + 1;
        int symbol = 0;
        int alphabetSize = norm.Length;
        bool previousIs0 = false;

        while (symbol < alphabetSize && remaining > 1)
        {
            if (previousIs0)
            {
                int start = symbol;
                while (symbol < alphabetSize && norm[symbol] == 0)
                {
                    symbol++;
                }

                if (symbol == alphabetSize)
                {
                    break;
                }

                while (symbol >= start + 24)
                {
                    start += 24;
                    bitStream += 0xFFFFu << bitCount;
                    output.Add((byte)bitStream);
                    output.Add((byte)(bitStream >> 8));
                    bitStream >>= 16;
                }

                while (symbol >= start + 3)
                {
                    start += 3;
                    bitStream += 3u << bitCount;
                    bitCount += 2;
                }

                bitStream += (uint)(symbol - start) << bitCount;
                bitCount += 2;
                if (bitCount > 16)
                {
                    output.Add((byte)bitStream);
                    output.Add((byte)(bitStream >> 8));
                    bitStream >>= 16;
                    bitCount -= 16;
                }
            }

            int count = norm[symbol++];
            int max = (2 * threshold) - 1 - remaining;
            remaining -= count < 0 ? -count : count;
            count++;
            if (count >= threshold)
            {
                count += max;
            }

            bitStream += (uint)count << bitCount;
            bitCount += nbBits;
            bitCount -= count < max ? 1 : 0;
            previousIs0 = count == 1;
            if (remaining < 1)
            {
                throw new InvalidOperationException("invalid distribution");
            }

            while (remaining < threshold)
            {
                nbBits--;
                threshold >>= 1;
            }

            if (bitCount > 16)
            {
                output.Add((byte)bitStream);
                output.Add((byte)(bitStream >> 8));
                bitStream >>= 16;
                bitCount -= 16;
            }
        }

        if (remaining != 1)
        {
            throw new InvalidOperationException("invalid distribution");
        }

        output.Add((byte)bitStream);
        output.Add((byte)(bitStream >> 8));
        int keep = output.Count - 2 + ((bitCount + 7) / 8);
        return output.Take(keep).ToArray();
    }
}
