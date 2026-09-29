using System;
using Vorticity.Expressions;

namespace Vorticity.Compute;

/// <summary>The kind of column a set of <c>IN</c> candidates is read against.</summary>
internal enum CandidateKind : byte
{
    /// <summary>No set is read against this column: the candidates are compared one by one.</summary>
    None,

    /// <summary>Signed integers.</summary>
    Signed,

    /// <summary>Unsigned integers.</summary>
    Unsigned,

    /// <summary>Floating point, compared as IEEE 754 compares for equality.</summary>
    Float,

    /// <summary>Decimals stored in eight bytes or fewer, as the signed integers they store.</summary>
    Decimal,

    /// <summary>Decimals stored in sixteen bytes.</summary>
    Decimal128,

    /// <summary>Decimals stored in thirty-two bytes.</summary>
    Decimal256,

    /// <summary>Utf8 or binary values, or fixed-size lists of bytes, compared byte for byte.</summary>
    Bytes,
}

/// <summary>
/// The candidates of an <c>IN</c>, hashed once for the whole scan for one kind of column: built per
/// batch, the set would merely move the candidate count from the row loop to the setup.
/// </summary>
internal abstract class CandidateSet
{
    /// <summary>The kind of column the set was built for, and the only kind it answers.</summary>
    internal abstract CandidateKind Kind { get; }

    /// <summary>
    /// Hashes <paramref name="literals"/> for a column of the given kind, or reports that they are
    /// not a set worth building.
    /// </summary>
    /// <param name="literals">The candidates.</param>
    /// <param name="kind">The column's kind.</param>
    /// <returns><see langword="null"/> when the caller must keep to an OR of equalities.</returns>
    internal static CandidateSet? For(ReadOnlySpan<FilterLiteral> literals, CandidateKind kind) => kind switch
    {
        CandidateKind.None => null,
        CandidateKind.Bytes or CandidateKind.Decimal128 or CandidateKind.Decimal256 =>
            BytesInSet.TryBuild(literals, kind),
        _ => InSet.TryBuild(literals, kind),
    };

    /// <summary>
    /// Fewer candidates than this and comparing them one by one is the cheaper answer, which turns on
    /// what one comparison costs against one probe: comparing bytes costs about what hashing them
    /// does, comparing two numbers a fraction of a probe.
    /// </summary>
    private protected static int LeastCandidates(CandidateKind kind) => kind switch
    {
        CandidateKind.Bytes => 2,
        CandidateKind.Decimal128 => 3,
        CandidateKind.Decimal256 => 5,
        _ => 4,
    };
}
