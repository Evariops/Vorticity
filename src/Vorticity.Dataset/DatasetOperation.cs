// What a commit says it did - docs/13-dataset.md §8.2: "the writer reads it and re-applies its
// LOGICAL OPERATIONS to that version's trees: add objects, add fragments, replace objects by
// compaction outputs, drop fragments. It never merges two trees."
//
// THIS FILE IS WHY A REBASE IS CHEAP AND CORRECT. A writer that had produced a TREE would have to
// merge two trees, which is either wrong (whose page wins?) or expensive (walk both). A writer that
// kept its INTENTIONS re-applies them to whatever the winner left, and each intention knows what to
// do when the ground moved under it -- which is what the seven rows of §8.2's matrix are. So the
// re-application rules live here, next to the operations, rather than in the committer: every row
// of that matrix is a branch you can point at.
using System;
using System.Collections.Generic;

namespace Vorticity.Dataset;

/// <summary>What a commit intends. Re-applied as is after a rebase (§8.2).</summary>
public abstract record DatasetOperation
{
    /// <summary>The level it acts on (§5.2); 0, where appends land, unless it says otherwise.</summary>
    /// <remarks>
    /// On the operation rather than in the committer, because a rebase re-applies the operation to
    /// a version whose levels have moved: an indexer's fragment belongs to the object it was built
    /// against, and which level that object sits in is part of naming it. A compaction says it per
    /// input and per output instead, since reading one level and writing another is what it is.
    /// </remarks>
    public int Level { get; init; }

    /// <summary>Adds one data object to the dataset.</summary>
    /// <param name="Key">Its sort key in the tree.</param>
    /// <param name="Entry">What the leaf entry will say.</param>
    /// <remarks>
    /// THE KEY MUST BE A FUNCTION OF THE OBJECT, and both halves of that matter. It must be UNIQUE
    /// to the object, because a tree's keys are unique and an add at a key another object already
    /// holds does not add, it REPLACES — a writer whose key came from a version another writer has
    /// already moved past would delete that writer's object and nothing would say so. And it must
    /// be the SAME key every time this operation is re-applied, because §8.2's rebase re-applies it
    /// and a store that crashed after its put will have the object under the first key it chose: a
    /// key recomputed from the winner's state would come out different and add the object twice.
    /// <c>VortexDataset</c> satisfies both by ending the key with the object's <c>uid</c>.
    /// </remarks>
    public sealed record AddObject(ReadOnlyMemory<byte> Key, ObjectEntry Entry) : DatasetOperation;

    /// <summary>Replaces objects by the outputs of a compaction (§5.3).</summary>
    /// <param name="Inputs">The objects consumed, each with the level it sits in.</param>
    /// <param name="Outputs">The objects produced, each with the level it goes to.</param>
    /// <remarks>
    /// THE LEVELS ARE PER INPUT because a leveled compaction reads two of them: the objects of
    /// level <c>i</c> and the objects of level <c>i + 1</c> whose key ranges they overlap (§5.2).
    /// Splitting that into one operation per level would split §8.2's row 5 with it — "an input is
    /// missing: the outputs are garbage" has to abandon the whole compaction, and two operations
    /// can abandon separately.
    /// </remarks>
    public sealed record ReplaceObjects(
        IReadOnlyList<(int Level, ReadOnlyMemory<byte> Key)> Inputs,
        IReadOnlyList<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> Outputs) : DatasetOperation;

    /// <summary>Attaches an index fragment to an object (§6.4).</summary>
    /// <param name="Key">The object's sort key.</param>
    /// <param name="Uid">The identity the fragment was built against (§7).</param>
    /// <param name="Fragment">
    /// The fragment's bytes, which the commit that applies this writes into its own commit object.
    /// </param>
    /// <remarks>
    /// THE BYTES, NOT A REFERENCE, and a rebase is why. A reference names the version that wrote the
    /// fragment, and which version that is depends on how many writers won before this one: the
    /// reference is minted by the commit that finally lands, where the fragment is actually written.
    /// </remarks>
    public sealed record AddFragment(ReadOnlyMemory<byte> Key, UInt128 Uid, ReadOnlyMemory<byte> Fragment) : DatasetOperation;

    /// <summary>Drops an index fragment from an object.</summary>
    /// <param name="Key">The object's sort key.</param>
    /// <param name="Fragment">
    /// The fragment to drop, matched by its content — length and hash — wherever it lies
    /// (<see cref="ObjectEntry.Holds(PageReference)"/>).
    /// </param>
    public sealed record DropFragment(ReadOnlyMemory<byte> Key, PageReference Fragment) : DatasetOperation;
}

/// <summary>What re-applying one operation decided, for `Explain` and for the tests.</summary>
public enum OperationOutcome
{
    /// <summary>It changed the tree.</summary>
    Applied = 0,

    /// <summary>The winner had already done it; nothing was written for it (§8.2, rows 3 and 4).</summary>
    AlreadyThere = 1,

    /// <summary>The ground moved and the operation no longer applies; it is dropped.</summary>
    Dropped = 2,

    /// <summary>An input is gone, so the whole operation is abandoned (§8.2, row 5).</summary>
    Abandoned = 3,
}
