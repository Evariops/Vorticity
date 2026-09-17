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
    /// <summary>Adds one data object to the dataset.</summary>
    /// <param name="Key">Its sort key in the tree.</param>
    /// <param name="Entry">What the leaf entry will say.</param>
    public sealed record AddObject(ReadOnlyMemory<byte> Key, ObjectEntry Entry) : DatasetOperation;

    /// <summary>Replaces objects by the outputs of a compaction (§5.3).</summary>
    /// <param name="Inputs">The keys of the objects consumed.</param>
    /// <param name="Outputs">The objects produced, with their keys.</param>
    public sealed record ReplaceObjects(
        IReadOnlyList<ReadOnlyMemory<byte>> Inputs,
        IReadOnlyList<(ReadOnlyMemory<byte> Key, ObjectEntry Entry)> Outputs) : DatasetOperation;

    /// <summary>Attaches an index fragment to an object (§6.4).</summary>
    /// <param name="Key">The object's sort key.</param>
    /// <param name="Uid">The identity the fragment was built against (§7).</param>
    /// <param name="Fragment">Where the fragment lies.</param>
    public sealed record AddFragment(ReadOnlyMemory<byte> Key, UInt128 Uid, PageReference Fragment) : DatasetOperation;

    /// <summary>Drops an index fragment from an object.</summary>
    /// <param name="Key">The object's sort key.</param>
    /// <param name="Fragment">The fragment to drop.</param>
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
