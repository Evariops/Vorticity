// The store seam - docs/13-dataset.md §11.
//
// FIVE OPERATIONS, AND NOT ONE MORE. §11's table is the whole surface: a ranged read, a head, a
// conditional creation, a delete and a prefix listing. Everything the dataset does is built from
// those, and the reason the list is short is that it is the list an object store offers: a library
// that implements this seam for S3, GCS or Azure writes five methods and nothing else.
//
// WHAT AN IMPLEMENTATION MUST DOCUMENT, quoting §11: an ATOMIC `PutIfAbsent`; a STRONGLY CONSISTENT
// `List`; a TOKEN that changes whenever the bytes under a key change; and `GetRange` on objects of
// any size. Those four are not niceties -- the commit protocol of §8 is a conditional creation and
// nothing else, so a store whose `PutIfAbsent` is a get-then-put loses commits under concurrency;
// §8.3 finds the newest version with one `List`, so an eventually consistent listing returns a
// stale dataset; and §7 binds an object's identity to its token.
//
// WHAT THIS LIBRARY WILL NEVER DO, also §11: retries, hedged requests, connection pooling,
// credentials. Those belong to the store library, which knows its service's failure modes.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>An object store: immutable objects under string keys.</summary>
/// <remarks>
/// <para>
/// <b>Thread safety.</b> Implementations MUST be thread-safe. A dataset reads and writes through
/// one store from several flows at once, exactly as 09 §1 requires of
/// <see cref="Vorticity.IO.ISegmentSource"/>.
/// </para>
/// <para>
/// <b>Immutability.</b> An object is created once and never changed. There is no put that
/// overwrites: <see cref="PutIfAbsentAsync"/> either creates the key or reports that it exists,
/// and that is the only way bytes enter the store. A key whose object was deleted may be created
/// again, and a store is free to refuse that (a dataset never does it: §10's vacuum deletes only
/// what no commit references).
/// </para>
/// <para>
/// <b>Cancellation.</b> Every operation takes a token and must honour it. A cancelled
/// <see cref="PutIfAbsentAsync"/> leaves the key created or absent, never half-written: that is
/// what "atomic" means here.
/// </para>
/// </remarks>
public interface IObjectStore : IAsyncDisposable
{
    /// <summary>Reads a byte range of an object.</summary>
    /// <param name="key">The object's key.</param>
    /// <param name="offset">A non-negative offset into the object.</param>
    /// <param name="length">
    /// The bytes wanted. Clamped to the object: asking for 64 KiB of a 3 KiB object returns 3 KiB,
    /// which is how the reader opens a file by its tail without knowing its length (02 §1).
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes and the object's token. The caller disposes it exactly once.</returns>
    /// <exception cref="ObjectNotFoundException">No object has that key.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="offset"/> is negative or starts at or past the object's end, or
    /// <paramref name="length"/> is negative.
    /// </exception>
    ValueTask<ObjectRange> GetRangeAsync(string key, long offset, int length, CancellationToken cancellationToken);

    /// <summary>The object's size and token, without its bytes.</summary>
    /// <param name="key">The object's key.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The head, or <see langword="null"/> when no object has that key.</returns>
    ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken);

    /// <summary>Creates an object if its key is free, atomically.</summary>
    /// <param name="key">The object's key.</param>
    /// <param name="content">Its bytes. The store copies what it needs before returning.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    /// <see cref="PutOutcome.Created"/> when these bytes are now the object,
    /// <see cref="PutOutcome.Exists"/> when the key was taken — by another writer, or by this one
    /// before a crash. The caller never learns whose bytes are there from this answer alone; §8.2
    /// says what a writer does about it.
    /// </returns>
    ValueTask<PutOutcome> PutIfAbsentAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    /// <summary>Deletes an object.</summary>
    /// <param name="key">The object's key.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>Whether an object was there to delete. Deleting an absent key is not an error.</returns>
    ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>The keys under a prefix, in ordinal order.</summary>
    /// <param name="prefix">The prefix; the empty string lists the whole store.</param>
    /// <param name="startAfter">
    /// Continue after this key, exclusive, or <see langword="null"/> to start at the beginning.
    /// </param>
    /// <param name="max">The most keys to return; at least one.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>
    /// Up to <paramref name="max"/> keys in ordinal order. A short answer does NOT mean the end:
    /// list again from the last key returned until the answer is empty.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="max"/> is not positive.</exception>
    ValueTask<IReadOnlyList<string>> ListAsync(
        string prefix, string? startAfter, int max, CancellationToken cancellationToken);
}

/// <summary>An object's size, token and creation time.</summary>
/// <param name="Length">Its bytes.</param>
/// <param name="Token">
/// A value that changes whenever the bytes under the key change (§11). An object is immutable, so
/// in practice the token changes only when a key is deleted and created again — which is exactly
/// the case §7's binding has to catch.
/// </param>
/// <param name="LastModified">
/// When the store created it, by the STORE's clock (§10). Vacuum keeps an unreferenced object younger
/// than the retention window, since it may be a writer's in flight; a writer's own clock is not what
/// decides that, because two writers' clocks need not agree and the store's is the one they share.
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
    /// <summary>Required by the exception guidelines; prefer <see cref="For"/>.</summary>
    public ObjectNotFoundException()
        : base("No object at that key.")
    {
    }

    /// <summary>Carries a message.</summary>
    /// <param name="message">The message.</param>
    public ObjectNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Carries a message and the cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ObjectNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The key, when the thrower named it.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>
    /// The dataset version whose read found the object missing; 0 when the thrower was not reading a
    /// version (§10: a reader that outlives the retention window "reports that, never a wrong answer").
    /// </summary>
    public ulong Version { get; init; }

    /// <summary>The exception naming <paramref name="key"/>.</summary>
    /// <param name="key">The key that is not there.</param>
    /// <returns>The exception, ready to throw.</returns>
    public static ObjectNotFoundException For(string key) =>
        new ObjectNotFoundException($"No object at '{key}'.") { Key = key };

    /// <summary>The exception a reader of <paramref name="version"/> throws for an object that is gone.</summary>
    /// <param name="key">The key its version names and the store no longer holds.</param>
    /// <param name="version">The version being read.</param>
    /// <param name="cause">The store's own exception.</param>
    /// <returns>The exception, ready to throw.</returns>
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
    /// <param name="message">The message.</param>
    public ObjectStoreException(string message)
        : base(message)
    {
    }

    /// <summary>Carries a message and the cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ObjectStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Required by the exception guidelines.</summary>
    public ObjectStoreException()
        : base("The object store refused the operation.")
    {
    }
}
