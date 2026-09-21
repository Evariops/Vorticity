// Tests of the oracle, not of the library.
//
// A conformance harness that cannot fail is worse than no harness: it reports every file passed
// whatever the reader does. These are the tests that make "every file passed" mean something - each
// one feeds the comparison something it MUST reject, and asserts it rejects it, at the right column
// and the right row.
//
// The two float cases are the ones the sidecar format is built around: `bits` is normative so
// that a wrong NaN payload or a -0.0 read as +0.0 fails the comparison. Here that claim is
// exercised against real decoded values from distributions/float_specials_f64_r1024, whose row 1
// is -0.0 and whose rows 2-4 are three different NaN bit patterns.
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Conformance.Comparison;
using Vorticity.Conformance.Corpus;
using Vorticity.Conformance.Sidecar;
using Vorticity.File;
using Vorticity.Scan;
using Xunit;

namespace Vorticity.Conformance;

public sealed class HarnessSelfTests
{
    private const string Floats = "distributions/float_specials_f64_r1024";
    private const int NegativeZeroRow = 1;
    private const int QuietNaNRow = 2;
    private const int PayloadNaNRow = 3;

    [Fact]
    public void TheJsonReaderKeepsDuplicateAndEmptyObjectKeysInOrder()
    {
        JsonValue value = JsonParser.Parse("""{"": 1, "a.b": 2, "a": 3, "a": 4}""");
        Assert.Equal(4, value.Keys.Length);
        Assert.Equal(string.Empty, value.Keys[0]);
        Assert.Equal("a.b", value.Keys[1]);
        Assert.Equal("a", value.Keys[2]);
        Assert.Equal("a", value.Keys[3]);
        Assert.Equal("4", value.Values[3].Text);
    }

    [Fact]
    public void TheJsonReaderDistinguishesNullFromEmptyStringListAndObject()
    {
        JsonValue value = JsonParser.Parse("""[null, {"b64": "", "len": 0}, [], {}]""");
        Assert.True(value.Items[0].IsNull);
        Assert.False(value.Items[1].IsNull);
        Assert.Equal(string.Empty, value.Items[1].RequireString("b64"));
        Assert.Equal(JsonKind.Array, value.Items[2].Kind);
        Assert.Empty(value.Items[2].Items);
        Assert.Equal(JsonKind.Object, value.Items[3].Kind);
    }

    [Fact]
    public void TheJsonReaderDecodesSurrogatePairsAndEscapes()
    {
        JsonValue value = JsonParser.Parse("\"\\ud83d\\ude00 \\u00e9lan\\t\\\"x\\\"\"");
        Assert.Equal("\U0001F600 élan\t\"x\"", value.Text);
    }

    [Fact]
    public async Task NegativeZeroIsNotPositiveZero()
    {
        ulong negativeZero = 0x8000000000000000UL;
        Assert.Equal(negativeZero, await BitsOfRowAsync(NegativeZeroRow));

        // The row really is -0.0; now assert the comparison refuses +0.0 for it, and accepts -0.0.
        MismatchLog wrong = await CompareRowAsync(
            NegativeZeroRow, """{"bits": "0x0000000000000000", "dec": "0.0"}""");
        Assert.Equal(1, wrong.Total);
        Assert.Contains("float bits", wrong.Kinds);

        MismatchLog right = await CompareRowAsync(
            NegativeZeroRow, """{"bits": "0x8000000000000000", "dec": "-0.0"}""");
        Assert.True(right.IsClean, right.Render());
    }

    [Fact]
    public async Task ANaNPayloadIsPartOfTheValue()
    {
        Assert.Equal(0x7FF8000000000000UL, await BitsOfRowAsync(QuietNaNRow));
        Assert.Equal(0x7FF8000000000001UL, await BitsOfRowAsync(PayloadNaNRow));

        // Both rows are NaN; a comparison that went through `double` would call them equal.
        MismatchLog wrong = await CompareRowAsync(
            PayloadNaNRow, """{"bits": "0x7ff8000000000000", "dec": "NaN", "special": "NaN"}""");
        Assert.Equal(1, wrong.Total);
        Assert.Contains("float bits", wrong.Kinds);

        MismatchLog right = await CompareRowAsync(
            PayloadNaNRow, """{"bits": "0x7ff8000000000001", "dec": "NaN", "special": "NaN"}""");
        Assert.True(right.IsClean, right.Render());
    }

