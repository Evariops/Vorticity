using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Advice;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Types;

namespace Vorticity;

/// <summary>What a session owns: memory, caches, I/O concurrency, parallelism and extensions.</summary>
/// <remarks>Set inside <see cref="VortexSession.Create"/>; frozen when it returns.</remarks>
public sealed class VortexSessionOptions
{
    private MemoryPool<byte> _memoryPool = AlignedMemoryPool.Shared;
    private SegmentCache? _segmentCache;
    private int _maxConcurrentReads = 16;
    private int _maxDegreeOfParallelism = 1;
    private long _indexCacheBytes = 64L * 1024 * 1024;
    private int _mappedFileCacheCount = 64;
    private bool _mapFiles = true;
    private QueryMemoryBudget? _memoryBudget;
    private string? _scratchDirectory;
    private ScratchBudget? _scratchBudget;
    private bool _frozen;

    internal VortexSessionOptions()
    {
    }

    /// <summary>
    /// Where the batches, the builders' buffers and the segments read through a caller's
    /// <see cref="ISegmentSource"/> come from. The engine decodes into 64-byte aligned native blocks,
    /// so an <see cref="AlignedMemoryPool"/> serves it directly; another pool serves the builders and
    /// the owned batches, and the engine falls back to <see cref="AlignedMemoryPool.Shared"/>.
    /// </summary>
    /// <remarks>
    /// The sources of this library read segments into <see cref="AlignedMemoryPool.Shared"/>, whatever
    /// the session's pool: a file opened from a path, a <see cref="FileSegmentSource"/>. A mapped file
    /// or bytes in memory are read where they lie.
    /// </remarks>
    public MemoryPool<byte> MemoryPool
    {
        get => _memoryPool;
        set => _memoryPool = Set(value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>
    /// Segments kept across scans, one budget for every file of the session whose reads do I/O;
    /// null keeps none.
    /// </summary>
    /// <remarks>
    /// A file does I/O when it is opened from an <see cref="ISegmentSource"/> that reads, or from a
    /// path while <see cref="MapFiles"/> is false. A mapped file, or bytes in memory, have nothing to
    /// keep: their segments are read where they lie.
    /// </remarks>
    public SegmentCache? SegmentCache
    {
        get => _segmentCache;
        set => _segmentCache = Set(value);
    }

    /// <summary>The most reads in flight across every scan of every file of the session whose reads do I/O.</summary>
    /// <remarks>
    /// As for <see cref="SegmentCache"/>: a file opened from an <see cref="ISegmentSource"/> that
    /// reads, or from a path while <see cref="MapFiles"/> is false. A mapped file faults its pages in
    /// instead of reading them, and bytes in memory are not read at all.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int MaxConcurrentReads
    {
        get => _maxConcurrentReads;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxConcurrentReads = Set(value);
        }
    }

    /// <summary>
    /// How many chunks a scan of the session decodes and aggregates at once, unless its
    /// <see cref="ScanOptions.DegreeOfParallelism"/> says otherwise, and how many threads a writer of
    /// the session works on, unless its <see cref="VortexWriteOptions.DegreeOfParallelism"/> does.
    /// 1 by default: a library does not take a host's cores without being asked.
    /// </summary>
    /// <remarks>
    /// A scan at 1 still decodes the batch after the one the caller holds, on the thread pool, as
    /// <see cref="ScanOptions.Prefetch"/> asks by default: one thread of the pool beside the
    /// caller's. A <see cref="ScanOptions.Prefetch"/> of 0 keeps the scan on the caller's thread.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int MaxDegreeOfParallelism
    {
        get => _maxDegreeOfParallelism;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxDegreeOfParallelism = Set(value);
        }
    }

