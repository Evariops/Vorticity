# Distributed execution

Run one aggregation over a dataset on several machines, each reading its share of the objects, and get the
answer one machine would give, to the bit.

```csharp
// The questions every node holds, under the same names.
static ClusterQuestions Questions() => new ClusterQuestions()
    .Add("load-by-service", (dataset, rows, arguments) => LoadByService(dataset.Scan<ServiceCall>().Rows(rows), BitConverter.ToInt16(arguments.Span)));

static Aggregation LoadByService(Scan<ServiceCall> calls, short status) => calls
    .Where(c => c.Status == status)
    .GroupBy(c => c.Service)
    .Select(g => (g.Key, g.Count(), g.Max(c => c.LatencyMs)));

// A node: a worker behind a server. Here one in this process, and one in a second process of the program.
await using ClusterWorker here = new ClusterWorker("a", Questions());
await using TcpClusterServer server = new TcpClusterServer(here, new IPEndPoint(IPAddress.Loopback, 0));
server.Start();

// The coordinator: the nodes by name, how to reach them, and the same questions.
await using TcpClusterTransport transport = new TcpClusterTransport(new Dictionary<string, EndPoint>
{
    ["a"] = server.EndPoint,
    ["b"] = new IPEndPoint(IPAddress.Loopback, port),
});
ClusterExecutor cluster = new ClusterExecutor(["a", "b"], transport, Questions());
Aggregation spread = await cluster.RunAsync(dataset, "load-by-service", BitConverter.GetBytes((short)500));
```

```
node b runs in process 50484; failed calls by service on two nodes: 30 services, 19930 calls, the slowest 900 ms
the same bits as on one node: True
```

The cluster lives in `Vorticity.Dataset`, which is experimental (VX0001, see [datasets.md](datasets.md)), and the
TCP transport in its own package, `Vorticity.Cluster.Tcp`, experimental as well (VX0003): its frames and its API may
change between releases.

## What travels, and what does not

Nodes share nothing but the store. Every object is immutable and every version is named by its number, so a node
that opens the dataset at the coordinator's version reads exactly the rows the coordinator would. What goes over the
wire is small and plain: the question's name, its arguments as the bytes your application codes them in, the
dataset's location, the version, and a range of rows. An expression never travels. Each node builds the question
from its name, with the same code as every other node, which is why every node holds the same `ClusterQuestions`:
register them in one place your nodes and your coordinator both call.

A question is an aggregation whose every aggregate a state answers whole: counts, sums, least and greatest values,
means of numbers, distributions and quantiles, approximate distinct counts. The coordinator refuses any other the
first time it runs it, with a `NotSupportedException`. `Add<TResult>` registers a question of one result a group,
read as `Aggregation<TResult>`; `Add` one of several results a group, read through a record with `As<TRecord>()`, as
the sample does. The builder receives the dataset, the range of rows of the part it answers, which it applies with
`Rows(rows)`, and the arguments.

## How a question is spread

`RunAsync` cuts the question into parts, four for each node by default (`ClusterOptions.TasksPerNode`), each a range
of rows of the version. A part goes to the owner of the object its first row lies in: every object has one owner
among the nodes, found by rendezvous hashing of the object's identity, so ownership moves only when nodes come or go,
and a node keeps reading the objects it read before, with their metadata in its memory and their bytes in its caches.

The aggregation `RunAsync` returns is enumerated once, as any other. Its enumeration sends the parts, at most four in
flight on each node, and folds each partial as it arrives, so the merge advances while the nodes read and a partial is
let go once folded. The partials merge to the bits of the question asked on one node, whatever the split.

A part a node cannot be reached for (`ClusterTransportException`) goes to the next owner in the rendezvous order,
twice at most (`ClusterOptions.Retries`). An error the question itself raises on a node is not retried: a network
transport brings it back as a `ClusterPartException` that carries the node's message, and the merge throws it.

## Keeping the nodes warm

A node opens each version it is asked for once, and keeps the handles of the last four versions asked. After an
append, a node's first part over the new version would read the new objects' metadata before their rows. A writer's
handle can tell the cluster instead: `DatasetOptions.Moved` is called with the version the handle moves to, by its own
commit or by a refresh, and `ClusterExecutor.WarmAsync(location, version)` has every node read, for the objects it owns
in that version, their sparse indexes and the marks of their columns:

```csharp
Channel<ulong> moved = Channel.CreateUnbounded<ulong>();
await using VortexDataset writer = await VortexDataset.OpenAsync(location, new DatasetOptions { Moved = version => moved.Writer.TryWrite(version) });

// Elsewhere, a loop of the host's.
await foreach (ulong version in moved.Reader.ReadAllAsync(stopping))
{
    await cluster.WarmAsync(location, version, stopping);
}
```

The hook runs on the committing thread and must not block: hand the version over, as here, rather than warm from it.

## Your own transport

`IClusterTransport` asks for one method, `RunAsync(node, task)`, and offers `WarmAsync(node, location, version, nodes)`,
which does nothing unless a transport carries it; a `ClusterWorker` answers both. `LocalClusterTransport` calls workers in the same process, which is what tests use; the TCP
transport sends length-prefixed frames, keeps up to sixteen idle connections a node, and retries once on a fresh one
when a kept connection the node had closed breaks. A transport of your own, over gRPC or a queue, only has to carry a
`ClusterTask` to the node's worker and its `AggregationPartial` back.

## Watch out

* `TcpClusterServer` supposes a trusted network: it reads every frame it is sent. Put the nodes on a private network,
  or carry the frames over a transport you secure.
* Every node and the coordinator must register the same questions under the same names, built the same way. A node
  that lacks a name fails its parts with an `ArgumentException`, which the TCP transport brings back as a
  `ClusterPartException`.
* The arguments are bytes your application codes and decodes; the library never looks inside them.
* The nodes open the dataset by the location the coordinator gives (`RunAsync`'s `location`, or the handle's own), so
  each must reach the same store, with its own credentials.
* A part is gathered on the coordinator. Groupings of very many groups are merged there too; the shuffles and
  broadcasts that would spread them are designed, not built.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- distributed-execution
```

The sample starts its own program a second time as the node `b`, with `--cluster-worker b`, and stops it when the
question is answered.
