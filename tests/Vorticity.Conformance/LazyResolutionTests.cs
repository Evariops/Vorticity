// THE LAZY-RESOLUTION PAIR: an unknown component fails at use, not at open.
//
// Both halves matter, and the second one is the one that gets deleted:
//
//   1. Reading a PROJECTED column that uses an unknown encoding fails with
//      VortexUnsupportedException naming the id and the kind.
//   2. Scanning while NOT projecting that column SUCCEEDS.
//
// Without (2), "resolve at open, fail at use" degrades into "fail at open" the first time somebody
// simplifies the open path, every test still passes, and a file with one unreadable column in a
// hundred becomes an unreadable file. (2) is also the only place the harness proves the projection
// actually skips a column's segments rather than decoding and discarding them.
//
// The fixture is forged/negative/unknown_encoding_id: containers/uncompressed_canonical with
// `vortex.primitive` overwritten by `vortex.unknown01`, same length, so every offset in the file
// stays valid and the only difference is a name no registry resolves. Its own manifest states the
// three expectations verbatim; they are the three assertions below.
using System;
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

public sealed class LazyResolutionTests
{
    private const string Fixture = "negative/unknown_encoding_id";
    private const string Source = "containers/uncompressed_canonical";
    private const string ForgedId = "vortex.unknown01";

    [Fact]
    public async Task OpeningAFileWithAnUnknownEncodingSucceeds()
    {
        Phase1Components.EnsureRegistered();
        ForgedFixture fixture = ForgedCatalog.Find(Fixture);

        await using VortexFile file = await VortexFile.OpenAsync(
            fixture.FullPath, TestContext.Current.CancellationToken);

        // "opening the file and reading its dtype and layout tree succeeds: an unknown id in
        // array_specs is not itself an error" - forged/manifest.json.
        Assert.Equal(fixture.RowCount, file.RowCount);
        Assert.True(file.IsTabular);
        Assert.Equal(2, file.Schema.FieldCount);
        Assert.Equal("ints", file.Schema.GetFieldName(0));
        Assert.Equal("strs", file.Schema.GetFieldName(1));

        bool sawForgedId = false;
        for (int i = 0; i < file.ArrayEncodingCount; i++)
        {
            if (string.Equals(file.GetArrayEncodingId(i), ForgedId, StringComparison.Ordinal))
            {
                sawForgedId = true;
                Assert.Equal(Vorticity.Arrays.ArrayEncodingId.Unknown, file.GetArrayEncoding(i));
            }
        }

        Assert.True(sawForgedId, $"the fixture must declare {ForgedId} in its array_specs");
    }

    [Fact]
    public async Task ProjectingTheUnknownColumnFailsNamingTheIdAndTheKind()
    {
        Phase1Components.EnsureRegistered();
        ForgedFixture fixture = ForgedCatalog.Find(Fixture);

        await using VortexFile file = await VortexFile.OpenAsync(
            fixture.FullPath, TestContext.Current.CancellationToken);

        VortexUnsupportedException error = await Assert.ThrowsAsync<VortexUnsupportedException>(async () =>
        {
            await foreach (RecordBatch batch in file.Scan().Project("ints").ExecuteAsync()
                .WithCancellation(TestContext.Current.CancellationToken))
            {
                batch.Dispose();
            }
        });

        // An unsupported component is reported with the id AND the kind, in the message.
        Assert.Equal(ForgedId, error.ComponentId);
        Assert.Equal("array", error.Kind);
        Assert.Contains(ForgedId, error.Message, StringComparison.Ordinal);
        Assert.Contains("array", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanningWithoutProjectingTheUnknownColumnSucceedsAndReadsTheOtherColumn()
    {
        // THE HALF THAT GETS DELETED. If this ever fails, the open path or the split planner
        // resolved a component it was not asked to read.
        Phase1Components.EnsureRegistered();
        ForgedFixture fixture = ForgedCatalog.Find(Fixture);
        CorpusEntry source = CorpusCatalog.Verdict(Source).Entry;

        MismatchLog log = new MismatchLog(Fixture);

        using SidecarReader sidecar = SidecarReader.Open(source.FullSidecarPath);
        sidecar.VerifyPairing(source.FullPath);
        SidecarRowStream stream = new SidecarRowStream(sidecar);

        await using VortexFile file = await VortexFile.OpenAsync(
            fixture.FullPath, TestContext.Current.CancellationToken);

        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().Project("strs").ExecuteAsync()
            .WithCancellation(TestContext.Current.CancellationToken))
        {
            rows += CompareProjectedColumn(batch, stream, log);
            batch.Dispose();
        }

        Assert.True(log.IsClean, log.Render());
        Assert.Equal(fixture.RowCount, rows);
        Assert.Equal(fixture.RowCount, sidecar.RowsRead);
    }

    /// <summary>
    /// Compares the projected <c>strs</c> column against the SOURCE file's sidecar. The forged
    /// fixture has no sidecar of its own and needs none: the patch is length-preserving and touches
    /// only an encoding id, so every value of the unpatched column is unchanged by construction.
    /// </summary>
    private static int CompareProjectedColumn(RecordBatch batch, SidecarRowStream stream, MismatchLog log)
    {
        int rows = batch.RowCount;

        // The projected schema is the file's schema minus the unprojected fields, not the whole
        // schema with a hole in it.
        if (batch.FieldCount != 1 || !string.Equals(batch.GetFieldName(0), "strs", StringComparison.Ordinal))
        {
            log.Add(string.Empty, batch.StartRow, "projected schema",
                "one field named strs",
                $"{batch.FieldCount} fields, first named {batch.GetFieldName(0) ?? "<none>"}");
            return rows;
        }

        for (int i = 0; i < rows; i++)
        {
            long fileRow = batch.StartRow + i;
            if (!stream.TryNext(out long expectedRow, out JsonValue row))
            {
                log.Add("strs", fileRow, "the sidecar ran out of values", "a value", "end of stream");
                return rows;
            }

            if (expectedRow != fileRow || row.Kind != JsonKind.Object)
            {
                log.Add("strs", fileRow, "the sidecar row does not line up", row.Summary(), "a struct row");
                return rows;
            }

            JsonValue expected = row.Require("strs");
            ValueComparer.Compare(expected, batch.Column(0), i, "strs", fileRow, log);
        }

        return rows;
    }

    /// <summary>
    /// The unknown id must not be reachable through the ROOT column either: a caller who projects
    /// nothing gets the whole file, which includes the patched column.
    /// </summary>
    [Fact]
    public async Task AFullScanOfTheForgedFileStillFails()
    {
        Phase1Components.EnsureRegistered();
        ForgedFixture fixture = ForgedCatalog.Find(Fixture);

        await using VortexFile file = await VortexFile.OpenAsync(
            fixture.FullPath, TestContext.Current.CancellationToken);

        VortexUnsupportedException error = await Assert.ThrowsAsync<VortexUnsupportedException>(async () =>
        {
            await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
                .WithCancellation(TestContext.Current.CancellationToken))
            {
                batch.Dispose();
            }
        });

        Assert.Equal(ForgedId, error.ComponentId);
        Assert.Equal("array", error.Kind);
    }
}
