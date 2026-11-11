// What a rent and a return of an open data object cost, alone and with sixteen threads at once.
//
// A dataset of 32 small objects is written into an in-memory store, and each object is rented once so that the
// handle's cache holds them all open. `RentReturn` then rents every object and gives it back, in turn; each thread
// walks every object, as a question's lanes do. Time and bytes are per rent and return.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Rents and returns of open data objects, against the threads asking.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditObjectCacheBenchmarks
{
    private const int Objects = 32;
    private const int Rows = 1_024;

    /// <summary>Threads asking at once.</summary>
    [Params(1, 16)]
    public int Threads { get; set; }

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;
    private ObjectEntry[] _entries = null!;

    /// <summary>Writes the objects and opens each once.</summary>
    [GlobalSetup]
    public void Setup() => SetupAsync().GetAwaiter().GetResult();

    private async Task SetupAsync()
    {
        _store = new MemoryObjectStore();
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(["key"], [types.Primitive(PType.I64, Nullability.NonNullable)], Nullability.NonNullable);
        _dataset = await VortexDataset.CreateAsync(
            _store, schema, new DatasetOptions { Seed = 0xA0D1_7006_5EED, Layout = new DatasetLayout { SortKey = [LayoutValue.Column("key")] } })
            .ConfigureAwait(false);
        for (int part = 0; part < Objects; part++)
        {
            await _dataset.AppendAsync(Batches(types, schema, part)).ConfigureAwait(false);
        }

        List<ObjectEntry> entries = [];
        await foreach (PositionedObject held in _dataset.Snapshot.WalkAsync(null, 0, long.MaxValue, null, CancellationToken.None).ConfigureAwait(false))
        {
            entries.Add(held.Entry);
        }

        _entries = [.. entries];
        Console.WriteLine($"// {_entries.Length} objects, {RentReturn()} rents");
    }

    /// <summary>Closes the dataset.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Every object rented and given back, by every thread.</summary>
    [Benchmark(OperationsPerInvoke = Objects)]
    public long RentReturn()
    {
        if (Threads == 1)
        {
            return WalkAsync(0).GetAwaiter().GetResult();
        }

        long total = 0;
        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, thread => Interlocked.Add(ref total, WalkAsync(thread).GetAwaiter().GetResult()));
        return total;
    }

    private async Task<long> WalkAsync(int thread)
    {
        long rents = 0;
        DatasetSnapshot snapshot = _dataset.Snapshot;
        for (int n = 0; n < Objects; n++)
        {
            ObjectLease lease = await snapshot.RentAsync(_entries[(n + thread) % Objects], CancellationToken.None).ConfigureAwait(false);
            rents += lease.WasCached ? 1 : 0;
            await lease.DisposeAsync().ConfigureAwait(false);
        }

        return rents;
    }

    private static async IAsyncEnumerable<RecordBatch> Batches(DTypeArena types, DType schema, int part)
    {
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer keys = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        Span<long> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(keyBytes);
        for (int i = 0; i < Rows; i++)
        {
            values[i] = ((long)part * Rows) + i;
        }

        int key = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, keys);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [key]);
        yield return new RecordBatch(arena, root, 0);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
