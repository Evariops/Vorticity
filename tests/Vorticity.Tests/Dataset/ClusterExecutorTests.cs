// A question run over several nodes (spec F; F1, F2, F5): its parts handed to their owners by rendezvous hashing, run by
// each node's worker on its own handle of the version, their partials merged as they arrive.
//
// THE ORACLE IS ONE NODE: the answers of three nodes are the bits of the question run alone; a node the transport does not
// reach has its parts run on their next owners, the same bits; with no retry, the merge throws the transport's error; a
// handle's Moved hook sees each version it commits, which the nodes warm and answer.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class ClusterExecutorTests
{
    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Eight appends of 20 000 rows in a directory, merged: a count, a sum, an extreme, a mean, a quantile and a distinct
    /// count by service, and a count by day, run on three local nodes, give the bits of one node, every node running parts;
    /// with a node unreached, its parts run on the next owners; with no retry, the merge throws; an append through a handle
    /// that hooks Moved reaches the nodes once warmed.
    /// </summary>
    [Fact]
    public async Task ThreeNodesAnswerWithTheBitsOfOne()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vorticity-cluster-{Guid.NewGuid():N}");
        Random random = new Random(83);
        long sequence = 0;
        DatasetOptions options = new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Row.Service))], ObjectBytes = 1 << 20, MergeFanout = 4, CalmAfter = null },
            ContentsObjectBytes = 0,
        };
        ClusterQuestions questions = new ClusterQuestions()
            .Add("by-service", (d, rows, args) => ByService(d.Scan<Row>().Rows(rows), BitConverter.ToDouble(args.Span)))
            .Add<long>("per-day", (d, rows, args) => d.Scan<Row>().Rows(rows).GroupBy(r => r.Day).Select(g => g.Count()));
        byte[] floor = BitConverter.GetBytes(250.0);
        ClusterWorker[] workers = [];
        try
        {
            await using VortexDataset dataset = await VortexDataset.CreateAsync(root, Row.Schema, options, Ct);
            for (int a = 0; a < 8; a++)
            {
                await AppendAsync(dataset);
            }

            // Three nodes that each own an object: an object's owner hangs on its identity, which is random, and one of
            // three nodes owns none of eight objects about one time in nine.
            string[] nodes = await OwningNodesAsync(dataset);
            workers = [.. nodes.Select(node => new ClusterWorker(node, questions))];

            // Three nodes, every part on its owner: the bits of one node.
            Counting transport = new Counting(new LocalClusterTransport(workers));
            ClusterExecutor executor = new ClusterExecutor(nodes, transport, questions);
            List<string> alone = await AnswersAsync(ByService(dataset.Scan<Row>(), 250.0));
            Assert.True(alone.Count > 40, $"{alone.Count} services");
            Assert.Equal(alone, await AnswersAsync(await executor.RunAsync(dataset, "by-service", floor, cancellationToken: Ct)));
            Assert.Equal(
                await CountsAsync(dataset.Scan<Row>().GroupBy(r => r.Day).Select(g => g.Count())),
                await CountsAsync(await executor.RunAsync<long>(dataset, "per-day", cancellationToken: Ct)));
            Assert.Equal(nodes, transport.Runs.Keys.Order(StringComparer.Ordinal));
            Assert.Empty(transport.Failures);

            // A node the transport does not reach: its parts run on their next owners, the same bits.
            Counting partial = new Counting(new LocalClusterTransport(workers.Take(2)));
            Assert.Equal(alone, await AnswersAsync(await new ClusterExecutor(nodes, partial, questions).RunAsync(dataset, "by-service", floor, cancellationToken: Ct)));
            Assert.True(partial.Failures.GetValueOrDefault(nodes[2]) > 0 && !partial.Runs.ContainsKey(nodes[2]), $"{partial.Failures.GetValueOrDefault(nodes[2])} parts failed on {nodes[2]}");

            // No retry: the merge throws the transport's error.
            ClusterExecutor strict = new ClusterExecutor(nodes, new LocalClusterTransport(workers.Take(2)), questions, new ClusterOptions { Retries = 0 });
            await Assert.ThrowsAsync<ClusterTransportException>(async () => await AnswersAsync(await strict.RunAsync(dataset, "by-service", floor, cancellationToken: Ct)));

            // A writer's Moved hook sees the version its append commits; the nodes warm it, and answer it.
            ConcurrentQueue<ulong> moved = new ConcurrentQueue<ulong>();
            await using VortexDataset writer = await VortexDataset.OpenAsync(root, options with { Moved = moved.Enqueue }, Ct);
            await AppendAsync(writer);
            Assert.Equal([writer.Version], moved);
            await executor.WarmAsync(root, writer.Version, Ct);
            List<string> after = await AnswersAsync(ByService(writer.Scan<Row>(), 250.0));
            Assert.NotEqual(alone, after);
            Assert.Equal(after, await AnswersAsync(await executor.RunAsync(writer, "by-service", floor, cancellationToken: Ct)));
        }
        finally
        {
            foreach (ClusterWorker worker in workers)
            {
                await worker.DisposeAsync();
            }

            Directory.Delete(root, recursive: true);
        }

        async Task AppendAsync(VortexDataset target)
        {
            await using ObjectDraft draft = target.StartObject();
            Row[] rows = [.. Enumerable.Range(0, 20_000).Select(_ => new Row(
                (int)(sequence % 7), $"svc-{random.Next(0, 50):D2}", Start.AddSeconds(random.Next(0, 3 * 86_400)), sequence++, Math.Round(random.NextDouble() * 1000, 3)))];
            await draft.Writer.WriteAsync<Row>(rows, Ct);
            await target.AppendAsync(draft, Ct);
        }
    }

    private static Vorticity.Aggregation ByService(Scan<Row> scan, double floor) => scan
        .Where(r => r.Value >= floor)
        .GroupBy(r => r.Service)
        .Select(g => (g.Key, g.Count(), g.Sum(r => r.Value), g.Max(r => r.Sequence), g.Average(r => r.Value), g.Quantile(r => r.Value, 0.9), g.ApproximateCountDistinct(r => r.Day)));

    private static async Task<List<string>> AnswersAsync(Vorticity.Aggregation question) =>
        [.. (await question.As<ServiceAnswer>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(static a => a.Service, StringComparer.Ordinal).Select(static a => a.ToString())];

    private static async Task<List<long>> CountsAsync(Aggregation<long> question) =>
        [.. (await question.ToListAsync(Ct)).Order()];

    /// <summary>The names of three nodes, a letter and a number each, every one of which owns an object of the dataset's version.</summary>
    private static async Task<string[]> OwningNodesAsync(VortexDataset dataset)
    {
        List<UInt128> uids = [];
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            uids.Add(held.Entry.Uid);
        }

        for (int n = 0; ; n++)
        {
            string[] nodes = [$"a{n}", $"b{n}", $"c{n}"];
            if (Array.TrueForAll(nodes, node => uids.Exists(uid => Rendezvous.Owner(uid, nodes) == node)))
            {
                return nodes;
            }
        }
    }

    /// <summary>A transport that counts the parts each node ran, and those that failed on it.</summary>
    private sealed class Counting(IClusterTransport inner) : IClusterTransport
    {
        public ConcurrentDictionary<string, int> Runs { get; } = new ConcurrentDictionary<string, int>();

        public ConcurrentDictionary<string, int> Failures { get; } = new ConcurrentDictionary<string, int>();

        public async ValueTask<AggregationPartial> RunAsync(string node, ClusterTask task, CancellationToken cancellationToken)
        {
            try
            {
                AggregationPartial partial = await inner.RunAsync(node, task, cancellationToken);
                Runs.AddOrUpdate(node, 1, static (_, n) => n + 1);
                return partial;
            }
            catch (ClusterTransportException)
            {
                Failures.AddOrUpdate(node, 1, static (_, n) => n + 1);
                throw;
            }
        }

        public ValueTask WarmAsync(string node, string location, ulong version, IReadOnlyList<string> nodes, CancellationToken cancellationToken) =>
            inner.WarmAsync(node, location, version, nodes, cancellationToken);
    }

    [VortexRecord]
    public partial record struct Row(int Day, string Service, DateTime At, long Sequence, double Value);

    [VortexRecord]
    public partial record struct ServiceAnswer(string Service, long Count, double Sum, long LastSequence, double? Mean, double? Value90, long Days);
}
