using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Enumeration;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Vorticity.IO;

namespace Vorticity.Dataset;

/// <summary>
/// An <see cref="IObjectStore"/> over a directory. A commit object, the key writers race for and
/// readers read before any version names it, is written beside its key under a name no key can have
/// and shown only once whole, by a rename refused when the key names an object, so a reader meets a
/// commit whole or not at all and a process that dies mid-commit leaves nothing under its key, only a
/// file beside it that a listing takes away a day later. A replacement is written beside its key and
/// renamed over it. Any other creation, under a key no other put shares and nothing reads before a
/// commit names it, claims its key and writes it in place under its lock, deleting it before the lock
/// goes when it fails. On a file system that can neither rename exclusively nor link, a commit is
/// claimed in place too, and a process that dies there leaves a short commit, which the dataset names
/// with <see cref="TornCommitException"/> and <see cref="VortexDataset.RemoveTornCommitAsync"/> removes.
/// </summary>
public sealed class FileObjectStore : IObjectStore
{
    /// <summary>How long a read waits for a put that still holds its object.</summary>
    private static readonly TimeSpan PutPatience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// What a file written beside its key adds to the key's path before a random part: a character past ASCII, which no
    /// key can hold, so that no listing ever shows such a file.
    /// </summary>
    private const string Beside = ".·";

    /// <summary>
    /// How old a file beside a key must be before a listing takes it away: a put that died left it there, since no put
    /// writes for a day.
    /// </summary>
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromDays(1);

    /// <summary>
    /// macOS's renamex_np with RENAME_EXCL: a rename refused when the new name is taken, which shows a created object
    /// whole in one step, at the price of a rename. Null elsewhere, and where the call cannot be found.
    /// </summary>
    private static readonly unsafe delegate* unmanaged<byte*, byte*, uint, int> RenameExclusiveMacOS =
        OperatingSystem.IsMacOS() && Environment.Is64BitProcess
            ? (delegate* unmanaged<byte*, byte*, uint, int>)FileInode.Export(["/usr/lib/libSystem.B.dylib"], "renamex_np")
            : null;

    /// <summary>Linux's renameat2 with RENAME_NOREPLACE, the same rename, from glibc 2.28. Null elsewhere.</summary>
    private static readonly unsafe delegate* unmanaged<int, byte*, int, byte*, uint, int> RenameExclusiveLinux =
        OperatingSystem.IsLinux() && Environment.Is64BitProcess
            ? (delegate* unmanaged<int, byte*, int, byte*, uint, int>)FileInode.Export(["libc.so.6", "libc.musl-x86_64.so.1", "libc.musl-aarch64.so.1"], "renameat2")
            : null;

    // renamex_np's flag that refuses a taken name, renameat2's, and the directory renameat2 resolves relative names in.
    private const uint RenameExcl = 0x4;
    private const uint RenameNoReplace = 0x1;
    private const int CurrentDirectory = -100;

    /// <summary>
    /// link(2): a second name for a file, refused when the name is taken: the same single step where an exclusive rename is
    /// missing, a link and an unlink, which costs more. Null on Windows, and where the call cannot be found.
    /// </summary>
    private static readonly unsafe delegate* unmanaged<byte*, byte*, int> LinkCall =
        OperatingSystem.IsWindows() || !Environment.Is64BitProcess ? null
        : OperatingSystem.IsMacOS() ? (delegate* unmanaged<byte*, byte*, int>)FileInode.Export(["/usr/lib/libSystem.B.dylib"], "link")
        : OperatingSystem.IsLinux() ? (delegate* unmanaged<byte*, byte*, int>)FileInode.Export(["libc.so.6", "libc.musl-x86_64.so.1", "libc.musl-aarch64.so.1"], "link")
        : null;

    /// <summary>How a creation shows its object, chosen at the first creation: an exclusive rename, a link, or a claim of the key.</summary>
    private enum Showing
    {
        Unknown = 0,
        Rename = 1,
        Link = 2,
        Claim = 3,
    }

    /// <summary>How this store's creations show their object (<see cref="Showing"/>), as an int for <see cref="Volatile"/>.</summary>
    private int _showing;

    /// <summary>
    /// Whether creations claim their key and write it in place, as they do on a file system that can neither rename
    /// exclusively nor link: what the tests of that path set, since a temporary directory does both.
    /// </summary>
    internal bool ClaimsKeys
    {
        init => _showing = value ? (int)Showing.Claim : 0;
    }

    /// <summary>Whether creations show their object by a link rather than an exclusive rename: what the tests of that path set.</summary>
    internal bool LinksKeys
    {
        init => _showing = value ? (int)Showing.Link : 0;
    }

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

