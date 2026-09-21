// What `ColumnCompressor.Choose` costs, and which candidate inside it costs it.
//
// `Choose` is 77% of the write path when the writer is profiled, and no benchmark measured
// a single candidate. The write side had an axis (`read and write back`), a profile scenario, and
// two allocation ratchets -- all of them totals. A total says the writer is slow; it does not say
// whether the cost is training an FSST symbol table, pricing zstd, scanning for runs, or building a
// dictionary, and those have nothing in common but the call that tries them all.
//
// ONE COLUMN CHUNK, ONE ARM PER CANDIDATE. The chunk is a decoded batch of a corpus file held for
// the life of the class, so every arm sees the same bytes in the same arena -- the candidates take
// `(arena, node)` and are pure decisions, which is what makes them comparable at all.
//
// THREE COLUMNS, BECAUSE THE ANSWER DEPENDS ON THE DATA and a single column would let one candidate
// look free by declining instantly: a high-cardinality i64 is what bit-packing is for and what
// dictionaries must reject, a nullable utf8 is FSST's case, an ALP-friendly f64 is ALP's. A
// candidate that returns null in a microsecond is reporting a real cost -- the cost of saying no --
// and reading it as speed is the trap this class exists to avoid.
//
// `Choose` IS AN ARM TOO, and it is the only one whose number can be compared with the profile: it
// is the decision the writer actually makes, candidates, ordering, early exits and all. The others
// say where its time goes. The two it does NOT decompose are the run scan and the dictionary, both
// of which live inside `Choose` as private code over a private comparer; isolating them means
// making that comparer internal, which is a change to the library for the benefit of a benchmark
// and was not made here.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>The compressor's decision, and each candidate it weighs, on one column chunk.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Path)]
public class CompressorBenchmarks
{
    /// <summary>The corpus column this chunk comes from.</summary>
    /// <remarks>
    /// Single-column files, so the batch's root IS the column and no field has to be picked out.
    /// </remarks>
    [ParamsSource(nameof(Columns))]
    public string Column { get; set; } = "i64";

    /// <summary>The columns the current profile measures.</summary>
    public static IEnumerable<string> Columns => BenchmarkConfig.Full
        ? ["i64", "utf8", "f64"]
        : ["i64"];

    private static readonly Dictionary<string, string> Files = new Dictionary<string, string>
    {
        ["i64"] = "distributions/high_cardinality_i64_r8193",
        ["utf8"] = "types/utf8_nullable_r1025",
        ["f64"] = "distributions/alp_friendly_f64_r8193",
    };

    private VortexFile? _file;
    private IAsyncEnumerator<RecordBatch>? _batches;
    private RecordBatch? _batch;
    private CanonicalArena _arena = null!;
    private int _node;
    private long _plain;

    [GlobalSetup]
    public void Setup()
    {
        // ONE BATCH, HELD. `RecordBatch` is invalidated by the next `MoveNextAsync`, so the
        // enumerator is kept and never advanced again; the arena behind it stays valid for every
        // arm. Disposal is in `Cleanup`, in the reverse order.
        _file = VortexFile.OpenAsync(Corpus.Path(Files[Column]), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        _batches = _file.Scan().ExecuteAsync().GetAsyncEnumerator(CancellationToken.None);
        if (!_batches.MoveNextAsync().AsTask().GetAwaiter().GetResult())
        {
            throw new InvalidOperationException($"{Files[Column]} produced no batch.");
        }

        _batch = _batches.Current;
        _arena = _batch.Arena;
        _node = _batch.RootIndex;

        // The size ceiling the real call passes its candidates: a plan that cannot beat the plain
        // bytes is not a plan, and giving a candidate an infinite budget measures a path the writer
        // never takes.
        CanonicalNode node = _arena.GetNode(_node);
        _plain = node.Length * (long)Math.Max(1, node.Kind == CanonicalKind.Primitive ? node.PType.ByteWidth() : 8);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _batch?.Dispose();
        _batches?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _file?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true, Description = "Choose, the whole decision")]
    public int Choose() => (int)ColumnCompressor.Choose(_arena, _node).Scheme;

    [Benchmark(Description = "candidate: sequence")]
    public bool Sequence() => SequencePlan.TryBuild(_arena, _arena.GetNode(_node)) is not null;

    [Benchmark(Description = "candidate: bit packing")]
    public bool BitPacking() =>
        BitPackPlan.TryBuild(_arena, _arena.GetNode(_node), zigzag: true) is not null;

    /// <summary>FSST, which only ever sees a string column.</summary>
    /// <remarks>
    /// THE GUARD IS THE REAL PATH, not a benchmark convenience: `Choose` reaches FSST only under
    /// `node.Kind == VarBinView`, and `FsstPlan.TryBuild` throws on anything else rather than
    /// returning null. Calling it unguarded measured an exception on the i64 column.
    /// </remarks>
    [Benchmark(Description = "candidate: FSST")]
    public bool Fsst() =>
        _arena.GetNode(_node).Kind == CanonicalKind.VarBinView &&
        FsstPlan.TryBuild(_arena, _node, _plain) is not null;

    [Benchmark(Description = "candidate: zstd")]
    public bool Zstd() => ZstdPlan.TryBuild(_arena, _node, _plain) is not null;

    /// <summary>ALP, which only ever sees a float column, for FSST's reason.</summary>
    [Benchmark(Description = "candidate: ALP")]
    public bool Alp()
    {
        CanonicalNode node = _arena.GetNode(_node);
        return node.Kind == CanonicalKind.Primitive && node.PType.IsFloat() &&
            AlpPlan.TryBuild(_arena, _node, _plain) is not null;
    }
}
