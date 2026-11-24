using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Writing;

namespace Vorticity;

/// <summary>
/// A writer's parts: builders of their own, filled by the caller's threads side by side and written in the order they
/// were created (<see cref="VortexFileWriter.CreatePartAsync{TRecord}"/>); and records written through parts filled on the
/// session's lanes (<see cref="VortexFileWriter.WriteRecordsAsync{TRecord}"/>).
/// </summary>
/// <remarks>
/// A writer has one builder, filled on one thread: what the caller does to make its columns, reading its source and
/// turning records into columns, is done on that thread, while the writer's own work spreads over the lanes. That work cost
/// a seventh of a write of ten million rows on one lane, and bounded the write at four and a half times its speed on one
/// whatever the lanes (2026-11-17). Filled in parts, it runs on as many threads as there are parts.
/// </remarks>
public sealed partial class VortexFileWriter
{
    private readonly Lock _partsGate = new Lock();
    private Queue<WritePart>? _parts;
    private SemaphoreSlim? _partSlots;
    private SemaphoreSlim? _partTurn;

    /// <summary>
    /// A part of the file: a builder of its own the caller fills on any thread, whose rows the file takes after those of
    /// every part created before it, once it is written (<see cref="WritePart.WriteAsync"/>). Creating a part waits
    /// while twice the session's degree of parts wait to be written, so that a caller faster than the writer waits there.
    /// </summary>
    /// <typeparam name="TRecord">The record type.</typeparam>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The part.</returns>
    /// <remarks>
    /// The rows of a part go in the order of the parts' creation, whatever order they are written in, so that a file written
    /// in parts is the file a single builder fed the same rows in that order writes. A part disposed without being written
    /// gives its place up. A writer is written through its own builder or through parts, not both at once.
    /// </remarks>
    public async ValueTask<WritePart<TRecord>> CreatePartAsync<TRecord>(CancellationToken cancellationToken = default)
        where TRecord : IVortexRecord<TRecord>
    {
        ThrowIfDone();
        SemaphoreSlim slots;
        lock (_partsGate)
        {
            slots = _partSlots ??= new SemaphoreSlim(Math.Max(2, 2 * Session.Options.MaxDegreeOfParallelism));
            _partTurn ??= new SemaphoreSlim(1, 1);
        }

        // The parts settled at the queue's head, a part given up among them, taken before the wait for a place, which their
        // places are: a part given up drains nothing itself, and a caller that gave one up then made another waited on it.
        if (slots.CurrentCount == 0)
        {
            await TakePartAsync(cancellationToken).ConfigureAwait(false);
        }

        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        StructStore root;
        try
        {
            root = ColumnStores.Root(_schema, Account, Session.Options.Extensions);
        }
        catch
        {
            slots.Release();
            throw;
        }

        WritePart<TRecord> part = new WritePart<TRecord>(this, root, new ColumnsBuilder<TRecord>(root, WriteBinding.Map(typeof(TRecord), TRecord.Schema, root.Type.FieldArray), owner: null));
        lock (_partsGate)
        {
            (_parts ??= new Queue<WritePart>()).Enqueue(part);
        }

        return part;
    }

