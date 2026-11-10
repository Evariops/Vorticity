// Audit CP-16: what the predicate an encoding answers costs made for an IN of a thousand texts, as a question's plan makes
// one for its filter.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Compute;
using Vorticity.Expressions;

namespace Vorticity.Benchmarks.Audit;

/// <summary>An IN predicate made.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditEncodedPredicateBenchmarks
{
    private readonly FilterLiteral[] _members = new FilterLiteral[1_000];

    /// <summary>Makes the members.</summary>
    [GlobalSetup]
    public void Setup()
    {
        for (int i = 0; i < _members.Length; i++)
        {
            _members[i] = FilterLiteral.From($"user-{i:D5}");
        }
    }

    /// <summary>The predicate made.</summary>
    [Benchmark]
    public object Make() => EncodedPredicate.In(_members);
}
