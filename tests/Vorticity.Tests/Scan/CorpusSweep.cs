// What the corpus sweeps share: how a corpus file opens, which rows a take asks of it, and the rows
// a plain scan of it returns, by digest, read once a run for every sweep that compares against them.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Scanning;
using Vorticity.Tests.Writing;
using Vorticity.Types;

namespace Vorticity.Tests.Scan;

internal static class CorpusSweep
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<List<UInt128>>>> Plain = new(StringComparer.Ordinal);

    /// <summary>
    /// The digest of each row a plain scan of the file returns: the oracle the windowed reads, the
    /// takes and the round trip are held to. Read once a run, whichever sweep asks first.
    /// </summary>
    internal static Task<List<UInt128>> PlainAsync(string path) =>
        Plain.GetOrAdd(path, key => new Lazy<Task<List<UInt128>>>(() => DigestAsync(key))).Value;

    /// <summary>The digest of each row a plain scan of the file returns, read now.</summary>
    internal static async Task<List<UInt128>> DigestAsync(string path)
    {
        List<UInt128> rows = [];
        await using VortexFile file = await VortexFile.OpenAsync(path, await OpenOptionsForAsync(path), CancellationToken.None);
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync().WithCancellation(CancellationToken.None))
        {
            Values.DigestRows(batch, rows);
        }

        return rows;
    }

    /// <summary>
    /// The file written again by this writer, under its default options, from a plain scan: once a
    /// run, for the round trip that reads it back and the ratchet that weighs it.
    /// </summary>
    internal static Task<string> RewriteAsync(string path) =>
        SharedFiles.GetAsync($"{nameof(CorpusSweep)}/rewrite/{path}", async written =>
        {
            await using VortexFile source = await VortexFile.OpenAsync(path, await OpenOptionsForAsync(path), CancellationToken.None);
            await using VortexFileWriter writer = VortexFileWriter.Create(written, source.DType);
            await foreach (RecordBatch batch in source.ScanBuilder().ExecuteAsync().WithCancellation(CancellationToken.None))
            {
                await writer.WriteAsync(batch, CancellationToken.None);
            }

            await writer.CompleteAsync(CancellationToken.None);
        });

    /// <summary>The line of each row a plain scan of the file returns, for a message.</summary>
    internal static async Task<List<string>> DescribeAsync(string path)
    {
        List<string> rows = [];
        await using VortexFile file = await VortexFile.OpenAsync(path, await OpenOptionsForAsync(path), CancellationToken.None);
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync().WithCancellation(CancellationToken.None))
        {
            Values.DescribeRows(batch, rows);
        }

        return rows;
    }

    /// <summary>The first and last rows, both sides of a FastLanes block boundary, and a prime stride between.</summary>
    internal static long[] TakeIndices(int rowCount)
    {
        SortedSet<long> wanted = [0, rowCount - 1];
        foreach (long boundary in (ReadOnlySpan<long>)[1023, 1024, 1025, 2047, 2048])
        {
            if (boundary < rowCount)
            {
                wanted.Add(boundary);
            }
        }

        for (long i = 7; i < rowCount; i += 331)
        {
            wanted.Add(i);
        }

        long[] indices = new long[wanted.Count];
        wanted.CopyTo(indices);
        return indices;
    }

    /// <summary>Open options for a corpus path: the schema out of band when the file has none.</summary>
    /// <remarks>
    /// <c>types/no_dtype_segment</c> has no dtype segment, so opening it without a DType is a
    /// <c>VortexFormatException</c>; the donor is a real corpus file with the identical schema.
    /// </remarks>
    internal static async ValueTask<VortexOpenOptions> OpenOptionsForAsync(string path) =>
        path.Contains("no_dtype_segment", StringComparison.Ordinal)
            ? new VortexOpenOptions { DType = await OutOfBandSchema.Value }
            : VortexOpenOptions.Default;

    private static readonly Lazy<Task<DType>> OutOfBandSchema = new(static async () =>
    {
        // The arena the DType points into outlives the file, so the schema stays usable once the
        // donor is closed.
        await using VortexFile donor = await VortexFile.OpenAsync(
            CorpusManifest.Get("types/user_metadata_segments").Path, VortexOpenOptions.Default, CancellationToken.None);
        return donor.DType;
    });
}
