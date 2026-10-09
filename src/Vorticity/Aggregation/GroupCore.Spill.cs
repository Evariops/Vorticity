using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>
/// The spill of the core: when the governor has nothing left to give back,
/// the part holding the most groups is taken out under its lock, the part starting again empty, and its
/// groups are written to the query's scratch as a run, a record and a key each, the entries of its
/// batches. At the end a spilled part's batches pending go to the scratch as one more run, and the
/// part's groups come back one part at a time as it is delivered: its runs applied into its sub-tables
/// again, which merges the groups the runs share, then given back.
/// </summary>
/// <remarks>
/// A part is a 256th of the key space: one held in memory again is that share of the groups, and a read
/// page. The runs are not sorted: a part comes back whole, by its sub-tables, never merged as a stream.
/// </remarks>
internal sealed partial class GroupCore
{
    private readonly SemaphoreSlim _spilling = new SemaphoreSlim(1, 1);
    private RunScratch? _scratch;
    private string? _scratchDirectory;
    private byte[]? _page;
    private ulong[]? _words;
    private int _spilledParts;
    private long _spilledBytes;

    /// <summary>The parts the core wrote to its scratch.</summary>
    internal int SpilledParts => Volatile.Read(ref _spilledParts);

    /// <summary>The bytes it wrote there.</summary>
    internal long SpilledBytes => Interlocked.Read(ref _spilledBytes);

    /// <summary>Whether the core spills when its budget holds no more: under the governor, the plan not forbidding it.</summary>
    internal bool Spills => _memory is not null && _plan.CoreSpills;

    /// <summary>
    /// Whether a part may go to the scratch: the core spills, and a part holds groups. A stack its
    /// budget cannot take then waits, and its lane spills the largest part before its next batch.
    /// </summary>
    internal bool CanSpill => Spills && Largest() is not null;

