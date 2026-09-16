// The round trip over the WHOLE in-scope corpus, not nine hand-picked files.
//
// Nine files test the shapes whoever wrote the list thought of. The corpus has every dtype crossed
// with nullability crossed with the row counts where block boundaries live, plus the container
// shapes no type matrix reaches -- and a writer fails on exactly the shape nobody listed. So this
// sweeps all of them and reports every failure at once rather than stopping at the first, because
// "the writer is broken" is useless next to "the writer is broken on lists and on nothing else".
//
// A shape the writer cannot serialize is a FAILURE here, not a skip. The point of a sweep is that
// its coverage is the corpus's rather than the author's, and a silent skip list would put the
// author back in charge of it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Scan;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class RoundTripSweepTests
{
    [Fact]
    public async Task EveryInScopeCorpusFileSurvivesAWriteAndAReadBack()
    {
        Decoders.EnsureRegistered();

        StringBuilder failures = new StringBuilder();
        int checkedFiles = 0;
        int rows = 0;

        foreach (CorpusEntry entry in CorpusManifest.InScope())
        {
            try
            {
                rows += await RoundTrip(entry);
                checkedFiles++;
            }
            catch (Exception error) when (failures.Length < 12_000)
            {
                failures.Append(entry.Id).Append(": ")
                    .Append(error.GetType().Name).Append(" - ")
                    .Append(error.Message).Append('\n');
            }
        }

        Console.Out.Write(
            "WRITER SWEEP: " + checkedFiles.ToString(CultureInfo.InvariantCulture) + " corpus files " +
            "written and read back, " + rows.ToString(CultureInfo.InvariantCulture) +
            " rows compared value for value.\n");

        // On the console as well as in the assertion: xunit truncates a long string comparison at
        // fifty characters, which on a sweep over 813 files is one file's name and the first word
        // of its exception.
        if (failures.Length > 0)
        {
            Console.Out.Write(failures.ToString());
        }

        Assert.Equal(string.Empty, failures.ToString());
        Assert.True(checkedFiles > 700, $"only {checkedFiles} files round-tripped");
    }

    [Fact]
    public async Task WritesTheCorpusOutForTheRustCrossCheck()
    {
        // The .NET half of criterion 2 (docs/01-scope.md §4): produce the files, and let
        // `cargo run --example verify_written` decide whether the reference agrees with them. It is
        // env-gated because it costs a full corpus write and only the cross-check consumes the
        // output -- and it SKIPS rather than passes when the variable is absent, so a CI job that
        // forgets to set it does not look like a green cross-check.
        string? root = Environment.GetEnvironmentVariable("VORTICITY_WRITE_CORPUS");
        Assert.SkipWhen(
            string.IsNullOrEmpty(root),
            "Set VORTICITY_WRITE_CORPUS to a directory to produce files for the Rust cross-check.");

        Decoders.EnsureRegistered();
        Directory.CreateDirectory(root!);

        int written = 0;
        int indexed = 0;
        Dictionary<string, int> built = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (CorpusEntry entry in CorpusManifest.InScope())
        {
            // NOT WRITTEN FOR THE CROSS-CHECK, and the reason is the verifier's, not ours. Our
            // output for types/no_dtype_segment is fine - RoundTrip reads it back with default
            // options - but `verify_written` opens the REFERENCE file to compare against, and that
            // one has no dtype segment by construction, so the Rust side fails at open. Supplying a
            // schema is a capability the example does not have; teaching it one is the better fix
            // and is recorded rather than done here.
            if (!entry.HasDTypeSegment)
            {
                continue;
            }

            string destination = Path.Combine(root!, entry.Id.Replace('/', Path.DirectorySeparatorChar) + ".vortex");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await using VortexFile source = await VortexFile.OpenAsync(
                entry.Path, OpenOptionsFor(entry), CancellationToken.None);
            // WITH INDEXES, so that the whole corpus crosses the rule of docs/10-indexes.md §3.1: a
            // file with an index opens and scans in a strict Rust 0.86.1 reader, without error and
            // without configuration. The files take four policies in turn, budget lifted: a Bloom
            // filter at all three resolutions on every column, postings on every column, sorted
            // runs cut into small segments on every column, and `Auto` with a Bloom on every other
            // field -- which puts payload regions BETWEEN the data chunks, where no layout
            // references them. The data the verifier compares is the default write's.
            WritePolicy policy = (written % 4) switch
            {
                0 => WritePolicy.None.WithDefault(IndexPolicy.Bloom(resolutions: 3)),
                1 => WritePolicy.None.WithDefault(IndexPolicy.Postings),
                2 => WritePolicy.None.WithDefault(IndexPolicy.SortedRuns.WithSegmentEntries(500)),
                _ => WritePolicy.Auto,
            };
            if (written % 4 == 3 && source.Schema.Kind == DTypeKind.Struct)
            {
                for (int field = 1; field < source.Schema.FieldCount; field += 2)
                {
                    policy = policy.For(source.Schema.GetFieldName(field), IndexPolicy.Bloom(resolutions: 3));
                }
            }

            await using VortexFileWriter writer = VortexFileWriter.Create(
                destination, source.Schema,
                new VortexWriteOptions { Indexes = policy, IndexBudgetPerMille = 1_000_000 });
            await foreach (RecordBatch batch in source.Scan().ExecuteAsync()
                .WithCancellation(CancellationToken.None))
            {
                await writer.WriteAsync(batch, CancellationToken.None);
            }

            // A struct with no field has no column to index, and so no directory.
            WriteReport report = await writer.CompleteAsync(CancellationToken.None);
            if (report.Indexes.Count > 0 || report.Columns.Count == 0)
            {
                indexed++;
            }

            HashSet<string> kinds = [];
            foreach (IndexWriteReport index in report.Indexes)
            {
                if (index.Outcome == IndexOutcome.Built && kinds.Add(index.Kind))
                {
                    built[index.Kind] = built.GetValueOrDefault(index.Kind) + 1;
                }
            }

            written++;
        }

        StringBuilder line = new StringBuilder("WROTE ")
            .Append(written.ToString(CultureInfo.InvariantCulture)).Append(" files to ").Append(root)
            .Append(", ").Append(indexed.ToString(CultureInfo.InvariantCulture)).Append(" of them with an index directory;");
        foreach ((string kind, int files) in built)
        {
            line.Append(' ').Append(kind).Append(": ").Append(files.ToString(CultureInfo.InvariantCulture)).Append(';');
        }

        Console.Out.Write(line.Append('\n').ToString());
        Assert.True(written > 700);
        Assert.Equal(written, indexed);

        // Enough files with payload regions between their chunks that the rule is tested, not assumed.
        foreach (string kind in new[] { IndexKinds.BloomSbbf, IndexKinds.PostingsBlocks, IndexKinds.SortedRuns })
        {
            int files = built.GetValueOrDefault(kind);
            Assert.True(files > 100, $"only {files} files carry {kind}");
        }
    }

    /// <summary>Writes one corpus file out and reads it back, comparing every value.</summary>
    /// <returns>How many rows were compared.</returns>
    private static async Task<int> RoundTrip(CorpusEntry entry)
    {
        string written = Path.Combine(Path.GetTempPath(), $"vorticity-{Guid.NewGuid():N}.vortex");
        try
        {
            List<string> original = [];
            DType schema;

            await using (VortexFile source = await VortexFile.OpenAsync(
                entry.Path, OpenOptionsFor(entry), CancellationToken.None))
            {
                schema = source.Schema;
                await using VortexFileWriter writer = VortexFileWriter.Create(written, schema);
                await foreach (RecordBatch batch in source.Scan().ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    Values.DescribeRows(batch, original);
                    await writer.WriteAsync(batch, CancellationToken.None);
                }

                await writer.CompleteAsync(CancellationToken.None);
            }

            List<string> readBack = [];
            await using (VortexFile target = await VortexFile.OpenAsync(written, CancellationToken.None))
            {
                if (target.RowCount != entry.RowCount)
                {
                    throw new InvalidOperationException(
                        $"wrote {entry.RowCount} rows and read back {target.RowCount}");
                }

                await foreach (RecordBatch batch in target.Scan().ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    Values.DescribeRows(batch, readBack);
                }
            }

            if (original.Count != readBack.Count)
            {
                throw new InvalidOperationException(
                    $"wrote {original.Count} values and read back {readBack.Count}");
            }

            for (int i = 0; i < original.Count; i++)
            {
                if (!string.Equals(original[i], readBack[i], StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"value {i} was '{original[i]}' and read back '{readBack[i]}'");
                }
            }

            return (int)entry.RowCount;
        }
        finally
        {
            if (System.IO.File.Exists(written))
            {
                System.IO.File.Delete(written);
            }
        }
    }

    /// <summary>
    /// Open options for one entry: the schema supplied out of band when the file has no dtype
    /// segment.
    /// </summary>
    /// <param name="entry">The corpus entry about to be opened.</param>
    /// <remarks>
    /// <c>types/no_dtype_segment</c> is the corpus's only such file, and it reached this sweep only
    /// when <c>vortex.map</c> gained a decoder and the file became in-scope. Opening it without a
    /// DType is a <c>VortexFormatException</c> BY CONTRACT §7.4, so the failure was the sweep
    /// calling the wrong overload rather than anything about the round trip. The donor is a real
    /// file with the identical schema, which is the same approach <c>Phase1CompositionTests</c>
    /// already takes.
    /// </remarks>
    private static VortexOpenOptions OpenOptionsFor(CorpusEntry entry) =>
        entry.HasDTypeSegment
            ? VortexOpenOptions.Default
            : new VortexOpenOptions { DType = OutOfBandSchema.Value };

    private static readonly Lazy<DType> OutOfBandSchema = new Lazy<DType>(static () =>
    {
        VortexFile donor = VortexFile
            .OpenAsync(
                CorpusManifest.Get("types/user_metadata_segments").Path,
                VortexOpenOptions.Default,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        return donor.Schema;
    });
}
