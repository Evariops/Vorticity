// The scan driven over the whole golden corpus, for the properties a value comparison cannot show:
// that every in-scope file yields exactly manifest.row_count rows, in contiguous batches, under the
// schema the projection promised. The value-by-value comparison against the sidecars belongs to the
// conformance component and is not duplicated here.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class ScanCorpusTests
{
    [Fact]
    public void TheCorpusIsPresentAndTheDispositionIsComputable()
    {
        Assert.Equal(856, CorpusManifest.All.Count);

        List<CorpusEntry> inScope = CorpusManifest.InScope();

        // A snapshot, not a configuration: if a decoder lands or is withdrawn this number moves,
        // and the test is updated to the new snapshot rather than the computation being replaced by
        // a list.
        Assert.Equal(856, inScope.Count);
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
        Assert.Equal(856, checkedFiles);
    }

    [Fact]
    public async Task EveryOutOfScopeFileFailsNamedOrNotAtAll()
    {
        // How an unsupported file fails, as a property of the scan rather than of the conformance
        // suite: a file this build cannot read must say which component it is missing. What it must
        // never do is throw VortexFormatException - that would blame the file for our gap - or
        // return values.
        //
        // "Or not at all" is not a hedge: a zero-row file decodes nothing, and a file whose only
        // out-of-scope encoding sits in a zone map is never asked to decode it: a scan without a
        // filter does not prune. Both read correctly and both are still out of scope by id.
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
                await using VortexFile file = await VortexFile.OpenAsync(
                    entry.Path, OpenOptionsFor(entry), CancellationToken.None);
                await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync())
                {
                    Assert.True(batch.RowCount > 0);
                }

                read++;
            }
            catch (VortexUnsupportedException error)
            {
                named++;
                if (error.ComponentId.Length == 0 || !Enum.IsDefined(error.Kind))
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

        // ZERO OUT-OF-SCOPE FILES is what the loop above finds. The property it guards -- that a
        // file this build cannot read says WHICH component it is missing, and never blames the
        // file with a VortexFormatException -- is asserted by the `catch` arms: they run for no
        // file in this corpus, and the moment a corpus regeneration adds a component we do not
        // have, they run again and still hold. The count is the finding.
        Assert.Equal(0, named + read);
    }

    private static async Task ScanOne(CorpusEntry entry)
    {
        await using VortexFile file = await VortexFile.OpenAsync(
            entry.Path, OpenOptionsFor(entry), CancellationToken.None);
        Assert.Equal(entry.RowCount, file.RowCount);

        long rows = 0;
        long batches = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync().WithCancellation(CancellationToken.None))
        {
            // Batches are contiguous and in order: row 0 of this batch is the row after the last.
            Assert.Equal(rows, batch.StartRow);
            Assert.True(batch.RowCount > 0, "a scan must not produce an empty batch");

            // An unprojected scan reproduces the file's schema exactly.
            Assert.Equal(file.DType, batch.DType);

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
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync())
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
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync())
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

        System.Collections.Generic.IAsyncEnumerable<RecordBatch> scan = file.ScanBuilder().ExecuteAsync();
        long first = await CountRows(scan);
        long second = await CountRows(scan);

        Assert.Equal(first, second);
        Assert.Equal(file.RowCount, first);

        BatchAsyncEnumerable typed = Assert.IsType<BatchAsyncEnumerable>(scan);
        Assert.Equal(file.DType, typed.Schema);
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
    public async Task TheDefaultBatchSizeIsTheWindow()
    {
        // The batch size of a scan that only reads derives from the file's zones: as many zone
        // lengths as a window holds. WithMaxBatchRows only ever makes it smaller, which is asserted
        // below by raising the cap far above that and getting the same batching.
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
        long batch = Math.Max(1, Vorticity.Layouts.FlatLayoutReader.WindowRows / zoneLength) * zoneLength;

        Assert.Equal(plain, raised);
        for (int i = 0; i < plain.Count; i++)
        {
            Assert.True(plain[i] <= batch);
        }
    }

    private static async Task<List<int>> BatchSizes(VortexFile file, int cap)
    {
        ScanBuilder builder = file.ScanBuilder();
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
        await foreach (RecordBatch batch in file.ScanBuilder().WithMaxBatchRows(1000).ExecuteAsync())
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

    /// <summary>Open options for one entry: the schema out of band when the file has none.</summary>
    /// <param name="entry">The corpus entry about to be opened.</param>
    /// <remarks>
    /// <c>types/no_dtype_segment</c> reached this sweep only when <c>vortex.map</c> gained a decoder
    /// and the file became in-scope. Opening it without a DType is a <c>VortexFormatException</c>,
    /// so the donor is a real corpus file with the identical schema.
    /// </remarks>
    private static VortexOpenOptions OpenOptionsFor(CorpusEntry entry) =>
        entry.HasDTypeSegment
            ? VortexOpenOptions.Default
            : new VortexOpenOptions { DType = OutOfBandSchema.Value };

    private static readonly Lazy<Vorticity.Types.DType> OutOfBandSchema =
        new Lazy<Vorticity.Types.DType>(static () =>
        {
            VortexFile donor = VortexFile
                .OpenAsync(
                    CorpusManifest.Get("types/user_metadata_segments").Path,
                    VortexOpenOptions.Default,
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            return donor.DType;
        });
}