    /// <summary>The bytes of decoded index runs a file of the session keeps.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public long IndexCacheBytes
    {
        get => _indexCacheBytes;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _indexCacheBytes = Set(value);
        }
    }

    /// <summary>
    /// How many files opened from a path the session keeps mapped once they are closed, so that
    /// opening one again takes over its mapping and every page already mapped in it; 0 keeps none.
    /// </summary>
    /// <remarks>
    /// A kept file is recognized by its device and inode, on Windows by its volume and file id, and
    /// mapped again when its length changed. A file deleted once closed keeps its disk space for as
    /// long as its mapping is kept: until files mapped since fill the cache, or until
    /// <see cref="VortexSession.ReleaseMappedFiles"/>. On Windows a kept file can be deleted and
    /// replaced under its name, but another writer can neither cut it short nor overwrite it from
    /// its start, <c>File.WriteAllBytes</c> included, until the session lets it go; the writers of
    /// this library let it go themselves. None is kept where the platform cannot tell one file from
    /// another, nor when <see cref="MapFiles"/> is false.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int MappedFileCacheCount
    {
        get => _mappedFileCacheCount;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _mappedFileCacheCount = Set(value);
        }
    }

    /// <summary>
    /// Whether a file opened from a path is mapped into memory by its first scan; true by default.
    /// False reads it positionally, as a <see cref="FileSegmentSource"/> does, through the session's
    /// <see cref="SegmentCache"/> and <see cref="MaxConcurrentReads"/>, and keeps no file mapped.
    /// </summary>
    /// <remarks>
    /// A mapping serves reads faster and with no buffer per batch. On Linux and macOS, though, a file
    /// cut short while it is mapped faults on the pages past its new end, and the fault kills the
    /// process. A writer of this library cannot cut a file a reader holds open; another process, or
    /// a writer that shares the file, can. Windows refuses the cut to the writer instead, as long as
    /// the file is mapped, kept mappings included. Positional reads turn that into a
    /// <see cref="VortexFormatException"/> on the read: the choice of a service that reads files
    /// others may truncate.
    /// </remarks>
    public bool MapFiles
    {
        get => _mapFiles;
        set => _mapFiles = Set(value);
    }

    /// <summary>
    /// The memory the session's queries share with those of every session given the same budget: the
    /// tables of their groups and the parts their merges build. Null, the default, shares the process's,
    /// a margin under the memory the process may use.
    /// </summary>
    /// <remarks>
    /// A query that needs more than its budget grants fails with a <see cref="VortexMemoryException"/>.
    /// The budget is the host's: disposing the session leaves it to the others.
    /// </remarks>
    public QueryMemoryBudget? MemoryBudget
    {
        get => _memoryBudget;
        set => _memoryBudget = Set(value);
    }

    /// <summary>
    /// Where a group by its memory budget cannot hold writes the groups it spills: a local directory,
    /// never an object store. Null, the default, is the system's temporary directory.
    /// </summary>
    /// <remarks>
    /// A directory on a file system in memory (<c>tmpfs</c>) is refused: spilling there would take the
    /// memory it gives back. The process's spills together take at most 90 % of the directory's free
    /// space; past it, the query fails with a <see cref="VortexMemoryException"/>. A spill's file has
    /// no name once open, outside Windows, which deletes it on close: none is left behind.
    /// </remarks>
    public string? ScratchDirectory
    {
        get => _scratchDirectory;
        set => _scratchDirectory = Set(value);
    }

    /// <summary>
    /// The scratch the session's queries share with those of every session given the same budget: the
    /// parts a group by spills and the runs of a sort, in <see cref="ScratchDirectory"/>. Null, the
    /// default, bounds them by the directory's free space alone, a tenth of it kept.
    /// </summary>
    /// <remarks>
    /// A query that needs more scratch than its budget grants fails with a
    /// <see cref="VortexMemoryException"/>, its files gone. Give a pod's queries one under its
    /// <c>ephemeral-storage</c> limit, which the free space of its disk does not show. The budget is
    /// the host's: disposing the session leaves it to the others.
    /// </remarks>
    public ScratchBudget? ScratchBudget
    {
        get => _scratchBudget;
        set => _scratchBudget = Set(value);
    }

    /// <summary>The extension dtypes this session knows beyond the frozen editions.</summary>
    public VortexExtensionRegistry Extensions { get; } = new VortexExtensionRegistry();

    /// <summary>The engine's pool: the aligned pool's own, or the shared one.</summary>
    internal AlignedBufferPool EnginePool => _memoryPool is AlignedMemoryPool aligned ? aligned.Inner : AlignedBufferPool.Shared;

    internal void Freeze()
    {
        _frozen = true;
        Extensions.Freeze();
    }

    private T Set<T>(T value)
    {
        if (_frozen)
        {
            throw new InvalidOperationException("The session is created; its options are frozen. Set them inside VortexSession.Create.");
        }

        return value;
    }
}

