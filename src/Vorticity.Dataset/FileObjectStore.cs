using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Vorticity.IO;

namespace Vorticity.Dataset;

/// <summary>
/// An <see cref="IObjectStore"/> over a directory. Claiming a key is atomic, but writing its content
/// is not: a read that meets a put still writing waits for it, and a process that dies mid-write
/// leaves a short object under a taken key. The dataset names such a commit with
/// <see cref="TornCommitException"/>, and <see cref="VortexDataset.RemoveTornCommitAsync"/> removes it.
/// </summary>
public sealed class FileObjectStore : IObjectStore
{
    /// <summary>How long a read waits for a put that still holds its object.</summary>
    private static readonly TimeSpan PutPatience = TimeSpan.FromSeconds(10);

    /// <summary>The <c>HResult</c> of an open refused by a lock another handle holds: Windows' sharing violation, and <c>EWOULDBLOCK</c> elsewhere.</summary>
    private const int SharingViolation = unchecked((int)0x80070020);

    private const int LinuxWouldBlock = 11;

    private const int BsdWouldBlock = 35;

    /// <summary>The bytes a write of an object's content takes when its pieces come smaller: a megabyte.</summary>
    private const int GatheredBytes = 1 << 20;

    /// <summary>The bytes from which a piece of an object's content is written as it lies rather than gathered.</summary>
    private const int WrittenAsItLies = 64 << 10;

    /// <summary>The last <see cref="Stamp"/> given, in ticks.</summary>
    private static long s_lastStamp;

    private readonly string _root;
    private bool _disposed;

    // The objects read lately, held open, the most recently read first; under the gate.
    private readonly Dictionary<string, HeldFile> _held = new Dictionary<string, HeldFile>(StringComparer.Ordinal);
    private readonly LinkedList<HeldFile> _recent = [];
    private readonly Lock _heldGate = new Lock();

    /// <summary>Opens a store over a directory, creating it if needed.</summary>
    /// <param name="root">The directory the objects live in.</param>
    public FileObjectStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    /// <summary>The directory the objects live in.</summary>
    public string Root => _root;

    /// <summary>
    /// Whether a created object's bytes are flushed to the device before the put returns. False by
    /// default; set it when the store is the system of record.
    /// </summary>
    public bool Durable { get; init; }

