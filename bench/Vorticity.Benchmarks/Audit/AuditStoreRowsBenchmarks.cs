// Audit WR-01: what appending a canonical column of 65,536 rows to a store of its type costs, per row, column by column:
// the copy every external sort, top-k and ingestion makes of its batches.
//
// Each column is built once in an arena: 64-bit integers with no null and with a null every tenth row, booleans with a
// null every tenth row, short texts (8 bytes, inline in their views) and long ones (40 bytes) with a null every tenth
// row. Each invocation appends the column to a fresh store and builds it back into an arena.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A canonical column appended to a store, per kind of column.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditStoreRowsBenchmarks
{
    private const int Rows = 65_536;

    /// <summary>The column appended.</summary>
    [Params("i64", "i64 nulls", "bool nulls", "short text nulls", "long text nulls")]
    public string Column { get; set; } = "i64";

    private CanonicalArena _source = null!;
    private int _node;
    private DType _type;
    private CanonicalArena _into = null!;

    // The store the source column was built from, which holds its buffers while the benchmark reads them.
    private ColumnStore _sourceStore = null!;

    /// <summary>Builds the column.</summary>
    [GlobalSetup]
    public void Setup()
    {
        DTypeArena types = new DTypeArena();
        _source = new CanonicalArena();
        _into = new CanonicalArena();
        bool nulls = Column.EndsWith("nulls", StringComparison.Ordinal);
        Nullability nullable = nulls ? Nullability.Nullable : Nullability.NonNullable;
        _type = Column switch
        {
            "i64" or "i64 nulls" => types.Primitive(PType.I64, nullable),
            "bool nulls" => types.Bool(nullable),
            _ => types.Utf8(nullable),
        };

        // The column written through a store of its own, one row at a time, and built: the source every append copies.
        ColumnStore source = _sourceStore = ColumnStores.Create(_type, AlignedBufferPool.Shared, null);
        int width = Column.StartsWith("short", StringComparison.Ordinal) ? 8 : 40;
        for (int row = 0; row < Rows; row++)
        {
            if (nulls && row % 10 == 0)
            {
                source.AppendNull();
                continue;
            }

            switch (source)
            {
                case FixedStore longs:
                    longs.Append(row * 7L);
                    break;
                case BoolStore bools:
                    bools.Append((row * 37 & 4) != 0);
                    break;
                case VarBinStore texts:
                    texts.Append(System.Text.Encoding.UTF8.GetBytes(row.ToString("D" + width, System.Globalization.CultureInfo.InvariantCulture)));
                    break;
            }
        }

        _node = source.Build(_source, Rows);
    }

    /// <summary>The column appended to a fresh store of its type, built back, its texts checked as an ingestion's are.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public int Append() => AppendAs(validated: false);

    /// <summary>The column appended as a result's is, its texts known to be UTF-8.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public int AppendValidated() => AppendAs(validated: true);

    private int AppendAs(bool validated)
    {
        ColumnStore store = ColumnStores.Create(_type, AlignedBufferPool.Shared, null);
        try
        {
            StoreRows.Append(store, _source, _node, validated);
            _into.ResetKeepingBlocks();
            return store.Build(_into, store.Rows);
        }
        finally
        {
            store.Release();
        }
    }
}
