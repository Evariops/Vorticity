using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Vorticity.Indexes;

namespace Vorticity;

/// <summary>The kinds of index a file can carry.</summary>
public enum IndexKind : byte
{
    /// <summary>A split-block Bloom filter per block: equality and membership.</summary>
    Bloom,

    /// <summary>A Bloom filter of trigrams per block: <c>Contains</c> and <c>Like</c>.</summary>
    NgramBloom,

    /// <summary>Value to blocks, exact: equality, membership, and the distinct keys of a cursor.</summary>
    Postings,

    /// <summary>Trigram to blocks, exact: <c>Contains</c> and <c>Like</c>.</summary>
    NgramPostings,

    /// <summary>Value to rows, sorted: ranges, key order and the key cursor.</summary>
    SortedRuns,
}

/// <summary>The indexes a writer builds, column by column, within a budget.</summary>
/// <remarks>
/// Immutable: every method returns a new policy. An index marked <c>required</c> that the writer
/// cannot build within the budget makes <c>CompleteAsync</c> throw, as it makes a
/// <see cref="VortexFileIndexer"/> call throw; one that is not required and is abandoned leaves no
/// bytes in the file. An unknown column throws at <c>CreateWriter</c>, or at the indexer's call.
/// </remarks>
public sealed class IndexPolicy
{
    private readonly WritePolicy _policy;
    private readonly ImmutableArray<string> _paths;

    private IndexPolicy(WritePolicy policy, ImmutableArray<string> paths, int budgetPerMille, IKeyEncoder? keyEncoder)
    {
        _policy = policy;
        _paths = paths;
        BudgetPerMille = budgetPerMille;
        KeyEncoder = keyEncoder;
    }

    /// <summary>No index: the zone maps and the statistics alone.</summary>
    public static IndexPolicy None { get; } = new IndexPolicy(WritePolicy.None, [], 100, null);

    /// <summary>Every cheap index on every column, kept only where the statistics and the budget say it pays.</summary>
    public static IndexPolicy Auto { get; } = new IndexPolicy(WritePolicy.Auto, [], 100, null);

    /// <summary>The bytes the file's indexes may take together, per thousand bytes of data.</summary>
    internal int BudgetPerMille { get; }

    /// <summary>The encoder of composite keys, from the row-encoding package.</summary>
    internal IKeyEncoder? KeyEncoder { get; }

    /// <summary>Every column path the policy names, for the check at <c>CreateWriter</c>.</summary>
    internal ImmutableArray<string> Paths => _paths;

    /// <summary>A Bloom filter on <paramref name="column"/>.</summary>
    /// <param name="column">The column path, <c>.</c>-separated for a nested field.</param>
    /// <param name="falsePositiveRate">The target false-positive rate, in (0, 0.5].</param>
    /// <param name="required">Whether the write fails rather than abandon it.</param>
    /// <returns>The policy with the index.</returns>
    public IndexPolicy Bloom(string column, double falsePositiveRate = 0.01, bool required = false)
    {
        if (!(falsePositiveRate > 0 && falsePositiveRate <= 0.5))
        {
            throw new ArgumentOutOfRangeException(nameof(falsePositiveRate), falsePositiveRate, "A rate in (0, 0.5].");
        }

        int ppm = Math.Max(1, (int)Math.Round(falsePositiveRate * 1_000_000));
        return With(column, IndexSpec.Bloom(ppm), required);
    }

    /// <summary>A Bloom filter of <paramref name="n"/>-grams on a text column, for <c>Contains</c> and <c>Like</c>.</summary>
    /// <param name="column">The column path.</param>
    /// <param name="n">The gram length; 3 is the length the format defines.</param>
    /// <param name="required">Whether the write fails rather than abandon it.</param>
    /// <returns>The policy with the index.</returns>
    public IndexPolicy NgramBloom(string column, int n = 3, bool required = false)
    {
        if (n != 3)
        {
            throw new ArgumentOutOfRangeException(nameof(n), n, "Trigrams are the only grams the index format defines.");
        }

        return With(column, IndexSpec.NgramBloom(), required);
    }

    /// <summary>Exact postings, value to blocks, on <paramref name="column"/>.</summary>
    /// <param name="column">The column path.</param>
    /// <param name="required">Whether the write fails rather than abandon it.</param>
    /// <returns>The policy with the index.</returns>
    public IndexPolicy Postings(string column, bool required = false) => With(column, IndexSpec.Postings, required);

