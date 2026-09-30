using System;
using System.Collections.Generic;

namespace Vorticity.Dataset;

/// <summary>What a commit intends. A writer keeps its intentions rather than a finished tree, so a
/// rebase re-applies them to the winner's trees instead of merging two trees.</summary>
internal abstract record DatasetOperation
{
    /// <summary>The level it acts on; 0, where appends land, unless it says otherwise. A compaction
    /// ignores this and says a level per input and per output instead.</summary>
    public int Level { get; init; }

    /// <summary>Adds one data object to the dataset.</summary>
    /// <remarks>
    /// The key must be a function of the object alone: unique, or the add silently replaces another
    /// writer's object, and unchanged across re-applications, or a rebase adds the object twice.
    /// Ending the key with the object's uid satisfies both.
    /// </remarks>
    public sealed record AddObject(ReadOnlyMemory<byte> Key, ObjectEntry Entry) : DatasetOperation;

    /// <summary>Replaces objects by the outputs of a compaction.</summary>
    /// <param name="Inputs">The objects consumed, each with the level it sits in.</param>
    /// <param name="Outputs">The objects produced, each with the level it goes to.</param>
    /// <remarks>
    /// The levels are carried per input because a leveled compaction reads two of them, and because
    /// a missing input has to abandon the whole compaction at once: one operation per level could
    /// abandon separately and leave the outputs as garbage.
    /// </remarks>
    public sealed record ReplaceObjects(
        IReadOnlyList<(int Level, ReadOnlyMemory<byte> Key)> Inputs,
        IReadOnlyList<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> Outputs) : DatasetOperation
    {
        /// <summary>
        /// The entries the inputs were read as, in the inputs' order, or null to ask only that each is
        /// still there. An input whose entry now names other rows -- another file, or rows a
        /// concurrent delete marked in it -- abandons the whole replacement, since its outputs were
        /// worked out from rows the input no longer holds.
        /// </summary>
        public IReadOnlyList<ObjectEntry>? Expected { get; init; }
    }

    /// <summary>Attaches an index fragment to an object.</summary>
    /// <param name="Key">The object's sort key.</param>
    /// <param name="Uid">The identity the fragment was built against.</param>
    /// <param name="Fragment">
    /// The fragment's bytes, not a reference: a reference names the version that wrote the fragment,
    /// and only the commit that finally lands knows which version that is.
    /// </param>
    public sealed record AddFragment(ReadOnlyMemory<byte> Key, UInt128 Uid, ReadOnlyMemory<byte> Fragment) : DatasetOperation;

    /// <summary>Drops an index fragment from an object.</summary>
    /// <param name="Key">The object's sort key.</param>
    /// <param name="Fragment">
    /// The fragment to drop, matched by its content — length and hash — wherever it lies
    /// (<see cref="ObjectEntry.Holds(PageReference)"/>).
    /// </param>
    public sealed record DropFragment(ReadOnlyMemory<byte> Key, PageReference Fragment) : DatasetOperation;

    /// <summary>
    /// Moves every page and fragment the version still references in these commit objects into
    /// the new one, so that vacuum can delete them once they leave the window.
    /// </summary>
    /// <param name="Versions">The commit objects to empty; vacuum names them in
    /// <see cref="VacuumResult.Sparse"/>.</param>
    /// <remarks>
    /// Metadata only: no row is read or written, and no entry changes what it says, only where its
    /// pages lie. Moving pages alone leaves the tree's content hash as it was; moving a fragment
    /// changes the entry that names it, and with it the hash.
    /// </remarks>
    public sealed record Repack(IReadOnlyList<ulong> Versions) : DatasetOperation;

    /// <summary>
    /// Changes the dataset's schema, from the one the change was worked out against: a rebase onto
    /// a version whose schema is another does not apply it, since what it checked of the columns
    /// no longer holds.
    /// </summary>
    /// <param name="From">The schema the change was checked against, as a header records it.</param>
    /// <param name="To">The schema the dataset takes.</param>
    /// <param name="Retired">The names retired once it has, the earlier ones included.</param>
    public sealed record ChangeSchema(ReadOnlyMemory<byte> From, ReadOnlyMemory<byte> To, IReadOnlyList<RetiredColumn> Retired) : DatasetOperation;
}

/// <summary>What a commit made of one change, once re-applied to the version it landed on.</summary>
public enum OperationOutcome
{
    /// <summary>It changed the tree.</summary>
    Applied = 0,

    /// <summary>The winner had already done it; nothing was written for it.</summary>
    AlreadyThere = 1,

    /// <summary>The ground moved and the operation has stopped applying; it is dropped.</summary>
    Dropped = 2,

    /// <summary>An input is gone, so the whole operation is abandoned.</summary>
    Abandoned = 3,
}
