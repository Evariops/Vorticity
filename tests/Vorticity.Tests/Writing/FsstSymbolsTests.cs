// The FSST encoder, tested against the DECODER THAT READS THE REFERENCE'S OWN FILES.
//
// The same discipline as FastLanes.PackBlock, and for the same reason: an encoder and a decoder
// written by one person from one reading of a format will agree with each other even when both are
// wrong, and the file they produce is then one no other implementation can read. So the round trip
// here goes through FsstSymbolTable -- the kernel the 55 vortex.fsst corpus files exercise -- which
// makes it a statement about the wire format rather than about internal consistency.
//
// The end-to-end guarantee is elsewhere and is stronger: the writer sweep hands every corpus file
// to `cargo run --example verify_written`, so a symbol table Rust cannot decode fails there.
using System;
using System.Collections.Generic;
using System.Text;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class FsstSymbolsTests
{
    [Fact]
    public void CompressingThenDecodingWithTheCorpusValidatedKernelReturnsTheValues()
    {
        List<ReadOnlyMemory<byte>> rows = Rows(
            "the quick brown fox jumps over the lazy dog",
            "the quick brown fox is quick",
            "the lazy dog sleeps",
            "brown fox, lazy dog, quick fox");

        FsstSymbols? table = FsstSymbols.Train(rows);
        Assert.NotNull(table);

        foreach (ReadOnlyMemory<byte> row in rows)
        {
            Assert.Equal(row.ToArray(), RoundTrip(table, row.Span));
        }
    }

    /// <summary>
    /// Every byte value round-trips, including the ones no symbol covers - that is what the escape
    /// code is for, and it is the only reason a table of 255 symbols can encode arbitrary bytes.
    /// </summary>
    [Fact]
    public void EveryByteValueSurvives()
    {
        byte[] all = new byte[256];
        for (int i = 0; i < all.Length; i++)
        {
            all[i] = (byte)i;
        }

        // Trained on text, so the binary row is mostly escapes: exactly the adversarial case.
        List<ReadOnlyMemory<byte>> rows = Rows("aaaaaaaabbbbbbbbcccccccc", "abcabcabcabcabcabc");
        rows.Add(all);

        FsstSymbols? table = FsstSymbols.Train(rows);
        Assert.NotNull(table);
        Assert.Equal(all, RoundTrip(table, all));
    }

    /// <summary>
    /// A highly repetitive corpus compresses, and by a lot. Without this the test above is
    /// satisfied by a table that escapes everything, which round-trips perfectly and compresses
    /// nothing.
    /// </summary>
    [Fact]
    public void ARepetitiveCorpusActuallyGetsSmaller()
    {
        List<ReadOnlyMemory<byte>> rows = [];
        for (int i = 0; i < 200; i++)
        {
            rows.Add(Encoding.UTF8.GetBytes("https://example.com/catalog/item/" + (i % 20)));
        }

        FsstSymbols? table = FsstSymbols.Train(rows);
        Assert.NotNull(table);

        int plain = 0;
        int coded = 0;
        byte[] destination = new byte[256];
        foreach (ReadOnlyMemory<byte> row in rows)
        {
            plain += row.Length;
            coded += table.Compress(row.Span, destination);
        }

        Assert.True(coded * 3 < plain, $"{coded} code bytes for {plain} plain bytes");
    }

    /// <summary>
    /// No symbol may reach past the end of a value. A match into the zero padding of the final
    /// word would decode to MORE bytes than the row held, which the reader detects as a length
    /// mismatch - so this is not a tidiness rule, it is the difference between a readable file and
    /// an unreadable one.
    /// </summary>
    [Fact]
    public void NoSymbolMatchesPastTheEndOfAValue()
    {
        // Trained on rows whose tails would form long symbols if the padding counted.
        List<ReadOnlyMemory<byte>> rows = Rows(
            "abcdefgh", "abcdefgh", "abcdefgh", "abcdef", "abcd", "ab", "a");

        FsstSymbols? table = FsstSymbols.Train(rows);
        Assert.NotNull(table);

        foreach (ReadOnlyMemory<byte> row in rows)
        {
            Assert.Equal(row.ToArray(), RoundTrip(table, row.Span));
        }
    }

    [Fact]
    public void AnEmptyCorpusTrainsNothing()
    {
        Assert.Null(FsstSymbols.Train([]));
        Assert.Null(FsstSymbols.Train([ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty]));
    }

    /// <summary>A corpus larger than the sample target exercises the sampler rather than the whole scan.</summary>
    [Fact]
    public void ACorpusBeyondTheSampleTargetStillRoundTrips()
    {
        List<ReadOnlyMemory<byte>> rows = [];
        for (int i = 0; i < 4000; i++)
        {
            rows.Add(Encoding.UTF8.GetBytes($"user-{i}@example.com|region-{i % 7}|status-active"));
        }

        FsstSymbols? table = FsstSymbols.Train(rows);
        Assert.NotNull(table);

        // Every row, not a sample of them: the table is trained on a sample and must still encode
        // the rows it never saw.
        foreach (ReadOnlyMemory<byte> row in rows)
        {
            Assert.Equal(row.ToArray(), RoundTrip(table, row.Span));
        }
    }

    [Fact]
    public void TheTableNeverExceedsTheCodeSpace()
    {
        List<ReadOnlyMemory<byte>> rows = [];
        for (int i = 0; i < 2000; i++)
        {
            // Deliberately diverse, so the candidate set is far larger than the table.
            rows.Add(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")));
        }

        FsstSymbols? table = FsstSymbols.Train(rows);
        Assert.NotNull(table);
        Assert.InRange(table.Count, 1, FsstSymbols.MaxSymbols);

        for (int i = 0; i < table.Count; i++)
        {
            Assert.InRange(table.SymbolLength(i), (byte)1, (byte)FsstSymbols.MaxSymbolLength);
        }
    }

    /// <summary>
    /// The symbol lengths read 2, 3, ... 8, then 1.
    /// </summary>
    /// <remarks>
    /// The reference VALIDATES this and refuses the array otherwise, while our own reader does not
    /// check it - so no round trip through our decoder can see the violation. It was found by the
    /// Rust cross-check, which rejected 35 of 774 written files, and this test is the cheap local
    /// guard that keeps it found.
    /// </remarks>
    [Theory]
    [InlineData(60)]
    [InlineData(400)]
    [InlineData(3000)]
    public void TheSymbolTableIsOrderedByLength(int rows)
    {
        List<ReadOnlyMemory<byte>> corpus = [];
        for (int i = 0; i < rows; i++)
        {
            // Mixed shapes, so the table ends up holding symbols of several lengths AND
            // single bytes - a table of one length would satisfy the ordering by accident.
            corpus.Add(Encoding.UTF8.GetBytes(
                i % 3 == 0 ? $"prefix-{i}-suffix"
                : i % 3 == 1 ? $"{(char)('a' + (i % 26))}{i}"
                : $"https://example.com/{i % 50}/detail?q={i}"));
        }

        FsstSymbols? table = FsstSymbols.Train(corpus);
        Assert.NotNull(table);

        int expected = 2;
        for (int i = 0; i < table.Count; i++)
        {
            byte length = table.SymbolLength(i);
            Assert.InRange(length, (byte)1, (byte)FsstSymbols.MaxSymbolLength);
            if (expected == 1)
            {
                Assert.Equal(1, length);
                continue;
            }

            if (length == 1)
            {
                expected = 1;
                continue;
            }

            Assert.True(
                length >= expected,
                $"symbol {i} has length {length} after a symbol of length {expected}");
            expected = length;
        }
    }

    private static List<ReadOnlyMemory<byte>> Rows(params string[] values)
    {
        List<ReadOnlyMemory<byte>> rows = [];
        foreach (string value in values)
        {
            rows.Add(Encoding.UTF8.GetBytes(value));
        }

        return rows;
    }

    /// <summary>Compresses, then decodes through the kernel that reads the reference's files.</summary>
    private static byte[] RoundTrip(FsstSymbols table, ReadOnlySpan<byte> value)
    {
        byte[] codes = new byte[Math.Max(value.Length * 2, 1)];
        int written = table.Compress(value, codes);

        byte[] symbols = new byte[table.Count * 8];
        byte[] lengths = new byte[table.Count];
        for (int i = 0; i < table.Count; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                symbols.AsSpan(i * 8), table.SymbolBits(i));
            lengths[i] = table.SymbolLength(i);
        }

        Span<byte> symbolScratch = stackalloc byte[FsstSymbolTable.SymbolScratchBytes];
        Span<byte> widthScratch = stackalloc byte[FsstSymbolTable.WidthScratchBytes];
        FsstDecodeTable decoder = FsstSymbolTable
            .Create(symbols, lengths, "vortex.fsst")
            .Prepare(symbolScratch, widthScratch);
        byte[] decoded = new byte[value.Length];
        uint escapeBits = 0;
        int produced = decoder.Decode(codes.AsSpan(0, written), decoded, "vortex.fsst", ref escapeBits);
        Assert.Equal(value.Length, produced);
        return decoded;
    }
}
