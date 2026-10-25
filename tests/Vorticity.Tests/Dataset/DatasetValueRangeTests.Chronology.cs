// A range asked part by part answers its newest part once that part is read, while every older part's question waits,
// and a consumer that stops cancels the questions asked ahead of it: none of them reads on.
using System;
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
    /// <summary>
    /// Thirty days, an object a day: the newest day's count arrives while the question of every older day waits for it,
    /// having read only objects the newest day's question reads on its own; the parts after it cover the range a day
    /// more each.
    /// </summary>
    [Fact]
    public async Task TheNewestDayOfThirtyIsAnsweredBeforeAnOlderDayIsRead()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using KeysRead store = new KeysRead(inner);
        List<Event> appended;
        await using (VortexDataset writer = await VortexDataset.CreateAsync(store, Event.Schema, ByDay(), Ct))
        {
            appended = await DaysAsync(writer, days: 30);
        }

        DateTimeOffset to = Midnight.AddDays(30);
        DateTimeOffset newest = Midnight.AddDays(29);

        // What the newest day's question reads alone, through a handle and a session of its own.
        HashSet<string> newestKeys;
        await using (VortexSession alone = VortexSession.Create(_ => { }))
        await using (VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = alone }, Ct))
        {
            store.Keys.Clear();
            Assert.Equal(appended.Count(row => row.At >= newest), await dataset.Scan<Event>().Where(r => r.At >= newest & r.At < to).CountAsync(Ct));
            newestKeys = [.. store.Keys.Where(IsData)];
        }

        Assert.NotEmpty(newestKeys);
        await using VortexSession session = VortexSession.Create(_ => { });
        await using VortexDataset asked = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct);
        TaskCompletionSource firstIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Keys.Clear();
        await using IAsyncEnumerator<ValuePart<DateTimeOffset, long>> parts = asked.AskByValuesAsync<DateTimeOffset, long>(
            Midnight, to, async (lower, upper, ct) =>
            {
                if (lower < newest)
                {
                    await firstIn.Task.WaitAsync(ct);
                }

                return await asked.Scan<Event>().Where(r => r.At >= lower & r.At < upper).CountAsync(ct);
            },
            cancellationToken: Ct).GetAsyncEnumerator(Ct);

        Assert.True(await parts.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct));
        HashSet<string> readFirst = [.. store.Keys.Where(IsData)];
        firstIn.SetResult();
        Assert.Equal((newest, to), (parts.Current.From, parts.Current.To));
        Assert.Equal(appended.Count(row => row.At >= newest), parts.Current.Answer);
        Assert.Equal(1.0 / 30, parts.Current.Covered, 12);
        Assert.NotEmpty(readFirst);
        Assert.Subset(newestKeys, readFirst);

        List<ValuePart<DateTimeOffset, long>> rest = [];
        while (await parts.MoveNextAsync())
        {
            rest.Add(parts.Current);
        }

        Assert.Equal(29, rest.Count);
        Assert.Equal(appended.Count, rest.Sum(part => part.Answer) + appended.Count(row => row.At >= newest));
        for (int day = 0; day < rest.Count; day++)
        {
            Assert.Equal((day + 2) / 30.0, rest[day].Covered, 12);
        }

        Assert.Equal(1.0, rest[^1].Covered);
    }

    /// <summary>
    /// Ten days: once the newest part is in, a cancellation ends the walk, and every older part's question, waiting
    /// still, sees it and goes no further; a consumer that stops without cancelling stops them all the same.
    /// </summary>
    [Fact]
    public async Task AConsumerThatStopsCancelsTheQuestionsAskedAhead()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Event.Schema, ByDay(), Ct);
        await DaysAsync(dataset, days: 10);
        DateTimeOffset to = Midnight.AddDays(10);
        DateTimeOffset newest = Midnight.AddDays(9);
        foreach (bool cancelling in (bool[])[true, false])
        {
            TaskCompletionSource never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int waiting = 0;
            int stopped = 0;
            int readOn = 0;
            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            IAsyncEnumerator<ValuePart<DateTimeOffset, long>> parts = dataset.AskByValuesAsync<DateTimeOffset, long>(
                Midnight, to, async (lower, upper, ct) =>
                {
                    if (lower < newest)
                    {
                        Interlocked.Increment(ref waiting);
                        try
                        {
                            await never.Task.WaitAsync(ct);
                        }
                        catch (OperationCanceledException)
                        {
                            Interlocked.Increment(ref stopped);
                            throw;
                        }

                        Interlocked.Increment(ref readOn);
                    }

                    return await dataset.Scan<Event>().Where(r => r.At >= lower & r.At < upper).CountAsync(ct);
                },
                cancellationToken: stop.Token).GetAsyncEnumerator(stop.Token);
            try
            {
                Assert.True(await parts.MoveNextAsync());
                Assert.Equal(newest, parts.Current.From);
                if (cancelling)
                {
                    await stop.CancelAsync();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await parts.MoveNextAsync());
                }
            }
            finally
            {
                await parts.DisposeAsync();
            }

            Assert.Equal(9, waiting);
            Assert.Equal(waiting, stopped);
            Assert.Equal(0, readOn);
        }
    }

    private static bool IsData(string key) => key.StartsWith(CommitKey.DataPrefix, StringComparison.Ordinal);
}
