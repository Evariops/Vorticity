// Audit DS-40 and DS-42: what a read of a held file costs a store on local disk, from one thread and from sixteen.
//
// A file object store in a temporary directory holds 64 objects of 64 KiB, kept open after their first read. Each
// operation reads 1 KiB of one of them under its token, as a dataset's reads of segments do: from one thread, and from
// sixteen at once.
using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;
using Vorticity.IO;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Reads of held files of a file object store.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditFileStoreReadsBenchmarks
{
    private const int Objects = 64;
    private const int ObjectBytes = 64 << 10;
    private const int Reads = 4_096;

    private string _directory = null!;
    private FileObjectStore _store = null!;
    private string[] _keys = null!;
    private string[] _tokens = null!;

    /// <summary>The threads reading at once.</summary>
    [Params(1, 16)]
    public int Threads { get; set; }

    /// <summary>Writes the objects and reads each once, which keeps it open.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-audit-files-{Guid.NewGuid():N}");
        _store = new FileObjectStore(_directory);
        _keys = new string[Objects];
        _tokens = new string[Objects];
        byte[] bytes = new byte[ObjectBytes];
        new Random(83).NextBytes(bytes);
        byte[] one = new byte[1];
        for (int o = 0; o < Objects; o++)
        {
            _keys[o] = $"data/object-{o:D3}.vortex";
            _store.PutAsync(_keys[o], PipeReader.Create(new ReadOnlySequence<byte>(bytes)), bytes.Length, PutCondition.None, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            ObjectRead read = _store.ReadAsync(_keys[o], 0, one, null, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            _tokens[o] = read.Token;
        }

        Console.WriteLine($"// {Read()} bytes read");
    }

    /// <summary>Drops the store and its directory.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        System.IO.Directory.Delete(_directory, recursive: true);
    }

    /// <summary>A kibibyte of a held object a read, under its token.</summary>
    [Benchmark(OperationsPerInvoke = Reads)]
    public long Read()
    {
        if (Threads == 1)
        {
            return ReadSome(0, Reads);
        }

        long total = 0;
        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, t => Interlocked.Add(ref total, ReadSome(t, Reads / Threads)));
        return total;
    }

    private long ReadSome(int thread, int count)
    {
        byte[] buffer = new byte[1_024];
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            int o = ((thread * 7) + i) % Objects;
            ObjectRead read = _store.ReadAsync(_keys[o], (i * 1_024L) % ObjectBytes, buffer, _tokens[o], CancellationToken.None).AsTask().GetAwaiter().GetResult();
            total += read.BytesRead;
        }

        return total;
    }
}