/// <summary>
/// The context every file, scan and writer runs in: one memory pool, one segment cache, one bound on
/// reads in flight, one degree of parallelism, one set of extensions.
/// </summary>
/// <remarks>
/// A session is thread-safe and meant to be shared by every request of a process.
/// <see cref="Default"/> is immutable and is what <see cref="VortexFile.OpenAsync(string, CancellationToken)"/> uses.
/// </remarks>
public sealed class VortexSession : IAsyncDisposable
{
    private readonly ConcurrentDictionary<VortexFile, string> _open = new();
    private int _disposed;

    private VortexSession(VortexSessionOptions options)
    {
        Options = options;
        ReadGate = new SemaphoreSlim(options.MaxConcurrentReads, options.MaxConcurrentReads);
        Mappings = options.MapFiles && options.MappedFileCacheCount > 0 && FileInode.IsSupported
            ? new MappedFileCache(options.MappedFileCacheCount)
            : null;
    }

    /// <summary>
    /// The process-wide session: the shared pool, no segment cache, 16 reads in flight, no
    /// parallelism, no extensions, 64 closed files kept mapped.
    /// </summary>
    public static VortexSession Default { get; } = CreateDefault();

    /// <summary>What this session owns, frozen.</summary>
    public VortexSessionOptions Options { get; }

    /// <summary>The bound on reads in flight across the session.</summary>
    internal SemaphoreSlim ReadGate { get; }

    /// <summary>The files kept mapped once closed, or null when the session keeps none.</summary>
    internal MappedFileCache? Mappings { get; }

    /// <summary>A session configured by <paramref name="configure"/>.</summary>
    /// <param name="configure">Sets the options; they are frozen when it returns.</param>
    /// <returns>The session; the caller disposes it once every file it opened is disposed.</returns>
    public static VortexSession Create(Action<VortexSessionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        VortexSessionOptions options = new VortexSessionOptions();
        configure(options);
        options.Freeze();
        return new VortexSession(options);
    }

    /// <summary>
    /// Opens the file at <paramref name="path"/>: its tail read positionally, the file mapped into
    /// memory by the first scan that reads data, and kept mapped once closed for the next open of
    /// the same file, as <see cref="VortexSessionOptions.MappedFileCacheCount"/> says; or every read
    /// positional, when <see cref="VortexSessionOptions.MapFiles"/> is false.
    /// </summary>
    /// <param name="path">A local file path.</param>
    /// <param name="options">What the open reads, trusts and refuses; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open file; the caller disposes it.</returns>
    /// <exception cref="VortexFormatException">The file is not a well-formed Vortex file.</exception>
    public async ValueTask<VortexFile> OpenAsync(string path, VortexOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ThrowIfDisposed();
        VortexFile file = await VortexFile.OpenAsync(path, Effective(options), this, cancellationToken).ConfigureAwait(false);
        await AttachAsync(file, path).ConfigureAwait(false);
        return file;
    }

    /// <summary>Opens a file over <paramref name="source"/>, which the file then owns.</summary>
    /// <param name="source">Where the bytes come from.</param>
    /// <param name="options">What the open reads, trusts and refuses; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open file; disposing it disposes the source.</returns>
    /// <exception cref="VortexFormatException">The bytes are not a well-formed Vortex file.</exception>
    public async ValueTask<VortexFile> OpenAsync(ISegmentSource source, VortexOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        ISegmentReader reader = source as ISegmentReader ?? new SourceReader(source, ownsSource: true, Options.EnginePool);
        VortexFile file = await VortexFile.OpenAsync(SessionReader.Wrap(reader, this), Effective(options), cancellationToken).ConfigureAwait(false);
        await AttachAsync(file, source.GetType().Name).ConfigureAwait(false);
        return file;
    }

    /// <summary>
    /// Starts a file at <paramref name="path"/>, which replaces whatever is there once it completes:
    /// until then the path holds what it held, and a file given up leaves it so.
    /// </summary>
    /// <remarks>
    /// The file is written beside the destination, as <c>.name.token.tmp</c>, and renamed over it,
    /// so its directory must be writable. A symbolic link is written through to its target, and a
    /// file replaced passes its Unix permissions on. A process that dies before completing leaves
    /// that file behind.
    /// </remarks>
    /// <param name="path">The destination.</param>
    /// <param name="schema">The file's columns.</param>
    /// <param name="options">What the file looks like; null for the defaults.</param>
    /// <returns>The writer; the caller completes and disposes it. Disposed without completing, it deletes what it wrote and leaves the path as it was.</returns>
    /// <exception cref="ArgumentException">A hint or an index names a column the schema does not have, or the metadata does not fit a postscript.</exception>
    /// <exception cref="VortexUnsupportedException">The schema names a component the target edition does not carry.</exception>
    public VortexFileWriter CreateWriter(string path, VortexSchema schema, VortexWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(schema);
        ThrowIfDisposed();
        VortexFileWriter writer = VortexFileWriter.Create(path, VortexTypes.ToDType(schema, new DTypeArena()), options ?? VortexWriteOptions.Default, this);
        writer.Declare(schema);
        return writer;
    }