    /// <summary>
    /// The comparison, pointed at a file whose values it does not describe. Same dtype, same row
    /// count, different data - so nothing but a real value comparison can tell them apart.
    /// </summary>
    [Fact]
    public async Task ComparingAFileAgainstAnotherFilesSidecarFails()
    {
        CorpusEntry constant = CorpusCatalog.Verdict("distributions/constant_i64_r1024").Entry;
        CorpusEntry varied = CorpusCatalog.Verdict("distributions/high_cardinality_i64_r1024").Entry;
        Assert.Equal(constant.DType, varied.DType);
        Assert.Equal(constant.RowCount, varied.RowCount);

        FileResult result = await ConformanceRunner.CompareAsync(
            constant, varied.FullSidecarPath, checkPairing: false, TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.False(result.Passed);
        Assert.Contains("integer value", result.Log.Kinds);
    }

    /// <summary>The pairing check catches the same swap before a single value is compared.</summary>
    [Fact]
    public async Task ThePairingCheckRejectsASidecarWrittenAgainstOtherBytes()
    {
        CorpusEntry constant = CorpusCatalog.Verdict("distributions/constant_i64_r1024").Entry;
        CorpusEntry varied = CorpusCatalog.Verdict("distributions/high_cardinality_i64_r1024").Entry;

        FileResult result = await ConformanceRunner.CompareAsync(
            constant, varied.FullSidecarPath, checkPairing: true, TestContext.Current.CancellationToken);

        Assert.NotNull(result.Error);
        Assert.IsType<SidecarFormatException>(result.Error);
        Assert.Contains("sha256", result.Error!.Message, StringComparison.Ordinal);
    }

    /// <summary>A sidecar whose row stream is shorter than the file is a failure, not a pass.</summary>
    [Fact]
    public async Task AShorterSidecarFailsRatherThanPassingOnTheRowsItHas()
    {
        CorpusEntry longer = CorpusCatalog.Verdict("types/i64_nullable_r1024").Entry;
        CorpusEntry shorter = CorpusCatalog.Verdict("types/i64_nullable_r1023").Entry;

        FileResult result = await ConformanceRunner.CompareAsync(
            longer, shorter.FullSidecarPath, checkPairing: false, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
    }

    private static async Task<ulong> BitsOfRowAsync(int row)
    {
        Phase1Components.EnsureRegistered();
        CorpusEntry entry = CorpusCatalog.Verdict(Floats).Entry;

        await using VortexFile file = await VortexFile.OpenAsync(
            entry.FullPath, TestContext.Current.CancellationToken);

        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(TestContext.Current.CancellationToken))
        {
            ulong bits = ReadBits(batch, row);
            batch.Dispose();
            return bits;
        }

        throw new InvalidOperationException($"{Floats} produced no batches.");
    }

    private static ulong ReadBits(RecordBatch batch, int row) =>
        BitConverter.DoubleToUInt64Bits(batch.Root.AsPrimitive<double>()[row]);

    private static async Task<MismatchLog> CompareRowAsync(int row, string expectedJson)
    {
        Phase1Components.EnsureRegistered();
        CorpusEntry entry = CorpusCatalog.Verdict(Floats).Entry;
        JsonValue expected = JsonParser.Parse(expectedJson);
        MismatchLog log = new MismatchLog(Floats);

        await using VortexFile file = await VortexFile.OpenAsync(
            entry.FullPath, TestContext.Current.CancellationToken);

        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(TestContext.Current.CancellationToken))
        {
            CompareOne(batch, row, expected, log);
            batch.Dispose();
            break;
        }

        return log;
    }

    private static void CompareOne(RecordBatch batch, int row, JsonValue expected, MismatchLog log) =>
        ValueComparer.Compare(expected, batch.Root, row, string.Empty, row, log);
}
