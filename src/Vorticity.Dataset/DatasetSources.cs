using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

namespace Vorticity.Dataset;

/// <summary>
/// The entries of one of the application's sources a commit takes into account: those past
/// <see cref="From"/> up to <see cref="To"/>, both in the source's own numbering.
/// </summary>
/// <remarks>
/// A source is a name the application chooses, a shard of raw data or a partition of a log, and an
/// offset a number that grows with its entries, a sequence number or a log offset: the dataset gives
/// them no meaning of their own. A commit that declares the range applies only where the version it
/// lands on counts the source up to <see cref="From"/> exactly, and the offset then moves to
/// <see cref="To"/>, so that each entry is counted once however many writers take it.
/// </remarks>
public readonly record struct SourceRange
{
    /// <summary>A range of <paramref name="source"/>: the entries past <paramref name="from"/> up to <paramref name="to"/>.</summary>
    /// <param name="source">The source's name.</param>
    /// <param name="from">The offset the dataset counts the source up to, which the range starts after.</param>
    /// <param name="to">The offset the range ends at, included: past <paramref name="from"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="source"/> is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="to"/> is not past <paramref name="from"/>.</exception>
    public SourceRange(string source, ulong from, ulong to)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        if (to <= from)
        {
            throw new ArgumentOutOfRangeException(nameof(to), to, $"A range ends past where it starts, {from}.");
        }

        Source = source;
        From = from;
        To = to;
    }

    /// <summary>The source's name.</summary>
    public string Source { get; }

    /// <summary>The offset the range starts after: what the dataset must count the source up to for the commit to apply.</summary>
    public ulong From { get; }

    /// <summary>The offset the range ends at, included: what the dataset counts the source up to once the commit applied.</summary>
    public ulong To { get; }
}

/// <summary>What a commit that declared the source ranges it consumed made of them.</summary>
public sealed record SourceCommit
{
    /// <summary>The version the commit created, or, when its ranges were already counted, the version that counts them.</summary>
    public ulong Version { get; init; }

    /// <summary>
    /// <see cref="OperationOutcome.Applied"/> when the commit applied and the offsets moved, or
    /// <see cref="OperationOutcome.AlreadyThere"/> when another commit had counted every range
    /// already, and this one added nothing.
    /// </summary>
    public OperationOutcome Outcome { get; init; }
}

/// <summary>
/// A commit declared source ranges that do not start where the dataset counts their sources up to:
/// one would leave a gap, or overlap a range another commit counted. Nothing was committed.
/// </summary>
/// <remarks>
/// Work out the ranges again from <see cref="Recorded"/>, the offsets of the version the commit was
/// decided against: the entries past them are the ones not counted yet.
/// </remarks>
public sealed class SourceOffsetConflictException : VortexException
{
    /// <summary>Creates the exception with a default message.</summary>
    public SourceOffsetConflictException()
        : base("The source ranges a commit declared do not start where the dataset counts their sources up to.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">Which ranges, and the offsets recorded.</param>
    public SourceOffsetConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    /// <param name="message">Which ranges, and the offsets recorded.</param>
    /// <param name="innerException">The cause.</param>
    public SourceOffsetConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The ranges the commit declared.</summary>
    public IReadOnlyList<SourceRange> Declared { get; init; } = [];

    /// <summary>For each source the commit declared, the offset the dataset counts it up to; 0 for one it never counted.</summary>
    public IReadOnlyDictionary<string, ulong> Recorded { get; init; } = ImmutableDictionary<string, ulong>.Empty;

    /// <summary>The version whose offsets the commit was decided against.</summary>
    public ulong Version { get; init; }

    /// <summary>That version's commit object, which the writer's handle moves to, so that it reads the offsets the conflict names.</summary>
    internal CommitObject? Held { get; init; }

    /// <summary>When the writer knew <see cref="Version"/> to be the latest.</summary>
    internal DateTimeOffset KnownAt { get; init; }
}

/// <summary>What a commit's declared ranges make of it, against the offsets of the version it lands on.</summary>
internal enum SourceVerdict
{
    /// <summary>Every range starts where its source is counted up to: the commit applies, and the offsets move.</summary>
    Applied,

    /// <summary>Every range is counted already: the commit adds nothing.</summary>
    AlreadyThere,

