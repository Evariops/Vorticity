using System;
using System.Collections.Generic;
using System.IO.Pipelines;
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
    /// Reads a byte range of an object, and the object's token in the same answer. The length is
    /// clamped to the object, which lets a reader open a file by its tail without knowing its length.
    /// </summary>
    /// <param name="key">The object's key.</param>
    /// <param name="offset">The first byte; at or past the end is refused, except 0 on an empty object.</param>
    /// <param name="length">The most bytes to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes and the token; the caller disposes the range once.</returns>
    /// <exception cref="ObjectNotFoundException">No object has that key.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range starts past the object's end.</exception>
    ValueTask<ObjectRange> GetRangeAsync(string key, long offset, int length, CancellationToken cancellationToken);

    /// <summary>
    /// The object's size, token and creation time, and the lock the store keeps it under, without its
    /// bytes.
    /// </summary>
    /// <param name="key">The object's key.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The head, or <see langword="null"/> when no object has that key.</returns>
    ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Creates an object from <paramref name="content"/> if its key is free, atomically.
    /// <see cref="PutOutcome.Exists"/> says only that the key was taken, never whose bytes are there.
    /// </summary>
    /// <param name="key">The object's key.</param>
    /// <param name="content">
    /// The bytes, streamed: the store reads them as it sends them, so an object need not fit in
    /// memory, and completes the reader whatever the outcome.
    /// </param>
    /// <param name="length">
    /// How many bytes <paramref name="content"/> holds, which a store uses to choose between one
    /// request and a multipart upload. Content that ends before or runs past it is refused, and
    /// nothing is created.
    /// </param>
    /// <param name="cancellationToken">Cancels the creation, leaving the key created or absent.</param>
    /// <returns>Whether the key was free and now holds the bytes.</returns>
    ValueTask<PutOutcome> PutIfAbsentAsync(string key, PipeReader content, long length, CancellationToken cancellationToken);

    /// <summary>Deletes objects. An absent key is not an error; a store whose service caps a batch splits it.</summary>
    /// <param name="keys">The keys, in any order.</param>
    /// <param name="cancellationToken">Cancels the requests not yet sent.</param>
    /// <returns>A task that completes when every key is absent.</returns>
    ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken);

    /// <summary>
    /// The keys under a prefix, in ordinal order, after <paramref name="startAfter"/>. The store asks
    /// for its pages as the enumeration advances, so a caller that stops at the first key pays for
    /// one page.
    /// </summary>
    /// <param name="prefix">The prefix; empty lists the whole store.</param>
    /// <param name="startAfter">The key to resume after, or <see langword="null"/> from the start.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The keys.</returns>
    IAsyncEnumerable<string> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken);
}

/// <summary>An object's size, token and creation time, and the lock the store keeps it under.</summary>
/// <param name="Length">Its bytes.</param>
/// <param name="Token">
/// A value that changes whenever the bytes under the key change; since objects are immutable, that
/// means the key was deleted and created again.
/// </param>
/// <param name="LastModified">
/// When the store created it, by the store's clock: two writers' clocks need not agree, and vacuum
/// dates an object's age against the shared one.
/// </param>
public readonly record struct ObjectHead(long Length, string Token, DateTimeOffset LastModified)
{
    /// <summary>
    /// The date before which the store refuses to delete or overwrite the object, when it keeps it under
    /// a retention lock: S3 Object Lock, Azure immutable blob storage, a GCS retention policy; null when
    /// it keeps none.
    /// </summary>
    public DateTimeOffset? RetainUntil { get; init; }

    /// <summary>Whether the object is under a legal hold, which no date lifts: the store refuses to delete it until the hold is released.</summary>
    public bool LegalHold { get; init; }

    /// <summary>Whether the store would refuse to delete the object at <paramref name="now"/>, by the store's clock.</summary>
    /// <param name="now">The moment asked about.</param>
    /// <returns>True under a legal hold, or before the retention date.</returns>
    public bool IsLockedAt(DateTimeOffset now) => LegalHold || RetainUntil > now;
}

/// <summary>What <see cref="IObjectStore.PutIfAbsentAsync"/> did.</summary>
public enum PutOutcome
{
    /// <summary>The key was free and now holds the caller's bytes.</summary>
    Created = 0,

    /// <summary>The key was taken. Nothing was written.</summary>
    Exists = 1,
}

/// <summary>No object has that key.</summary>
public sealed class ObjectNotFoundException : VortexException
{
    /// <summary>Creates the exception; prefer <see cref="For"/>, which names the key.</summary>
    public ObjectNotFoundException()
        : base("No object at that key.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What is missing.</param>
    public ObjectNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    /// <param name="message">What is missing.</param>
    /// <param name="innerException">The store's own failure.</param>
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
    /// <param name="key">The key.</param>
    /// <returns>The exception.</returns>
    public static ObjectNotFoundException For(string key) =>
        new ObjectNotFoundException($"No object at '{key}'.") { Key = key };

    /// <summary>The exception a reader of a version throws for an object that is gone.</summary>
    internal static ObjectNotFoundException InVersion(string key, ulong version, Exception cause) =>
        new ObjectNotFoundException(
            $"Version {version} of the dataset names '{key}', which the store no longer holds. A reader that " +
            "outlives the retention window loses its objects to vacuum; refresh to read the latest " +
            "version.",
            cause)
        {
            Key = key,
            Version = version,
        };
}

/// <summary>A store refused or failed an operation, or a dataset operation the store's answers refused.</summary>
public sealed class ObjectStoreException : VortexException
{
    /// <summary>Creates the exception with a default message.</summary>
    public ObjectStoreException()
        : base("The object store refused the operation.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What was refused, and why.</param>
    public ObjectStoreException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    /// <param name="message">What was refused, and why.</param>
    /// <param name="innerException">The store's own failure.</param>
    public ObjectStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
