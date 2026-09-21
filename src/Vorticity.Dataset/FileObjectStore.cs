using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>
/// An <see cref="IObjectStore"/> over a directory. Claiming a key is atomic, but writing its
/// content is not: a process that dies mid-write leaves a short object under a taken key, which the
/// format detects on read rather than the store preventing it.
/// </summary>
internal sealed class FileObjectStore : IObjectStore
{
    private readonly string _root;
    private bool _disposed;

    /// <summary>Opens a store over a directory, creating it if needed.</summary>
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
            // The claim is ours and the content is not: take the key back rather than leave a torn
            // object behind.
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

        // A directory walk has no order, so the ordinal order callers rely on is imposed here.
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
    /// catch is a key deleted and created again.
    /// </summary>
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
