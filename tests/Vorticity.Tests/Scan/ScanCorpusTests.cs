// The scan driven over the whole golden corpus, for the properties a value comparison cannot show:
// that every in-scope file yields exactly manifest.row_count rows, in contiguous batches, under the
// schema the projection promised. The value-by-value comparison against the sidecars belongs to the
// conformance component (contract §1.8) and is not duplicated here.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class ScanCorpusTests
{
    [Fact]
    public void TheCorpusIsPresentAndTheDispositionIsComputable()
    {
        Assert.Equal(821, CorpusManifest.All.Count);

        List<CorpusEntry> inScope = CorpusManifest.InScope();

        // A snapshot, not a configuration: if a decoder lands or is withdrawn this number moves,
        // and the test is updated to the new snapshot rather than the computation being replaced by
        // a list. 616 was Phase 1; +32 vortex.decimal_byte_parts, +6 vortex.datetimeparts,
        // +5 vortex.zstd, +33 vortex.alp, +6 vortex.alprd, +30 vortex.fsst, +46 vortex.onpair,
        // +2 distributions/sorted_disjoint_utf8.
        Assert.Equal(776, inScope.Count);
    }

    [Fact]
    public async Task EveryInScopeFileScansToItsManifestRowCount()
    {
        Decoders.EnsureRegistered();
        StringBuilder failures = new StringBuilder();
        int checkedFiles = 0;

        foreach (CorpusEntry entry in CorpusManifest.InScope())
        {
            try
            {
                await ScanOne(entry);
                checkedFiles++;
            }
            catch (Exception exception)
            {
                if (failures.Length < 8000)
                {
                    failures.Append(entry.Id).Append(": ").Append(exception.Message).Append('\n');
                }
            }
        }

        Assert.Equal(string.Empty, failures.ToString());
        Assert.Equal(776, checkedFiles);
    }

    [Fact]
    public async Task EveryOutOfScopeFileFailsNamedOrNotAtAll()
    {
        // Contract §14.1's second half, as a property of the scan rather than of the conformance
        // suite: a file this build cannot read must say which component it is missing. What it must
        // never do is throw VortexFormatException - that would blame the file for our gap - or
        // return values.
        //
        // "Or not at all" is not a hedge: a zero-row file decodes nothing, and a file whose only
        // out-of-scope encoding sits in a zone map is never asked to decode it, because Phase 1
        // does not prune. Both read correctly and both are still out of scope by id.
        Decoders.EnsureRegistered();
        StringBuilder wrong = new StringBuilder();
        int named = 0;
        int read = 0;

        foreach (CorpusEntry entry in CorpusManifest.All)
        {
            if (CorpusManifest.IsInScope(entry) || !entry.HasDTypeSegment)
            {
                continue;
            }

            try
            {
                await using VortexFile file = await VortexFile.OpenAsync(entry.Path, CancellationToken.None);
                await foreach (RecordBatch batch in file.Scan().ExecuteAsync())
                {
                    Assert.True(batch.RowCount > 0);
                }

                read++;
            }
            catch (VortexUnsupportedException error)
            {
                named++;
                if (error.ComponentId.Length == 0 || error.Kind.Length == 0)
                {
                    wrong.Append(entry.Id).Append(": unnamed component\n");
                }
            }
            catch (Exception other)
            {
                if (wrong.Length < 8000)
                {
                    wrong.Append(entry.Id).Append(": ").Append(other.GetType().Name)
                        .Append(" - ").Append(other.Message).Append('\n');
                }
            }
        }

        Assert.Equal(string.Empty, wrong.ToString());
        Assert.Equal(44, named + read);

        // A PROPORTION, not a count: the absolute number shrinks with every decoder that lands,
        // while the property being asserted -- that an out-of-scope file almost always reaches the
        // component it is missing, rather than being quietly readable -- does not.
        Assert.True(
            named * 4 >= (named + read) * 3,
            $"{named} of {named + read} out-of-scope files reached their missing component; " +
            "the rest were readable, which should stay the rare case");
    }

    private static async Task ScanOne(CorpusEntry entry)
    {
        await using VortexFile file = await VortexFile.OpenAsync(entry.Path, CancellationToken.None);
        Assert.Equal(entry.RowCount, file.RowCount);

        long rows = 0;
        long batches = 0;
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync().WithCancellation(CancellationToken.None))
        {
            // Batches are contiguous and in order: row 0 of this batch is the row after the last.
            Assert.Equal(rows, batch.StartRow);
            Assert.True(batch.RowCount > 0, "a scan must not produce an empty batch");

            // An unprojected scan reproduces the file's schema exactly (contract §13 traps).
            Assert.Equal(file.Schema, batch.Schema);

            rows += batch.RowCount;
            batches++;
        }

        Assert.Equal(entry.RowCount, rows);
        Assert.Equal(entry.RowCount == 0, batches == 0);
    }

    [Theory]
    [InlineData("containers/uncompressed_canonical", 4096)]
    [InlineData("containers/chunked_stream_3", 300)]
    [InlineData("types/struct_field_names", 1025)]
    [InlineData("encodings/null_r0", 0)]
    [InlineData("distributions/high_cardinality_i64_r8193", 8193)]
    public async Task NamedFilesScanToTheirRowCount(string id, long expected)
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(id), CancellationToken.None);

        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync())
        {
            rows += batch.RowCount;
        }

        Assert.Equal(expected, rows);
    }

    [Fact]
    public async Task AZeroRowFileProducesNoBatches()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path("encodings/null_r0"), CancellationToken.None);

        int batches = 0;
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync())
        {
            batches++;
            batch.Dispose();
        }

        Assert.Equal(0, batches);
    }

    [Fact]
    public async Task TheEnumerableIsReEnumerableAndTheSchemaIsStable()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path("containers/uncompressed_canonical"), CancellationToken.None);

        System.Collections.Generic.IAsyncEnumerable<RecordBatch> scan = file.Scan().ExecuteAsync();
        long first = await CountRows(scan);
        long second = await CountRows(scan);

        Assert.Equal(first, second);
        Assert.Equal(file.RowCount, first);

        BatchAsyncEnumerable typed = Assert.IsType<BatchAsyncEnumerable>(scan);
        Assert.Equal(file.Schema, typed.Schema);
        Assert.True(typed.Projection.IsAll);
    }

    private static async Task<long> CountRows(System.Collections.Generic.IAsyncEnumerable<RecordBatch> scan)
    {
        long rows = 0;
        await foreach (RecordBatch batch in scan)
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    [Fact]
    public async Task TheDefaultBatchSizeIsTheFilesZoneLength()
    {
        // docs/03-architecture.md §3.4: "batch size derives from the file's zones (8192 by
        // default)". WithMaxBatchRows only ever makes it smaller, which is asserted below by
        // raising the cap far above the zone length and getting the same batching.
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path("distributions/high_cardinality_i64_r8193"), CancellationToken.None);

        Vorticity.Layouts.LayoutTree tree = Vorticity.Layouts.LayoutTree.Parse(file);
        long zoneLength = 0;
        for (int i = 0; i < tree.NodeCount && zoneLength == 0; i++)
        {
            if (tree.GetNode(i).TryGetZoneMap(out Vorticity.Layouts.ZoneMap map))
            {
                zoneLength = map.ZoneLength;
            }
        }

        Assert.True(zoneLength > 0, "the fixture is a zoned file, so it must carry a zone length");

        List<int> plain = await BatchSizes(file, 0);
        List<int> raised = await BatchSizes(file, int.MaxValue);

        Assert.Equal(plain, raised);
        for (int i = 0; i < plain.Count; i++)
        {
            Assert.True(plain[i] <= zoneLength);
        }
    }

    private static async Task<List<int>> BatchSizes(VortexFile file, int cap)
    {
        ScanBuilder builder = file.Scan();
        if (cap > 0)
        {
            builder = builder.WithMaxBatchRows(cap);
        }

        List<int> sizes = new List<int>();
        await foreach (RecordBatch batch in builder.ExecuteAsync())
        {
            sizes.Add(batch.RowCount);
        }

        return sizes;
    }

    [Fact]
    public async Task ABatchNeverExceedsTheCap()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path("distributions/high_cardinality_i64_r8193"), CancellationToken.None);

        long rows = 0;
        int batches = 0;
        await foreach (RecordBatch batch in file.Scan().WithMaxBatchRows(1000).ExecuteAsync())
        {
            Assert.True(
                batch.RowCount <= 1000,
                string.Create(CultureInfo.InvariantCulture, $"batch of {batch.RowCount} rows exceeds the cap"));
            rows += batch.RowCount;
            batches++;
        }

        Assert.Equal(8193, rows);
        Assert.True(batches >= 9, "8193 rows capped at 1000 needs at least nine batches");
    }
}
