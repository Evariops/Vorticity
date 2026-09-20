using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>An object store: immutable objects under string keys.</summary>
/// <remarks>
/// Implementations must be thread-safe, must list with strong consistency, and must create objects
/// atomically: a cancelled <see cref="PutIfAbsentAsync"/> leaves the key created or absent, never
/// half-written. An object is never overwritten, and its token changes whenever the bytes under the
/// key change. Retries, pooling and credentials belong to the implementation, not to this seam.
/// </remarks>
public interface IObjectStore : IAsyncDisposable
{
    /// <summary>
    /// Reads a byte range of an object. The length is clamped to the object, which lets a reader
    /// open a file by its tail without knowing its length. The caller disposes the result once.
    /// </summary>
    /// <exception cref="ObjectNotFoundException">No object has that key.</exception>
    ValueTask<ObjectRange> GetRangeAsync(string key, long offset, int length, CancellationToken cancellationToken);

    /// <summary>
    /// The object's size and token, without its bytes, or <see langword="null"/> when no object has
    /// that key.
    /// </summary>
    ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Creates an object if its key is free, atomically. The store copies the content it needs
    /// before returning. <see cref="PutOutcome.Exists"/> says only that the key was taken, never
    /// whose bytes are there.
    /// </summary>
    ValueTask<PutOutcome> PutIfAbsentAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes an object and reports whether one was there. Deleting an absent key is not an error.
    /// </summary>
    ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Up to <c>max</c> keys under a prefix, in ordinal order, resuming after <c>startAfter</c>. An
    /// empty prefix lists the whole store. A short answer does not mean the end: list again from
    /// the last key returned until the answer is empty.
    /// </summary>
    ValueTask<IReadOnlyList<string>> ListAsync(
        string prefix, string? startAfter, int max, CancellationToken cancellationToken);
}

/// <summary>An object's size, token and creation time.</summary>
/// <param name="Length">Its bytes.</param>
/// <param name="Token">
/// A value that changes whenever the bytes under the key change; since objects are immutable, that
/// means the key was deleted and created again.
/// </param>
/// <param name="LastModified">
/// When the store created it, by the store's clock: two writers' clocks need not agree, and vacuum
/// dates an object's age against the shared one.
/// </param>
public readonly record struct ObjectHead(long Length, string Token, DateTimeOffset LastModified);

/// <summary>What <see cref="IObjectStore.PutIfAbsentAsync"/> did.</summary>
public enum PutOutcome
{
    /// <summary>The key was free and now holds the caller's bytes.</summary>
    Created = 0,

    /// <summary>The key was taken. Nothing was written.</summary>
    Exists = 1,
}

/// <summary>No object has that key.</summary>
public sealed class ObjectNotFoundException : Exception
{
    /// <summary>Prefer <see cref="For"/>, which names the key.</summary>
    public ObjectNotFoundException()
        : base("No object at that key.")
    {
    }

    /// <summary>Carries a message.</summary>
    public ObjectNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Carries a message and the cause.</summary>
    public ObjectNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The key, when the thrower named it.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>
    /// The dataset version whose read found the object missing; 0 when the thrower was not reading
    /// a version.
    /// </summary>
    public ulong Version { get; init; }

    /// <summary>The exception naming the key that is not there.</summary>
    public static ObjectNotFoundException For(string key) =>
        new ObjectNotFoundException($"No object at '{key}'.") { Key = key };

    /// <summary>The exception a reader of a version throws for an object that is gone.</summary>
    public static ObjectNotFoundException InVersion(string key, ulong version, Exception cause) =>
        new ObjectNotFoundException(
            $"Version {version} of the dataset names '{key}', which the store no longer holds. A reader that " +
            "outlives the retention window loses its objects to vacuum (docs/13-dataset.md §10); refresh to " +
            "read the latest version.",
            cause)
        {
            Key = key,
            Version = version,
        };
}

/// <summary>A store refused or failed an operation.</summary>
public sealed class ObjectStoreException : Exception
{
    /// <summary>Carries a message.</summary>
    public ObjectStoreException(string message)
        : base(message)
    {
    }

    /// <summary>Carries a message and the cause.</summary>
    public ObjectStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Reports a refused operation with no further detail.</summary>
    public ObjectStoreException()
        : base("The object store refused the operation.")
    {
    }
}
