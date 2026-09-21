// One in-scope corpus file, read and compared end to end. This is the acceptance test of Phase 1
// reduced to a method: open the file, scan it, and check every value, every validity bit and every
// nested structure against the Rust-produced sidecar, localizing any disagreement to
// (file, column, row).
//
// The order of the checks is deliberate. The sidecar's sha256 is verified against the .vortex
// FIRST, because every later comparison is meaningless if the pair is not a pair. The dtype tree
// comes next, because a schema mismatch explains every value mismatch that would follow. Only then
// are the values compared, and only then the null counts, which are a whole-file aggregate and
// cannot localize anything on their own.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Conformance.Comparison;
using Vorticity.Conformance.Corpus;
using Vorticity.Conformance.Sidecar;
using Vorticity.Types;
using Vorticity.File;
using Vorticity.Scan;

namespace Vorticity.Conformance;

/// <summary>The outcome of comparing one file against its sidecar.</summary>
internal sealed class FileResult
{
    internal required string EntryId { get; init; }

    internal required MismatchLog Log { get; init; }

    /// <summary>Set when the read threw instead of producing values.</summary>
    internal Exception? Error { get; init; }

    internal long Rows { get; init; }

    internal long Batches { get; init; }

    internal bool Passed => Error is null && Log.IsClean;

    /// <summary>The failure, rendered for a report. Empty when the file passed.</summary>
    internal string Describe()
    {
        if (Passed)
        {
            return string.Empty;
        }

        if (Error is not null)
        {
            return $"{EntryId}: threw {Error.GetType().Name}: {Error.Message}";
        }

        return Log.Render();
    }

    /// <summary>The failure in one line, for the run summary.</summary>
    internal string Summarize()
    {
        if (Passed)
        {
            return "pass";
        }

        return Error is not null
            ? $"threw {Error.GetType().Name}: {Firstline(Error.Message)}"
            : Log.Summary();
    }

    private static string Firstline(string message)
    {
        int at = message.IndexOf('\n');
        string line = at < 0 ? message : message[..at];
        return line.Length <= 220 ? line : line[..220] + "...";
    }
}

/// <summary>Reads one corpus file and compares it, value for value, to its sidecar.</summary>
internal static class ConformanceRunner
{
    /// <summary>Runs the whole comparison for one in-scope entry.</summary>
    /// <param name="entry">The corpus entry.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>What matched and what did not.</returns>
    internal static Task<FileResult> CompareAsync(
        CorpusEntry entry, CancellationToken cancellationToken = default) =>
        CompareAsync(entry, entry.FullSidecarPath, checkPairing: true, cancellationToken);

    /// <summary>
    /// The same comparison against a caller-chosen sidecar. Exists for the harness's own self-test,
    /// which points a file at another file's sidecar to prove the comparison can fail; nothing in
    /// the conformance run uses anything but the paired one.
    /// </summary>
    /// <param name="entry">The corpus entry to read.</param>
    /// <param name="sidecarPath">The sidecar to compare against.</param>
    /// <param name="checkPairing">Whether to verify the sidecar's sha256 against the file.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>What matched and what did not.</returns>
    internal static async Task<FileResult> CompareAsync(
        CorpusEntry entry, string sidecarPath, bool checkPairing, CancellationToken cancellationToken)
    {
        Phase1Components.EnsureRegistered();

        MismatchLog log = new MismatchLog(entry.Id);
        long rows = 0;
        long batches = 0;

        try
        {
            using SidecarReader sidecar = SidecarReader.Open(sidecarPath);

            // Hash the .vortex and compare it against the header before trusting
            // anything else in the file.
            if (checkPairing)
            {
                sidecar.VerifyPairing(entry.FullPath);

                Check(log, "sidecar entry_id", entry.Id, sidecar.EntryId);
                Check(log, "sidecar path", entry.Path, sidecar.FilePath);
                Check(log, "sidecar sha256", entry.Sha256, sidecar.Sha256);
                Check(log, "sidecar dtype display", entry.DType, sidecar.DTypeDisplay);
                CheckNumber(log, "sidecar row_count", entry.RowCount, sidecar.RowCount);
            }

            SidecarRowStream stream = new SidecarRowStream(sidecar);
            NullCountAccumulator nulls = new NullCountAccumulator();

            // types/no_dtype_segment has no dtype segment, so opening it without one is a
            // VortexFormatException and the caller is expected to supply the
            // schema. It reached this runner only when vortex.map gained a decoder and the file
            // became in-scope; the donor is a real corpus file with the identical schema.
            await using VortexFile file = await VortexFile
                .OpenAsync(entry.FullPath, OpenOptionsFor(entry), cancellationToken)
                .ConfigureAwait(false);

            CheckNumber(log, "row count", sidecar.RowCount, file.RowCount);
            SchemaComparer.Compare(sidecar.DTypeTree, file.Schema, string.Empty, log);
            await CompareMetadataAsync(file, sidecar, log, cancellationToken).ConfigureAwait(false);

            await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                batches++;
                rows += CompareBatch(batch, stream, nulls, log);
                batch.Dispose();
            }

            CheckNumber(log, "rows produced by the scan", sidecar.RowCount, rows);

            // Drain what the scan did not consume, so a scan that stopped early is reported as a
            // row-count mismatch AND the null_counts line is still reached.
            sidecar.FinishRows();
            CheckNumber(log, "rows in the sidecar", sidecar.RowCount, sidecar.RowsRead);

            CompareNullCounts(sidecar, nulls, log);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new FileResult
            {
                EntryId = entry.Id,
                Log = log,
                Error = error,
                Rows = rows,
                Batches = batches,
            };
        }

