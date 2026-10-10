using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Api;

/// <summary>
/// The allocation promises a scan makes: nothing per batch on any sink but rows, and so nothing on
/// the large object heap once it is under way.
/// </summary>
/// <remarks>
/// In <see cref="AllocationCollection"/>, which runs alone: two of these measurements count every
/// thread's allocations, and the scans of a neighbouring class would be counted with them.
/// </remarks>
[Trait("Category", "ApiContract")]
[Collection(nameof(AllocationCollection))]
public sealed class AllocationContractTests
{
    /// <summary>Full scans before any measurement: the JIT, its tiers and every lazy static settle.</summary>
    private const int WarmUp = 4;

    /// <summary>Measured scans whose floor is taken where other threads can add to the count.</summary>
    private const int Runs = 5;

    private static readonly ScanOptions Sequential = new ScanOptions { Prefetch = 0 };

    [Fact]
    public async Task ATypedScanAllocatesNothingPerBatchAfterTheFirst()
    {
        ReleaseOnlyCeilings.Require();
        await using VortexFile file = await VortexFile.OpenAsync(await ContractFile.PathAsync(), TestContext.Current.CancellationToken);

        for (int i = 0; i < WarmUp; i++)
        {
            await TypedAsync(file, Sequential);
        }

        (long batches, long allocated) = await TypedAsync(file, Sequential);
        Assert.True(batches > 8, $"the file gave {batches} batches, too few to say anything per batch");
        Assert.True(
            allocated == 0,
            string.Create(CultureInfo.InvariantCulture, $"a typed scan allocated {allocated} B over its {batches - 1} batches after the first"));
    }

    /// <summary>
    /// A sealed file's batches cost what a plain file's do: the frames decrypted into pooled blocks,
    /// cipher instances kept per lane, the ciphertext read through a request set the reader keeps.
    /// The sealed bytes are held in memory and published as shared views, so that what is counted is
    /// the sealing and not the source under it.
    /// </summary>
    [Fact]
    public async Task AScanOfASealedFileAllocatesNothingPerBatchAfterTheFirst()
    {
        ReleaseOnlyCeilings.Require();
        System.Threading.CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = Sealing.SealedObjects.Keyring();
        byte[] sealedBytes;
        using (DataKey key = await keyring.GenerateAsync(ReadOnlyMemory<byte>.Empty, ct))
        {
            byte[] plain = await System.IO.File.ReadAllBytesAsync(await ContractFile.PathAsync(), ct);
            sealedBytes = await Sealing.SealedObjects.SealAsync(plain, key, Vorticity.Sealing.SealParameters.ForFile(), 1 << 20, ct);
        }

        Vorticity.Sealing.SealedSegmentReader reader = await Vorticity.Sealing.SealedSegmentReader.OpenAsync(
            new SharedViews(sealedBytes),
            ownsInner: true,
            (descriptor, token) => keyring.UnwrapAsync(descriptor.KeyId, descriptor.WrappedKey, descriptor.KeyContext, token),
            ct);
        await using VortexFile file = await VortexFile.OpenAsync(reader, VortexOpenOptions.Default, ct);
        for (int i = 0; i < WarmUp; i++)
        {
            await TypedAsync(file, Sequential);
        }

        (long batches, long allocated) = await TypedAsync(file, Sequential);
        Assert.True(batches > 8, $"the file gave {batches} batches, too few to say anything per batch");
        Assert.True(
            allocated == 0,
            string.Create(CultureInfo.InvariantCulture, $"a scan of a sealed file allocated {allocated} B over its {batches - 1} batches after the first"));
    }

