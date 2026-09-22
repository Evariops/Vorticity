using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
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
    private bool _frozen;

    internal VortexSessionOptions()
    {
    }

    /// <summary>
    /// Where every batch, segment and builder buffer comes from. The engine decodes into 64-byte
    /// aligned native blocks, so an <see cref="AlignedMemoryPool"/> serves it directly; another pool
    /// serves the builders and the owned batches, and the engine falls back to <see cref="AlignedMemoryPool.Shared"/>.
    /// </summary>
    public MemoryPool<byte> MemoryPool
    {
        get => _memoryPool;
        set => _memoryPool = Set(value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>Segments kept across scans, one budget for every file of the session; null keeps none.</summary>
    public SegmentCache? SegmentCache
    {
        get => _segmentCache;
        set => _segmentCache = Set(value);
    }

    /// <summary>The most reads in flight across every scan of every file of the session.</summary>
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
    /// <see cref="ScanOptions.DegreeOfParallelism"/> says otherwise. 1 by default: a library does not
    /// take a host's cores without being asked.
    /// </summary>
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
    }

    /// <summary>The process-wide session: the shared pool, no segment cache, 16 reads in flight, no parallelism, no extensions.</summary>
    public static VortexSession Default { get; } = CreateDefault();

    /// <summary>What this session owns, frozen.</summary>
    public VortexSessionOptions Options { get; }

    /// <summary>The bound on reads in flight across the session.</summary>
    internal SemaphoreSlim ReadGate { get; }

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

    /// <summary>Opens the file at <paramref name="path"/>, mapped into memory.</summary>
    /// <param name="path">A local file path.</param>
    /// <param name="options">What the open reads, trusts and refuses; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open file; the caller disposes it.</returns>
    /// <exception cref="VortexFormatException">The file is not a well-formed Vortex file.</exception>
    public async ValueTask<VortexFile> OpenAsync(string path, VortexOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ThrowIfDisposed();
        VortexFile file = await VortexFile.OpenAsync(path, Effective(options), cancellationToken).ConfigureAwait(false);
        Attach(file, path);
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
        Attach(file, source.GetType().Name);
        return file;
    }

    /// <summary>Starts a file at <paramref name="path"/>, replacing whatever is there.</summary>
    /// <param name="path">The destination.</param>
    /// <param name="schema">The file's columns.</param>
    /// <param name="options">What the file looks like; null for the defaults.</param>
    /// <returns>The writer; the caller completes and disposes it. Disposed without completing, it deletes the file.</returns>
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
    /// <param name="path">The destination, replaced if it exists.</param>
    /// <param name="options">What the file looks like; null for the defaults.</param>
    /// <returns>The writer; the caller completes and disposes it. Disposed without completing, it deletes the file.</returns>
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
    public ValueTask<VortexFileWriter> AppendAsync(string path, VortexWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ThrowIfDisposed();
        return VortexFileWriter.AppendInSessionAsync(path, options, this, cancellationToken);
    }

    /// <summary>Clears the segment cache and trims the pool. A file of the session still open makes this throw.</summary>
    /// <returns>A completed task.</returns>
    /// <exception cref="InvalidOperationException">A file opened through the session is still open.</exception>
    public ValueTask DisposeAsync()
    {
        if (ReferenceEquals(this, Default))
        {
            return ValueTask.CompletedTask;
        }

        foreach ((VortexFile _, string name) in _open)
        {
            throw new InvalidOperationException($"The session still has '{name}' open; dispose every file before the session.");
        }

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        Options.SegmentCache?.Clear();
        if (Options.MemoryPool is AlignedMemoryPool aligned && !ReferenceEquals(aligned, AlignedMemoryPool.Shared))
        {
            aligned.Dispose();
        }

        ReadGate.Dispose();
        return ValueTask.CompletedTask;
    }

    internal void Detach(VortexFile file) => _open.TryRemove(file, out _);

    private void Attach(VortexFile file, string name)
    {
        file.Session = this;
        if (!ReferenceEquals(this, Default))
        {
            _open[file] = name;
        }
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
