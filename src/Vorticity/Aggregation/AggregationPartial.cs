using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity;

/// <summary>
/// The states of an aggregation's groups over a part of its rows: what a node that ran the part sends to the one that
/// merges, which folds the partials of every part to the answer the whole question gives, to the same bits
/// (<see cref="Aggregation.Merge(System.Collections.Generic.IEnumerable{AggregationPartial})"/>).
/// </summary>
/// <remarks>
/// <para>
/// A partial is a Vortex file of rows of states, a row a group: the group's keys, as the key's functions gave them, then the
/// rows it counts and each measure's exact states, its count of values, its sum's words, its least and its greatest,
/// and the sketches of its distributions and of its approximate distinct counts. Integer sums are exact in 128 bits and
/// float sums are exact accumulators, so that partials merged in any order give the bits the whole rows give.
/// </para>
/// <para>
/// Its bytes hold no question: the node that merges builds the same question, over the same version of the same source,
/// and merges the partials of its parts through it. A partial of another question, whose keys or measures differ, is
/// refused when the merge reads it.
/// </para>
/// </remarks>
public sealed class AggregationPartial
{
    /// <summary>
    /// <paramref name="bytes"/> copied once into an array of the partial's own, which the collector may move: the bytes of
    /// a part's file go back to the pool when the export returns, and those another node sent are the caller's. An open
    /// copies them into a block of the pool on the boundary its segments declare (<see cref="MemorySegmentSource"/>), which
    /// goes back when the merge closes the file. A copy pinned on that boundary at its own size, kept as long as the
    /// partial, mixed sizes into the pinned heap, which a process held to a hard limit counts: a merge of thousands of
    /// partials a question fragmented it.
    /// </summary>
    private AggregationPartial(ReadOnlyMemory<byte> bytes) => Bytes = bytes.ToArray();

    /// <summary>The partial as bytes, a Vortex file, to send to the node that merges.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>A partial from the bytes another node sent (<see cref="Bytes"/>), read when a merge reads it.</summary>
    /// <param name="bytes">The bytes of a partial.</param>
    /// <returns>The partial.</returns>
    public static AggregationPartial FromBytes(ReadOnlyMemory<byte> bytes) => new AggregationPartial(bytes);

    /// <summary>The partial over the bytes of the file a part wrote.</summary>
    internal static AggregationPartial Of(ReadOnlyMemory<byte> bytes) => new AggregationPartial(bytes);

    /// <summary>The partial's file, opened in <paramref name="session"/> over its bytes.</summary>
    internal ValueTask<VortexFile> OpenAsync(VortexSession session, CancellationToken cancellationToken) =>
        session.OpenAsync(new MemorySegmentSource(Bytes), VortexOpenOptions.Default, cancellationToken);
}