    /// <summary>
    /// How many objects the store keeps open after reading them, the most recently read: a later read that names an
    /// object's token reads the file already open, where each read opened, queried and closed it, a tenth of a millisecond
    /// on the reading thread. A read with no token, as an object's first is, opens the file again and holds that one. A put
    /// or a delete of a key by this store lets its file go first. 64 by default; 0 opens the file at every read.
    /// </summary>
    public int HeldFiles
    {
        get => _heldFiles;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _heldFiles = value;
        }
    }

    private readonly int _heldFiles = 64;

    /// <summary>
    /// Reads suffixes, lists in order, creates a key only while it is free by the file system's
    /// exclusive creation, and keeps no locks, which each entry of its listing therefore reports. It
    /// cannot replace on a token's match: no file system compares and renames in one step across
    /// processes.
    /// </summary>
    public ObjectStoreCapabilities Capabilities =>
        ObjectStoreCapabilities.ReadsSuffix | ObjectStoreCapabilities.ListsInOrder | ObjectStoreCapabilities.CreatesConditionally;

    /// <inheritdoc/>
    public ValueTask<ObjectRead> ReadAsync(
        string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return ReadCoreAsync(key, offset, destination, ifToken, tail: false, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken) =>
        ReadCoreAsync(key, 0, destination, null, tail: true, cancellationToken);

    /// <summary>A read from <paramref name="offset"/>, or of the last bytes with <paramref name="tail"/>, on one handle that also gives the length.</summary>
    private async ValueTask<ObjectRead> ReadCoreAsync(
        string key, long offset, Memory<byte> destination, string? ifToken, bool tail, CancellationToken cancellationToken)
    {
        ObjectKey.Check(key);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        // A read that names the token of an object held open reads that file: the bytes of the object the token names,
        // whatever a rename over the key has put there since.
        if (!tail && ifToken is not null && Lease(key, ifToken) is { } held)
        {
            try
            {
                return await ReadAtAsync(held.Handle, key, offset, destination, held.Length, held.Token, tail: false, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Return(held);
            }
        }

        string path = PathOf(key);
        SafeFileHandle handle;
        try
        {
            handle = await OpenToReadAsync(path, key, _heldFiles > 0, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException cause)
        {
            throw new ObjectNotFoundException($"No object at '{key}'.", cause) { Key = key };
        }
        catch (DirectoryNotFoundException cause)
        {
            throw new ObjectNotFoundException($"No object at '{key}'.", cause) { Key = key };
        }

        bool kept = false;
        try
        {
            // The length and the date of the file this handle opened, which a rename over the key since
            // leaves as they were: a token read from the path could name the next object.
            long size = RandomAccess.GetLength(handle);
            string token = Token(size, System.IO.File.GetLastWriteTimeUtc(handle));
            if (ifToken is not null && !string.Equals(ifToken, token, StringComparison.Ordinal))
            {
                throw ObjectStoreExtensions.Changed(key, ifToken, token);
            }

            ObjectRead read = await ReadAtAsync(handle, key, offset, destination, size, token, tail, cancellationToken).ConfigureAwait(false);
            kept = Hold(key, handle, size, token);
            return read;
        }
        finally
        {
            if (!kept)
            {
                handle.Dispose();
            }
        }
    }

    /// <summary>
    /// Reads <paramref name="destination"/> from <paramref name="offset"/> of the file <paramref name="handle"/> opened, of
    /// <paramref name="size"/> bytes under <paramref name="token"/>, or its last bytes with <paramref name="tail"/>.
    /// </summary>
    private static async ValueTask<ObjectRead> ReadAtAsync(
        SafeFileHandle handle, string key, long offset, Memory<byte> destination, long size, string token, bool tail, CancellationToken cancellationToken)
    {
        if (tail)
        {
            offset = Math.Max(0, size - destination.Length);
        }
        else if (offset >= size && !(offset == 0 && size == 0))
        {
            throw ObjectStoreExtensions.PastEnd(key, offset, size);
        }

        int available = (int)Math.Min(destination.Length, size - offset);
        int read = 0;
        while (read < available)
        {
            int got = await RandomAccess
                .ReadAsync(handle, destination[read..available], offset + read, cancellationToken)
                .ConfigureAwait(false);
            if (got == 0)
            {
                throw new ObjectStoreException(
                    $"'{key}' ended after {read} of {available} bytes; it changed under the read.");
            }

            read += got;
        }

        return new ObjectRead(available, size, token);
    }

    /// <summary>
    /// Keeps <paramref name="handle"/>, just read under <paramref name="token"/>, open for the next reads of
    /// <paramref name="key"/> that name that token; false, the handle the caller's to close, when the store keeps none or
    /// already holds that file.
    /// </summary>
    private bool Hold(string key, SafeFileHandle handle, long size, string token)
    {
        if (_heldFiles == 0)
        {
            return false;
        }

        List<HeldFile>? closed = null;
        lock (_heldGate)
        {
            if (_disposed)
            {
                return false;
            }

            if (_held.TryGetValue(key, out HeldFile? held))
            {
                if (string.Equals(held.Token, token, StringComparison.Ordinal))
                {
                    _recent.Remove(held.Node);
                    _recent.AddFirst(held.Node);
                    return false;
                }

                Retire(held, ref closed);
            }

            HeldFile kept = new HeldFile(key, handle, size, token);
            _held[key] = kept;
            _recent.AddFirst(kept.Node);
            while (_held.Count > _heldFiles)
            {
                Retire(_recent.Last!.Value, ref closed);
            }
        }

        Close(closed);
        return true;
    }

    /// <summary>The file held for <paramref name="key"/> under <paramref name="token"/>, leased to a read; null when none is.</summary>
    private HeldFile? Lease(string key, string token)
    {
        lock (_heldGate)
        {
            if (!_held.TryGetValue(key, out HeldFile? held) || !string.Equals(held.Token, token, StringComparison.Ordinal))
            {
                return null;
            }

            held.Leases++;
            _recent.Remove(held.Node);
            _recent.AddFirst(held.Node);
            return held;
        }
    }

    /// <summary>Ends a read's lease of <paramref name="held"/>: a file let go meanwhile closes with its last lease.</summary>
    private void Return(HeldFile held)
    {
        bool close;
        lock (_heldGate)
        {
            close = --held.Leases == 0 && held.Retired;
        }

        if (close)
        {
            held.Handle.Dispose();
        }
    }

    /// <summary>Lets go of the file held for <paramref name="key"/>, before this store writes or deletes the key.</summary>
    private void LetGo(string key)
    {
        List<HeldFile>? closed = null;
        lock (_heldGate)
        {
            if (_held.TryGetValue(key, out HeldFile? held))
            {
                Retire(held, ref closed);
            }
        }

        Close(closed);
    }

    /// <summary>Takes <paramref name="held"/> out of the files held, to <paramref name="closed"/> when no read leases it; under the gate.</summary>
    private void Retire(HeldFile held, ref List<HeldFile>? closed)
    {
        _held.Remove(held.Key);
        _recent.Remove(held.Node);
        held.Retired = true;
        if (held.Leases == 0)
        {
            (closed ??= []).Add(held);
        }
    }

    private static void Close(List<HeldFile>? closed)
    {
        foreach (HeldFile held in closed ?? [])
        {
            held.Handle.Dispose();
        }
    }

    /// <summary>A file held open after a read: its key, handle, length and token, the reads that lease it, its place among the recent.</summary>
    private sealed class HeldFile
    {
        internal HeldFile(string key, SafeFileHandle handle, long length, string token)
        {
            Key = key;
            Handle = handle;
            Length = length;
            Token = token;
            Node = new LinkedListNode<HeldFile>(this);
        }

        internal string Key { get; }

        internal SafeFileHandle Handle { get; }

        internal long Length { get; }

        internal string Token { get; }

        internal LinkedListNode<HeldFile> Node { get; }

        internal int Leases { get; set; }

        internal bool Retired { get; set; }
    }

    /// <inheritdoc/>
    public ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        ObjectKey.Check(key);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        FileInfo info = new FileInfo(PathOf(key));
        return new ValueTask<ObjectHead?>(
            info.Exists ? new ObjectHead(info.Length, Token(info.Length, info.LastWriteTimeUtc), new DateTimeOffset(info.LastWriteTimeUtc)) : null);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A creation claims the key before it writes, so a read that meets it waits for its last byte. A
    /// put that replaces writes beside the object and renames over it, so a reader sees the old bytes
    /// or the new, whole. A put on a token's match is refused: no file system compares and renames in
    /// one step across processes.
    /// </remarks>
    public async ValueTask<PutOutcome> PutAsync(
        string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        SafeFileHandle handle;
        string path;
        string target;
        try
        {
            ObjectKey.Check(key);
            ArgumentOutOfRangeException.ThrowIfLessThan(length, -1);
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (condition.Kind == PutConditionKind.IfMatch)
            {
                throw new NotSupportedException(
                    $"A {nameof(FileObjectStore)} cannot replace '{key}' only while it keeps a token: no file system compares and renames in one step across processes.");
            }

            // A replacement is written beside the object under a name no key can have, a character
            // past ASCII in it, so that no listing ever shows it. The file held for the key goes first: a read
            // that names its token then meets the object as it is.
            path = PathOf(key);
            LetGo(key);
            target = condition.Kind == PutConditionKind.IfAbsent
                ? path
                : string.Create(CultureInfo.InvariantCulture, $"{path}.·{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                handle = System.IO.File.OpenHandle(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    Durable ? FileOptions.Asynchronous | FileOptions.WriteThrough : FileOptions.Asynchronous);
            }
            catch (IOException) when (condition.Kind == PutConditionKind.IfAbsent && System.IO.File.Exists(path))
            {
                await content.CompleteAsync().ConfigureAwait(false);
                return PutOutcome.Exists;
            }
        }
        catch (Exception refused)
        {
            await content.CompleteAsync(refused).ConfigureAwait(false);
            throw;
        }

        Exception? failure = null;
        try
        {
            using (handle)
            {
                if (length > 0)
                {
                    RandomAccess.SetLength(handle, length);
                }

                // A piece of the content of some size goes to the file as it lies; smaller ones, which a pipe of small
                // segments hands over, are gathered into one write, so that an object costs a write a megabyte, where a
                // pipe of 4 KiB segments cost one every 4 KiB, half a million for an object of 2 GB.
                byte[]? gathered = null;
                int held = 0;
                long written = 0;
                try
                {
                    while (true)
                    {
                        ReadResult result = await content.ReadAsync(cancellationToken).ConfigureAwait(false);
                        ReadOnlySequence<byte> buffer = result.Buffer;
                        if (length >= 0 && written + held + buffer.Length > length)
                        {
                            throw new ObjectStoreException(
                                $"'{key}' was announced as {length} bytes and its content runs past them.");
                        }

                        foreach (ReadOnlyMemory<byte> segment in buffer)
                        {
                            if (segment.Length >= WrittenAsItLies)
                            {
                                if (held > 0)
                                {
                                    await RandomAccess.WriteAsync(handle, gathered.AsMemory(0, held), written, cancellationToken).ConfigureAwait(false);
                                    written += held;
                                    held = 0;
                                }

                                await RandomAccess.WriteAsync(handle, segment, written, cancellationToken).ConfigureAwait(false);
                                written += segment.Length;
                                continue;
                            }

                            gathered ??= ArrayPool<byte>.Shared.Rent(GatheredBytes);
                            ReadOnlyMemory<byte> rest = segment;
                            while (!rest.IsEmpty)
                            {
                                int take = Math.Min(GatheredBytes - held, rest.Length);
                                rest.Span[..take].CopyTo(gathered.AsSpan(held));
                                rest = rest[take..];
                                held += take;
                                if (held == GatheredBytes)
                                {
                                    await RandomAccess.WriteAsync(handle, gathered.AsMemory(0, held), written, cancellationToken).ConfigureAwait(false);
                                    written += held;
                                    held = 0;
                                }
                            }
                        }

                        content.AdvanceTo(buffer.End);
                        if (result.IsCanceled)
                        {
                            throw new OperationCanceledException($"The content of '{key}' was cancelled after {written + held} bytes.");
                        }

                        if (result.IsCompleted)
                        {
                            break;
                        }
                    }

                    if (held > 0)
                    {
                        await RandomAccess.WriteAsync(handle, gathered.AsMemory(0, held), written, cancellationToken).ConfigureAwait(false);
                        written += held;
                        held = 0;
                    }
                }
                finally
                {
                    if (gathered is not null)
                    {
                        ArrayPool<byte>.Shared.Return(gathered);
                    }
                }

                if (length >= 0 && written != length)
                {
                    throw new ObjectStoreException($"'{key}' was announced as {length} bytes and its content ended after {written}.");
                }

                System.IO.File.SetLastWriteTimeUtc(handle, Stamp());
            }
        }
        catch (Exception caught)
        {
            // The claim is ours and the content is not: take the key back, or drop the replacement,
            // rather than leave a torn object behind.
            failure = caught;
            TryDelete(target);
            throw;
        }
        finally
        {
            await content.CompleteAsync(failure).ConfigureAwait(false);
        }

        if (!ReferenceEquals(target, path))
        {
            // A rename over the object, which a reader sees whole on either side of.
            System.IO.File.Move(target, path, overwrite: true);
        }

        return PutOutcome.Created;
    }

    /// <inheritdoc/>
    public ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (string key in keys)
        {
            ObjectKey.Check(key);
        }

        foreach (string key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = PathOf(key);
            LetGo(key);
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ObjectListing> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return System.Linq.AsyncEnumerable.ToAsyncEnumerable(Listed(prefix, startAfter, cancellationToken));
    }

    /// <summary>The keys of <see cref="Keys"/> with their heads, each read as it is reached; a key deleted meanwhile is skipped.</summary>
    private IEnumerable<ObjectListing> Listed(string prefix, string? startAfter, CancellationToken cancellationToken)
    {
        foreach (string key in Keys(prefix, startAfter, cancellationToken))
        {
            FileInfo info = new FileInfo(PathOf(key));
            if (info.Exists)
            {
                yield return new ObjectListing(key, new ObjectHead(info.Length, Token(info.Length, info.LastWriteTimeUtc), new DateTimeOffset(info.LastWriteTimeUtc)), LockReported: true);
            }
        }
    }

    /// <summary>
    /// The keys under a prefix, sorted: a directory walk has no order, so the ordinal order callers
    /// rely on is imposed here, over one walk, when the enumeration starts.
    /// </summary>
    private IEnumerable<string> Keys(string prefix, string? startAfter, CancellationToken cancellationToken)
    {
        // Only the directory the prefix names can hold its keys, so a listing of the commits, which
        // every open and commit makes, walks the commits and not every data object. A prefix whose
        // directory is no key's walks the root, and the filter below decides alone.
        string walked = _root;
        int slash = prefix.LastIndexOf('/');
        if (slash > 0 && ObjectKey.IsValid(prefix[..slash]))
        {
            walked = PathOf(prefix[..slash]);
            if (!Directory.Exists(walked))
            {
                yield break;
            }
        }

        List<string> keys = [];
        foreach (string file in Directory.EnumerateFiles(walked, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string key = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');

            // A replacement being written lies under a name no key can have.
            if (key.StartsWith(prefix, StringComparison.Ordinal)
                && (startAfter is not { } after || string.CompareOrdinal(key, after) > 0)
                && ObjectKey.IsValid(key))
            {
                keys.Add(key);
            }
        }

        keys.Sort(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            yield return key;
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        List<HeldFile>? closed = null;
        lock (_heldGate)
        {
            _disposed = true;
            foreach (HeldFile held in new List<HeldFile>(_held.Values))
            {
                Retire(held, ref closed);
            }
        }

        Close(closed);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Opens an object to read it. A put holds its object exclusively until its last byte, so a read
    /// that meets one waits for the put to let go, and then reads the object whole.
    /// </summary>
    private static async ValueTask<SafeFileHandle> OpenToReadAsync(string path, string key, bool kept, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        int pause = 1;
        while (true)
        {
            try
            {
                // A file the store may hold open lets a rename or a delete of its key go through, as a store in the cloud does.
                return System.IO.File.OpenHandle(path, FileMode.Open, FileAccess.Read, kept ? FileShare.Read | FileShare.Delete : FileShare.Read, FileOptions.Asynchronous);
            }
            catch (IOException held) when (HeldByAPut(held))
            {
                if (Stopwatch.GetElapsedTime(started) >= PutPatience)
                {
                    throw new ObjectStoreException(
                        $"'{key}' is still being written: its put has held it for {PutPatience.TotalSeconds:0} seconds.", held);
                }

                await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
                pause = Math.Min(pause * 2, 64);
            }
        }
    }

    /// <summary>Whether an open failed because another handle holds the file exclusively, as a put does.</summary>
    private static bool HeldByAPut(IOException failure) =>
        failure.HResult == (OperatingSystem.IsWindows() ? SharingViolation
            : OperatingSystem.IsLinux() || OperatingSystem.IsAndroid() ? LinuxWouldBlock : BsdWouldBlock);

    /// <summary>The object's path, under the root and never outside it.</summary>
    private string PathOf(string key)
    {
        string path = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_root, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{key}' resolves outside the store's root.", nameof(key));
        }

        return path;
    }

    /// <summary>
    /// Length and last-write time: an object is never modified, so the only change this has to
    /// catch is a key written again, whose stamp differs (<see cref="Stamp"/>).
    /// </summary>
    private static string Token(long length, DateTime lastWrite) => string.Create(
        CultureInfo.InvariantCulture,
        $"{length:x}-{lastWrite.Ticks:x}");

    /// <summary>
    /// The last-write time a put stamps on its object before it shows it: now, or a tick past the
    /// previous stamp, so that two objects this process writes under one key never share a token, however
    /// coarse the file system's own clock. Linux dates a write by a clock that moves every few
    /// milliseconds.
    /// </summary>
    private static DateTime Stamp()
    {
        long now = DateTime.UtcNow.Ticks;
        while (true)
        {
            long last = Volatile.Read(ref s_lastStamp);
            long next = Math.Max(now, last + 1);
            if (Interlocked.CompareExchange(ref s_lastStamp, next, last) == last)
            {
                return new DateTime(next, DateTimeKind.Utc);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (IOException)
        {
            // The caller's own exception is on its way up and carries the reason; throwing over it
            // would hide that.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning: nothing here is recoverable, and none of it is the failure the caller
            // is about to see.
        }
    }
}