    /// <summary>
    /// Writes <paramref name="records"/>: turned into columns a part of whole blocks at a time on the session's lanes, and the
    /// parts written in the records' order. The file is the one <c>WriteAsync(records.Span)</c> writes.
    /// </summary>
    /// <typeparam name="TRecord">The record type.</typeparam>
    /// <param name="records">The records, read before this returns.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>A task that completes when every record is taken.</returns>
    public async ValueTask WriteRecordsAsync<TRecord>(ReadOnlyMemory<TRecord> records, CancellationToken cancellationToken = default)
        where TRecord : IVortexRecord<TRecord>
    {
        ThrowIfDone();
        int degree = Math.Max(1, Session.Options.MaxDegreeOfParallelism);
        int block = _blockRows > 0 ? _blockRows : VortexWriteOptions.Default.BlockRows;

        // Whole blocks a part, as many parts as lanes at least for a long array, a block at least for a short one.
        int per = Math.Max(block, (int)Math.Min(int.MaxValue, (records.Length / Math.Max(1, degree * 2) + block - 1) / block * block));
        if (degree == 1 || records.Length <= per)
        {
            await WriteAsync(records.Span, cancellationToken).ConfigureAwait(false);
            return;
        }

        int count = (records.Length + per - 1) / per;
        WritePart<TRecord>[] parts = new WritePart<TRecord>[count];
        for (int i = 0; i < count; i++)
        {
            parts[i] = await CreatePartAsync<TRecord>(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // The caller writes a part, and each lane of the session's free now another, the parts taken in their order.
            await Execution.LaneWorkers.ForAsync(
                Session.Lanes,
                count,
                degree,
                async (i, token) =>
                {
                    int start = i * per;
                    ReadOnlyMemory<TRecord> slice = records.Slice(start, Math.Min(per, records.Length - start));
                    TRecord.WriteRows(parts[i].Builder, slice.Span);
                    await parts[i].WriteAsync(token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (WritePart<TRecord> part in parts)
            {
                part.Dispose();
            }
        }
    }

    /// <summary>
    /// Takes the rows of every part at the queue's head settled already, in their order: a part just written, the parts
    /// after it written before it, and past a part given up the ones after it. Called by a part's write, and by the file's
    /// completion, which awaits it.
    /// </summary>
    internal async ValueTask TakePartAsync(CancellationToken cancellationToken)
    {
        SemaphoreSlim turn = _partTurn!;
        await turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                WritePart? head;
                lock (_partsGate)
                {
                    head = _parts is { Count: > 0 } queue && queue.Peek().Settled ? queue.Dequeue() : null;
                }

                if (head is null)
                {
                    return;
                }

                try
                {
                    if (head.Written)
                    {
                        await WritePartRowsAsync(head.Root, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    head.Root.Release();
                    _partSlots!.Release();
                }
            }
        }
        finally
        {
            turn.Release();
        }
    }

    /// <summary>The rows of a part's store, as one batch of the file's, over the store's own buffers.</summary>
    private async ValueTask WritePartRowsAsync(StructStore root, CancellationToken cancellationToken)
    {
        if (root.Inconsistency() is { } why)
        {
            throw new VortexSchemaException($"The part cannot be written: {why}. Complete every row before writing it.");
        }

        root.Commit();
        int rows = root.Committed;
        if (rows == 0)
        {
            return;
        }

        Carry();
        _acceptedRows += rows;
        CanonicalArena arena = BuilderArena();
        try
        {
            await WriteCoreAsync(arena, root.BuildRoot(arena, rows), seal: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            arena.Reset();
        }
    }

    /// <summary>Throws when a part is created and neither written nor disposed: the file would end without its rows.</summary>
    private void RequireNoPartPending()
    {
        lock (_partsGate)
        {
            if (_parts is { Count: > 0 })
            {
                throw new InvalidOperationException("A part of the file is neither written nor disposed: write it, or dispose it, before the file is completed.");
            }
        }
    }
}

/// <summary>A part of a file being written: its builder, and whether it was written or given up.</summary>
public abstract class WritePart : IDisposable
{
    private readonly VortexFileWriter _writer;
    private int _state;

    private protected WritePart(VortexFileWriter writer, StructStore root)
    {
        _writer = writer;
        Root = root;
    }

    internal StructStore Root { get; }

    /// <summary>Whether the part was written or given up, which the file no longer waits for.</summary>
    internal bool Settled => Volatile.Read(ref _state) != 0;

    /// <summary>Whether the part was written: its rows go in the file.</summary>
    internal bool Written => Volatile.Read(ref _state) == 1;

    /// <summary>
    /// Hands the part's rows to the file, which takes them after those of every part created before it: at once when every
    /// one of those is written already, or when the last of them is.
    /// </summary>
    /// <param name="cancellationToken">Cancels the writes this call makes.</param>
    /// <returns>A task that completes when the rows are handed over.</returns>
    /// <exception cref="InvalidOperationException">The part was written or disposed already.</exception>
    public ValueTask WriteAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            throw new InvalidOperationException("A part is written once.");
        }

        return _writer.TakePartAsync(cancellationToken);
    }

    /// <summary>
    /// Gives the part up when it was not written: the file takes the parts after it without it, at the next part written or
    /// at the file's completion, which await the writes they make.
    /// </summary>
    public void Dispose() =>
        // Settled given up, and nothing more: a drain started here was a task nobody awaited, whose failure was lost and
        // which wrote beside the file's completion.
        Interlocked.CompareExchange(ref _state, 2, 0);
}

/// <summary>A part of a file being written, whose builder takes records of <typeparamref name="TRecord"/>.</summary>
/// <typeparam name="TRecord">The record type.</typeparam>
public sealed class WritePart<TRecord> : WritePart
    where TRecord : IVortexRecord<TRecord>
{
    internal WritePart(VortexFileWriter writer, StructStore root, ColumnsBuilder<TRecord> builder)
        : base(writer, root) => Builder = builder;

    /// <summary>The part's builder, filled by one thread at a time, any thread.</summary>
    public ColumnsBuilder<TRecord> Builder { get; }
}
