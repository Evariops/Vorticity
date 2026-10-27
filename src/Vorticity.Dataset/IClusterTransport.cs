using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;

namespace Vorticity.Dataset;

/// <summary>
/// A part of a question a cluster's coordinator sends a node (spec F, 3): the question's name and its arguments
/// (<see cref="ClusterQuestions"/>), the dataset's location, which the node resolves in its session, the version every
/// node reads, and the part's rows.
/// </summary>
/// <param name="Question">The question's name.</param>
/// <param name="Arguments">Its arguments, as the application codes them.</param>
/// <param name="Location">Where the dataset lies, as the node's session resolves it.</param>
/// <param name="Version">The version of the dataset every part reads.</param>
/// <param name="Rows">The part's rows.</param>
public sealed record ClusterTask(string Question, ReadOnlyMemory<byte> Arguments, string Location, ulong Version, RowRange Rows);

/// <summary>
/// How a cluster's coordinator reaches its nodes (spec F, 6): it runs a part on a node and has a node warm what it owns. The
/// application gives one: <see cref="LocalClusterTransport"/> in one process, a network transport between processes.
/// </summary>
public interface IClusterTransport
{
    /// <summary>Runs <paramref name="task"/> on <paramref name="node"/>, and gives the partial of its part.</summary>
    /// <exception cref="ClusterTransportException">The node is not reached, or went away: the part is run elsewhere.</exception>
    ValueTask<AggregationPartial> RunAsync(string node, ClusterTask task, CancellationToken cancellationToken);

    /// <summary>
    /// Has <paramref name="node"/> read the metadata of the objects it owns among <paramref name="nodes"/> in the version
    /// <paramref name="version"/> of the dataset at <paramref name="location"/> (spec F, 7); nothing by default.
    /// </summary>
    ValueTask WarmAsync(string node, string location, ulong version, IReadOnlyList<string> nodes, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

/// <summary>
/// A part a node failed for a reason of its own, the question's error rather than the transport's, as a network transport
/// tells it: the part is not run elsewhere, and the merge throws it.
/// </summary>
public sealed class ClusterPartException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ClusterPartException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public ClusterPartException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and the failure behind it.</summary>
    public ClusterPartException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The node that failed the part.</summary>
    public string? Node { get; init; }

    /// <summary>The type of the node's error, by its full name.</summary>
    public string? ErrorType { get; init; }
}

/// <summary>A node a transport did not reach, or that went away: its part is run on the next node of its owners.</summary>
public sealed class ClusterTransportException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ClusterTransportException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public ClusterTransportException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and the failure behind it.</summary>
    public ClusterTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
