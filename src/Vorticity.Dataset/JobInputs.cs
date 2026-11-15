using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Vorticity.Aggregating;
using Vorticity.Buffers;
using Vorticity.IO;

namespace Vorticity.Dataset;

/// <summary>
/// The inputs of one compaction job, each copied once to the local scratch and read from there: the store is asked for
/// each object once, in one stream, and the job reads its segments from the disk, a window at a time, rather than hold
/// them. The bytes stay as the store holds them, sealed when the dataset seals them, and are decrypted segment by segment
/// as they are read: nothing in clear reaches the disk.
/// </summary>
/// <remarks>
/// <para>
/// A compaction needs a scratch on a disk: the session's <see cref="VortexSessionOptions.ScratchDirectory"/>, or the
/// default one, never a file system in memory, whose bytes would be the memory the scratch saves. A job reserves the bytes
/// of its inputs there before it starts, under the session's <see cref="VortexSessionOptions.ScratchBudget"/> when it has
/// one, and keeps a tenth of the directory's free space otherwise.
/// </para>
/// <para>
/// A job's files lie in a directory of its own, removed with the job. One a process left behind when it died is removed
/// by the next job of any process once it is a day old.
/// </para>
/// </remarks>
internal sealed class JobInputs : IAsyncDisposable
{
    /// <summary>🧪 The bytes of a job's inputs from which they are read through the scratch: below, from the store, as before. Provisional, to be measured.</summary>
    internal const long FromBytes = 16L << 20;

    /// <summary>The bytes a copy to the scratch moves at a time, through one block of the job's inputs.</summary>
    private const int CopyBytes = 4 << 20;

    /// <summary>The prefix of a job's directory in the scratch.</summary>
    private const string DirectoryPrefix = "vorticity-job-";

    private readonly DatasetSnapshot _snapshot;
    private readonly string _directory;
    private readonly IObjectStore _source;
    private readonly ObjectCache _cache;
    private readonly MemoryAccount _account;
    private readonly ScratchBudget? _budget;
    private readonly long _reserved;
    private readonly Lock _gate = new Lock();
    private readonly Dictionary<string, Task> _copies = new(StringComparer.Ordinal);
    private long _copied;

    private JobInputs(VortexDataset dataset, string directory, MemoryAccount account, ScratchBudget? budget, long reserved)
    {
        _snapshot = dataset.Snapshot;
        _directory = directory;
        _account = account;
        _budget = budget;
        _reserved = reserved;

        // The store's own bytes: under a sealed dataset, those under its sealing, which the copy keeps sealed.
        _source = dataset.Store is SealedObjectStore sealedStore ? sealedStore.Inner : dataset.Store;
        FileObjectStore local = new FileObjectStore(directory);
        IObjectStore reading = dataset.Store is SealedObjectStore sealing ? sealing.ReadingFrom(local) : local;
        _cache = new ObjectCache(reading, int.MaxValue, dataset.Session);
    }

    /// <summary>The bytes copied to the scratch.</summary>
    internal long CopiedBytes => Interlocked.Read(ref _copied);

