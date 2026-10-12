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

    /// <summary>A scan is about to read some <paramref name="segments"/> data segments, of <paramref name="bytes"/> bytes in all.</summary>
    /// <param name="segments">The segments the plan reads, estimated from the share of the file it reads.</param>
    /// <param name="bytes">Their bytes, estimated the same way.</param>
    void AnticipateData(long segments, long bytes);
}

/// <summary>
/// The reader of a file opened from a path: positional reads until a scan's plan announces data
/// to read, and a mapping of the whole file from then on; or, on Windows when the session keeps
/// no mapping, positional reads throughout, but for a file of segments smaller than a page and for
/// large segments a read touches little of.
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
/// That holds where the mapping is kept, and where a page is 16 KiB. Windows maps 4 KiB pages,
/// faults each in for 0.5 to 0.9 µs, and unmaps each a scan touched for 0.3 to 2 µs more, on the
/// thread that closes the file, the dearest when the lanes of a wide scan touched them on many
/// cores; a page that a segment of 100 bytes touches costs all of it. A file mapped for one scan
/// and dropped with its reader, when the session keeps no mapping, spent a quarter to a third of
/// a cold scan of the reference files unmapping it as it closed, and its projections touched a
/// page per segment. There a scan reads positionally: the segments of a batch in runs coalesced
/// over gaps a read costs less than, each run one read on the lane's thread into a pooled block
/// its segments are views of, a copy from the system cache with no fault and nothing to unmap.
/// </para>
/// <para>
/// A copy costs every byte of a segment, and a mapping only the pages a decode touches. A file
/// whose segments average less than a page, a zone map's few rows each, makes a read per run of
/// them where a mapping faults each page once for several: a scan of 64 of them or more maps such
/// a file, a cold scan of one of 0.7 MB in 325 segments a fifth faster on 32 lanes than read, and
/// a narrow filter that reads a few still reads them. And a segment of 256 KiB or more of which a
/// decode touches less than a quarter, a take's few rows or one field of a struct stored whole, is
/// served from a mapping made for those alone, its other segments still read: a take of 1,000
/// rows from a file Vortex Rust wrote, in segments of 1 MB, took 4.4 ms rather than 11.4 on 32
/// lanes. Segments this library writes stay under 256 KiB, and its scans are read.
/// </para>
/// <para>
/// A positional read is a <c>pread</c> on the caller's thread into a pooled buffer, completed
/// before the call returns, as a read from the mapping is: a page the cache does not hold blocks
/// either one the same way, and a hop to the thread pool would cost more than the read. What it
/// serves before a mapping is the tail and the few structures a plan reads before it decides, so
/// its segments are read one by one, without coalescing.
/// </para>
/// <para>
/// Windows runs the reads of a handle opened for synchronous I/O one at a time, whatever their
/// offsets, so the lanes of a scan copied from the system cache at one thread's pace: a group by
/// over 48 MB of a file took the same 5.3 ms on one lane as on four. Once the reads that found
/// another under way on the handle have waited on 2 MiB, the next opens a twin of it for
/// overlapped I/O, whose reads run side by side, and every read that finds the handle busy reads
/// through the twin from then on: that group by takes 3.5 ms on four lanes, 2.9 on eight, 4.2 rather
/// than 7.0 on 32. A read through the twin costs 0.8 µs more, which a scan of small reads on one
/// lane lost 3 to 7 % to when every read took it: such a scan never opens it.
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
internal sealed class LocalFileSource : ISegmentReader, IReadAnticipation, IPositionalReader
{
    /// <summary>
    /// The runs a scan reads positionally: segments less than 16 KiB apart are read as one, the
    /// bytes between them copied for less than a read costs, about 2 µs, and a run is at most
    /// 4 MiB, a block the pool keeps.
    /// </summary>
    private static readonly SegmentReadOptions Runs = new SegmentReadOptions
    {
        CoalesceGapBytes = 16 * 1024,
        MaxCoalescedReadBytes = 4 << 20,
    };

    /// <summary>
    /// The size of a segment a positional reader serves from a mapping when a decode touches less
    /// than a quarter of it: a copy of anything smaller costs less than the mapping's own 33 µs.
    /// </summary>
    private const int SparseSegmentBytes = 256 * 1024;

    /// <summary>The page a mapping faults in and unmaps at once.</summary>
    private const int PageBytes = 4096;

    /// <summary>The segments smaller than a page a positional reader maps the file for, rather than read.</summary>
    private const long MappedSegmentsAtLeast = 64;

