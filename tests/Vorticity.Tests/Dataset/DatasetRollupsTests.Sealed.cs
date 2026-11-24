// An encrypted dataset's rollups: sealed as its data objects are, each bound to the uid its entry names and its entry recording
// the bytes the store holds, summed rank over rank from sealed rollups, read by a question as a plain dataset's are, and
// verified. Before, a sealed store wrote none, and a question over a long range read the counts of every object.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetRollupsTests
{
    [Fact]
    public async Task AnEncryptedDatasetSealsItsRollupsAndAnswersFromThem()
    {
        using VortexKeyring keyring = Sealing.SealedObjects.Keyring();
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(options => options.Keyring = keyring);
        DatasetOptions options = Options() with { Session = session, Encrypted = true };
        await using VortexDataset dataset = await WriteAsync(store, options: options);
        Assert.True(dataset.IsEncrypted);

        int put = await dataset.RollUpAsync(Ct);
        List<RollupEntry> rollups = await RollupsAsync(dataset);
        Console.WriteLine($"SEALED ROLLUPS {put} put, {rollups.FindAll(r => r.Rank == 1).Count} of rank 1, {rollups.FindAll(r => r.Rank > 1).Count} above");
        Assert.Equal(put, rollups.Count);
        Assert.Contains(rollups, r => r.Rank == 1);

        // Rank 2 sums the totals of rank 1, which it reads through the sealed reader.
        Assert.Contains(rollups, r => r.Rank == 2);
        await HoldsAsync(dataset, rollups);

        // Each object is sealed, its entry recording the bytes the store holds: a service's name lies in its plaintext, which the
        // sealed reader opens bound to the entry's uid, and not in what the store holds.
        foreach (RollupEntry rollup in rollups)
        {
            Assert.True(await store.HeadAsync(rollup.Key, Ct) is { } head && head.Length == rollup.Bytes, $"'{rollup.Key}' holds other bytes than its entry says");
            byte[] bytes = new byte[rollup.Bytes];
            await store.ReadAsync(rollup.Key, 0, bytes, null, Ct);
            Assert.True(bytes.AsSpan().StartsWith(Vorticity.Sealing.SealedFormat.HeaderMagic), $"'{rollup.Key}' is not sealed");
            Assert.Equal(-1, bytes.AsSpan().IndexOf("billing"u8));

            await using Vorticity.IO.ISegmentReader opened = await SealedObjectStore.OpenDataAsync(dataset.Store, rollup.Key, rollup.Uid, rollup.Bytes, Ct);
            long plain = await opened.GetLengthAsync(Ct);
            using Vorticity.Buffers.SegmentOwner plaintext = await opened.ReadRangeAsync(0, (int)plain, 1, Ct);
            Assert.True(plaintext.Buffer.Span.IndexOf("billing"u8) >= 0, $"'{rollup.Key}' names no service in its plaintext");
        }

        // A handle opened anew answers a count by service from the rollups, as the rows answer it.
        await using VortexDataset reader = await VortexDataset.OpenAsync(store, options, Ct);
        List<string> rows = await CountByServiceAsync(reader, synopses: false);
        ScanPlan plan = await CountByService(reader, synopses: true).ExplainAsync(analyze: true, Ct);
        Assert.Contains(plan.Units, step => step.Alternative == "rollup");
        Assert.Equal(rows, await CountByServiceAsync(reader, synopses: true));

        Assert.Equal(0, await reader.RollUpAsync(Ct));
        Assert.True((await reader.VerifyAsync(cancellationToken: Ct)).Holds);
    }

    /// <summary>The rows of <see cref="CountByService"/>, described and sorted.</summary>
    private static async Task<List<string>> CountByServiceAsync(VortexDataset dataset, bool synopses)
    {
        List<string> rows = [];
        await using (IAsyncEnumerator<RecordBatch> batches = CountByService(dataset, synopses).Query.Batches(Ct))
        {
            while (await batches.MoveNextAsync())
            {
                Vorticity.Tests.Writing.Values.DescribeRows(batches.Current, rows);
            }
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    /// <summary>The hits of each service, over every hour.</summary>
    private static Vorticity.Aggregation CountByService(VortexDataset dataset, bool synopses) =>
        dataset.Scan<Hit>().With(new ScanOptions { UseSynopses = synopses }).GroupBy(h => h.Service).Select(g => (g.Key, g.Count()));
}
