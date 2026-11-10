// Audit DE-06: what a column written in pco costs its lanes, each decode keeping the state it reuses.
//
// 4,194,304 jittered timestamps (i64, a second apart with a millisecond of jitter), written under the smallest profile,
// which writes them in pco, in blocks of 65,536 rows; read from memory at one lane and at eight, every value consumed.
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A pco column read at one lane and at eight.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditPcoLanesBenchmarks
{
    private const int Rows = 1 << 22;
    private const int Batch = 65_536;

    private VortexFile _file = null!;

    /// <summary>The scan's lanes.</summary>
    [Params(1, 8)]
    public int Degree { get; set; }

    /// <summary>Writes the column into memory and opens it there.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-audit-pco-{Guid.NewGuid():N}.vortex");
        string encodings = WriteAsync(path).GetAwaiter().GetResult();
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        System.IO.File.Delete(path);
        _file = VortexFile.OpenAsync(new MemorySegmentSource(bytes), VortexOpenOptions.Default).AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"// {bytes.Length} bytes, written as {encodings}; {Scan()} rows read");
    }

    /// <summary>Closes the file.</summary>
    [GlobalCleanup]
    public void Cleanup() => _file.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Every value of the column, read.</summary>
    [Benchmark]
    public long Scan() => ScanAsync().GetAwaiter().GetResult();

    private async Task<long> ScanAsync()
    {
        long rows = 0;
        await foreach (RecordBatch batch in _file.ScanBuilder().WithDegreeOfParallelism(Degree).ExecuteAsync().ConfigureAwait(false))
        {
            using (batch)
            {
                rows += Vorticity.Bench.Scenarios.Consume.Batch(batch);
            }
        }

        return rows;
    }

    private static async Task<string> WriteAsync(string path)
    {
        DTypeArena types = new DTypeArena();
        DType column = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(["at"], [column], Nullability.NonNullable);
        WriteReport report;
        await using (VortexFileWriter writer = VortexFileWriter.Create(path, schema, new VortexWriteOptions { Compression = CompressionProfile.Smallest }))
        {
            for (int start = 0; start < Rows; start += Batch)
            {
                CanonicalArena arena = new CanonicalArena();
                VortexBuffer buffer = arena.Allocate(Batch * sizeof(long), 64, out Span<byte> bytes);
                Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
                for (int i = 0; i < Batch; i++)
                {
                    long row = start + i;
                    ulong mix = (ulong)row * 0x9E37_79B9_7F4A_7C15UL;
                    values[i] = 1_700_000_000_000L + (row * 1000) + (long)((mix >> 33) % 1000);
                }

                int at = arena.AddPrimitive(column, Batch, Validity.NonNullable, PType.I64, buffer);
                int root = arena.AddStruct(schema, Batch, Validity.NonNullable, [at]);
                using RecordBatch batch = new RecordBatch(arena, root, start);
                await writer.WriteAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }

            report = await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return string.Join(",", report.Columns[0].Encodings);
    }
}