    /// <summary>
    /// A filter adds the evaluation, the comparison an encoding answers in place, and either the
    /// compaction or the selection a batch delivered whole carries.
    /// </summary>
    [Theory]
    [InlineData("Celsius > 30", true)]
    [InlineData("Celsius > 30", false)]
    [InlineData("City = Lyon", true)]
    [InlineData("City = Lyon", false)]
    public async Task AFilteredScanAllocatesNothingPerBatchAfterTheFirst(string filter, bool compact)
    {
        ReleaseOnlyCeilings.Require();
        await using VortexFile file = await VortexFile.OpenAsync(await ContractFile.PathAsync(), TestContext.Current.CancellationToken);
        Func<Scan<Reading>> scan = () =>
        {
            Scan<Reading> all = file.Scan<Reading>().With(Sequential with { Compact = compact });
            return filter == "City = Lyon" ? all.Where(r => r.City == "Lyon") : all.Where(r => r.Celsius > 30.0);
        };

        for (int i = 0; i < WarmUp; i++)
        {
            await TypedAsync(scan());
        }

        (long batches, long allocated) = await TypedAsync(scan());
        Assert.True(batches > 8, $"the filter left {batches} batches, too few to say anything per batch");
        Assert.True(
            allocated == 0,
            string.Create(CultureInfo.InvariantCulture, $"a scan filtered on {filter} allocated {allocated} B over its {batches - 1} batches after the first"));
    }

    [Fact]
    public async Task AToolScanAllocatesNothingPerBatchAfterTheFirst()
    {
        ReleaseOnlyCeilings.Require();
        await using VortexFile file = await VortexFile.OpenAsync(await ContractFile.PathAsync(), TestContext.Current.CancellationToken);

        for (int i = 0; i < WarmUp; i++)
        {
            await ToolAsync(file);
        }

        (long batches, long allocated) = await ToolAsync(file);
        Assert.True(batches > 8, $"the file gave {batches} batches, too few to say anything per batch");
        Assert.True(
            allocated == 0,
            string.Create(CultureInfo.InvariantCulture, $"a tool scan allocated {allocated} B over its {batches - 1} batches after the first"));
    }

    /// <summary>
    /// The default options decode ahead of the consumer on other threads, so the count is every
    /// thread's; its floor over a few scans is what the scan costs, the rest being the runtime's.
    /// </summary>
    [Fact]
    public async Task AScanThatDecodesAheadAllocatesNothingPerBatchAfterTheFirst()
    {
        ReleaseOnlyCeilings.Require();
        await using VortexFile file = await VortexFile.OpenAsync(await ContractFile.PathAsync(), TestContext.Current.CancellationToken);

        foreach (int prefetch in new[] { 1, 2 })
        {
            ScanOptions options = new ScanOptions { Prefetch = prefetch };
            for (int i = 0; i < WarmUp; i++)
            {
                await TypedAsync(file, options, everyThread: true);
            }

            long floor = long.MaxValue;
            for (int i = 0; i < Runs && floor > 0; i++)
            {
                floor = Math.Min(floor, (await TypedAsync(file, options, everyThread: true)).Allocated);
            }

            Assert.True(
                floor == 0,
                string.Create(CultureInfo.InvariantCulture, $"a scan decoding {prefetch} batches ahead allocated at least {floor} B after its first batch"));
        }
    }

