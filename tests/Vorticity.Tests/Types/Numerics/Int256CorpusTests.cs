// Real wire bytes, not our own encoder. types/decimal40_10_nonnull_r1025.vortex is a decimal(40,10)
// column written by Vortex 0.86.1; its `vortex.decimal` data node is a single uncompressed buffer
// of 1025 little-endian two's-complement i256 values (the sidecar's layout line shows
// vortex.zoned -> data: vortex.flat -> {id: "vortex.decimal", nbuffers: 1} with metadata
// `values_type = 5`). This test locates that buffer by content and decodes it.
//
// Value-by-value comparison against the sidecar belongs to the conformance component (§1.8), so
// the golden values below are transcribed literals, not a sidecar parse.
using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vorticity.Types.Numerics;
using Xunit;

namespace Vorticity.Tests.Types.Numerics;

public sealed class Int256CorpusTests
{
    private const string CorpusRelativePath =
        "tests/Vorticity.Conformance/corpus/types/decimal40_10_nonnull_r1025.vortex";

    private const int RowCount = 1025;

    /// <summary>(row index, little-endian hex of the stored 32 bytes, expected rendering).</summary>
    private static readonly (int Row, string Hex, string Unscaled)[] Golden =
    {
        (0, "0000000000000000000000000000000000000000000000000000000000000000", "0"),
        (1, "01000000009f0a4654405ba33c0ed69ce2ffffffffffffffffffffffffffffff",
            "-9999999999999999999999999999999999999999"),
        (2, "ffffffffff60f5b9abbfa45cc3f129631d000000000000000000000000000000",
            "9999999999999999999999999999999999999999"),
        (3, "a8aaaaaaaadf58171cc07336145a4734f6ffffffffffffffffffffffffffffff",
            "-3333333333333333333333333333333333333336"),
        (4, "595555555520a7e8e33f8cc9eba5b8cb09000000000000000000000000000000",
            "3333333333333333333333333333333333333337"),
        (5, "a6aaaaaaaadf58171cc07336145a4734f6ffffffffffffffffffffffffffffff",
            "-3333333333333333333333333333333333333338"),
        (512, "555755555520a7e8e33f8cc9eba5b8cb09000000000000000000000000000000",
            "3333333333333333333333333333333333333845"),
        (1022, "535955555520a7e8e33f8cc9eba5b8cb09000000000000000000000000000000",
            "3333333333333333333333333333333333334355"),
        (1023, "aca6aaaaaadf58171cc07336145a4734f6ffffffffffffffffffffffffffffff",
            "-3333333333333333333333333333333333334356"),
        (1024, "555955555520a7e8e33f8cc9eba5b8cb09000000000000000000000000000000",
            "3333333333333333333333333333333333334357"),
    };

    [Fact]
    public void GoldenWireBytesDecodeAndRender()
    {
        Span<byte> roundTrip = stackalloc byte[Int256.ByteCount];
        foreach ((int _, string hex, string unscaled) in Golden)
        {
            byte[] bytes = Int256Tests.Hex(hex);
            Int256 value = Int256.FromLittleEndianBytes(bytes);
            Assert.Equal(unscaled, value.ToString());

            // And the decimal(40,10) rendering the column actually means.
            VortexDecimal decimalValue = new VortexDecimal(value, 40, 10);
            Assert.Equal(DecimalStorageType.I256, decimalValue.Storage);
            Assert.Equal(Scale10(unscaled), decimalValue.ToString());

            value.WriteLittleEndianBytes(roundTrip);
            Assert.True(roundTrip.SequenceEqual(bytes));
        }
    }

    [Fact]
    public void CorpusBufferDecodesValueForValue()
    {
        string? path = FindCorpusFile();
        Assert.SkipWhen(path is null, $"Corpus file not found: {CorpusRelativePath}");

        byte[] file = global::System.IO.File.ReadAllBytes(path!);

        // Locate the value buffer by its content: the first six stored values in sequence occur
        // exactly once in the file.
        byte[] probe = new byte[6 * Int256.ByteCount];
        for (int i = 0; i < 6; i++)
        {
            Int256Tests.Hex(Golden[i].Hex).CopyTo(probe, i * Int256.ByteCount);
        }

        int offset = IndexOf(file, probe);
        Assert.True(offset >= 0, "the decimal value buffer was not found in the corpus file");
        Assert.Equal(-1, IndexOf(file.AsSpan(offset + 1).ToArray(), probe));

        int bufferBytes = RowCount * Int256.ByteCount;
        Assert.True(offset + bufferBytes <= file.Length);
        ReadOnlySpan<byte> buffer = file.AsSpan(offset, bufferBytes);

        foreach ((int row, string hex, string unscaled) in Golden)
        {
            ReadOnlySpan<byte> stored = buffer.Slice(row * Int256.ByteCount, Int256.ByteCount);
            Assert.Equal(Int256Tests.Hex(hex), stored.ToArray());
            Assert.Equal(unscaled, Int256.FromLittleEndianBytes(stored).ToString());
        }

        // Every row: decodes, renders exactly as BigInteger does, and re-encodes bit-identically.
        Span<byte> reencoded = stackalloc byte[Int256.ByteCount];
        for (int row = 0; row < RowCount; row++)
        {
            ReadOnlySpan<byte> stored = buffer.Slice(row * Int256.ByteCount, Int256.ByteCount);
            Int256 value = Int256.FromLittleEndianBytes(stored);
            BigInteger oracle = new BigInteger(stored, isUnsigned: false, isBigEndian: false);
            Assert.Equal(oracle.ToString(CultureInfo.InvariantCulture), value.ToString());

            value.WriteLittleEndianBytes(reencoded);
            Assert.True(reencoded.SequenceEqual(stored));

            // decimal(40,10) never fits System.Decimal at full magnitude, which is the whole
            // reason Int256 exists; the ones that do must convert exactly.
            VortexDecimal decimalValue = new VortexDecimal(value, 40, 10);
            bool fits = BigInteger.Abs(oracle) <= new BigInteger(decimal.MaxValue);
            Assert.Equal(fits, decimalValue.TryToDecimal(out decimal converted));
            if (fits)
            {
                Assert.Equal(oracle, new BigInteger(converted * 10_000_000_000m));
            }
        }
    }

    /// <summary>Renders an unscaled decimal string at scale 10, independently of the code under test.</summary>
    private static string Scale10(string unscaled)
    {
        bool negative = unscaled.StartsWith('-');
        string digits = negative ? unscaled.Substring(1) : unscaled;
        if (unscaled == "0")
        {
            return "0.0000000000";
        }

        string body = digits.Length > 10
            ? digits.Substring(0, digits.Length - 10) + "." + digits.Substring(digits.Length - 10)
            : "0." + new string('0', 10 - digits.Length) + digits;
        return negative ? "-" + body : body;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) =>
        haystack.IndexOf(needle);

    private static string SourceDirectory([CallerFilePath] string path = "") =>
        Path.GetDirectoryName(path) ?? string.Empty;

    private static string? FindCorpusFile()
    {
        // The source directory is the only anchor that survives every runner: `dotnet run` on the
        // per-component check project sets neither the working directory nor the base directory
        // anywhere inside the repository.
        string[] starts =
        {
            SourceDirectory(), Directory.GetCurrentDirectory(), AppContext.BaseDirectory,
        };
        foreach (string start in starts)
        {
            if (start.Length == 0)
            {
                continue;
            }

            DirectoryInfo? directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                string candidate = Path.Combine(
                    directory.FullName, CorpusRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (global::System.IO.File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }
}
