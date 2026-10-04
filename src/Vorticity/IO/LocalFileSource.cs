using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.IO;

/// <summary>What a reader is told by a scan's plan before the scan reads anything.</summary>
internal interface IReadAnticipation
{
    /// <summary>A scan is about to read data segments.</summary>
    void AnticipateData();
}

/// <summary>
/// The reader of a file opened from a path: positional reads until a scan's plan announces data
/// to read, and a mapping of the whole file from then on.
/// </summary>
/// <remarks>
/// <para>
/// An open reads the file's last 8 KiB, or the whole file up to 64 KiB, which one positional
/// read serves for less than a mapping costs to make and to drop, and a file that is opened for
/// its schema, its statistics or a count they answer reads nothing else. A file the open read
/// whole serves its scans from that read and is never mapped. A scan maps any other: the mapping
/// is kept, and serves every read after it two to three times faster than a positional read of
/// the same size, with no buffer and no allocation per batch, and synchronously. A positional scan
/// would save the mapping's cost, about 16 µs and 0.8 µs for each 16 KiB page touched a first
/// time, only on a file read once and under 4 MiB, and would pay for it with an allocation and an
/// asynchronous completion per batch.
/// </para>
/// <para>
/// A positional read is a <c>pread</c> on the caller's thread into a pooled buffer, completed
/// before the call returns, as a read from the mapping is: a page the cache does not hold blocks
/// either one the same way, and a hop to the thread pool would cost more than the read. What it
/// serves before a mapping is the tail and the few structures a plan reads before it decides, so
/// its segments are read one by one, without coalescing.
/// </para>
/// <para>
/// With the session's <see cref="MappedFileCache"/>, the mapping outlives the reader: the next open
/// of the same file takes it over with every page already mapped in it, so a file scanned again
/// pays neither the mapping nor a fault per page nor the unmapping. Where the platform reads a
/// file's identity from its name, Windows 11 24H2 and later, such an open does not open the file at
/// all: the mapping kept for that identity and length serves every read, the tail's included. An
/// open is the dearest thing there, a hundred microseconds where endpoint filters inspect each one.
/// </para>
/// <para>
/// A read already under way when the mapping is published finishes positionally. The mapping
/// does not need the handle once made, so disposing the reader closes the handle and drops the
/// reader's reference on the mapping, which lives on for as long as a lease reads from it.
/// </para>
/// </remarks>
internal sealed class LocalFileSource : ISegmentReader, IReadAnticipation
{
    // Null when the open took over a kept mapping without opening the file.
    private readonly SafeFileHandle? _handle;
    private readonly string _path;
    private readonly MappedFileCache? _mappings;
    private MappedFileOwner? _mapping;
    private int _disposed;

    private LocalFileSource(SafeFileHandle handle, string path, MappedFileCache? mappings, long length)
    {
        _handle = handle;
        _path = path;
        _mappings = mappings;
        Length = length;
    }

    private LocalFileSource(MappedFileOwner kept, string path, MappedFileCache mappings, long length)
    {
        _path = path;
        _mappings = mappings;
        _mapping = kept;
        Length = length;
    }

    /// <summary>Opens <paramref name="path"/> read-only; nothing is mapped yet.</summary>
    /// <param name="path">A local file path.</param>
    /// <param name="mappings">The session's kept mappings, or null to map the file for this reader alone.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="IOException">The file could not be opened.</exception>
    internal static LocalFileSource Open(string path, MappedFileCache? mappings = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        // A file kept mapped, still under this name at the same length, needs no open: its mapping
        // is the file, rewritten in place or not.
        if (mappings is not null && FileInode.TryGetByName(path, out FileInode named, out long length)
            && length > 0 && mappings.TryTake(named, length) is { } kept)
        {
            return new LocalFileSource(kept, path, mappings, length);
        }

        SafeFileHandle handle = NativeFile.OpenRead(path, out long opened);
        return new LocalFileSource(handle, path, mappings, opened);
    }

    /// <summary>The file length in bytes, read once at open.</summary>
    internal long Length { get; }

    /// <summary>Whether a scan has had the file mapped.</summary>
    internal bool IsMapped => Volatile.Read(ref _mapping) is not null;

    /// <summary>The mapping this reader reads, once a scan had the file mapped.</summary>
    internal MappedFileOwner? Mapping => Volatile.Read(ref _mapping);