    /// <summary>A local disk's: 0.1 ms a read among 64 in flight over 3 GB/s, a request worth some 4.7 KB.</summary>
    public CostProfile CostProfile => CostProfile.LocalDisk;

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

    /// <summary>The bytes past which a read waits for its file asynchronously; below, it reads in place.</summary>
    private const int SmallRead = 64 << 10;

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
            int got = available <= SmallRead
                ? RandomAccess.Read(handle, destination.Span[read..available], offset + read)
                : await RandomAccess.ReadAsync(handle, destination[read..available], offset + read, cancellationToken).ConfigureAwait(false);
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
    /// A commit object is written beside its key, under a name no key can have, and shown only once
    /// whole: an exclusive rename (renamex_np on macOS, renameat2 on Linux) or, where the file system
    /// has none, a link moves it to the key in one system call that fails when the key names an object,
    /// and Windows moves it without replacing, the same single step. A reader therefore meets a commit
    /// whole or not at all, a put that fails leaves nothing under its key, and no put answers for a
    /// file it did not write. A replacement is written beside its key and
    /// renamed over it. Any other creation claims its key and writes it in place, under the lock .NET
    /// takes after the open, and deletes it before letting the lock go when it fails: its key is its
    /// own, and nothing reads it before a commit names it. So is a commit on a file system that can
    /// neither rename exclusively nor link, where a reader may meet the commit being written and wait
    /// for its last byte, or, opening the key between the claim and the lock, read it torn. A put on a
    /// token's match is refused: no file system compares and renames in one step across processes.
    /// </remarks>
    public async ValueTask<PutOutcome> PutAsync(
        string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        SafeFileHandle handle;
        string path;
        string target;
        bool claims;
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

            // The file held for the key goes first: a read that names its token then meets the object as it is.
            path = PathOf(key);
            LetGo(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // A commit object's key is the one writers race for, and the one a reader reads before any
            // version names it, so a creation there is shown whole. Any other key a creation takes is
            // its own, which nothing reads before a commit names it: it is claimed and written in place,
            // which saves a rename, a tenth of a millisecond on a disk whose every operation an
            // endpoint agent inspects.
            claims = condition.Kind == PutConditionKind.IfAbsent
                && (!CommitKey.IsCommit(key) || ShowingIn(path) == Showing.Claim);
            target = claims ? path : string.Create(CultureInfo.InvariantCulture, $"{path}{Beside}{Guid.NewGuid():N}");
            try
            {
                handle = System.IO.File.OpenHandle(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    Durable ? FileOptions.Asynchronous | FileOptions.WriteThrough : FileOptions.Asynchronous);
            }
            catch (IOException refused) when (claims && HeldByAPut(refused))
            {
                // The claim took the key, and a reader opened the file before the lock .NET takes after
                // the open could: the empty file is this put's own, and goes. The put has not landed,
                // where answering Exists would send a commit to rebase on its own empty claim.
                TryDelete(path);
                throw new ObjectStoreException(
                    $"'{key}' was opened by a reader between its claim and its lock; the put did not land and can be made again.", refused);
            }
            catch (IOException) when (claims && System.IO.File.Exists(path))
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
                try
                {
                    await WriteAsync(handle, key, content, length, cancellationToken).ConfigureAwait(false);
                }
                catch when (claims)
                {
                    // The claim is this put's and the content is not whole: the key goes while the lock
                    // still keeps readers and RemoveTornCommitAsync out, so no other put's object can have
                    // taken the key in between, as it could once the lock went with the file.
                    TryDelete(path);
                    throw;
                }
            }
        }
        catch (Exception caught)
        {
            failure = caught;
            if (!claims)
            {
                // The file beside the key is this put's alone.
                TryDelete(target);
            }

            throw;
        }
        finally
        {
            await content.CompleteAsync(failure).ConfigureAwait(false);
        }

        if (claims)
        {
            return PutOutcome.Created;
        }

        if (condition.Kind != PutConditionKind.IfAbsent)
        {
            // A rename over the object, which a reader sees whole on either side of.
            System.IO.File.Move(target, path, overwrite: true);
            return PutOutcome.Created;
        }