    /// <summary>
    /// The bytes of the reads that found another under way on the handle past which a read opens its
    /// overlapped twin: what the system cache copies one read at a time in 200 µs, about twice what
    /// the open and the close cost.
    /// </summary>
    /// <remarks>
    /// The lanes of a scan start together, and their first reads are the queue the twin takes away.
    /// At 4 MiB it came as that queue drained: a take of 1,000 rows on 32 lanes from a file Vortex
    /// Rust wrote, 6 MB read, paid its open and close for a dozen reads, 3.2 ms then 3.4 to 3.6. Opened
    /// by the first read that found another, it served a projection of that file small reads it
    /// costs more on than it saves, 2.4 ms then 3.3.
    /// </remarks>
    private const long TwinAfterBytes = 2 << 20;

    // The fields of _reads: the reads under way on the handle in the low bits, the KiB of those that
    // found one before them above, and the sign once a read opens the twin, or fails to.
    private const long UnderWay = 0xFFFF;
    private const int WaitedShift = 16;

    // Null when the open took over a kept mapping without opening the file.
    private readonly SafeFileHandle? _handle;

    // The handle's twin opened for overlapped I/O, null until reads that waited on another passed
    // TwinAfterBytes, and the reads of the handle, one field: 16 bytes an open.
    private SafeFileHandle? _twin;
    private long _reads;
    private readonly string _path;
    private readonly MappedFileCache? _mappings;

    // The mapping a scan reads, or where scans read positionally, the one the large segments a
    // decode touches little of are served from.
    private MappedFileOwner? _mapping;
    private int _disposed;

    /// <summary>
    /// Whether scans read positionally rather than through a mapping: on Windows, when the session
    /// keeps none, until a scan of many segments smaller than a page has the file mapped.
    /// </summary>
    private bool _positional;