    /// <summary>
    /// The parts holding the most, their groups and their entries pending, written to the scratch, their
    /// sub-tables and batches given back, the largest first, until the query holds three quarters
    /// of its budget's ceiling at most: the lanes' deposits go on into the parts, empty again. One spill
    /// at a time.
    /// </summary>
    internal async ValueTask SpillAsync(CancellationToken cancellationToken)
    {
        await _spilling.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            QueryMemory memory = _memory!;
            do
            {
                if (Largest() is not { } part)
                {
                    break;
                }

                await EvictAsync(part, cancellationToken).ConfigureAwait(false);
                await WritePendingAsync(part, cancellationToken).ConfigureAwait(false);
            }
            while (memory.Held > memory.Ceiling / 4 * 3);
        }
        finally
        {
            _spilling.Release();
        }
    }

    /// <summary>The part whose sub-tables and pending entries hold the most, as their last application and deposits left them; null when none holds any.</summary>
    private CorePart? Largest()
    {
        CorePart? largest = null;
        long most = 0;
        foreach (CorePart part in _parts)
        {
            long held = Volatile.Read(ref part.Groups) + Volatile.Read(ref part.Pending.Value);
            if (held > most)
            {
                most = held;
                largest = part;
            }
        }

        return largest;
    }

    /// <summary>
    /// The part's sub-tables taken under its lock, the part empty again, then their groups written as a
    /// run and their arrays let go. The lock is tried, never waited on: a burst holds it a while.
    /// </summary>
    private async ValueTask EvictAsync(CorePart part, CancellationToken cancellationToken)
    {
        while (!part.Gate.TryEnter())
        {
            await Task.Yield();
        }

        SubTable[] taken;
        bool emitted;
        try
        {
            taken = [.. part.Tables];
            part.Tables.Clear();
            part.Directory = [0];
            part.Depth = 0;
            Volatile.Write(ref part.Groups, 0);

            // Its keys written, a key new to its sub-tables may be one of them; and those it made
            // since a first eviction were told to no one.
            emitted = !part.Silent;
            part.Silent = true;
        }
        finally
        {
            part.Gate.Exit();
        }

        // From the first spill on, what the core gives back leaves the query's count rather than wait on
        // the shelf's piles, counted: a spill is there to give memory back.
        _shelf.Drops = true;
        long start = -1;
        long entries = 0;
        try
        {
            foreach (SubTable table in taken)
            {
                // A paged part's values no entry met are no groups to write (SubTable.CopyEntries).
                table.DropUnmet();
                int from = 0;
                while (from < table.Keys.Count)
                {
                    from = table.CopyEntries(Shape, from, MemoryMarshal.Cast<byte, ulong>(Page().AsSpan()), out int copied);
                    if (copied > 0)
                    {
                        long at = await AppendAsync(copied, cancellationToken).ConfigureAwait(false);
                        start = start < 0 ? at : start;
                        entries += copied;
                    }
                }
            }
        }
        finally
        {
            foreach (SubTable table in taken)
            {
                table.Release();
            }
        }

        Ran(part, start, entries, emitted);
    }

    /// <summary>
    /// At the end, a spilled part's stack written as one more run, entries as they are, rather than
    /// applied into sub-tables its budget may not hold: the part comes back whole when it is delivered.
    /// </summary>
    private async ValueTask WritePendingAsync(CorePart part, CancellationToken cancellationToken)
    {
        // Entries written as they came: none told to the emitter, nor any the part makes after.
        part.Silent = true;
        PartBatch? stack = Interlocked.Exchange(ref part.Head.Value, null);
        long start = -1;
        long entries = 0;
        int words = Shape.Words;
        PartBatch? kept = null;
        PartBatch? last = null;
        int keptCount = 0;
        PartBatch? batch = stack;
        while (batch is not null)
        {
            PartBatch? next = batch.Next;
            int at = 0;
            while (at < batch.Count)
            {
                int copied = Math.Min(batch.Count - at, Page().Length / (words * sizeof(ulong)));
                MemoryMarshal.AsBytes(batch.Words.AsSpan(batch.Start + (at * words), copied * words)).CopyTo(Page());
                long offset = await AppendAsync(copied, cancellationToken).ConfigureAwait(false);
                start = start < 0 ? offset : start;
                entries += copied;
                at += copied;
            }

            Interlocked.Add(ref part.Pending.Value, -batch.Count);
            Pend(-batch.Count);
            if (batch.Alone)
            {
                Drop(batch);
            }
            else
            {
                // A batch the lanes fill again, written: back to any lane, rather than a slab more.
                batch.Next = kept;
                kept = batch;
                last ??= batch;
                keptCount++;
            }

            batch = next;
        }

        if (kept is not null)
        {
            GiveShared(kept, last!, keptCount);
        }

        if (entries > 0)
        {
            Ran(part, start, entries, emitted: false);
        }
    }

    /// <summary>A run of <paramref name="entries"/> entries from <paramref name="start"/> recorded on its part, their keys <paramref name="emitted"/> or not.</summary>
    private void Ran(CorePart part, long start, long entries, bool emitted)
    {
        if (entries == 0)
        {
            return;
        }

        (part.Runs ??= []).Add(new PartRun(start, entries, emitted));
        if (part.Runs.Count == 1)
        {
            Interlocked.Increment(ref _spilledParts);
        }
    }

    /// <summary>
    /// The first <paramref name="entries"/> entries of the page appended to the scratch: past a tenth of
    /// the free space the process's spills leave, the query fails instead.
    /// </summary>
    /// <returns>Where they start in the scratch.</returns>
    private async ValueTask<long> AppendAsync(int entries, CancellationToken cancellationToken)
    {
        RunScratch scratch = Scratch();
        int bytes = entries * Shape.Words * sizeof(ulong);
        if (!SpillSpace.TryClaim(_scratchDirectory!, bytes))
        {
            throw _memory!.Exceeded("spill of a group by", -1, bytes);
        }

        // Under the host's scratch budget, every byte written counted, and given back when the scratch closes.
        if (_source?.Session.Options.ScratchBudget is { } budget && !budget.TryReserve(bytes))
        {
            SpillSpace.Release(bytes);
            throw ScratchExceeded(budget, "spill of a group by", bytes);
        }

        Interlocked.Add(ref _spilledBytes, bytes);
        return await scratch.AppendAsync(Page().AsMemory(0, bytes), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The scratch, made at the first spill in the session's directory, never one in memory.</summary>
    private RunScratch Scratch()
    {
        if (_scratch is not null)
        {
            return _scratch;
        }

        string directory = _source?.Session.Options.ScratchDirectory ?? Path.GetTempPath();
        if (InMemory(directory))
        {
            throw new VortexMemoryException(
                $"The group by needs to spill, and its scratch directory {directory} lies on a file system in memory, which would take the memory spilling gives back. Give its session a ScratchDirectory on a disk.");
        }

        _scratchDirectory = directory;
        _scratch = new RunScratch(memoryBudget: 0, directory);
        return _scratch;
    }

    /// <summary>
    /// The page the core writes and reads its scratch by, held past the budget's ceiling once: a
    /// sixteenth of the ceiling, between 64 KiB and a megabyte.
    /// </summary>
    private byte[] Page()
    {
        if (_page is null)
        {
            int bytes = PageBytes();
            _memory?.Force(bytes);
            _page = new byte[bytes];
        }

        return _page;
    }

    private int PageBytes() => (int)Math.Clamp((_memory?.Ceiling ?? RunScratch.PageBytes) / 16, 64 * 1024, RunScratch.PageBytes);

    /// <summary>
    /// A spilled part's groups in memory again: every run read back a page at a time and applied into
    /// the part's sub-tables, which merges the groups the runs share; the runs dropped.
    /// </summary>
    internal async ValueTask MaterializeAsync(CorePart part, CancellationToken cancellationToken)
    {
        if (part.Runs is not { } runs)
        {
            return;
        }

        // Each part brought back gives its arrays back once delivered: they leave the query's count, the
        // next part under its budget alone.
        _shelf.Drops = true;
        CoreApplier applier = new CoreApplier(this, lane: null);
        int entryBytes = Shape.Words * sizeof(ulong);
        int perPage = Page().Length / entryBytes;
        if (_words is null)
        {
            _memory?.Force(Page().Length);
            _words = new ulong[Page().Length / sizeof(ulong)];
        }

        // The sub-tables the part made in memory since it first spilled were made in silence: their keys
        // wait with the runs the emitter was not told of, set aside until the others are back.
        SubTable[] silent = [];
        if (Emitter is not null && part.Tables.Count > 0)
        {
            silent = [.. part.Tables];
            part.Tables.Clear();
            part.Directory = [0];
            part.Depth = 0;
            Volatile.Write(ref part.Groups, 0);
        }

        // The runs whose keys the emitter was told come back first, silently; then the others, whose
        // keys not among them are told as they enter the part's set.
        try
        {
            foreach (bool emitted in (bool[])[true, false])
            {
                part.Silent = emitted;
                foreach (PartRun run in runs)
                {
                    if (run.Emitted != emitted)
                    {
                        continue;
                    }

                    long offset = run.Offset;
                    long left = run.Entries;
                    while (left > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int entries = (int)Math.Min(perPage, left);
                        int bytes = entries * entryBytes;
                        await _scratch!.ReadAsync(offset, Page().AsMemory(0, bytes), cancellationToken).ConfigureAwait(false);
                        Buffer.BlockCopy(_page!, 0, _words, 0, bytes);
                        PartBatch batch = new PartBatch(_words, 0, entries) { Count = entries };
                        Pend(entries);
                        applier.Apply(part, batch, burst: false);
                        offset += bytes;
                        left -= entries;
                    }
                }
            }

            foreach (SubTable table in silent)
            {
                int from = 0;
                while (from < table.Keys.Count)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    from = table.CopyEntries(Shape, from, _words, out int copied);
                    if (copied > 0)
                    {
                        PartBatch batch = new PartBatch(_words, 0, copied) { Count = copied };
                        Pend(copied);
                        applier.Apply(part, batch, burst: false);
                    }
                }
            }
        }
        finally
        {
            foreach (SubTable table in silent)
            {
                table.Release();
            }
        }

        part.Silent = false;
        part.Runs = null;
    }

    /// <summary>
    /// The part's sub-tables let go once delivered: their arrays on the shelf, where the parts still
    /// applied take them again; once every part is applied, handed to the process's shelf, for the
    /// next query, and leaving the query's count; let go, past the first part brought back from the scratch.
    /// </summary>
    internal void Let(CorePart part)
    {
        Interlocked.Add(ref _tablesLet, part.Tables.Count);
        foreach (SubTable table in part.Tables)
        {
            table.Release();
        }

        part.Tables.Clear();
        part.Directory = [0];
        part.Depth = 0;
        Volatile.Write(ref part.Groups, 0);
        if (!_shelf.Drops && Volatile.Read(ref _allApplied))
        {
            _shelf.Clear();
        }
    }

    /// <summary>The spill's scratch closed and its file gone, the shelf handed to the process's: the query is done with its parts.</summary>
    internal void CloseSpill()
    {
        if (_scratch is not null)
        {
            long spilled = Interlocked.Read(ref _spilledBytes);
            SpillSpace.Release(spilled);
            _source?.Session.Options.ScratchBudget?.Release(spilled);
            _scratch.Dispose();
            _scratch = null;
        }

        _shelf.Drops = false;
        _shelf.Clear();
    }

    /// <summary>An empty sub-table's keys and slots: the result of a core delivered part by part, before its first part.</summary>
    internal (GroupKeys Keys, AggregateSlot[] Slots) Empty()
    {
        SubTable empty = NewTable(0);
        return (empty.Keys, empty.Slots);
    }

    /// <summary>
    /// The exception a query fails with when <paramref name="what"/> asks for <paramref name="bytes"/> of
    /// scratch more than <paramref name="budget"/> grants.
    /// </summary>
    internal static VortexMemoryException ScratchExceeded(ScratchBudget budget, string what, long bytes) => new VortexMemoryException(
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"The {what} asks for {bytes:N0} bytes of scratch more, past its scratch budget of {budget.CeilingBytes:N0} bytes, {budget.ReservedBytes:N0} of which the queries under it hold. Give its session a larger ScratchBudget, a larger QueryMemoryBudget, which spills less, or group by fewer keys at once."));

    /// <summary>The bytes free where <paramref name="directory"/> lies, or null when the system does not tell.</summary>
    internal static long? Free(string directory)
    {
        try
        {
            string full = Path.GetFullPath(directory);
            DriveInfo? best = null;
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                string root = drive.RootDirectory.FullName;
                if (full.StartsWith(root, StringComparison.Ordinal) && (best is null || root.Length > best.RootDirectory.FullName.Length))
                {
                    best = drive;
                }
            }

            return best?.AvailableFreeSpace;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The parts never spilled let go once their groups are delivered, before the spilled ones come back,
    /// and what the shelf kept, the slabs of the batches among it, handed to the process's: the parts
    /// brought back reserve what they take, under the budget alone.
    /// </summary>
    internal void LetHeld()
    {
        foreach (CorePart part in _parts)
        {
            if (part.Runs is null)
            {
                Let(part);
            }
        }

        _shelf.Clear();
    }

    /// <summary>Whether <paramref name="directory"/> lies on a file system in memory, <c>tmpfs</c> or <c>ramfs</c>, as Linux's mounts say.</summary>
    internal static bool InMemory(string directory)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            string full = Path.GetFullPath(directory);
            string? type = null;
            int longest = -1;
            foreach (string line in System.IO.File.ReadLines("/proc/mounts"))
            {
                string[] fields = line.Split(' ');
                if (fields.Length >= 3 && full.StartsWith(fields[1], StringComparison.Ordinal) && fields[1].Length > longest
                    && (full.Length == fields[1].Length || fields[1] == "/" || full[fields[1].Length] == '/'))
                {
                    longest = fields[1].Length;
                    type = fields[2];
                }
            }

            return type is "tmpfs" or "ramfs";
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
