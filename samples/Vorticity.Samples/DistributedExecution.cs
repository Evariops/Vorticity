using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Vorticity.Cluster.Tcp;
using Vorticity.Dataset;

namespace Vorticity.Samples;

/// <summary>A service's failed calls: how many, and the slowest.</summary>
[VortexRecord]
public partial record struct ServiceLoad(string Service, long Count, double Slowest);

/// <summary>
/// The distributed execution page: a question every node knows by its name, cut into parts that each node runs over its
/// rows of the version the coordinator reads, here a node in this process and one in a second process of this program,
/// both reached over TCP on the loopback; their partials merged to the bits the question gives on one node.
/// </summary>
internal static class DistributedExecution
{
    /// <summary>The argument that starts this program as a cluster's node rather than as the samples.</summary>
    internal const string WorkerArgument = "--cluster-worker";

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The questions every node holds, under the same names.</summary>
    private static ClusterQuestions Questions() => new ClusterQuestions()
        .Add("load-by-service", (dataset, rows, arguments) => LoadByService(dataset.Scan<ServiceCall>().Rows(rows), BitConverter.ToInt16(arguments.Span)));

    /// <summary>The calls of one status by service: how many, and the slowest.</summary>
    private static Aggregation LoadByService(Scan<ServiceCall> calls, short status) => calls
        .Where(c => c.Status == status)
        .GroupBy(c => c.Service)
        .Select(g => (g.Key, g.Count(), g.Max(c => c.LatencyMs)));

    internal static async Task RunAsync()
    {
        // A dataset at a location every node opens: here a directory, in production a bucket.
        string location = Demo.Path("cluster");
        await using VortexDataset dataset = await VortexDataset.CreateAsync(location, ServiceCall.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(ServiceCall.Service))], ObjectBytes = 1 << 20, CalmAfter = null },
        });
        Random random = new Random(19);
        for (int append = 0; append < 8; append++)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<ServiceCall>([.. Enumerable.Range(0, 25_000).Select(i => new ServiceCall(
                Start.AddSeconds((append * 25_000) + i), $"svc-{random.Next(30):D2}", random.Next(10) == 0 ? (short)500 : (short)200,
                Math.Round(random.NextDouble() * 900, 1)))]);
            await dataset.AppendAsync(draft);
        }

        // A node in this process, and one in a process of its own, each a worker behind a TCP server on the loopback.
        ClusterQuestions questions = Questions();
        await using ClusterWorker here = new ClusterWorker("a", questions);
        await using TcpClusterServer server = new TcpClusterServer(here, new IPEndPoint(IPAddress.Loopback, 0));
        server.Start();
        using Process other = StartNode("b");
        try
        {
            int port = int.Parse(await other.StandardOutput.ReadLineAsync() ?? throw new InvalidOperationException("the second node did not start"));
            await using TcpClusterTransport transport = new TcpClusterTransport(new Dictionary<string, EndPoint>
            {
                ["a"] = server.EndPoint,
                ["b"] = new IPEndPoint(IPAddress.Loopback, port),
            });

            // The coordinator cuts the question into parts, sends each to the owner of its first object, and merges the
            // partials as they arrive; the arguments travel as bytes the application codes.
            ClusterExecutor cluster = new ClusterExecutor(["a", "b"], transport, questions);
            byte[] failed = BitConverter.GetBytes((short)500);
            List<ServiceLoad> spread = await ReadAsync(await cluster.RunAsync(dataset, "load-by-service", failed));
            List<ServiceLoad> alone = await ReadAsync(LoadByService(dataset.Scan<ServiceCall>(), 500));
            Console.WriteLine($"node b runs in process {other.Id}; failed calls by service on two nodes: {spread.Count} services, " +
                $"{spread.Sum(static load => load.Count)} calls, the slowest {spread.Max(static load => load.Slowest)} ms");
            Console.WriteLine($"the same bits as on one node: {spread.SequenceEqual(alone)}");
        }
        finally
        {
            other.StandardInput.Close();
            await other.WaitForExitAsync();
        }
    }

    /// <summary>
    /// This program as the node <paramref name="name"/>: a worker behind a TCP server on a free port of the loopback, which
    /// it writes on its first line, serving until its input closes.
    /// </summary>
    internal static async Task<int> ServeAsync(string name)
    {
        await using ClusterWorker worker = new ClusterWorker(name, Questions());
        await using TcpClusterServer server = new TcpClusterServer(worker, new IPEndPoint(IPAddress.Loopback, 0));
        server.Start();
        Console.WriteLine(server.EndPoint.Port);
        await Console.In.ReadToEndAsync();
        return 0;
    }

    /// <summary>A second process of this program, started as the node <paramref name="name"/>.</summary>
    private static Process StartNode(string name)
    {
        string program = Environment.ProcessPath ?? throw new InvalidOperationException("this process has no path to start again");
        ProcessStartInfo start = new ProcessStartInfo(program) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(program).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(DistributedExecution).Assembly.Location);
        }

        start.ArgumentList.Add(WorkerArgument);
        start.ArgumentList.Add(name);
        return Process.Start(start) ?? throw new InvalidOperationException("the second node did not start");
    }

    /// <summary>A question's answers, by service.</summary>
    private static async Task<List<ServiceLoad>> ReadAsync(Aggregation question) =>
        [.. (await question.As<ServiceLoad>().ToRecordsAsync().ToListAsync()).OrderBy(static load => load.Service, StringComparer.Ordinal)];
}