    private LocalFileSource(SafeFileHandle handle, string path, MappedFileCache? mappings, long length)
    {
        _handle = handle;
        _path = path;
        _mappings = mappings;
        Length = length;
        _positional = mappings is null && OperatingSystem.IsWindows();
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

    /// <summary>Whether the file is mapped: for a scan, or where scans read positionally, for a large segment a decode touches little of.</summary>
    internal bool IsMapped => Volatile.Read(ref _mapping) is not null;

    /// <summary>The mapping this reader reads, once a scan had the file mapped.</summary>
    internal MappedFileOwner? Mapping => Volatile.Read(ref _mapping);

    /// <summary>Whether a positional read served a large segment it touched little of from a mapping.</summary>
    internal bool MapsSparseSegments => _positional && IsMapped;

    /// <summary>Whether reads that find another under way on the handle read beside it, through its overlapped twin.</summary>
    internal bool ReadsBeside => Volatile.Read(ref _twin) is not null;

    /// <inheritdoc/>
    public void AnticipateData() => AnticipateData(0, 0);

    /// <inheritdoc/>
    public void AnticipateData(long segments, long bytes)
    {
        // A reader without a handle took its mapping over at the open, and has it until disposed.
        if (Length == 0 || Volatile.Read(ref _disposed) != 0 || _handle is null)
        {
            return;
        }

        // Where scans read positionally, a scan of many segments smaller than a page maps the file:
        // a read per run of them costs more than the pages they share, by about a microsecond a
        // segment, and some 64 repay the 45 µs a mapping costs to make and to drop.
        if (_positional && (segments < MappedSegmentsAtLeast || bytes >= segments * PageBytes))
        {
            return;
        }

        if (Volatile.Read(ref _mapping) is null)
        {
            // This reader holds one reference on the mapping either way, and releases it the same way.
            MappedFileOwner mapped = _mappings is not null && FileInode.TryGet(_handle, Length, out FileInode identity)
                ? _mappings.Acquire(identity, _path, _handle, Length)
                : MappedFileOwner.Map(_handle, Length, ownsHandle: false);
            if (Interlocked.CompareExchange(ref _mapping, mapped, null) is not null)
            {
                // Another scan published its mapping first, or a read its mapping of the file.
                mapped.Release();
            }
            else if (Volatile.Read(ref _disposed) != 0 && Interlocked.Exchange(ref _mapping, null) is { } late)
            {
                // A dispose ran between the check and the publication and found nothing to drop.
                late.Release();
                return;
            }
        }

        // From here every read is served by the mapping, a read already under way finishing as it began.
        _positional = false;
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

        // Where scans read positionally, a mapping serves only the segments a decode touches little of.
        MappedFileOwner? mapping = _positional ? null : Borrow();
        if (mapping is null && !_positional && Pending(requests) >= ParallelBatch)
        {
            return ReadManyInParallelAsync(requests, cancellationToken);
        }

        try
        {
            if (mapping is not null)
            {
                mapping.ReadMany(requests, Length);
            }
            else if (_positional)
            {
                ServeSparse(requests);
                CoalescedReads.ReadMany(requests, this, Length, Runs);
            }
            else
            {
                ReadManyPositionally(requests);
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

    // A scan maps the file before it reads a data segment, unless it reads positionally.
    bool ISegmentReader.ReadsInPlace => !_positional;

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
        Interlocked.Exchange(ref _twin, null)?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    int IPositionalReader.Read(Span<byte> destination, long offset) => ReadAt(destination, offset);

    /// <summary>
    /// Reads at <paramref name="offset"/> on the caller's thread: through the handle, or, where another
    /// read is under way on it, through its overlapped twin, which reads beside it.
    /// </summary>
    private int ReadAt(Span<byte> destination, long offset)
    {
        // Only a disposed reader without a handle gets here: its mapping is gone with its dispose.
        SafeFileHandle handle = _handle ?? throw new ObjectDisposedException(nameof(LocalFileSource));
        if ((Interlocked.Increment(ref _reads) & UnderWay) > 1 && Twin(destination.Length) is { } twin)
        {
            Interlocked.Decrement(ref _reads);
            int read = NativeFile.ReadOverlapped(twin, destination, offset);
            if (read >= 0)
            {
                return read;
            }

            // A failure is the handle's read to report, with the exception it throws.
            Interlocked.Increment(ref _reads);
        }

        try
        {
            return NativeFile.Read(handle, destination, offset);
        }
        finally
        {
            Interlocked.Decrement(ref _reads);
        }
    }

    /// <summary>
    /// The twin a read that found another under way on the handle reads through: opened by the read
    /// whose bytes take those that waited past <see cref="TwinAfterBytes"/>; null before, where the
    /// platform has none, and while that read opens it.
    /// </summary>
    private SafeFileHandle? Twin(int length)
    {
        if (Volatile.Read(ref _twin) is { } twin)
        {
            return twin;
        }

        // The KiB waited counted above the reads under way; once the sign is set, a read took the open.
        if (!OperatingSystem.IsWindows()
            || Interlocked.Add(ref _reads, (long)(length >> 10) << WaitedShift) >> WaitedShift < TwinAfterBytes >> 10
            || Interlocked.Or(ref _reads, long.MinValue) < 0)
        {
            return null;
        }

        SafeFileHandle? opened = NativeFile.TryOpenOverlapped(_handle!);
        Volatile.Write(ref _twin, opened);
        if (Volatile.Read(ref _disposed) != 0 && Interlocked.Exchange(ref _twin, null) is { } late)
        {
            // A dispose ran between the open and the publication and found nothing to close.
            late.Dispose();
            return null;
        }

        return opened;
    }

    /// <summary>
    /// Fills, from a mapping of the file, every slot not yet filled whose segment is at least
    /// <see cref="SparseSegmentBytes"/> long and of which the decode touches less than a quarter:
    /// the mapping faults in the pages touched, where a read would copy them all. The caller reads
    /// the others.
    /// </summary>
    private void ServeSparse(SegmentRequestSet requests)
    {
        MappedFileOwner? mapping = null;
        try
        {
            for (int slot = 0; slot < requests.Count; slot++)
            {
                if (requests.IsFilled(slot))
                {
                    continue;
                }

                SegmentSpec spec = requests.GetSpec(slot);
                if (spec.Length < SparseSegmentBytes || !requests.TouchesLittle(slot))
                {
                    continue;
                }

                mapping ??= BorrowSparse();
                requests.SetResult(slot, mapping.Read(in spec, Length));
            }
        }
        finally
        {
            mapping?.Release();
        }
    }

    /// <summary>
    /// The mapping <see cref="ServeSparse"/> reads, made by the first read that needs it, with a
    /// reference held for one operation as <see cref="Borrow"/> holds one.
    /// </summary>
    private MappedFileOwner BorrowSparse()
    {
        if (Volatile.Read(ref _mapping) is null)
        {
            MappedFileOwner made = MappedFileOwner.Map(_handle!, Length, ownsHandle: false);
            if (Interlocked.CompareExchange(ref _mapping, made, null) is not null)
            {
                // Another lane published its mapping first.
                made.Release();
            }
            else if (Volatile.Read(ref _disposed) != 0 && Interlocked.Exchange(ref _mapping, null) is { } late)
            {
                // A dispose ran between the read's check and the publication and found nothing to drop.
                late.Release();
                throw new ObjectDisposedException(nameof(LocalFileSource));
            }
        }

        return (MappedFileOwner)(Volatile.Read(ref _mapping) ?? throw new ObjectDisposedException(nameof(LocalFileSource))).Retain();
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
        if (_handle is null)
        {
            throw new ObjectDisposedException(nameof(LocalFileSource));
        }

        // The shared pool keeps a block past its classes as well, for the next read of a segment that large.
        NativeSegmentOwner owner = AlignedBufferPool.Shared.Rent(length, VortexLimits.MaxAlignment);
        try
        {
            Span<byte> destination = owner.WritableSpan[..length];
            int done = 0;
            while (done < length)
            {
                // A short count is legal and not the end; none at all is a file shorter than its
                // footer says.
                int read = ReadAt(destination[done..], offset + done);
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
