// The invariant an equality pushed into `vortex.fsst` rests on: compressing a value with a
// column's table yields the code sequence that column stores for an equal value.
//
// It is one rule -- take the longest symbol that matches here, escape the byte when none does --
// and it does not depend on the order symbols are examined in, because two symbols of equal width
// that both match the same bytes are the same symbol. So the rule is a function of the symbol set,
// and any two implementations of it agree. This asserts that of the two the repository has: the
// writer's, which builds a hash of prefixes and a table of every two-byte pair because it
// compresses a whole column, and the reader's, which scans the symbols because it compresses one
// needle.
//
// What this does not cover, and does not need to: that the table survives serialization. Every
// `vortex.fsst` file of the corpus decodes to its expected values, which it could not do if the
// symbols the reader rebuilds were not the symbols the writer trained.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class FsstCompressInvariantTests
{
    /// <summary>Both compressors agree, value for value, on a table trained from those values.</summary>
    [Fact]
    public void TheTwoCompressorsAgreeOnEveryValueOfATrainedTable()
    {
        List<byte[]> values = Corpus();
        List<ReadOnlyMemory<byte>> rows = values.ConvertAll(v => new ReadOnlyMemory<byte>(v));

        FsstSymbols? trained = FsstSymbols.Train(rows);
        Assert.NotNull(trained);

        int count = trained.Count;
        byte[] symbols = new byte[count * 8];
        byte[] lengths = new byte[count];
        for (int code = 0; code < count; code++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                symbols.AsSpan(code * 8, 8), trained.SymbolBits(code));
            lengths[code] = trained.SymbolLength(code);
        }

        FsstSymbolTable table = FsstSymbolTable.Create(symbols, lengths, "vortex.fsst");

        int escapes = 0;
        foreach (byte[] value in values)
        {
            byte[] byWriter = new byte[FsstSymbolTable.MaxCompressedLength(value.Length) + 1];
            int writerWritten = trained.Compress(value, byWriter);

            byte[] byReader = new byte[FsstSymbolTable.MaxCompressedLength(value.Length) + 1];
            Assert.True(table.TryCompress(value, byReader, out int readerWritten));

            Assert.Equal(writerWritten, readerWritten);
            Assert.Equal(byWriter.AsSpan(0, writerWritten).ToArray(), byReader.AsSpan(0, readerWritten).ToArray());

            for (int i = 0; i < writerWritten; i++)
            {
                if (byWriter[i] == 255)
                {
                    escapes++;
                    i++;
                }
            }
        }

        // The corpus below is chosen to make the escape path run: an agreement reached only on
        // values the table covers entirely would say nothing about the branch that costs two bytes.
        Assert.True(escapes > 0, "no value escaped a byte, so the escape path was never compared");
    }

    /// <summary>A needle the table has never seen still compresses the same way on both sides.</summary>
    [Fact]
    public void ANeedleAbsentFromTheTrainingSetCompressesTheSameWayOnBothSides()
    {
        List<ReadOnlyMemory<byte>> rows =
            Corpus().ConvertAll(v => new ReadOnlyMemory<byte>(v));
        FsstSymbols? trained = FsstSymbols.Train(rows);
        Assert.NotNull(trained);

        int count = trained.Count;
        byte[] symbols = new byte[count * 8];
        byte[] lengths = new byte[count];
        for (int code = 0; code < count; code++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                symbols.AsSpan(code * 8, 8), trained.SymbolBits(code));
            lengths[code] = trained.SymbolLength(code);
        }

        FsstSymbolTable table = FsstSymbolTable.Create(symbols, lengths, "vortex.fsst");

        // A literal in a filter is not a row of the column, so this is the case the push-down
        // actually runs: bytes the training never saw, mixed with prefixes it did.
        foreach (string needle in new[]
        {
            "https://example.invalid/vortex/conformance/999999999",
            "éèê unseen bytes ÿþ",
            "",
            "z",
        })
        {
            byte[] value = Encoding.UTF8.GetBytes(needle);
            byte[] byWriter = new byte[FsstSymbolTable.MaxCompressedLength(value.Length) + 1];
            int writerWritten = trained.Compress(value, byWriter);

            byte[] byReader = new byte[FsstSymbolTable.MaxCompressedLength(value.Length) + 1];
            Assert.True(table.TryCompress(value, byReader, out int readerWritten));

            Assert.Equal(writerWritten, readerWritten);
            Assert.Equal(
                byWriter.AsSpan(0, writerWritten).ToArray(),
                byReader.AsSpan(0, readerWritten).ToArray());
        }
    }

    /// <summary>Values shaped like the corpus's text columns, with bytes no symbol will cover.</summary>
    private static List<byte[]> Corpus()
    {
        List<byte[]> values = [];
        for (int row = 0; row < 512; row++)
        {
            values.Add(Encoding.UTF8.GetBytes(string.Create(
                CultureInfo.InvariantCulture,
                $"https://example.invalid/vortex/conformance/{row:D9}")));
        }

        for (int row = 0; row < 64; row++)
        {
            values.Add(Encoding.UTF8.GetBytes(string.Create(
                CultureInfo.InvariantCulture, $"label-{row % 16:D2}")));
        }

        values.Add([0x00, 0xFF, 0x7F, 0x80]);
        values.Add(Encoding.UTF8.GetBytes("éèêû"));
        return values;
    }
}
