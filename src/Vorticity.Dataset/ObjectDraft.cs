using System;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>
/// A data object being written for a dataset: a core writer over a fresh key, which the dataset
/// completes, puts in the store and commits when it is handed to
/// <see cref="VortexDataset.AppendAsync(ObjectDraft, System.Threading.CancellationToken)"/> or
/// <see cref="VortexDataset.ReplaceAsync"/>.
/// </summary>
/// <remarks>
/// Write the rows through <see cref="Writer"/> and leave its completion to the dataset. Disposing a
/// draft that was never committed abandons it: nothing reaches the store.
/// </remarks>
public sealed class ObjectDraft : IAsyncDisposable
{
    private bool _taken;

    internal ObjectDraft(VortexDataset owner, Guid identity, string key, ObjectSegmentSink sink, VortexFileWriter writer)
    {
        Owner = owner;
        Identity = identity;
        Key = key;
        Sink = sink;
        Writer = writer;
    }

    /// <summary>The key the object will have in the store.</summary>
    public string Key { get; }

    /// <summary>The writer the rows go through, with the dataset's schema and write options.</summary>
    public VortexFileWriter Writer { get; }

    /// <summary>The dataset the object is written for.</summary>
    internal VortexDataset Owner { get; }

    /// <summary>The identity minted for the object before a byte of it was written.</summary>
    internal Guid Identity { get; }

    /// <summary>Where the bytes wait until the object is put.</summary>
    internal ObjectSegmentSink Sink { get; }

    /// <summary>
    /// Whether the object is written with no run on the clustering key, its rows coming in key order:
    /// its seal checks that the statistics say so, since a key cursor then walks the column itself.
    /// </summary>
    internal bool BySortedColumn { get; init; }

    /// <summary>Abandons the object unless a commit took it.</summary>
    /// <returns>A task that completes when the writer's buffers are released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_taken)
        {
            return;
        }

        _taken = true;
        Writer.Abandon();
        await Writer.DisposeAsync().ConfigureAwait(false);
        Sink.Discard();
    }

    /// <summary>Marks the draft as taken by a commit, once.</summary>
    /// <exception cref="InvalidOperationException">It was already committed or disposed.</exception>
    internal void Take()
    {
        if (_taken)
        {
            throw new InvalidOperationException($"The draft of '{Key}' was already committed or disposed.");
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
        DeletedRows = held.Entry.Deletions.Count,
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
