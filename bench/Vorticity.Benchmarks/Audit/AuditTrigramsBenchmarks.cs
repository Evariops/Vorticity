// What finding the distinct trigrams a text predicate requires costs, which a Bloom or key index's pruner
// asks of each file a question reads: a LIKE of a 40-character run, and a starts-with of 12 characters.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Expressions;
using Vorticity.Indexes;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A text predicate's trigrams.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditTrigramsBenchmarks
{
    private readonly StringMatchExpr _like = new StringMatchExpr(
        Expr.Field("message"), StringMatchOp.Like, FilterLiteral.From("%connection reset by peer while reading the%"u8.ToArray()), (byte)0x5C);

    private readonly StringMatchExpr _starts = new StringMatchExpr(
        Expr.Field("message"), StringMatchOp.StartsWith, FilterLiteral.From("GET /api/v2/"u8.ToArray()), (byte)0x5C);

    /// <summary>A LIKE's trigrams.</summary>
    [Benchmark]
    public int Like() => Trigrams.Required(_like, fold: false).Count;

    /// <summary>A starts-with's trigrams.</summary>
    [Benchmark]
    public int StartsWith() => Trigrams.Required(_starts, fold: false).Count;
}
