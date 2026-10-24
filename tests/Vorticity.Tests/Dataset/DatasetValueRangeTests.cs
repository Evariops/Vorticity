// A range of a dataset's clustering values: dropped by an expiry that reads no object, and asked part by part, a part a
// value. The values are the application's, a day of an instant here, a bucket of ids there; the dataset only knows the
// function that gives them.
//
// THE ORACLE IS THE ROWS APPENDED: an expiry drops exactly the rows of the objects its filter covers whole, every one of
// them a whole value's under the cut, and the parts' answers add up to the question asked over the whole range.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetValueRangeTests
{
    private static readonly DateTimeOffset Midnight = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnExpiryDropsTheDaysAFilterCoversWholeAndReadsNoObject()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using KeysRead store = new KeysRead(inner);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Event.Schema, ByDay(), Ct);
        List<Event> appended = await DaysAsync(dataset, days: 5);

        store.Keys.Clear();
        RowChangeResult result = await dataset.ExpireAsync<Event>(r => r.At < Midnight.AddDays(2), Ct);
        Assert.Equal(OperationOutcome.Applied, result.Outcome);
        Assert.Equal(appended.Count(row => row.At < Midnight.AddDays(2)), result.Rows);
        Assert.DoesNotContain(store.Keys, key => key.StartsWith("data/", StringComparison.Ordinal));
        Assert.Equal(appended.Count(row => row.At >= Midnight.AddDays(2)), dataset.RowCount);

        // A bound inside a day drops the days before it alone: the objects of its own day hold rows past it.
        result = await dataset.ExpireAsync(VortexExpr.Parse($"At < {Micros(Midnight.AddDays(3).AddHours(12))}"), Ct);
        Assert.Equal(appended.Count(row => row.At >= Midnight.AddDays(2) && row.At < Midnight.AddDays(3)), result.Rows);
        Assert.Equal(appended.Count(row => row.At >= Midnight.AddDays(3)), dataset.RowCount);

        // Nothing left to cover: the version stays.
        ulong version = dataset.Version;
        result = await dataset.ExpireAsync<Event>(r => r.At < Midnight.AddDays(1), Ct);
        Assert.Equal(OperationOutcome.AlreadyThere, result.Outcome);
        Assert.Equal(version, dataset.Version);
    }

    [Fact]
    public async Task AnExpiryKeepsAnObjectOfLevelZeroThatStraddlesItsBound()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Event.Schema, ByDay(), Ct);
        await AppendAsync(dataset, [.. Enumerable.Range(0, 48).Select(i => new Event(Midnight.AddHours(i), i))]);

        RowChangeResult result = await dataset.ExpireAsync<Event>(r => r.At < Midnight.AddDays(1), Ct);
        Assert.Equal(OperationOutcome.AlreadyThere, result.Outcome);
        Assert.Equal(48, dataset.RowCount);

        // Compacted, the object of each day stands alone, and the first goes.
        await DrainAsync(dataset);
        result = await dataset.ExpireAsync<Event>(r => r.At < Midnight.AddDays(1), Ct);
        Assert.Equal(24, result.Rows);
        Assert.Equal(24, dataset.RowCount);
    }

    [Fact]
    public async Task ARangeIsAskedADayAtATimeTheLastFirst()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Event.Schema, ByDay(), Ct);
        List<Event> appended = await DaysAsync(dataset, days: 5);
        DateTimeOffset from = Midnight.AddHours(6);
        DateTimeOffset to = Midnight.AddDays(3).AddHours(2);

        List<ValuePart<DateTimeOffset, long>> parts = await dataset.AskByValuesAsync<DateTimeOffset, long>(
            from, to, (lower, upper, ct) => dataset.Scan<Event>().Where(r => r.At >= lower & r.At < upper).CountAsync(ct), cancellationToken: Ct).ToListAsync(Ct);

        Assert.Equal(
            [(Midnight.AddDays(3), to), (Midnight.AddDays(2), Midnight.AddDays(3)), (Midnight.AddDays(1), Midnight.AddDays(2)), (from, Midnight.AddDays(1))],
            parts.Select(part => (part.From, part.To)));
        Assert.All(parts, part => Assert.Equal(appended.Count(row => row.At >= part.From && row.At < part.To), part.Answer));
        Assert.Equal(appended.Count(row => row.At >= from && row.At < to), parts.Sum(part => part.Answer));

        // The first part first.
        List<ValuePart<DateTimeOffset, long>> ascending = await dataset.AskByValuesAsync<DateTimeOffset, long>(
            from, to, (lower, upper, ct) => ValueTask.FromResult(0L), descending: false, Ct).ToListAsync(Ct);
        Assert.Equal(parts.Select(part => (part.From, part.To)).Reverse(), ascending.Select(part => (part.From, part.To)));
    }

    [Fact]
    public async Task ARangeOfIdsIsAskedABucketAtATime()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Numbered.Schema, new DatasetOptions { ClusteringKey = [nameof(Numbered.Id)], Clustering = new ClusteringOptions { Value = ClusteringValue.Bucket(1_000) } }, Ct);
        List<ValuePart<long, long>> parts = await dataset.AskByValuesAsync<long, long>(
            -1_500, 2_500, (lower, upper, ct) => ValueTask.FromResult(upper - lower), cancellationToken: Ct).ToListAsync(Ct);
        Assert.Equal([(2_000L, 2_500L), (1_000L, 2_000L), (0L, 1_000L), (-1_000L, 0L), (-1_500L, -1_000L)], parts.Select(part => (part.From, part.To)));

        // The column itself cuts at every value.
        await using MemoryObjectStore other = new MemoryObjectStore();
        await using VortexDataset each = await VortexDataset.CreateAsync(
            other, Numbered.Schema, new DatasetOptions { ClusteringKey = [nameof(Numbered.Id)], Clustering = new ClusteringOptions { Value = ClusteringValue.Column } }, Ct);
        List<ValuePart<long, long>> values = await each.AskByValuesAsync<long, long>(
            7, 10, (lower, upper, ct) => ValueTask.FromResult(lower), descending: false, Ct).ToListAsync(Ct);
        Assert.Equal([7L, 8L, 9L], values.Select(part => part.Answer));
    }

    [Fact]
    public async Task ARangeIsAskedByValuesOnlyOfAValueWhoseNextOneIsKnown()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset plain = await VortexDataset.CreateAsync(store, Event.Schema, new DatasetOptions { ClusteringKey = [nameof(Event.At)] }, Ct);
        Assert.Throws<InvalidOperationException>(() => plain.AskByValuesAsync<DateTimeOffset, long>(Midnight, Midnight.AddDays(1), (_, _, _) => ValueTask.FromResult(0L), cancellationToken: Ct));

        await using MemoryObjectStore texts = new MemoryObjectStore();
        await using VortexDataset tenants = await VortexDataset.CreateAsync(
            texts, Tenanted.Schema, new DatasetOptions { ClusteringKey = [nameof(Tenanted.Tenant)], Clustering = new ClusteringOptions { Value = ClusteringValue.Column } }, Ct);
        Assert.Throws<InvalidOperationException>(() => tenants.AskByValuesAsync<string, long>("a", "b", (_, _, _) => ValueTask.FromResult(0L), cancellationToken: Ct));

        await using MemoryObjectStore days = new MemoryObjectStore();
        await using VortexDataset byDay = await VortexDataset.CreateAsync(days, Event.Schema, ByDay(), Ct);
        Assert.Throws<ArgumentException>(() => byDay.AskByValuesAsync<DateTimeOffset, long>(Midnight, Midnight, (_, _, _) => ValueTask.FromResult(0L), cancellationToken: Ct));
    }

    /// <summary>
    /// Days of rows, 300 a day at random seconds, appended five parts at a time across every day, then compacted until
    /// nothing is due: every row out of level 0, every object of one day.
    /// </summary>
    private static async Task<List<Event>> DaysAsync(VortexDataset dataset, int days)
    {
        Random random = new Random(0xDA75);
        List<Event> appended = [];
        for (int part = 0; part < 5; part++)
        {
            Event[] rows = [.. Enumerable.Range(0, 60 * days).Select(i => new Event(Midnight.AddSeconds(random.Next(days * 86_400)), i))];
            appended.AddRange(rows);
            await AppendAsync(dataset, rows);
        }

        await DrainAsync(dataset);
        return appended;
    }

    private static async Task DrainAsync(VortexDataset dataset)
    {
        CompactionOptions options = new CompactionOptions { LevelZeroCeiling = 0, TargetBytesAtLevelOne = 4 << 10, Fanout = 3, MaxObjectBytes = 1L << 30 };
        for (int step = 0; await dataset.CompactAsync(options, Ct) is not null; step++)
        {
            Assert.True(step < 1_000, "compaction never runs out of jobs");
        }
    }

    private static async Task AppendAsync<T>(VortexDataset dataset, T[] rows)
        where T : IVortexRecord<T>
    {
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<T>(rows, Ct);
        await dataset.AppendAsync(draft, Ct);
    }

    private static long Micros(DateTimeOffset at) => (at - DateTimeOffset.UnixEpoch).Ticks / 10;

    private static DatasetOptions ByDay() => new DatasetOptions
    {
        Seed = 0xDA_7E,
        ClusteringKey = [nameof(Event.At)],
        Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Day) },
    };

    /// <summary>A store that notes every key read.</summary>
    private sealed class KeysRead(IObjectStore inner) : ForwardingStore(inner)
    {
        internal ConcurrentBag<string> Keys { get; } = [];

        public override ValueTask<ObjectRead> ReadAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
        {
            Keys.Add(key);
            return base.ReadAsync(key, offset, destination, ifToken, cancellationToken);
        }

        public override ValueTask<ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken)
        {
            Keys.Add(key);
            return base.ReadTailAsync(key, destination, cancellationToken);
        }
    }

    [VortexRecord]
    public partial record struct Event(DateTimeOffset At, long Size);

    [VortexRecord]
    public partial record struct Numbered(long Id, long Size);

    [VortexRecord]
    public partial record struct Tenanted(string Tenant, long Size);
}
