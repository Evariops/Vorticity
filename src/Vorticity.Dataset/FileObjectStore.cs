using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.Dataset;

/// <summary>
/// An <see cref="IObjectStore"/> over a directory. Claiming a key is atomic, but writing its
/// content is not: a process that dies mid-write leaves a short object under a taken key, which the
/// format detects on read rather than the store preventing it.
/// </summary>
public sealed class FileObjectStore : IObjectStore
{
    private readonly string _root;
    private bool _disposed;

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
            (byte[] bytes, PooledBuffer owner) = PooledBuffer.Rent(available);
            try
            {
                int read = 0;
                while (read < available)
                {
                    int got = await RandomAccess
                        .ReadAsync(stream.SafeFileHandle, bytes.AsMemory(read, available - read), offset + read, cancellationToken)
                        .ConfigureAwait(false);
                    if (got == 0)
                    {
                        throw new ObjectStoreException(
                            $"'{key}' ended after {read} of {available} bytes; it changed under the read.");
                    }

                    read += got;
                }

                return new ObjectRange(new SegmentLease(bytes.AsMemory(0, available), owner), Token(new FileInfo(path)));
            }
            catch
            {
                owner.Dispose();
                throw;
            }
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
        string key, PipeReader content, long length, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        FileStream stream;
        string path;
        try
        {
            ObjectKey.Check(key);
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            path = PathOf(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
            await using (stream.ConfigureAwait(false))
            {
                if (length > 0)
                {
                    stream.SetLength(length);
                }

                long written = 0;
                while (true)
                {
                    ReadResult result = await content.ReadAsync(cancellationToken).ConfigureAwait(false);
                    ReadOnlySequence<byte> buffer = result.Buffer;
                    if (written + buffer.Length > length)
                    {
                        throw new ObjectStoreException(
                            $"'{key}' was announced as {length} bytes and its content runs past them.");
                    }

                    foreach (ReadOnlyMemory<byte> segment in buffer)
                    {
                        await RandomAccess.WriteAsync(stream.SafeFileHandle, segment, written, cancellationToken).ConfigureAwait(false);
                        written += segment.Length;
                    }

                    content.AdvanceTo(buffer.End);
                    if (result.IsCanceled)
                    {
                        throw new OperationCanceledException($"The content of '{key}' was cancelled after {written} bytes.");
                    }

                    if (result.IsCompleted)
                    {
                        break;
                    }
                }

                if (written != length)
                {
                    throw new ObjectStoreException($"'{key}' was announced as {length} bytes and its content ended after {written}.");
                }
            }
        }
        catch (Exception caught)
        {
            // The claim is ours and the content is not: take the key back rather than leave a torn
            // object behind.
            failure = caught;
            TryDelete(path);
            throw;
        }
        finally
        {
            await content.CompleteAsync(failure).ConfigureAwait(false);
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
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<string> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return System.Linq.AsyncEnumerable.ToAsyncEnumerable(Keys(prefix, startAfter, cancellationToken));
    }

    /// <summary>
    /// The keys under a prefix, sorted: a directory walk has no order, so the ordinal order callers
    /// rely on is imposed here, over one walk, when the enumeration starts.
    /// </summary>
    private IEnumerable<string> Keys(string prefix, string? startAfter, CancellationToken cancellationToken)
    {
        List<string> keys = [];
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string key = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (key.StartsWith(prefix, StringComparison.Ordinal)
                && (startAfter is not { } after || string.CompareOrdinal(key, after) > 0))
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