    /// <summary>Anything else: a range would leave a gap, or overlap one counted.</summary>
    Conflict,
}

/// <summary>The offsets a header records, judged against a commit's ranges, and moved past them.</summary>
internal static class SourceTable
{
    /// <summary>
    /// The verdict on <paramref name="consumed"/> against <paramref name="recorded"/>: applied when every
    /// range starts at its source's offset, already there when every range ends at or before it, a
    /// conflict otherwise, since a commit's rows mix its sources and cannot apply in part.
    /// </summary>
    internal static SourceVerdict Judge(IReadOnlyList<SourceOffset> recorded, IReadOnlyList<SourceRange> consumed)
    {
        bool applies = true;
        bool counted = true;
        foreach (SourceRange range in consumed)
        {
            ulong offset = OffsetOf(recorded, range.Source);
            applies &= offset == range.From;
            counted &= offset >= range.To;
        }

        return applies ? SourceVerdict.Applied : counted ? SourceVerdict.AlreadyThere : SourceVerdict.Conflict;
    }

    /// <summary>The offsets of <paramref name="recorded"/> with each consumed source moved to the end of its range, in ordinal order of the names.</summary>
    internal static IReadOnlyList<SourceOffset> Advanced(
        IReadOnlyList<SourceOffset> recorded, IReadOnlyList<SourceRange> consumed, long atUnixMilliseconds)
    {
        SortedDictionary<string, SourceOffset> table = new SortedDictionary<string, SourceOffset>(StringComparer.Ordinal);
        foreach (SourceOffset source in recorded)
        {
            table[source.Source] = source;
        }

        foreach (SourceRange range in consumed)
        {
            table[range.Source] = new SourceOffset(range.Source, range.To, atUnixMilliseconds);
        }

        return [.. table.Values];
    }

    /// <summary>The offsets of <paramref name="recorded"/> without the sources <paramref name="forgotten"/> names.</summary>
    internal static IReadOnlyList<SourceOffset> Without(IReadOnlyList<SourceOffset> recorded, IReadOnlyCollection<string> forgotten) =>
        [.. recorded.Where(source => !forgotten.Contains(source.Source))];

    /// <summary>The offset <paramref name="recorded"/> counts <paramref name="source"/> up to; 0 for a source it does not name.</summary>
    internal static ulong OffsetOf(IReadOnlyList<SourceOffset> recorded, string source)
    {
        int low = 0;
        int high = recorded.Count - 1;
        while (low <= high)
        {
            int mid = (low + high) >>> 1;
            int sign = string.CompareOrdinal(recorded[mid].Source, source);
            if (sign == 0)
            {
                return recorded[mid].Offset;
            }

            if (sign < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return 0;
    }

    /// <summary>Refuses ranges a commit cannot declare: none at all, or two of one source.</summary>
    /// <exception cref="ArgumentException">The list is empty, or names a source twice.</exception>
    internal static void Check(IReadOnlyList<SourceRange> consumed, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(consumed, parameterName);
        if (consumed.Count == 0)
        {
            throw new ArgumentException("A commit that declares the ranges it consumed declares at least one.", parameterName);
        }

        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (SourceRange range in consumed)
        {
            if (range.Source is null)
            {
                throw new ArgumentException("A range names its source.", parameterName);
            }

            if (!seen.Add(range.Source))
            {
                throw new ArgumentException($"The ranges name the source '{range.Source}' twice: one range a source per commit.", parameterName);
            }
        }
    }

    /// <summary>The conflict of <paramref name="consumed"/> against the offsets <paramref name="recorded"/> of <paramref name="version"/>.</summary>
    internal static SourceOffsetConflictException Conflict(
        IReadOnlyList<SourceOffset> recorded, IReadOnlyList<SourceRange> consumed, ulong version, CommitObject? held, DateTimeOffset knownAt)
    {
        Dictionary<string, ulong> offsets = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (SourceRange range in consumed)
        {
            offsets[range.Source] = OffsetOf(recorded, range.Source);
        }

        string ranges = string.Join(", ", consumed.Select(range => string.Create(
            CultureInfo.InvariantCulture, $"'{range.Source}' ({range.From}, {range.To}] where version {version} counts it up to {offsets[range.Source]}")));
        return new SourceOffsetConflictException(
            $"The commit declared {ranges}: a range would leave a gap or overlap one counted. Work the ranges out again from the offsets recorded.")
        {
            Declared = consumed,
            Recorded = offsets,
            Version = version,
            Held = held,
            KnownAt = knownAt,
        };
    }
}
