// Audit DS-65: what writing a dataset's rollups costs in round trips, each object's counts read for the rank above. A
// dataset of 128 hours of hits (64 rows an hour, an append an hour, compacted), partitioned by the hour, rolled up with a
// fan-out of 4, copied afresh before each build into a store whose every read, head or tail answers after a millisecond.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;
using Vorticity.IO;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A dataset's rollups written from scratch.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditRollupBuildBenchmarks
{
    private const int Hours = 128;
    private const int RowsPerHour = 64;

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] Services = ["api", "auth", "billing", "cart"];

    private readonly Dictionary<string, byte[]> _pristine = [];
    private MemoryObjectStore _store = null!;
    private DelayedStore _delayed = null!;
    private VortexDataset _dataset = null!;

    /// <summary>A hit: its instant, service, status and bytes.</summary>
    [VortexRecord]
    public partial record struct Hit(DateTimeOffset At, string Service, short Status, long Bytes);

    /// <summary>Writes and compacts the dataset once, with no rollup, and keeps its objects.</summary>
    [GlobalSetup]
    public void Setup()
    {
        MemoryObjectStore store = new MemoryObjectStore();
        BuildAsync(store).GetAwaiter().GetResult();
        foreach (ObjectListing listed in store.ListAsync(string.Empty, null, CancellationToken.None).ToBlockingEnumerable())
        {
            byte[] bytes = new byte[listed.Head.Length];
            store.ReadAsync(listed.Key, 0, bytes, null, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            _pristine[listed.Key] = bytes;
        }

        store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Fresh();
        Console.WriteLine($"// {_dataset.ObjectCount} objects; {RollUp()} rollups put in {_delayed.Requests} requests");
    }

    /// <summary>Copies the dataset afresh, its objects as written.</summary>
    [IterationSetup]
    public void Fresh()
    {
        _dataset?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store = new MemoryObjectStore();
        foreach ((string key, byte[] bytes) in _pristine)
        {
            _store.PutAsync(key, PipeReader.Create(new ReadOnlySequence<byte>(bytes)), bytes.Length, PutCondition.None, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
        }

        _delayed = new DelayedStore(_store);
        _dataset = VortexDataset.OpenAsync(_delayed, Options()).AsTask().GetAwaiter().GetResult();
        _delayed.Requests = 0;
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Every rollup the dataset lacks, written; the rollups put.</summary>
    [Benchmark]
    [InvocationCount(1)]
    public int RollUp() => _dataset.RollUpAsync().AsTask().GetAwaiter().GetResult();

    private static DatasetOptions Options() => new DatasetOptions
    {
        Seed = 0x5EED_0D_5065,
        SheetObjectBytes = 0,
        ContentsObjectBytes = 0,
        RollupFanout = 4,
        Layout = new DatasetLayout
        {
            Partition = LayoutValue.Column(nameof(Hit.At)).Truncate(CalendarUnit.Hour),
            SortKey = [LayoutValue.Column(nameof(Hit.At))],
            Organization = LayoutOrganization.Leveled,
        },
    };

    private static async Task BuildAsync(MemoryObjectStore store)
    {
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Hit.Schema, Options() with { RollupFanout = 0 }).ConfigureAwait(false);
        Random random = new Random(97);
        Hit[] hits = new Hit[RowsPerHour];
        for (int hour = 0; hour < Hours; hour++)
        {
            for (int i = 0; i < RowsPerHour; i++)
            {
                hits[i] = new Hit(Start.AddHours(hour).AddSeconds(i * 50), Services[random.Next(Services.Length)], (short)(200 + random.Next(3)), random.NextInt64(100, 100_000));
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Hit>(hits).ConfigureAwait(false);
            await dataset.AppendAsync(draft).ConfigureAwait(false);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }).ConfigureAwait(false) is not null)
        {
        }
    }

    /// <summary>A store whose reads, heads and tails answer a millisecond after they are asked, counted.</summary>
    private sealed class DelayedStore(IObjectStore inner) : IObjectStore
    {
        private long _requests;

        /// <summary>The reads, heads and tails asked since the count was set.</summary>
        internal long Requests
        {
            get => Interlocked.Read(ref _requests);
            set => Interlocked.Exchange(ref _requests, value);
        }

        public async ValueTask<ObjectRead> ReadAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
        {
            await Wait().ConfigureAwait(false);
            return await inner.ReadAsync(key, offset, destination, ifToken, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken)
        {
            await Wait().ConfigureAwait(false);
            return await inner.ReadTailAsync(key, destination, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken)
        {
            await Wait().ConfigureAwait(false);
            return await inner.HeadAsync(key, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<PutOutcome> PutAsync(string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken) =>
            inner.PutAsync(key, content, length, condition, cancellationToken);

        public ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken) => inner.DeleteAsync(keys, cancellationToken);

        public IAsyncEnumerable<ObjectListing> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, startAfter, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ObjectStoreCapabilities Capabilities => inner.Capabilities;

        // A millisecond to the tenth, one clock completing every wait due, as a round trip that holds no thread: a wait
        // that spun on a thread of the pool starved the pool under reads side by side.
        private Task Wait()
        {
            Interlocked.Increment(ref _requests);
            return Vorticity.Benchmarks.Queries.PreciseDelay.Delay(TimeSpan.FromMilliseconds(1));
        }
    }
}