    /// <inheritdoc/>
    public void AnticipateData()
    {
        // A reader without a handle took its mapping over at the open, and has it until disposed.
        if (Length == 0 || Volatile.Read(ref _mapping) is not null || Volatile.Read(ref _disposed) != 0 || _handle is null)
        {
            return;
        }

        // This reader holds one reference on the mapping either way, and releases it the same way.
        MappedFileOwner mapped = _mappings is not null && FileInode.TryGet(_handle, Length, out FileInode identity)
            ? _mappings.Acquire(identity, _path, _handle, Length)
            : MappedFileOwner.Map(_handle, Length, ownsHandle: false);
        if (Interlocked.CompareExchange(ref _mapping, mapped, null) is not null)
        {
            // Another scan published its mapping first.
            mapped.Release();
        }
        else if (Volatile.Read(ref _disposed) != 0 && Interlocked.Exchange(ref _mapping, null) is { } late)
        {
            // A dispose ran between the check and the publication and found nothing to drop.
            late.Release();
        }
    }

    ValueTask<long> ISegmentReader.GetLengthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<long>(Length);
    }

    ValueTask<SegmentOwner> ISegmentReader.ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        MappedFileOwner? mapping = Borrow();
        if (mapping is null)
        {
            SegmentIo.ValidateSpec(in spec, out long offset, out int length);
            SegmentIo.CheckInFile(offset, length, Length);
            return new ValueTask<SegmentOwner>(length == 0 ? new EmptySegmentOwner() : ReadPositionally(offset, length));
        }

        try
        {
            return new ValueTask<SegmentOwner>(mapping.Read(in spec, Length));
        }
        finally
        {
            mapping.Release();
        }
    }

    ValueTask ISegmentReader.ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (requests.IsPopulated)
        {
            return ValueTask.CompletedTask;
        }

        MappedFileOwner? mapping = Borrow();
        if (mapping is null && Pending(requests) >= ParallelBatch)
        {
            return ReadManyInParallelAsync(requests, cancellationToken);
        }

        try
        {
            if (mapping is null)
            {
                ReadManyPositionally(requests);
            }
            else
            {
                mapping.ReadMany(requests, Length);
            }

            requests.Complete();
        }
        catch
        {
            requests.AbandonPending();
            throw;
        }
        finally
        {
            mapping?.Release();
        }

        return ValueTask.CompletedTask;
    }

    ValueTask<SegmentOwner> ISegmentReader.ReadRangeAsync(
        long offset, int length, int alignment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        MappedFileOwner? mapping = Borrow();
        if (mapping is null)
        {
            int actual = SegmentIo.ClampRange(offset, length, alignment, Length);
            return new ValueTask<SegmentOwner>(actual == 0 ? new EmptySegmentOwner() : ReadPositionally(offset, actual));
        }

        try
        {
            return new ValueTask<SegmentOwner>(mapping.ReadRange(offset, length, alignment, Length));
        }
        finally
        {
            mapping.Release();
        }
    }

    // A scan maps the file before it reads a data segment.
    bool ISegmentReader.ReadsInPlace => true;

    // A positional read is a copy and no round trip: the last 8 KiB hold the footer of a file of a
    // few columns, and read in a quarter of the time the window's 64 KiB take; a larger footer is
    // read whole by a second read.
    int ISegmentReader.TailReadSize => 8 * 1024;

    /// <summary>Drops the reader's reference on the mapping and closes the handle; leases already handed out stay valid.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        Interlocked.Exchange(ref _mapping, null)?.Release();
        _handle?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A batch of this many reads before a mapping is read on several threads at once: a cold file
    /// serves each read at the device's latency, which reads one after another add up and reads in
    /// flight together share. An append that keeps a file's chunks reads the end of each chunk's
    /// every segment, hundreds of them, before anything maps the file.
    /// </summary>
    private const int ParallelBatch = 16;

    /// <summary>The reads of such a batch in flight at once.</summary>
    private const int ReadLanes = 8;

    /// <summary>The slots not yet filled.</summary>
    private static int Pending(SegmentRequestSet requests)
    {
        int pending = 0;
        for (int slot = 0; slot < requests.Count; slot++)
        {
            pending += requests.IsFilled(slot) ? 0 : 1;
        }

        return pending;
    }

    /// <summary>
    /// Every slot not yet filled, as <see cref="ReadManyPositionally"/> reads them, by
    /// <see cref="ReadLanes"/> threads that take the next read as each finishes one: the buffers go
    /// to their slots once every read is in, and back to the pool if any read failed.
    /// </summary>
    private async ValueTask ReadManyInParallelAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        List<(int Slot, long Offset, int Length)> reads = new List<(int, long, int)>(requests.Count);
        try
        {
            for (int slot = 0; slot < requests.Count; slot++)
            {
                if (requests.IsFilled(slot))
                {
                    continue;
                }

                SegmentSpec spec = requests.GetSpec(slot);
                SegmentIo.ValidateSpec(in spec, out long offset, out int length);
                SegmentIo.CheckInFile(offset, length, Length);
                reads.Add((slot, offset, length));
            }
        }
        catch
        {
            requests.AbandonPending();
            throw;
        }

        SegmentOwner?[] owners = new SegmentOwner?[reads.Count];
        int next = -1;
        try
        {
            Task[] lanes = new Task[Math.Min(ReadLanes, reads.Count)];
            for (int lane = 0; lane < lanes.Length; lane++)
            {
                lanes[lane] = Task.Run(
                    () =>
                    {
                        for (int i = Interlocked.Increment(ref next); i < reads.Count; i = Interlocked.Increment(ref next))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            (_, long offset, int length) = reads[i];
                            owners[i] = length == 0 ? new EmptySegmentOwner() : ReadPositionally(offset, length);
                        }
                    },
                    cancellationToken);
            }

            await Task.WhenAll(lanes).ConfigureAwait(false);
            for (int i = 0; i < reads.Count; i++)
            {
                requests.SetResult(reads[i].Slot, owners[i]!);
                owners[i] = null;
            }

            requests.Complete();
        }
        catch
        {
            foreach (SegmentOwner? owner in owners)
            {
                owner?.Dispose();
            }

            requests.AbandonPending();
            throw;
        }
    }

    /// <summary>Every slot not yet filled, each segment in a buffer of its own that the slot takes over.</summary>
    private void ReadManyPositionally(SegmentRequestSet requests)
    {
        int count = requests.Count;
        for (int slot = 0; slot < count; slot++)
        {
            if (requests.IsFilled(slot))
            {
                continue;
            }

            SegmentSpec spec = requests.GetSpec(slot);
            SegmentIo.ValidateSpec(in spec, out long offset, out int length);
            SegmentIo.CheckInFile(offset, length, Length);
            requests.SetResult(slot, length == 0 ? new EmptySegmentOwner() : ReadPositionally(offset, length));
        }
    }

    /// <summary>
    /// <paramref name="length"/> bytes at <paramref name="offset"/>, validated against the file
    /// already, into a buffer whose 64-byte base satisfies every alignment a segment may declare.
    /// </summary>
    private NativeSegmentOwner ReadPositionally(long offset, int length)
    {
        // Only a disposed reader without a handle gets here: its mapping is gone with its dispose.
        SafeFileHandle handle = _handle ?? throw new ObjectDisposedException(nameof(LocalFileSource));
        NativeSegmentOwner owner = length <= SegmentReadOptions.DefaultMaxPooledBytes
            ? AlignedBufferPool.Shared.Rent(length, VortexLimits.MaxAlignment)
            : NativeSegmentOwner.Allocate(length, VortexLimits.MaxAlignment);
        try
        {
            Span<byte> destination = owner.WritableSpan[..length];
            int done = 0;
            while (done < length)
            {
                // A short count is legal and not the end; none at all is a file shorter than its
                // footer says.
                int read = NativeFile.Read(handle, destination[done..], offset + done);
                if (read <= 0)
                {
                    SegmentIo.ThrowTruncatedRead(offset, length, done);
                }

                done += read;
            }

            return owner;
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The mapping with a reference held for one operation, or null while there is none: a
    /// concurrent dispose then resolves as <see cref="ObjectDisposedException"/> from the
    /// reference count instead of a read of unmapped memory.
    /// </summary>
    private MappedFileOwner? Borrow() =>
        Volatile.Read(ref _mapping) is { } mapping ? (MappedFileOwner)mapping.Retain() : null;

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(LocalFileSource));
        }
    }
}
