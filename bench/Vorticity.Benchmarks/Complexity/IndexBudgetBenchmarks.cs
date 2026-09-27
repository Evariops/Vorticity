// What writing a wide file with an index on every column costs, against the columns: the index
// writer's bookkeeping of its payloads and its budget grows with the payloads times the builders.
//
// A file of `Columns` Int64 columns of 16,384 rows, in blocks of 64, each column under a Bloom
// index, is written to memory. Every column's builder closes a payload per generation of blocks,
// and each payload is taken, placed and weighed against the budget. Run in a checkout of the
// original and in the tree.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A wide file written with a Bloom index on every column, against the columns.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class IndexBudgetBenchmarks
{
    /// <summary>Rows of the file: 256 blocks, sixteen generations of Bloom filters per column.</summary>
    private const int Rows = 16_384;

    /// <summary>Columns of the file, each indexed.</summary>
    [Params(256, 4_096)]
    public int Columns { get; set; }

    private DType _schema;
    private VortexWriteOptions _options = null!;
    private CanonicalArena _arena = null!;
    private int _root;

    /// <summary>Lays the batch out once, and checks the file holds an index per column.</summary>
    [GlobalSetup]
    public void Setup()
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        string[] names = new string[Columns];
        DType[] fields = new DType[Columns];
        WritePolicy policy = WritePolicy.None;
        for (int c = 0; c < Columns; c++)
        {
            names[c] = "c" + c.ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields[c] = i64;
            policy = policy.For(names[c], IndexSpec.Bloom());
        }

        _schema = types.Struct(names, fields, Nullability.NonNullable);
        _options = new VortexWriteOptions { RowBlockSize = 64, IndexBudgetPerMille = 1_000_000, WritePolicy = policy };
        _arena = new CanonicalArena();
        int[] columns = new int[Columns];
        for (int c = 0; c < Columns; c++)
        {
            VortexBuffer buffer = _arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
            Span<long> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes);
            for (int row = 0; row < Rows; row++)
            {
                values[row] = ((long)row * 2_654_435_761L) ^ ((long)c << 40);
            }

            columns[c] = _arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, buffer);
        }

        _root = _arena.AddStruct(_schema, Rows, Validity.NonNullable, columns);
        if (Write() <= (long)Rows * Columns)
        {
            throw new InvalidOperationException("The file is smaller than its data.");
        }
    }

    /// <summary>The file written to memory; its length.</summary>
    [Benchmark]
    public long Write() => WriteAsync().GetAwaiter().GetResult();

    private async Task<long> WriteAsync()
    {
        System.IO.MemoryStream written = new System.IO.MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(written), _schema, _options))
        {
            // The batch borrows the arena, which the setup built once and every write reads.
            RecordBatch batch = new RecordBatch(_arena, _root, 0);
            await writer.WriteAsync(batch).ConfigureAwait(false);
            WriteReport report = await writer.CompleteAsync().ConfigureAwait(false);
            int built = 0;
            foreach (IndexWriteReport index in report.Indexes)
            {
                built += index.Outcome == IndexOutcome.Built ? 1 : 0;
            }

            if (built != Columns)
            {
                throw new InvalidOperationException($"{built} indexes were built for {Columns} columns.");
            }
        }

        return written.Length;
    }
}
