// Audit DS-61: what a dataset without a key pays its tiered compactions over its life, the objects already as large as the
// level above aims at moved whole or concatenated again.
//
// 2,097,152 calls (an identifier, an instant, a service of 20, bytes, a latency) appended in objects of 4,096 or 32,768
// rows to a dataset without a layout, in an in-memory store made afresh each time, compacted until nothing is due after
// every append. The defaults' shape scaled down 64 times: level 1's objects aim at 64 KiB, a fan-out of 4, objects capped
// at 1 MiB from level 3 up, a column's chunks at 16 KiB, so that the ~22 MB the calls take climb two levels past the cap.
// Scale 4 multiplies the rows, the appends, the targets and the chunks by four, so that what a compaction's batch weighs
// against an object, which sets how far an object it rolls passes its target, comes four times nearer the defaults'.
using System;
using System.Linq;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Audit;

/// <summary>The life of a dataset without a key: its appends and every compaction they make due.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditTieredMovesBenchmarks
{
    private const int TotalRows = 2_097_152;

    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Services = [.. Enumerable.Range(0, 20).Select(service => $"svc-{service:D2}")];

    private Call[][] _appends = null!;
    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;

    /// <summary>Whether the objects a concatenation would only cut or write again move whole.</summary>
    [Params(false, true)]
    public bool Moves { get; set; }

    /// <summary>The rows of an append at scale 1: objects under level 1's target, or past level 2's.</summary>
    [Params(4_096, 32_768)]
    public int RowsPerAppend { get; set; }

    /// <summary>What the rows, the appends, the targets and the chunks are multiplied by.</summary>
    [Params(1, 4)]
    public int Scale { get; set; }

    /// <summary>A call: its identifier, instant, service, bytes and latency.</summary>
    [VortexRecord]
    public partial record struct Call(long Id, DateTime At, string Service, long Bytes, double Latency);

    /// <summary>Makes the appends' rows, and prints what one life rewrites and leaves.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(41);
        long id = 0;
        _appends = new Call[TotalRows / RowsPerAppend][];
        for (int a = 0; a < _appends.Length; a++)
        {
            _appends[a] = [.. Enumerable.Range(0, RowsPerAppend * Scale).Select(_ => new Call(
                id, Start.AddMilliseconds(id++ * 3), Services[random.Next(20)], random.NextInt64(100, 100_000), random.NextDouble() * 250))];
        }

        Fresh();
        Life life = LiveAsync().GetAwaiter().GetResult();
        CompactionPlan plan = _dataset.PlanCompactionAsync(Options()).AsTask().GetAwaiter().GetResult();
        string levels = string.Join(' ', plan.ObjectsByLevel.Select((objects, level) => $"L{level}={objects}/{plan.BytesByLevel[level] / 1e6:F2}MB"));
        long held = plan.BytesByLevel.Sum();
        int top = plan.ObjectsByLevel.Length - 1;
        Console.WriteLine(
            $"// moves={Moves} rows/append={RowsPerAppend * Scale} scale={Scale}: {life.Jobs} jobs, {life.Moved} objects moved, " +
            $"{life.Rewritten / 1e6:F2} MB rewritten in {life.Rewrites} rewrites ({(double)life.Rewritten / held:F2} times what the dataset holds); " +
            $"{_dataset.RowCount:N0} rows in {_dataset.ObjectCount} objects, {held / 1e6:F2} MB ({levels}); the top level's objects at " +
            $"{(double)plan.BytesByLevel[top] / plan.ObjectsByLevel[top] / Options().MaxObjectBytes:F2} of their target");
    }

    /// <summary>A fresh store and dataset.</summary>
    [IterationSetup]
    public void Fresh()
    {
        _dataset?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store = new MemoryObjectStore();
        _dataset = VortexDataset.CreateAsync(_store, Call.Schema, new DatasetOptions { ChunkBytes = (16 << 10) * Scale }).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Every append, each followed by the compactions it makes due.</summary>
    [Benchmark]
    [InvocationCount(1)]
    public long Live() => LiveAsync().GetAwaiter().GetResult().Rewritten;

    private CompactionOptions Options() => new CompactionOptions
    {
        TargetBytesAtLevelOne = (64 << 10) * Scale,
        Fanout = 4,
        MaxObjectBytes = (1 << 20) * Scale,
        TrivialMoves = Moves,
    };

    private async Task<Life> LiveAsync()
    {
        CompactionOptions options = Options();
        Life life = default;
        foreach (Call[] rows in _appends)
        {
            await using (ObjectDraft draft = _dataset.StartObject())
            {
                await draft.Writer.WriteAsync<Call>(rows).ConfigureAwait(false);
                await _dataset.AppendAsync(draft).ConfigureAwait(false);
            }

            while (await _dataset.CompactAsync(options).ConfigureAwait(false) is { } done)
            {
                life.Jobs++;
                life.Moved += done.ObjectsMoved;
                if (done.ObjectsMoved == 0)
                {
                    life.Rewrites++;
                    life.Rewritten += done.BytesIn;
                }
            }
        }

        return life;
    }

    private struct Life
    {
        public long Jobs;
        public long Moved;
        public long Rewrites;
        public long Rewritten;
    }
}