    /// <summary>
    /// An aggregate and a count hand no batch to the caller, so the loop is reached through the
    /// source: what the scan's thread had allocated when it asked for the second split's segments,
    /// and when it asked for the last.
    /// </summary>
    [Fact]
    public async Task AggregatesAndCountsAllocateNothingBetweenTheirReads()
    {
        ReleaseOnlyCeilings.Require();
        SamplingSource source = new SamplingSource(await ContractFile.PathAsync());
        await using VortexFile file = await VortexSession.Default.OpenAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        Func<Scan<Reading>> all = () => file.Scan<Reading>().With(Sequential);
        Func<Scan<Reading>> filtered = () => file.Scan<Reading>().With(Sequential).Where(r => r.Celsius > 30.0);
        (string Sink, Func<Scan<Reading>> Scan, Func<Scan<Reading>, Task> Run)[] sinks =
        [
            ("SumAsync", all, async scan => await scan.SumAsync(r => r.Day)),
            ("AverageAsync", all, async scan => await scan.AverageAsync(r => r.Celsius)),
            ("AggregateAsync", all, async scan => await scan.AggregateAsync<SumAndCount>(a => (a.Sum(r => r.Celsius), a.Count()))),
            ("GroupBy", all, async scan =>
            {
                await foreach (CityCount group in scan.GroupBy(r => r.City).Select(g => (g.Key, g.Count())).As<CityCount>().ToRecordsAsync())
                {
                    GC.KeepAlive(group.City);
                }
            }),
            ("GroupBy, filtered group", all, async scan =>
            {
                await foreach (CityFiltered group in scan.GroupBy(r => r.City).Select(g => (g.Key, g.Count(x => x.Celsius > 30.0), g.Any(x => x.Day > 900))).As<CityFiltered>().ToRecordsAsync())
                {
                    GC.KeepAlive(group.City);
                }
            }),
            ("AggregateAsync, filtered", all, async scan => await scan.AggregateAsync<SumAndCount>(a => (a.Where(r => r.Celsius > 30.0).Sum(r => r.Celsius), a.Count()))),
            ("CountAsync", filtered, async scan => await scan.CountAsync()),
            ("SumAsync, filtered", filtered, async scan => await scan.SumAsync(r => r.Day)),
        ];

        List<string> failures = await BetweenReadsAsync(source, sinks);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// A group by that streams folds its batches on the thread that reads them, closes the groups a
    /// batch finishes and keeps the open ones: on a sorted text key and on a composite one, with and
    /// without a filter on its groups, its result read as batches, nothing between its reads.
    /// </summary>
    [Fact]
    public async Task AStreamingGroupByAllocatesNothingBetweenItsReads()
    {
        ReleaseOnlyCeilings.Require();
        SamplingSource source = new SamplingSource(await SortedNames.PathAsync());
        await using VortexFile file = await VortexSession.Default.OpenAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        Func<Scan<SortedName>> all = () => file.Scan<SortedName>().With(Sequential);
        (string Sink, Func<Scan<SortedName>> Scan, Func<Scan<SortedName>, Task> Run)[] sinks =
        [
            ("a text key", all, scan => ReadAsync(scan.GroupBy(n => n.Name).Select(g => (g.Key, g.Count(), g.Average(n => n.Value))).As<NameMean>())),
            ("a text key, a filter on its groups", all, scan => ReadAsync(scan.GroupBy(n => n.Name).Where(g => g.Count() > 100).Select(g => (g.Key, g.Count(), g.Average(n => n.Value))).As<NameMean>())),
            ("a composite key", all, scan => ReadAsync(scan.GroupBy(n => (n.Day, n.Name)).Select(g => (g.Key.Day, g.Key.Name, g.Count())).As<DayNameCount>())),
            ("a composite key, a filter on its groups", all, scan => ReadAsync(scan.GroupBy(n => (n.Day, n.Name)).Where(g => g.Count() > 100).Select(g => (g.Key.Day, g.Key.Name, g.Count())).As<DayNameCount>())),
        ];

        List<string> failures = await BetweenReadsAsync(source, sinks);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>Reads a result as the batches it is, borrowed, touching nothing a group would allocate: its groups.</summary>
    private static async Task<long> ReadAsync<T>(Scan<T> groups)
        where T : IVortexRecord<T>
    {
        long count = 0;
        await foreach (Columns<T> batch in groups)
        {
            count += batch.RowCount;
        }

        return count;
    }

    /// <summary>
    /// What each sink's thread allocated from its read of the second split's segments to its last
    /// read, once warm: the failures, a line each.
    /// </summary>
    private static async Task<List<string>> BetweenReadsAsync<T>(
        SamplingSource source, (string Sink, Func<Scan<T>> Scan, Func<Scan<T>, Task> Run)[] sinks)
        where T : IVortexRecord<T>
    {
        List<string> failures = [];
        foreach ((string sink, Func<Scan<T>> scan, Func<Scan<T>, Task> run) in sinks)
        {
            for (int i = 0; i < WarmUp; i++)
            {
                await run(scan());
            }

            // What the plan reads is what the sink reads before its first split, the structures
            // its filter consults; the split after the first one begins at the next read.
            source.Clear();
            await scan().ExplainAsync(TestContext.Current.CancellationToken);
            int second = source.Count + 1;

            source.Clear();
            await run(scan());
            int reads = source.Count;
            if (reads < second + 3)
            {
                failures.Add($"{sink} read {reads} times, too few to say anything per batch");
                continue;
            }

            int thread = source.ThreadAt(second);
            bool oneThread = true;
            for (int i = second; i < reads; i++)
            {
                oneThread &= source.ThreadAt(i) == thread;
            }

            if (!oneThread)
            {
                failures.Add($"{sink} read on more than one thread, so the per-thread counts do not add up");
                continue;
            }

            long allocated = source.AllocatedAt(reads - 1) - source.AllocatedAt(second);
            if (allocated != 0)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture, $"{sink} allocated {allocated} B from its read {second} to its last of {reads}"));
            }
        }

