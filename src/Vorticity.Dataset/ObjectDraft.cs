using System;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>
/// Rows being written for a dataset, which it sorts by its key into objects, one a partition, and commits when the draft is
/// handed to <see cref="VortexDataset.AppendAsync(ObjectDraft, System.Threading.CancellationToken)"/> or
/// <see cref="VortexDataset.ReplaceObjectsAsync(System.Collections.Generic.IReadOnlyList{DataObject}, System.Collections.Generic.IReadOnlyList{ObjectDraft}, System.Threading.CancellationToken)"/>.
/// </summary>
/// <remarks>
/// Write the rows through <see cref="Writer"/> and leave the objects to the dataset. Disposing a draft that was never
/// committed abandons it: the objects it wrote already are left for vacuum, and nothing else reaches the store.
/// </remarks>
public sealed class ObjectDraft : IAsyncDisposable
{
    private bool _taken;

    internal ObjectDraft(VortexDataset owner, DatasetWriter writer)
    {
        Owner = owner;
        Writer = writer;
    }

    /// <summary>The writer the rows go through, with the dataset's schema: held by partition and written sorted.</summary>
    public DatasetWriter Writer { get; }

    /// <summary>The dataset the rows are written for.</summary>
    internal VortexDataset Owner { get; }

    /// <summary>Abandons the rows unless a commit took them.</summary>
    /// <returns>A task that completes when the writer's buffers are released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_taken)
        {
            return;
        }

        _taken = true;
        await Writer.AbandonAsync().ConfigureAwait(false);
    }

    /// <summary>Marks the draft as taken by a commit, once.</summary>
    /// <exception cref="InvalidOperationException">It was already committed or disposed.</exception>
    internal void Take()
    {
        if (_taken)
        {
            throw new InvalidOperationException("The draft was already committed or disposed.");
        }

        _taken = true;
    }
}

/// <summary>One data object of a version, as the dataset's walk finds it: what a caller removes or replaces.</summary>
public sealed record DataObject
{
    internal DataObject()
    {
    }

    /// <summary>Its key in the store.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>The level whose tree holds it; level 0 is the one appends land in.</summary>
    public int Level { get; init; }

    /// <summary>Its first row among the version's, in the order a scan delivers them.</summary>
    public long FirstRow { get; init; }

    /// <summary>Its rows, those a delete took out of it not counted.</summary>
    public long Rows { get; init; }

    /// <summary>
    /// The rows a delete took out of it without rewriting it: no read returns them, and its file
    /// holds them until a compaction, or a delete that finds too many, rewrites it without them.
    /// </summary>
    public long DeletedRows { get; init; }

    /// <summary>Its bytes in the store.</summary>
    public long Bytes { get; init; }

    /// <summary>Its key in the level's tree, in hexadecimal: what a removal names it by.</summary>
    internal string TreeKey { get; init; } = string.Empty;

    /// <summary>Its entry as the walk read it: what a replacement checks it still is.</summary>
    internal ObjectEntry? Entry { get; init; }

    /// <summary>The object as a caller sees it.</summary>
    internal static DataObject Of(in PositionedObject held) => new DataObject
    {
        Key = held.Entry.Key,
        Level = held.Level,
        FirstRow = held.FirstRow,
        Rows = held.Entry.Rows,
        DeletedRows = held.Entry.DeletedRows,
        Bytes = held.Entry.Bytes,
        TreeKey = Convert.ToHexString(held.TreeKey.Span),
        Entry = held.Entry,
    };
}

/// <summary>What a replacement or a removal did.</summary>
public sealed record ReplaceResult
{
    /// <summary>The version the commit created; the one the handle already held when there was nothing to commit, and the latest one when the commit was abandoned.</summary>
    public ulong Version { get; init; }

    /// <summary>
    /// <see cref="OperationOutcome.Applied"/>; <see cref="OperationOutcome.Abandoned"/> when an
    /// object to remove was already gone from the version the commit landed on, in which case
    /// nothing changed and the objects written for it are left for vacuum; or
    /// <see cref="OperationOutcome.AlreadyThere"/> when there was nothing to remove nor to add.
    /// </summary>
    public OperationOutcome Outcome { get; init; }
}