    /// <summary>
    /// The inputs of <paramref name="job"/> of <paramref name="dataset"/> read through the scratch, <paramref name="bytes"/>
    /// of them read whole, their copies counted under <paramref name="account"/>; null for a job that reads less than
    /// <see cref="FromBytes"/> of them whole, which reads from the store.
    /// </summary>
    /// <exception cref="VortexMemoryException">No scratch on a disk, or not room enough there for the job's inputs.</exception>
    internal static JobInputs? For(VortexDataset dataset, CompactionJob job, long bytes, MemoryAccount account)
    {
        if (bytes < FromBytes)
        {
            return null;
        }

        string scratch = ScratchDirectories.Of(dataset.Session.Options.ScratchDirectory);
        if (GroupCore.InMemory(scratch))
        {
            throw new VortexMemoryException(
                $"A compaction reads its inputs through a scratch on a disk, and the session's scratch directory {scratch} lies on a file system in memory, which would take the memory it saves. Give the session a ScratchDirectory on a disk: an emptyDir or a volume of a few times the largest job's inputs.");
        }

        Directory.CreateDirectory(scratch);
        SweepLeftovers(scratch);
        ScratchBudget? budget = dataset.Session.Options.ScratchBudget;
        if (budget is not null && !budget.TryReserve(bytes))
        {
            throw new VortexMemoryException(string.Create(
                CultureInfo.InvariantCulture,
                $"A compaction of {job.Inputs.Count} objects reads {bytes:N0} bytes through its scratch, past what the session's scratch budget leaves. Give the budget more room, or the layout smaller objects."));
        }

        if (budget is null && GroupCore.Free(scratch) is long free && bytes > free - (free / 10))
        {
            throw new VortexMemoryException(string.Create(
                CultureInfo.InvariantCulture,
                $"A compaction of {job.Inputs.Count} objects reads {bytes:N0} bytes through its scratch {scratch}, which has {free:N0} bytes free, a tenth of which it keeps. Give the scratch more room."));
        }

        string directory = Path.Combine(scratch, string.Create(CultureInfo.InvariantCulture, $"{DirectoryPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(directory);
        return new JobInputs(dataset, directory, account, budget, budget is null ? 0 : bytes);
    }

    /// <summary>
    /// A lease of the input <paramref name="entry"/>, read from its copy in the scratch, which the first rent makes: one
    /// stream from the store, whichever pass asks for it first.
    /// </summary>
    internal async ValueTask<ObjectLease> RentAsync(ObjectEntry entry, CancellationToken cancellationToken)
    {
        if (!entry.IsResolved)
        {
            await entry.ResolveAsync(_snapshot.Pages, cancellationToken).ConfigureAwait(false);
        }

        await CopyAsync(entry, cancellationToken).ConfigureAwait(false);
        try
        {
            return await _cache.RentAsync(entry, _snapshot.Pages, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectNotFoundException missing) when (!DatasetVacuum.IsLost(missing))
        {
            throw DatasetVacuum.Lost(entry.Key, _snapshot.Version, missing);
        }
    }

    /// <summary>The copy of <paramref name="entry"/>'s object, made once whatever asks for it.</summary>
    private Task CopyAsync(ObjectEntry entry, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_copies.TryGetValue(entry.Key, out Task? copying) || copying.IsFaulted || copying.IsCanceled)
            {
                copying = CopyOnceAsync(entry.Key, entry.Bytes, cancellationToken);
                _copies[entry.Key] = copying;
            }

            return copying;
        }
    }

    /// <summary>Streams the object <paramref name="key"/>, <paramref name="length"/> bytes, from the store to its file in the job's directory.</summary>
    private async Task CopyOnceAsync(string key, long length, CancellationToken cancellationToken)
    {
        string path = Path.GetFullPath(Path.Combine(_directory, key.Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        BlockOwner block = _account.Rent((int)Math.Min(CopyBytes, Math.Max(length, 1)), 64);
        try
        {
            using SafeFileHandle file = System.IO.File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None);
            long offset = 0;
            while (offset < length)
            {
                Memory<byte> into = block.WritableMemory(0, (int)Math.Min(block.Length, length - offset));
                ObjectRead read = await _source.ReadAsync(key, offset, into, null, cancellationToken).ConfigureAwait(false);
                if (read.BytesRead <= 0)
                {
                    throw new ObjectNotFoundException($"'{key}' ended at {offset} of the {length} bytes its entry names.");
                }

                await RandomAccess.WriteAsync(file, into[..read.BytesRead], offset, cancellationToken).ConfigureAwait(false);
                offset += read.BytesRead;
            }

            Interlocked.Add(ref _copied, length);
        }
        finally
        {
            block.Release();
        }
    }

    /// <summary>Removes the directories of jobs a process left behind when it died, once a day old.</summary>
    private static void SweepLeftovers(string scratch)
    {
        try
        {
            foreach (string left in Directory.EnumerateDirectories(scratch, DirectoryPrefix + "*"))
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(left) > TimeSpan.FromDays(1))
                {
                    Directory.Delete(left, recursive: true);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Closes the copies, removes the job's directory, and gives back what the job reserved of the scratch.</summary>
    public async ValueTask DisposeAsync()
    {
        await _cache.DisposeAsync().ConfigureAwait(false);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A file still open on a system that will not delete it: the next job's sweep takes it once a day old.
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (_reserved > 0)
        {
            _budget!.Release(_reserved);
        }
    }
}