        return new FileResult { EntryId = entry.Id, Log = log, Rows = rows, Batches = batches };
    }

    /// <summary>
    /// Compares one batch. Synchronous and separate from the scan loop on purpose: every column
    /// view is a <c>ref struct</c> that cannot cross an <c>await</c>.
    /// </summary>
    private static int CompareBatch(
        RecordBatch batch, SidecarRowStream stream, NullCountAccumulator nulls, MismatchLog log)
    {
        int rowCount = batch.RowCount;
        for (int i = 0; i < rowCount; i++)
        {
            long fileRow = batch.StartRow + i;
            if (!stream.TryNext(out long expectedRow, out JsonValue expected))
            {
                log.Add(string.Empty, fileRow, "the sidecar ran out of values",
                    "a value", "end of the sidecar's row stream");
                return rowCount;
            }

            if (expectedRow != fileRow)
            {
                log.Add(string.Empty, fileRow, "the sidecar and the scan disagree on the row index",
                    expectedRow.ToString(CultureInfo.InvariantCulture),
                    fileRow.ToString(CultureInfo.InvariantCulture));
                return rowCount;
            }

            ValueComparer.Compare(expected, batch.Root, i, string.Empty, fileRow, log);
        }

        nulls.Accumulate(batch);
        return rowCount;
    }

    /// <summary>
    /// User metadata segments, keys and PAYLOADS, in stored order. The sidecar carries the bytes as
    /// base64; nothing else in the test suite reads them back, and a metadata value is as much a
    /// value as a column's is.
    /// </summary>
    internal static async Task CompareMetadataAsync(
        VortexFile file, SidecarReader sidecar, MismatchLog log, CancellationToken cancellationToken)
    {
        SidecarMetadataSegment[] expected = sidecar.Metadata;
        CheckNumber(log, "user metadata segment count", expected.Length, file.MetadataCount);

        int count = Math.Min(expected.Length, file.MetadataCount);
        for (int i = 0; i < count; i++)
        {
            Check(log, $"metadata[{i}] key", expected[i].Key, file.GetMetadataKey(i));

            SegmentOwner owner = await file.ReadMetadataAsync(i, cancellationToken).ConfigureAwait(false);
            try
            {
                CompareMetadataBytes(expected[i], owner, log, i);
            }
            finally
            {
                owner.Release();
            }
        }
    }

    private static void CompareMetadataBytes(
        SidecarMetadataSegment expected, SegmentOwner owner, MismatchLog log, int index)
    {
        ReadOnlySpan<byte> actual = owner.Buffer.Span;
        if (!actual.SequenceEqual(expected.Value))
        {
            log.Add(string.Empty, -1, $"metadata[{index}] payload",
                $"{expected.Value.Length.ToString(CultureInfo.InvariantCulture)} bytes",
                $"{actual.Length.ToString(CultureInfo.InvariantCulture)} bytes, differing");
        }
    }

    private static void CompareNullCounts(SidecarReader sidecar, NullCountAccumulator nulls, MismatchLog log)
    {
        SidecarNullCount[] expected = sidecar.NullCounts;

        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (SidecarNullCount entry in expected)
        {
            seen.Add(entry.Path);

            // A file whose field names contain a '.' or are empty makes these paths ambiguous by
            // construction. The VALUES of those columns are compared by index and are unaffected;
            // it is only this aggregate that cannot be attributed, so the path is named and
            // skipped rather than matched against a number two different columns contributed to.
            if (nulls.AmbiguousPaths.Contains(entry.Path))
            {
                log.SkippedNullCountPaths.Add(entry.Path);
                continue;
            }

            long actual = nulls.Counts.TryGetValue(entry.Path, out long value) ? value : -1;
            if (actual != entry.Count)
            {
                log.Add(entry.Path, -1, "null_count",
                    entry.Count.ToString(CultureInfo.InvariantCulture),
                    actual < 0 ? "no such path in the decoded schema" : actual.ToString(CultureInfo.InvariantCulture));
            }
        }

        foreach (KeyValuePair<string, long> pair in nulls.Counts)
        {
            if (!seen.Contains(pair.Key) && !nulls.AmbiguousPaths.Contains(pair.Key))
            {
                log.Add(pair.Key, -1, "a decoded path the sidecar does not list",
                    "no such path", pair.Value.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private static void Check(MismatchLog log, string what, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            log.Add(string.Empty, -1, what, expected, actual);
        }
    }

    private static VortexOpenOptions OpenOptionsFor(CorpusEntry entry) =>
        entry.HasDTypeSegment
            ? VortexOpenOptions.Default
            : new VortexOpenOptions { DType = OutOfBandSchema.Value };

    private static readonly Lazy<DType> OutOfBandSchema = new Lazy<DType>(static () =>
    {
        VortexFile donor = VortexFile
            .OpenAsync(
                System.Array.Find(
                    CorpusCatalog.Entries,
                    static e => string.Equals(e.Id, "types/user_metadata_segments", StringComparison.Ordinal))!.FullPath,
                VortexOpenOptions.Default,
                System.Threading.CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        return donor.Schema;
    });

    private static void CheckNumber(MismatchLog log, string what, long expected, long actual)
    {
        if (expected != actual)
        {
            log.Add(string.Empty, -1, what,
                expected.ToString(CultureInfo.InvariantCulture),
                actual.ToString(CultureInfo.InvariantCulture));
        }
    }
}
