using System.Threading.Tasks;

namespace Vorticity.Writing;

/// <summary>
/// What the work a writer is part of tells it of the work's memory, at the points where the writer asks: a compaction
/// job's (its memory deals the writer a share of the jobs' budget). A writer that is part of no such work has none, and
/// goes as fast as its lanes take it.
/// </summary>
/// <remarks>
/// Nothing it answers changes what the writer writes: the writer waits, starts fewer things at once or gives back the
/// blocks it keeps idle, and the bytes of its file are the same.
/// </remarks>
internal abstract class WriteRoom
{
    /// <summary>
    /// Whether the work admits <paramref name="bytes"/> more of the writer's past what the writer holds: a block in flight
    /// past the one that must go, a chunk more in emission, a leaf more laid out. Counted by the work when it does not.
    /// </summary>
    internal abstract bool Admits(long bytes);

    /// <summary>
    /// Whether the work holds what the writer holds now. While it does, the writer's arenas keep their blocks from one
    /// use to the next, which spares them the pool and the budget each time; once it does not, each gives its blocks back
    /// where it kept them, until it does again.
    /// </summary>
    internal abstract bool Holds { get; }

    /// <summary>
    /// Has the work give back what it started ahead of the writer, on the flow that drives both: what a start of the
    /// writer asks once the writer waited for everything it had in the air and the work still does not admit it.
    /// </summary>
    internal virtual ValueTask MakeRoomAsync() => ValueTask.CompletedTask;
}