    /// <summary>Starts a file at <paramref name="path"/> whose columns are the members of <typeparamref name="TRecord"/>.</summary>
    /// <typeparam name="TRecord">The record type; its schema is the file's.</typeparam>
    /// <param name="path">The destination, replaced once the file completes.</param>
    /// <param name="options">What the file looks like; null for the defaults.</param>
    /// <returns>The writer; the caller completes and disposes it. Disposed without completing, it deletes what it wrote and leaves the path as it was.</returns>
    /// <exception cref="ArgumentException">A hint or an index names a column the record does not have.</exception>
    public VortexFileWriter CreateWriter<TRecord>(string path, VortexWriteOptions? options = null)
        where TRecord : IVortexRecord<TRecord>
    {
        VortexFileWriter writer = CreateWriter(path, TRecord.Schema, options);
        try
        {
            writer.Builder<TRecord>();
        }
        catch
        {
            writer.Abandon();
            throw;
        }

        return writer;
    }

    /// <summary>Starts a file written to <paramref name="sink"/>: a file, a socket, a multipart upload.</summary>
    /// <param name="sink">
    /// Where the bytes go; <c>FlushAsync</c> waits for it to accept them. The writer completes the
    /// pipe when the file is whole, and completes it with an error when the file is abandoned.
    /// </param>
    /// <param name="schema">The file's columns.</param>
    /// <param name="options">What the file looks like; null for the defaults.</param>
    /// <returns>The writer; the caller completes and disposes it.</returns>
    /// <exception cref="ArgumentException">A hint or an index names a column the schema does not have, or the metadata does not fit a postscript.</exception>
    public VortexFileWriter CreateWriter(PipeWriter sink, VortexSchema schema, VortexWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(schema);
        ThrowIfDisposed();
        VortexFileWriter writer = VortexFileWriter.Create(sink, VortexTypes.ToDType(schema, new DTypeArena()), options ?? VortexWriteOptions.Default, this);
        writer.Declare(schema);
        return writer;
    }

    /// <summary>Opens the file at <paramref name="path"/> to append rows to it.</summary>
    /// <param name="path">A file this library wrote, or one of the same shape.</param>
    /// <param name="options">What the new rows look like; null takes the file's own policy.</param>
    /// <param name="cancellationToken">Cancels the reads and the first writes.</param>
    /// <returns>
    /// The writer, positioned after the file's rows; <c>RowCount</c> says where the append resumes.
    /// Abandoned, or disposed without completing, it truncates the file back to what it was.
    /// </returns>
    /// <exception cref="VortexUnsupportedException">The file's layout is not one this library can continue.</exception>
    public ValueTask<VortexFileWriter> OpenWriterAsync(string path, VortexWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ThrowIfDisposed();
        return VortexFileWriter.AppendInSessionAsync(path, options, this, cancellationToken);
    }

    /// <summary>
    /// Measures, on a sample of <paramref name="file"/>, every way the writer can write each of its
    /// columns, and ranks them for the reads <paramref name="goal"/> describes.
    /// </summary>
    /// <param name="file">The data to advise on: a table, whose columns of booleans, numbers, text and binary are measured.</param>
    /// <param name="goal">The reads the files written from such data will serve; null for <see cref="EncodingGoal.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the measurement.</param>
    /// <returns>Per column, the candidates measured and the one to take; and the options that take them.</returns>
    /// <remarks>
    /// A measurement, made once for a kind of data and not for every file: each candidate is a write
    /// and some reads of the sample, on this machine. The advice moves with the machine, and the
    /// write that takes it is as exact as any other.
    /// </remarks>
    /// <exception cref="ArgumentException">The file's root is not a struct.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A value of <paramref name="goal"/> describes no goal.</exception>
    public ValueTask<EncodingAdvice> AdviseAsync(VortexFile file, EncodingGoal? goal = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ThrowIfDisposed();
        return EncodingAdvisor.AdviseAsync(file, goal ?? EncodingGoal.Default, this, cancellationToken);
    }

