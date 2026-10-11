using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.IO;

/// <summary>
/// A byte range of an object and the object's token, in one answer: bytes fetched in one call and a
/// token fetched in another would be no proof that the two describe the same object.
/// </summary>
/// <remarks>
/// The bytes are a <see cref="SegmentLease"/>, so a store hands out a pooled buffer, memory it keeps,
/// or a response that arrived in pieces, and gets it back when the range is disposed.
/// </remarks>
public struct ObjectRange : IDisposable
{
    private SegmentLease _bytes;

    /// <summary>A range over bytes a store holds, with the token of the object they came from.</summary>
    /// <param name="bytes">The bytes, released when the range is disposed.</param>
    /// <param name="token">The object's token, as <see cref="ObjectHead.Token"/> defines it.</param>
    public ObjectRange(SegmentLease bytes, string token)
        : this(bytes, token, -1)
    {
    }

    /// <summary>A range over bytes a store holds, with the token and the length of the object they came from.</summary>
    /// <param name="bytes">The bytes, released when the range is disposed.</param>
    /// <param name="token">The object's token, as <see cref="ObjectHead.Token"/> defines it.</param>
    /// <param name="objectLength">The whole object's length, as the same answer gave it, or -1 when the store does not say.</param>
    public ObjectRange(SegmentLease bytes, string token, long objectLength)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentOutOfRangeException.ThrowIfLessThan(objectLength, -1);
        _bytes = bytes;
        Token = token;
        ObjectLength = objectLength;
    }

    /// <summary>The object's token, as <see cref="ObjectHead.Token"/> defines it.</summary>
    public string Token { get; }

    /// <summary>The whole object's length, as the answer that brought the bytes gave it, or -1 when the store does not say.</summary>
    public long ObjectLength { get; }

    /// <summary>The bytes, valid until <see cref="Dispose"/>.</summary>
    public readonly ReadOnlySequence<byte> Bytes => _bytes.Bytes;

    /// <summary>Whether the bytes are one block, so <see cref="Memory"/> is valid.</summary>
    public readonly bool IsContiguous => _bytes.IsContiguous;

    /// <summary>The bytes as one block.</summary>
    /// <exception cref="InvalidOperationException">The bytes arrived in several blocks; read <see cref="Bytes"/>.</exception>
    public readonly ReadOnlyMemory<byte> Memory => _bytes.Memory;

    /// <summary>The number of bytes.</summary>
    public readonly int Length => checked((int)_bytes.Bytes.Length);

    /// <summary>Releases the bytes. Idempotent.</summary>
    public void Dispose() => _bytes.Dispose();

    /// <summary>The bytes as one block, copied only when they arrived in several.</summary>
    internal readonly ReadOnlyMemory<byte> Contiguous() => IsContiguous ? _bytes.Memory : _bytes.Bytes.ToArray();
}

/// <summary>An array rented from the shared pool, returned when the lease over it is disposed.</summary>
internal sealed class PooledBuffer : IDisposable
{
    private byte[]? _array;

    private PooledBuffer(byte[] array) => _array = array;

    /// <summary>Rents at least <paramref name="length"/> bytes and hands back the array with its owner.</summary>
    internal static (byte[] Array, PooledBuffer Owner) Rent(int length)
    {
        byte[] array = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
        return (array, new PooledBuffer(array));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _array, null) is { } array)
        {
            ArrayPool<byte>.Shared.Return(array);
        }
    }
}

