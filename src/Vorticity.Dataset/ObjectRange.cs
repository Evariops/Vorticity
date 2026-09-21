using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.Dataset;

/// <summary>
/// A byte range of an object and the object's token, in one answer: bytes fetched in one call and a
/// token fetched in another would be no proof that the two describe the same object.
/// </summary>
/// <remarks>
/// The bytes are a <see cref="SegmentLease"/>, so a store hands out a pooled buffer, memory it keeps,
/// or a response that arrived in pieces, and gets it back when the range is disposed.
/// </remarks>
internal struct ObjectRange : IDisposable
{
    private SegmentLease _bytes;

    /// <summary>A range over bytes a store holds, with the token of the object they came from.</summary>
    /// <param name="bytes">The bytes, released when the range is disposed.</param>
    /// <param name="token">The object's token, as <see cref="ObjectHead.Token"/> defines it.</param>
    public ObjectRange(SegmentLease bytes, string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        _bytes = bytes;
        Token = token;
    }

    /// <summary>The object's token, as <see cref="ObjectHead.Token"/> defines it.</summary>
    public string Token { get; }

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

/// <summary>The store operations the dataset asks in the shapes it uses them in.</summary>
internal static class ObjectStoreExtensions
{
    /// <summary>Creates an object from bytes already in memory.</summary>
    internal static ValueTask<PutOutcome> PutIfAbsentAsync(
        this IObjectStore store, string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
        store.PutIfAbsentAsync(key, PipeReader.Create(new ReadOnlySequence<byte>(content)), content.Length, cancellationToken);

    /// <summary>Deletes one object.</summary>
    internal static ValueTask DeleteAsync(this IObjectStore store, string key, CancellationToken cancellationToken) =>
        store.DeleteAsync([key], cancellationToken);

    /// <summary>Up to <paramref name="max"/> keys under a prefix, after <paramref name="startAfter"/>.</summary>
    internal static async ValueTask<IReadOnlyList<string>> ListAsync(
        this IObjectStore store, string prefix, string? startAfter, int max, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        List<string> keys = [];
        await foreach (string key in store.ListAsync(prefix, startAfter, cancellationToken).ConfigureAwait(false))
        {
            keys.Add(key);
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
        await foreach (string key in store.ListAsync(prefix, null, cancellationToken).ConfigureAwait(false))
        {
            keys.Add(key);
        }

        return keys;
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
}