        return failures;
    }

    [Fact]
    public async Task NothingReachesTheLargeObjectHeapOnceAScanIsUnderWay()
    {
        ReleaseOnlyCeilings.Require();
        await using VortexFile file = await VortexFile.OpenAsync(await ContractFile.PathAsync(), TestContext.Current.CancellationToken);

        for (int i = 0; i < WarmUp; i++)
        {
            await TypedAsync(file, ScanOptions.Default, everyThread: true);
        }

        int collections = GC.CollectionCount(2);
        long allocated = 0;
        for (int i = 0; i < Runs; i++)
        {
            allocated += (await TypedAsync(file, ScanOptions.Default, everyThread: true)).Allocated;
        }

        Assert.Equal(collections, GC.CollectionCount(2));
        Assert.True(
            allocated < 85_000,
            string.Create(CultureInfo.InvariantCulture, $"{Runs} scans allocated {allocated} B after their first batches, room for an object on the large object heap"));
    }

    /// <summary>A typed scan of the whole file under <paramref name="options"/>.</summary>
    private static Task<(long Batches, long Allocated)> TypedAsync(VortexFile file, ScanOptions options, bool everyThread = false) =>
        TypedAsync(file.Scan<Reading>().With(options), everyThread);

    /// <summary>A typed scan, reading every column of every batch.</summary>
    /// <returns>The batches, and the bytes allocated after the first one was delivered.</returns>
    private static async Task<(long Batches, long Allocated)> TypedAsync(Scan<Reading> scan, bool everyThread = false)
    {
        Scan<Reading>.AsyncEnumerator batches = scan.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        try
        {
            if (!await batches.MoveNextAsync())
            {
                return (0, 0);
            }

            int thread = Environment.CurrentManagedThreadId;
            long start = Allocated(everyThread);
            long count = 1;
            double sink = 0;
            do
            {
                Columns<Reading> columns = batches.Current;
                sink += Touch(columns);
                count++;
            }
            while (await batches.MoveNextAsync());

            long allocated = Allocated(everyThread) - start;
            Assert.True(everyThread || thread == Environment.CurrentManagedThreadId, "the scan left the measuring thread, so its count is not the scan's");
            GC.KeepAlive(sink);
            return (count - 1, allocated);
        }
        finally
        {
            await batches.DisposeAsync();
        }
    }

    /// <summary>A tool scan with no read-ahead, reading every column of every batch.</summary>
    private static async Task<(long Batches, long Allocated)> ToolAsync(VortexFile file)
    {
        Vorticity.Scan scan = file.Scan().With(Sequential);
        Vorticity.Scan.AsyncEnumerator batches = scan.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        try
        {
            if (!await batches.MoveNextAsync())
            {
                return (0, 0);
            }

            int thread = Environment.CurrentManagedThreadId;
            long start = GC.GetAllocatedBytesForCurrentThread();
            long count = 1;
            double sink = 0;
            do
            {
                BatchView view = batches.Current;
                sink += Touch(view);
                count++;
            }
            while (await batches.MoveNextAsync());

            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.True(thread == Environment.CurrentManagedThreadId, "the scan left the measuring thread, so its count is not the scan's");
            GC.KeepAlive(sink);
            return (count - 1, allocated);
        }
        finally
        {
            await batches.DisposeAsync();
        }
    }

    private static long Allocated(bool everyThread) =>
        everyThread ? GC.GetTotalAllocatedBytes(precise: true) : GC.GetAllocatedBytesForCurrentThread();

    private static double Touch(Columns<Reading> columns)
    {
        ReadOnlySpan<int> days = columns.Column<int>(0).Values;
        ReadOnlySpan<double> celsius = columns.Column<double?>(1).Values;
        Column<string> city = columns.Column<string>(2);
        double sum = 0;
        for (int i = 0; i < days.Length; i++)
        {
            sum += days[i] + celsius[i] + city.GetLength(i);
        }

        return sum;
    }

    private static double Touch(BatchView view)
    {
        ReadOnlySpan<int> days = view.Column<int>(0).Values;
        ReadOnlySpan<double> celsius = view.Column<double?>(1).Values;
        Column<string> city = view.Column<string>(2);
        double sum = 0;
        for (int i = 0; i < days.Length; i++)
        {
            sum += days[i] + celsius[i] + city.GetLength(i);
        }

        return sum;
    }

    /// <summary>Bytes in memory whose batch reads hand out views of one pinned block, allocating nothing per read.</summary>
    private sealed class SharedViews(byte[] bytes) : Vorticity.IO.ISegmentReader
    {
        private readonly Vorticity.Buffers.PinnedArraySegmentOwner _block = Vorticity.Buffers.PinnedArraySegmentOwner.CopyOf(bytes, 64);

        public System.Threading.Tasks.ValueTask<long> GetLengthAsync(System.Threading.CancellationToken cancellationToken) =>
            new System.Threading.Tasks.ValueTask<long>(bytes.Length);

        public System.Threading.Tasks.ValueTask<Vorticity.Buffers.SegmentOwner> ReadAsync(Vorticity.Serialization.Schemas.SegmentSpec spec, System.Threading.CancellationToken cancellationToken) =>
            ReadRangeAsync((long)spec.Offset, (int)spec.Length, 1, cancellationToken);

        public System.Threading.Tasks.ValueTask ReadManyAsync(Vorticity.IO.SegmentRequestSet requests, System.Threading.CancellationToken cancellationToken)
        {
            for (int slot = 0; slot < requests.Count; slot++)
            {
                if (!requests.IsFilled(slot))
                {
                    Vorticity.Serialization.Schemas.SegmentSpec spec = requests.GetSpec(slot);
                    requests.SetSharedResult(slot, _block, Vorticity.Buffers.VortexBuffer.FromPinned(_block.Buffer.Span.Slice((int)spec.Offset, (int)spec.Length), 0));
                }
            }

            requests.Complete();
            return System.Threading.Tasks.ValueTask.CompletedTask;
        }

        public System.Threading.Tasks.ValueTask<Vorticity.Buffers.SegmentOwner> ReadRangeAsync(long offset, int length, int alignment, System.Threading.CancellationToken cancellationToken)
        {
            int available = (int)Math.Min(length, bytes.Length - offset);
            return new System.Threading.Tasks.ValueTask<Vorticity.Buffers.SegmentOwner>(
                new Vorticity.IO.SliceSegmentOwner(_block, Vorticity.Buffers.VortexBuffer.FromPinned(_block.Buffer.Span.Slice((int)offset, available), 0)));
        }

        public System.Threading.Tasks.ValueTask DisposeAsync()
        {
            _block.Release();
            return System.Threading.Tasks.ValueTask.CompletedTask;
        }
    }
}