    /// <summary>
    /// Measures, on a sample of <paramref name="rows"/>, every way the writer can write each column of
    /// <typeparamref name="TRecord"/>, and ranks them for the reads <paramref name="goal"/> describes.
    /// </summary>
    /// <typeparam name="TRecord">The record type; its schema is the columns advised on.</typeparam>
    /// <param name="rows">The data to advise on; only the sample's rows are written, into memory.</param>
    /// <param name="goal">The reads the files written from such data will serve; null for <see cref="EncodingGoal.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the measurement.</param>
    /// <returns>Per column, the candidates measured and the one to take; and the options that take them.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value of <paramref name="goal"/> describes no goal.</exception>
    public ValueTask<EncodingAdvice> AdviseAsync<TRecord>(ReadOnlyMemory<TRecord> rows, EncodingGoal? goal = null, CancellationToken cancellationToken = default)
        where TRecord : IVortexRecord<TRecord>
    {
        ThrowIfDisposed();
        return EncodingAdvisor.AdviseAsync(rows, goal ?? EncodingGoal.Default, this, cancellationToken);
    }

    /// <summary>
    /// Lets go of the files the session keeps mapped once closed, <see cref="Default"/>'s included:
    /// a file deleted since it was closed gets its disk space back. The files still open keep
    /// reading, and the next open of a file let go maps it again.
    /// </summary>
    public void ReleaseMappedFiles() => Mappings?.Clear();

    /// <summary>Clears the segment cache, lets the kept mappings go and trims the pool. A file of the session still open makes this throw.</summary>
    /// <returns>A completed task.</returns>
    /// <exception cref="InvalidOperationException">A file opened through the session is still open.</exception>
    public ValueTask DisposeAsync()
    {
        if (ReferenceEquals(this, Default))
        {
            return ValueTask.CompletedTask;
        }

        lock (_open)
        {
            foreach ((VortexFile _, string name) in _open)
            {
                throw new InvalidOperationException($"The session still has '{name}' open; dispose every file before the session.");
            }

            if (_disposed != 0)
            {
                return ValueTask.CompletedTask;
            }

            Volatile.Write(ref _disposed, 1);
        }

        Options.SegmentCache?.Clear();
        Mappings?.Clear();
        if (Options.MemoryPool is AlignedMemoryPool aligned && !ReferenceEquals(aligned, AlignedMemoryPool.Shared))
        {
            aligned.Dispose();
        }

        ReadGate.Dispose();
        return ValueTask.CompletedTask;
    }

    internal void Detach(VortexFile file) => _open.TryRemove(file, out _);

    /// <summary>Counts <paramref name="file"/> among the files the session has open, or closes it when the session was disposed while it opened.</summary>
    /// <param name="file">The file just opened.</param>
    /// <param name="name">What a refused dispose calls it.</param>
    /// <exception cref="ObjectDisposedException">The session was disposed while the file opened.</exception>
    internal async ValueTask AttachAsync(VortexFile file, string name)
    {
        file.Session = this;
        if (ReferenceEquals(this, Default))
        {
            return;
        }

        // Under the monitor the dispose checks the open files under: a file counted first refuses
        // the dispose, and one that comes after finds the session disposed.
        lock (_open)
        {
            if (_disposed == 0)
            {
                _open[file] = name;
                return;
            }
        }

        // What the file gives back as it closes, a mapping it took over, goes with the rest.
        await file.DisposeAsync().ConfigureAwait(false);
        Options.SegmentCache?.Clear();
        Mappings?.Clear();
        ThrowIfDisposed();
    }

    /// <summary>The options an open runs under: the caller's, with the session's index cache budget.</summary>
    /// <remarks>A copy only when the budgets differ, so that an open under the defaults allocates no options.</remarks>
    private VortexOpenOptions Effective(VortexOpenOptions? options)
    {
        VortexOpenOptions given = options ?? VortexOpenOptions.Default;
        return given.IndexCacheBytes == Options.IndexCacheBytes
            ? given
            : given with { IndexCacheBytes = Options.IndexCacheBytes };
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static VortexSession CreateDefault()
    {
        VortexSessionOptions options = new VortexSessionOptions();
        options.Freeze();
        return new VortexSession(options);
    }
}
