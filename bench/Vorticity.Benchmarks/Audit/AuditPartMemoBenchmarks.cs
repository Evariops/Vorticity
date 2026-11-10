// Audit DS-11: what a part of a scan worked out again of its filter before it took the scan's own.
//
// A part (a run of objects read as rows, an object's granules its statistics leave) is a new builder of the scan's
// filter, projection and schema. Each found again what depends on those alone: the filter's pruner over summaries,
// the paths it reads, the top-level columns those name, and the key of its pages (the filter's text formatted, the
// schema's bytes hashed whole). The dataset has 24 columns; the filter three predicates over three of them.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>The work a part of a scan did again of its filter, schema and projection.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditPartMemoBenchmarks
{
    private const int Columns = 24;

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;
    private VortexExpr _filter = null!;
    private DType _schema;
    private ReadOnlyMemory<byte> _schemaBytes;

    /// <summary>Creates the dataset, empty: only its schema is read.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        DTypeArena types = new DTypeArena();
        string[] names = new string[Columns];
        DType[] fields = new DType[Columns];
        for (int c = 0; c < Columns; c++)
        {
            names[c] = $"column_{c:D2}";
            fields[c] = types.Primitive(c % 3 == 0 ? PType.F64 : PType.I64, Nullability.Nullable);
        }

        _dataset = VortexDataset.CreateAsync(_store, types.Struct(names, fields, Nullability.NonNullable), new DatasetOptions()).AsTask().GetAwaiter().GetResult();
        _schema = _dataset.Snapshot.Schema.DType;
        _schemaBytes = _dataset.Snapshot.Schema.Bytes;
        _filter = Expr.And(
            Expr.And(
                Expr.Gt(Expr.Field("column_03"), Expr.Literal(FilterLiteral.From(0.25))),
                Expr.Lt(Expr.Field("column_07"), Expr.Literal(FilterLiteral.From(1_000L)))),
            Expr.Eq(Expr.Field("column_11"), Expr.Literal(FilterLiteral.From(42L))));
        Console.WriteLine($"// schema {_schemaBytes.Length} bytes, filter {ExprText.Format(_filter)}");
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>What a part found again: the pruner, the filter's paths and columns, the key of its pages.</summary>
    [Benchmark]
    public int PartFoundAgain()
    {
        SummaryPruner pruner = new SummaryPruner(_filter);
        HashSet<string> paths = [];
        _filter.CollectFields(paths);
        Span<bool> read = stackalloc bool[Columns];
        int total = 0;
        foreach (string path in paths)
        {
            int field = _schema.IndexOfField(path);
            if (field >= 0 && !read[field])
            {
                read[field] = true;
                total++;
            }
        }

        int[] fieldsRead = new int[total];
        for (int field = 0, at = 0; field < Columns; field++)
        {
            if (read[field])
            {
                fieldsRead[at++] = field;
            }
        }

        UInt128 key = System.IO.Hashing.XxHash128.HashToUInt128(System.Runtime.InteropServices.MemoryMarshal.AsBytes(ExprText.Format(_filter).AsSpan()))
            ^ System.IO.Hashing.XxHash128.HashToUInt128(_schemaBytes.Span);
        return pruner.Paths.Count + fieldsRead.Length + (int)(ulong)key;
    }
}
