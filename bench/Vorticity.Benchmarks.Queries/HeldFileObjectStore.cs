using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Vorticity.Dataset;
using Vorticity.IO;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The paths bench's directory store with its objects held open (<c>--keep-handles</c>): an object's first read goes
/// to the <see cref="FileObjectStore"/> under it, which gives its length and token, and opens a handle kept with them;
/// every later read of the object is a positional read of that handle. A store in the cloud costs no file opened and
/// closed on the reading thread, which here took a tenth of a millisecond and a third of a lane's cycles over a
/// question of 16 481 reads. A write or a delete of a key retires its handle first; a read whose token is not the
/// one held, and every head and listing, goes to the store under it.
/// </summary>
internal sealed class HeldFileObjectStore(FileObjectStore inner) : IObjectStore
{
    private readonly ConcurrentDictionary<string, Held> _held = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<SafeFileHandle> _retired = [];

    // The store under this one as its interface, whose writes of a bound or of a sequence a directory store takes by default.
    private readonly IObjectStore _store = inner;

    public ObjectStoreCapabilities Capabilities => inner.Capabilities;

    public async ValueTask<ObjectRead> ReadAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
    {
        if (!_held.TryGetValue(key, out Held? held) || (ifToken is not null && !string.Equals(ifToken, held.Token, StringComparison.Ordinal))
            || (offset >= held.Length && !(offset == 0 && held.Length == 0)))
        {
            ObjectRead read = await inner.ReadAsync(key, offset, destination, ifToken, cancellationToken).ConfigureAwait(false);
            Hold(key, read);
            return read;
        }

        return await ReadHeldAsync(held, key, offset, destination, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (!_held.TryGetValue(key, out Held? held))
        {
            ObjectRead read = await inner.ReadTailAsync(key, destination, cancellationToken).ConfigureAwait(false);
            Hold(key, read);
            return read;
        }

        return await ReadHeldAsync(held, key, Math.Max(0, held.Length - destination.Length), destination, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken) => inner.HeadAsync(key, cancellationToken);

    public ValueTask<PutOutcome> PutAsync(string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken)
    {
        Release(key);
        return inner.PutAsync(key, content, length, condition, cancellationToken);
    }

    public ValueTask<PutOutcome> PutAsync(string key, PipeReader content, long length, long maxLength, PutCondition condition, CancellationToken cancellationToken)
    {
        Release(key);
        return _store.PutAsync(key, content, length, maxLength, condition, cancellationToken);
    }

    public ValueTask<PutOutcome> PutAsync(string key, ReadOnlySequence<byte> content, PutCondition condition, CancellationToken cancellationToken)
    {
        Release(key);
        return _store.PutAsync(key, content, condition, cancellationToken);
    }

    public ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        foreach (string key in keys)
        {
            Release(key);
        }

        return inner.DeleteAsync(keys, cancellationToken);
    }

    public IAsyncEnumerable<ObjectListing> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken) =>
        inner.ListAsync(prefix, startAfter, cancellationToken);

    public ValueTask DisposeAsync()
    {
        foreach (Held held in _held.Values)
        {
            held.Handle.Dispose();
        }

        foreach (SafeFileHandle handle in _retired)
        {
            handle.Dispose();
        }

        _held.Clear();
        return inner.DisposeAsync();
    }

    /// <summary>
    /// Opens the object a read of the store under it just read, kept with the length and token that read gave, unless
    /// one of that token is held already; one of another token is retired, not closed, since a read may still use it.
    /// </summary>
    private void Hold(string key, ObjectRead read)
    {
        string path = Path.GetFullPath(Path.Combine(inner.Root, key.Replace('/', Path.DirectorySeparatorChar)));
        SafeFileHandle handle = System.IO.File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.Asynchronous);
        if (RandomAccess.GetLength(handle) != read.ObjectLength)
        {
            handle.Dispose();
            return;
        }

        Held fresh = new Held(handle, read.ObjectLength, read.Token);
        while (true)
        {
            if (_held.TryGetValue(key, out Held? old))
            {
                if (string.Equals(old.Token, fresh.Token, StringComparison.Ordinal))
                {
                    handle.Dispose();
                    return;
                }

                if (_held.TryUpdate(key, fresh, old))
                {
                    _retired.Add(old.Handle);
                    return;
                }
            }
            else if (_held.TryAdd(key, fresh))
            {
                return;
            }
        }
    }

    /// <summary>Lets go of a key's handle before a write or a delete of it, retired until the store is disposed.</summary>
    private void Release(string key)
    {
        if (_held.TryRemove(key, out Held? held))
        {
            _retired.Add(held.Handle);
        }
    }

    private static async ValueTask<ObjectRead> ReadHeldAsync(Held held, string key, long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        int available = (int)Math.Min(destination.Length, held.Length - offset);
        int read = 0;
        while (read < available)
        {
            int got = await RandomAccess.ReadAsync(held.Handle, destination[read..available], offset + read, cancellationToken).ConfigureAwait(false);
            if (got == 0)
            {
                throw new ObjectStoreException($"'{key}' ended after {read} of {available} bytes; it changed under the read.");
            }

            read += got;
        }

        return new ObjectRead(available, held.Length, held.Token);
    }

    private sealed record Held(SafeFileHandle Handle, long Length, string Token);
}