/// <summary>The shapes a caller often wants, over the members of <see cref="IObjectStore"/>.</summary>
public static class ObjectStoreExtensions
{
    /// <summary>
    /// Reads a byte range of an object into a buffer from the shared pool, with the object's token and
    /// length. The length is clamped to the object, which lets a reader open a file by its tail
    /// without knowing its length.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="key">The object's key.</param>
    /// <param name="offset">The first byte; at or past the end is refused, except 0 on an empty object.</param>
    /// <param name="length">The most bytes to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes and the token; the caller disposes the range once.</returns>
    /// <exception cref="ObjectNotFoundException">No object has that key.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range starts past the object's end.</exception>
    public static async ValueTask<ObjectRange> GetRangeAsync(
        this IObjectStore store, string key, long offset, int length, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        (byte[] array, PooledBuffer owner) = PooledBuffer.Rent(length);
        try
        {
            ObjectRead read = await store.ReadAsync(key, offset, array.AsMemory(0, length), null, cancellationToken).ConfigureAwait(false);
            return new ObjectRange(new SegmentLease(array.AsMemory(0, read.BytesRead), owner), read.Token, read.ObjectLength);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates an object from <paramref name="content"/> if its key is free, atomically.
    /// <see cref="PutOutcome.Exists"/> says only that the key was taken, never whose bytes are there.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="key">The object's key.</param>
    /// <param name="content">The bytes, streamed; the store completes the reader whatever the outcome.</param>
    /// <param name="length">How many bytes <paramref name="content"/> holds.</param>
    /// <param name="cancellationToken">Cancels the creation, leaving the key created or absent.</param>
    /// <returns>Whether the key was free and now holds the bytes.</returns>
    public static ValueTask<PutOutcome> PutIfAbsentAsync(
        this IObjectStore store, string key, PipeReader content, long length, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.PutAsync(key, content, length, PutCondition.IfAbsent, cancellationToken);
    }

    /// <summary>Creates an object from bytes already in memory.</summary>
    internal static ValueTask<PutOutcome> PutIfAbsentAsync(
        this IObjectStore store, string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
        store.PutAsync(key, PipeReader.Create(new ReadOnlySequence<byte>(content)), content.Length, PutCondition.IfAbsent, cancellationToken);

    /// <summary>Deletes one object.</summary>
    internal static ValueTask DeleteAsync(this IObjectStore store, string key, CancellationToken cancellationToken) =>
        store.DeleteAsync([key], cancellationToken);

    /// <summary>Up to <paramref name="max"/> keys under a prefix, after <paramref name="startAfter"/>.</summary>
    internal static async ValueTask<IReadOnlyList<string>> ListAsync(
        this IObjectStore store, string prefix, string? startAfter, int max, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        List<string> keys = [];
        await foreach (ObjectListing listed in store.ListAsync(prefix, startAfter, cancellationToken).ConfigureAwait(false))
        {
            keys.Add(listed.Key);
            if (keys.Count == max)
            {
                break;
            }
        }

        return keys;
    }

    /// <summary>Every key under a prefix.</summary>
    internal static async ValueTask<List<string>> ListAllAsync(
        this IObjectStore store, string prefix, CancellationToken cancellationToken)
    {
        List<string> keys = [];
        await foreach (ObjectListing listed in store.ListAsync(prefix, null, cancellationToken).ConfigureAwait(false))
        {
            keys.Add(listed.Key);
        }

        return keys;
    }

    /// <summary>Every object under a prefix, with its head.</summary>
    internal static async ValueTask<List<ObjectListing>> ListEntriesAsync(
        this IObjectStore store, string prefix, CancellationToken cancellationToken)
    {
        List<ObjectListing> listed = [];
        await foreach (ObjectListing entry in store.ListAsync(prefix, null, cancellationToken).ConfigureAwait(false))
        {
            listed.Add(entry);
        }

        return listed;
    }

    /// <summary>
    /// Reads a put's content to its end into one array, refusing content that is not
    /// <paramref name="length"/> bytes; the reader is completed whatever happens.
    /// </summary>
    internal static async ValueTask<byte[]> ReadAllAsync(
        PipeReader content, long length, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length > Array.MaxLength)
        {
            await content.CompleteAsync().ConfigureAwait(false);
            throw new ObjectStoreException($"'{key}' is {length} bytes, more than one array holds.");
        }

        byte[] bytes = new byte[length];
        long read = 0;
        Exception? failure = null;
        try
        {
            while (true)
            {
                ReadResult result = await content.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;
                if (read + buffer.Length > length)
                {
                    throw new ObjectStoreException(
                        $"'{key}' was announced as {length} bytes and its content runs past them.");
                }

                buffer.CopyTo(bytes.AsSpan((int)read));
                read += buffer.Length;
                content.AdvanceTo(buffer.End);
                if (result.IsCanceled)
                {
                    throw new OperationCanceledException($"The content of '{key}' was cancelled after {read} bytes.");
                }

                if (result.IsCompleted)
                {
                    break;
                }
            }

            if (read != length)
            {
                throw new ObjectStoreException($"'{key}' was announced as {length} bytes and its content ended after {read}.");
            }

            return bytes;
        }
        catch (Exception caught)
        {
            failure = caught;
            throw;
        }
        finally
        {
            await content.CompleteAsync(failure).ConfigureAwait(false);
        }
    }

    /// <summary>The refusal of a read past an object's end, as every store words it.</summary>
    internal static ArgumentOutOfRangeException PastEnd(string key, long offset, long length) =>
        new ArgumentOutOfRangeException(nameof(offset), offset, $"'{key}' holds {length} bytes, so the range starts past its end.");

    /// <summary>The refusal of a read whose token condition failed: the object changed under its reader.</summary>
    internal static VortexFormatException Changed(string key, string expected, string actual) =>
        new VortexFormatException($"'{key}' changed under this reader: it was {expected} and is now {actual}.");
}