    /// <summary>Sorted runs, value to rows, on <paramref name="column"/>: ranges, key order, the key cursor.</summary>
    /// <param name="column">The column path.</param>
    /// <param name="required">Whether the write fails rather than abandon it.</param>
    /// <returns>The policy with the index.</returns>
    public IndexPolicy SortedRuns(string column, bool required = false) => With(column, IndexSpec.SortedRuns, required);

    /// <summary>An index of <paramref name="kind"/> on the tuple of <paramref name="columns"/>.</summary>
    /// <param name="columns">The key's columns, in key order; one column is a plain column index.</param>
    /// <param name="kind">The index kind; a key of several columns is served by <see cref="IndexKind.SortedRuns"/>.</param>
    /// <param name="required">Whether the write fails rather than abandon it.</param>
    /// <returns>The policy with the index.</returns>
    public IndexPolicy ForKey(ReadOnlySpan<string> columns, IndexKind kind, bool required = false)
    {
        if (columns.IsEmpty)
        {
            throw new ArgumentException("A key has at least one column.", nameof(columns));
        }

        IndexSpec spec = SpecOf(kind);
        if (columns.Length == 1)
        {
            return With(columns[0], spec, required);
        }

        if (kind != IndexKind.SortedRuns)
        {
            throw new ArgumentException("A key of several columns is served by sorted runs and by no other kind.", nameof(kind));
        }

        string[] paths = columns.ToArray();
        foreach (string path in paths)
        {
            ArgumentException.ThrowIfNullOrEmpty(path, nameof(columns));
        }

        return new IndexPolicy(
            _policy.ForKey(paths, required ? spec.AsRequired() : spec), _paths.AddRange(paths), BudgetPerMille, KeyEncoder);
    }

    /// <summary>An index of <paramref name="kind"/> on the tuple of <paramref name="columns"/>, its keys encoded by <paramref name="encoder"/>.</summary>
    /// <param name="columns">The key's columns, in key order.</param>
    /// <param name="kind">The index kind; a key of several columns is served by <see cref="IndexKind.SortedRuns"/>.</param>
    /// <param name="encoder">What turns a tuple into bytes in tuple order, e.g. <c>RowKeyEncoder</c>; every composite key of a file shares one.</param>
    /// <param name="required">Whether the write fails rather than abandon it.</param>
    /// <returns>The policy with the index.</returns>
    /// <exception cref="ArgumentException">The policy already encodes its keys in another format.</exception>
    public IndexPolicy ForKey(ReadOnlySpan<string> columns, IndexKind kind, IKeyEncoder encoder, bool required = false)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        if (KeyEncoder is { } existing && existing.Format != encoder.Format)
        {
            throw new ArgumentException(
                $"The composite keys of a file share one encoder: this policy encodes in '{existing.Format}', not '{encoder.Format}'.",
                nameof(encoder));
        }

        return ForKey(columns, kind, required).WithKeyEncoder(encoder);
    }

    /// <summary>The bytes the indexes may take together, per thousand bytes of data; 100 by default.</summary>
    /// <param name="perMille">The budget; 0 abandons every index that is not required.</param>
    /// <returns>The policy with the budget.</returns>
    public IndexPolicy WithBudgetPerMille(int perMille)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(perMille);
        return new IndexPolicy(_policy, _paths, perMille, KeyEncoder);
    }

    /// <summary>The policy with the encoder its composite keys are written with.</summary>
    internal IndexPolicy WithKeyEncoder(IKeyEncoder encoder) => new IndexPolicy(_policy, _paths, BudgetPerMille, encoder);

    internal WritePolicy ToWritePolicy() => _policy;

    private IndexPolicy With(string column, IndexSpec spec, bool required)
    {
        ArgumentException.ThrowIfNullOrEmpty(column);
        return new IndexPolicy(_policy.For(column, required ? spec.AsRequired() : spec), _paths.Add(column), BudgetPerMille, KeyEncoder);
    }

    private static IndexSpec SpecOf(IndexKind kind) => kind switch
    {
        IndexKind.Bloom => IndexSpec.Bloom(),
        IndexKind.NgramBloom => IndexSpec.NgramBloom(),
        IndexKind.Postings => IndexSpec.Postings,
        IndexKind.NgramPostings => IndexSpec.NgramPostings(),
        IndexKind.SortedRuns => IndexSpec.SortedRuns,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a defined index kind."),
    };
}