        return Publish(key, target, path, ShowingIn(path));
    }

    /// <summary>
    /// Writes the content into the file, then stamps its last-write time, the token's second half.
    /// </summary>
    private static async ValueTask WriteAsync(
        SafeFileHandle handle, string key, PipeReader content, long length, CancellationToken cancellationToken)
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

    /// <summary>
    /// Shows the file written beside the key under the key, in one step refused when the key names an
    /// object: a move that does not replace on Windows, an exclusive rename on Unix where the file
    /// system has one, and a link elsewhere. The name beside the key is gone afterwards either way.
    /// </summary>
    private static PutOutcome Publish(string key, string target, string path, Showing showing)
    {
        bool shown;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // MoveFileEx without MOVEFILE_REPLACE_EXISTING: refused when the key names a file.
                System.IO.File.Move(target, path, overwrite: false);
                return PutOutcome.Created;
            }

            shown = showing == Showing.Rename ? RenameExclusive(target, path) : Link(target, path);
        }
        catch (IOException) when (System.IO.File.Exists(path))
        {
            TryDelete(target);
            return PutOutcome.Exists;
        }
        catch
        {
            TryDelete(target);
            throw;
        }

        // Renamed, the name beside the key is gone already. Linked, it goes now. Refused, the key names
        // another put's object, and the file this put wrote goes.
        if (shown)
        {
            if (showing == Showing.Link)
            {
                TryDelete(target);
            }

            return PutOutcome.Created;
        }

        TryDelete(target);
        if (System.IO.File.Exists(path))
        {
            return PutOutcome.Exists;
        }

        throw new ObjectStoreException($"'{key}' could not be shown from the file written for it, and names no object.");
    }

    /// <summary>
    /// How a creation shows its object in the directory of <paramref name="path"/>: by an exclusive
    /// rename where the file system has one (APFS and HFS+, ext4, XFS, Btrfs, tmpfs), by a link where
    /// it has only links, and by a claim of the key where it has neither (FAT, exFAT, some network
    /// shares). Windows moves without replacing, which is the rename. Asked once, by trying each in
    /// the key's directory and looking at what each did, rather than by reading error codes.
    /// </summary>
    private Showing ShowingIn(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return Showing.Rename;
        }

        int known = Volatile.Read(ref _showing);
        if (known == 0)
        {
            known = (int)Probe(Path.GetDirectoryName(path)!);
            Volatile.Write(ref _showing, known);
        }

        return (Showing)known;
    }

    /// <summary>What the file system of <paramref name="directory"/> does with an exclusive rename and with a link.</summary>
    private static Showing Probe(string directory)
    {
        string first = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"{Beside}{Guid.NewGuid():N}"));
        string taken = first + "-taken";
        string renamed = first + "-renamed";
        string linked = first + "-linked";
        try
        {
            Touch(first);
            Touch(taken);

            // An exclusive rename must refuse the taken name, leaving both files, and take a free one.
            if (!RenameExclusive(first, taken) && System.IO.File.Exists(first)
                && RenameExclusive(first, renamed) && !System.IO.File.Exists(first) && System.IO.File.Exists(renamed))
            {
                return Showing.Rename;
            }

            if (!System.IO.File.Exists(first))
            {
                Touch(first);
            }

            return Link(first, linked) && System.IO.File.Exists(linked) ? Showing.Link : Showing.Claim;
        }
        catch (IOException)
        {
            return Showing.Claim;
        }
        catch (UnauthorizedAccessException)
        {
            return Showing.Claim;
        }
        finally
        {
            TryDelete(first);
            TryDelete(taken);
            TryDelete(renamed);
            TryDelete(linked);
        }

        static void Touch(string file)
        {
            using (System.IO.File.OpenHandle(file, FileMode.CreateNew, FileAccess.Write))
            {
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="to"/> now names the file <paramref name="from"/> named, by an exclusive
    /// rename: false when <paramref name="to"/> was taken, or when the call is missing or refused.
    /// </summary>
    private static unsafe bool RenameExclusive(string from, string to)
    {
        if (RenameExclusiveMacOS == null && RenameExclusiveLinux == null)
        {
            return false;
        }

        using CNames names = new CNames(from, to);
        fixed (byte* bytes = names.Bytes)
        {
            return RenameExclusiveMacOS != null
                ? RenameExclusiveMacOS(bytes, bytes + names.Second, RenameExcl) == 0
                : RenameExclusiveLinux(CurrentDirectory, bytes, CurrentDirectory, bytes + names.Second, RenameNoReplace) == 0;
        }
    }

    /// <summary>
    /// Whether <paramref name="created"/> now names the file <paramref name="existing"/> names, by
    /// link(2): false when it was taken, or when the call is missing or refused.
    /// </summary>
    private static unsafe bool Link(string existing, string created)
    {
        if (LinkCall == null)
        {
            return false;
        }

        using CNames names = new CNames(existing, created);
        fixed (byte* bytes = names.Bytes)
        {
            return LinkCall(bytes, bytes + names.Second) == 0;
        }
    }

    /// <summary>Two paths in UTF-8, each ended by a zero as C reads a path, one after the other in a rented array.</summary>
    private readonly struct CNames : IDisposable
    {
        public CNames(string first, string second)
        {
            int firstBytes = Encoding.UTF8.GetByteCount(first) + 1;
            int secondBytes = Encoding.UTF8.GetByteCount(second) + 1;
            Bytes = ArrayPool<byte>.Shared.Rent(firstBytes + secondBytes);
            Encoding.UTF8.GetBytes(first, Bytes);
            Bytes[firstBytes - 1] = 0;
            Encoding.UTF8.GetBytes(second, Bytes.AsSpan(firstBytes));
            Bytes[firstBytes + secondBytes - 1] = 0;
            Second = firstBytes;
        }

        /// <summary>The two paths.</summary>
        public byte[] Bytes { get; }

        /// <summary>Where the second path starts.</summary>
        public int Second { get; }

        public void Dispose() => ArrayPool<byte>.Shared.Return(Bytes);
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

        // Each file's key read from its directory, relative to the root, made once a directory, and its name: a string for a
        // key kept alone. The full path, made relative and its separators replaced, a file at a time, took three strings a
        // file and more than the walk itself: the newest of 20,000 commits listed in 16.7 ms and 7.83 MB, the walk 6.9 ms.
        List<string> keys = [];
        List<string>? abandoned = null;
        DateTime aged = DateTime.UtcNow - AbandonedAfter;
        string? directory = null;
        string relative = string.Empty;
        FileSystemEnumerable<string> files = new FileSystemEnumerable<string>(
            walked, (ref FileSystemEntry entry) => KeyOf(Relative(ref entry), entry.FileName), Walked)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
            {
                if (entry.IsDirectory)
                {
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();
                string at = Relative(ref entry);
                ReadOnlySpan<char> name = entry.FileName;
                int length = (at.Length == 0 ? 0 : at.Length + 1) + name.Length;
                Span<char> key = length <= 512 ? stackalloc char[length] : new char[length];
                WriteKey(key, at, name);

                // An object being written lies beside its key, under a name no key can have. One a day old
                // was left by a put that died, and goes once the walk is over.
                if (key.IndexOf(Beside, StringComparison.Ordinal) >= 0)
                {
                    if (entry.LastWriteTimeUtc.UtcDateTime < aged)
                    {
                        (abandoned ??= []).Add(entry.ToFullPath());
                    }

                    return false;
                }

                return key.StartsWith(prefix, StringComparison.Ordinal) && (startAfter is not { } after || key.SequenceCompareTo(after) > 0);
            },
        };

        foreach (string key in files)
        {
            if (ObjectKey.IsValid(key))
            {
                keys.Add(key);
            }
        }

        if (abandoned is not null)
        {
            foreach (string file in abandoned)
            {
                TryDelete(file);
            }
        }

        keys.Sort(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            yield return key;
        }

        // The directory of the entry relative to the root, '/' between its names, made once a directory; empty for the root.
        string Relative(ref FileSystemEntry entry)
        {
            ReadOnlySpan<char> at = entry.Directory;
            if (directory is null || !at.SequenceEqual(directory))
            {
                directory = at.ToString();
                relative = Path.GetRelativePath(_root, directory).Replace(Path.DirectorySeparatorChar, '/');
                relative = relative == "." ? string.Empty : relative;
            }

            return relative;
        }
    }

    /// <summary>What a walk of the store's files skips: nothing, as <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/> did, in every directory.</summary>
    private static readonly EnumerationOptions Walked = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };

    /// <summary>The key of the file <paramref name="name"/> in the directory <paramref name="directory"/>, relative to the root.</summary>
    private static string KeyOf(string directory, ReadOnlySpan<char> name)
    {
        int length = (directory.Length == 0 ? 0 : directory.Length + 1) + name.Length;
        Span<char> key = length <= 512 ? stackalloc char[length] : new char[length];
        WriteKey(key, directory, name);
        return key.ToString();
    }

    /// <summary>Writes into <paramref name="key"/> the key of the file <paramref name="name"/> in the directory <paramref name="directory"/>.</summary>
    private static void WriteKey(Span<char> key, string directory, ReadOnlySpan<char> name)
    {
        int at = 0;
        if (directory.Length > 0)
        {
            directory.CopyTo(key);
            key[directory.Length] = '/';
            at = directory.Length + 1;
        }

        name.CopyTo(key[at..]);
        key[at..].Replace(Path.DirectorySeparatorChar, '/');
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
