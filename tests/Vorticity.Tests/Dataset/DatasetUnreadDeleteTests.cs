// A delete whose filter the summaries prove true for every row of an object removes it without reading it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetUnreadDeleteTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DatasetOptions Options() => new DatasetOptions
    {
        ClusteringKey = [nameof(Reading.At)],
        Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour) },
    };

    /// <summary>
    /// A delete of every row before an hour's start removes the objects that lie wholly before it
    /// unread, each an hour's.
    /// </summary>
    [Fact]
    public async Task ADeleteTrueForEveryRowOfAnObjectDoesNotReadIt()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using (VortexDataset writer = await VortexDataset.CreateAsync(memory, Reading.Schema, Options(), Ct))
        {
            await FillAsync(writer, hours: 3);
        }

        DataReads store = new DataReads(memory);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, Options(), Ct);
        long cutoff = Noon.AddHours(2).ToUnixTimeMilliseconds() * 1000;
        RowChangeResult deleted = await dataset.DeleteAsync(VortexExpr.Parse($"At < {cutoff}"), Ct);
        Assert.Equal(120, deleted.Rows);
        Assert.Equal(0, store.Reads);
        Assert.Equal(60, await CountAsync(dataset));
    }

    /// <summary>Appends <paramref name="hours"/> hours of a row a minute in quarters, and compacts until nothing is due.</summary>
    private static async Task FillAsync(VortexDataset dataset, int hours)
    {
        for (int quarter = 0; quarter < hours * 4; quarter++)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Reading>([.. Enumerable.Range(0, 15).Select(i => new Reading(Noon.AddMinutes((quarter * 15) + i), i))], Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 1 };
        while (await dataset.CompactAsync(compaction, Ct) is not null)
        {
        }
    }

    private static async Task<long> CountAsync(VortexDataset dataset) =>
        (await dataset.Scan<Reading>().Select(r => r.Value).ToListAsync(Ct)).Count;

    /// <summary>A store that counts the reads of data objects, their bytes or their heads.</summary>
    private sealed class DataReads(IObjectStore inner) : ForwardingStore(inner)
    {
        internal int Reads { get; set; }

        public override ValueTask<ObjectRead> ReadAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
        {
            Count(key);
            return base.ReadAsync(key, offset, destination, ifToken, cancellationToken);
        }

        public override ValueTask<ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken)
        {
            Count(key);
            return base.ReadTailAsync(key, destination, cancellationToken);
        }

        public override ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken)
        {
            Count(key);
            return base.HeadAsync(key, cancellationToken);
        }

        private void Count(string key)
        {
            if (key.StartsWith(CommitKey.DataPrefix, StringComparison.Ordinal))
            {
                Reads++;
            }
        }
    }

    [VortexRecord]
    public partial record struct Reading(DateTimeOffset At, long Value);
}
