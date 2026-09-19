// The local case of the store seam - docs/13-dataset.md §11, "three implementations ship here: the
// file system, an in-memory store ... and a counting decorator".
//
// HOW `PutIfAbsent` IS ATOMIC HERE, since §11 requires it and a file system offers no such call by
// that name: the key is claimed with `FileMode.CreateNew`, which is `O_CREAT | O_EXCL` on every
// platform this runs on -- the kernel creates the file or tells us someone else did, with no window
// between the two. What is NOT atomic is the content: a process that dies between the claim and the
// last byte leaves a short object under a taken key. That is the torn tail of 02 §6 and of 13 §7,
// and the format answers it -- a commit object ends with an XXH3-64 over itself (§3), a data object
// with the postscript the reader validates -- so a torn object is DETECTED rather than prevented.
// A store that can prevent it (an S3 multipart upload's completion) should; this one says what it
// does instead of claiming more.
//
// THE KEY IS A RELATIVE PATH, and `ObjectKey.Check` is what makes that safe: no leading slash, no
// `..`, no backslash, no empty segment. Without those rules a key could name a file outside the
// root, which is the one bug a store like this must not have.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>An <see cref="IObjectStore"/> over a directory.</summary>
public sealed class FileObjectStore : IObjectStore
{
    private readonly string _root;
    private bool _disposed;

    /// <summary>Opens a store over <paramref name="root"/>, creating the directory if needed.</summary>
    /// <param name="root">The directory that holds the objects.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is empty.</exception>
    public FileObjectStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    /// <summary>The directory the objects live in.</summary>
    public string Root => _root;

    /// <summary>
    /// Whether a created object's bytes are flushed to the device before the put returns.
    /// </summary>
    /// <remarks>
    /// False by default, which is what a test wants and what a local dataset can afford: the
    /// content's durability is the file system's business, and the format detects a torn object
    /// either way. Set it when the store is the system of record.
    /// </remarks>
    public bool Durable { get; init; }

    /// <inheritdoc/>
    public async ValueTask<ObjectRange> GetRangeAsync(
        string key, long offset, int length, CancellationToken cancellationToken)
    {
        ObjectKey.Check(key);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        string path = PathOf(key);
        FileStream stream;
        try
        {
            stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.Asynchronous);
        }
        catch (FileNotFoundException cause)
        {
            throw new ObjectNotFoundException($"No object at '{key}'.", cause) { Key = key };
        }
        catch (DirectoryNotFoundException cause)
        {
            throw new ObjectNotFoundException($"No object at '{key}'.", cause) { Key = key };
        }

        await using (stream.ConfigureAwait(false))
        {
            long size = stream.Length;
            if (offset >= size && !(offset == 0 && size == 0))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(offset), offset, $"'{key}' holds {size} bytes, so the range starts past its end.");
            }

            int available = (int)Math.Min(length, size - offset);
            byte[] bytes = new byte[available];
            int read = 0;
            while (read < available)
            {
                int got = await System.IO.RandomAccess
                    .ReadAsync(stream.SafeFileHandle, bytes.AsMemory(read), offset + read, cancellationToken)
                    .ConfigureAwait(false);
                if (got == 0)
                {
                    throw new ObjectStoreException(
                        $"'{key}' ended after {read} of {available} bytes; it changed under the read.");
                }

                read += got;
            }

            return ObjectRange.CopyOf(bytes, Token(new FileInfo(path)));
        }
    }

    /// <inheritdoc/>
    public ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        ObjectKey.Check(key);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        FileInfo info = new FileInfo(PathOf(key));
        return new ValueTask<ObjectHead?>(
            info.Exists ? new ObjectHead(info.Length, Token(info), new DateTimeOffset(info.LastWriteTimeUtc)) : null);
    }

    /// <inheritdoc/>
    public async ValueTask<PutOutcome> PutIfAbsentAsync(
        string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        ObjectKey.Check(key);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        string path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                Durable ? FileOptions.Asynchronous | FileOptions.WriteThrough : FileOptions.Asynchronous);
        }
        catch (IOException) when (System.IO.File.Exists(path))
        {
            return PutOutcome.Exists;
        }

        try
        {
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // The claim is ours and the content is not there: take the key back rather than leave a
            // torn object for a reader to detect. A process that dies here cannot do this, which is
            // why the format checks anyway.
            TryDelete(path);
            throw;
        }

        return PutOutcome.Created;
    }

    /// <inheritdoc/>
    public ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        ObjectKey.Check(key);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        string path = PathOf(key);
        if (!System.IO.File.Exists(path))
        {
            return new ValueTask<bool>(false);
        }

        System.IO.File.Delete(path);
        return new ValueTask<bool>(true);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> ListAsync(
        string prefix, string? startAfter, int max, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        // ENUMERATED AND SORTED, which a real store does not have to do: its index is already in
        // key order. A directory walk is not, so the order §3's "newest commit sorts first" relies
        // on has to be imposed here.
        List<string> keys = [];
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            string key = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (key.StartsWith(prefix, StringComparison.Ordinal)
                && (startAfter is not { } after || string.CompareOrdinal(key, after) > 0))
            {
                keys.Add(key);
            }
        }

        keys.Sort(StringComparer.Ordinal);
        if (keys.Count > max)
        {
            keys.RemoveRange(max, keys.Count - max);
        }

        return new ValueTask<IReadOnlyList<string>>(keys);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>The object's path, under the root and never outside it.</summary>
    /// <param name="key">The key, already checked.</param>
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
    /// The token §11 asks for: a value that changes whenever the bytes under the key change.
    /// </summary>
    /// <param name="info">The file, freshly stat'ed.</param>
    /// <remarks>
    /// Length and last-write time, which is what a file system offers and what every rsync-like
    /// tool has used for thirty years. An object here is created once and never modified, so the
    /// case this must catch is a key deleted and created again -- a new file, a new time.
    /// </remarks>
    private static string Token(FileInfo info) => string.Create(
        CultureInfo.InvariantCulture,
        $"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}");

    private static void TryDelete(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (IOException)
        {
            // Leaving the claim is worse than throwing over it: the caller's own exception is on
            // its way up and carries the reason.
        }
        catch (UnauthorizedAccessException)
        {
            // The same answer for the arrivals File.Delete reports as this rather than as an
            // IOException: a read-only file, a path that has become a directory, a permission the
            // process no longer holds. None of them is recoverable here, and none of them is the
            // failure the caller is about to see.
        }
    }
}
