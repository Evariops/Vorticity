using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.IO;

/// <summary>An object store: objects under string keys, read by range and written whole.</summary>
/// <remarks>
/// <para>
/// Implementations are thread-safe. A put is atomic: a cancelled or failed <see cref="PutAsync(string, PipeReader, long, PutCondition, CancellationToken)"/>
/// leaves the key as it was or holding the new bytes whole, never half-written, and a put conditioned
/// on the key's absence is answered <see cref="PutOutcome.Created"/> for one caller only when several
/// race. Reads and heads see every write that completed. A listing may lag behind them for a while, as
/// some services let it, so a caller that must see a write it knows of reads its key. An object's
/// token changes whenever the bytes under its key do.
/// </para>
/// <para>
/// Retries, pooling and credentials belong to the implementation, not to this seam. A creation is
/// <see cref="ObjectStoreExtensions.PutIfAbsentAsync(IObjectStore, string, PipeReader, long, CancellationToken)"/>,
/// over these members.
/// </para>
/// </remarks>
public interface IObjectStore : IAsyncDisposable
{
    /// <summary>What the store does in one request, so that a caller takes the cheaper path without trying the other.</summary>
    ObjectStoreCapabilities Capabilities { get; }

    /// <summary>
    /// Reads bytes of an object from <paramref name="offset"/> into <paramref name="destination"/>,
    /// as many as it holds or as the object has past the offset, whichever is fewer, with the object's
    /// length and token from the same answer.
    /// </summary>
    /// <param name="key">The object's key.</param>
    /// <param name="offset">The first byte; at or past the end is refused, except 0 on an empty object.</param>
    /// <param name="destination">Where the bytes go, written from its start.</param>
    /// <param name="ifToken">
    /// The token the object must still have, or null for any: a store asks the service to refuse the
    /// read of another object rather than send its bytes.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes read, the object's length and its token.</returns>
    /// <exception cref="ObjectNotFoundException">No object has that key.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The offset is past the object's end.</exception>
    /// <exception cref="VortexFormatException">The object's token is not <paramref name="ifToken"/>: it changed under the reader.</exception>
    ValueTask<ObjectRead> ReadAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the last bytes of an object into <paramref name="destination"/>, as many as it holds or as
    /// the object has, whichever is fewer, with the object's length and token from the same answer. A
    /// store that reads suffixes (<see cref="ObjectStoreCapabilities.ReadsSuffix"/>) answers in one
    /// request; another asks the length first.
    /// </summary>
    /// <param name="key">The object's key.</param>
    /// <param name="destination">Where the bytes go, written from its start.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes read, the object's length and its token.</returns>
    /// <exception cref="ObjectNotFoundException">No object has that key.</exception>
    ValueTask<ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken);

    /// <summary>The object's length, token and creation time, and the lock the store keeps it under, without its bytes.</summary>
    /// <param name="key">The object's key.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The head, or <see langword="null"/> when no object has that key.</returns>
    ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Writes an object from <paramref name="content"/>, atomically, if <paramref name="condition"/>
    /// holds: always, only when the key is free, or only when the object still has a token.
    /// </summary>
    /// <param name="key">The object's key.</param>
    /// <param name="content">
    /// The bytes, streamed: the store reads them as it sends them, so an object need not fit in
    /// memory, and completes the reader whatever the outcome.
    /// </param>
    /// <param name="length">
    /// How many bytes <paramref name="content"/> holds, which a store uses to choose between one
    /// request and a multipart upload. Content that ends before or runs past it is refused, and
    /// nothing is written. -1 when the writer does not know it yet, as a file written to a location:
    /// the content's end says it.
    /// </param>
    /// <param name="condition">When the put may write.</param>
    /// <param name="cancellationToken">Cancels the put, leaving the key as it was or holding the new bytes whole.</param>
    /// <returns>Whether the bytes were written, or which condition failed.</returns>
    /// <exception cref="NotSupportedException">The condition is one the store cannot hold (<see cref="ObjectStoreCapabilities.ReplacesOnTokenMatch"/>).</exception>
    /// <exception cref="ObjectStoreException">The store failed, or cannot tell whether the conditional put landed.</exception>
    ValueTask<PutOutcome> PutAsync(string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken);

    /// <summary>
    /// Writes an object from <paramref name="content"/> as
    /// <see cref="PutAsync(string, PipeReader, long, PutCondition, CancellationToken)"/> does, its length
    /// unknown until its end but at most <paramref name="maxLength"/>: what a writer knows of an object it
    /// assembles from inputs of known sizes, as a compaction does. A store that sends in parts sizes them
    /// by the bound, so that it streams an object it would otherwise hold on disk or refuse; the default
    /// takes the length as unknown.
    /// </summary>
    /// <param name="key">The object's key.</param>
    /// <param name="content">The bytes, streamed; the store completes the reader whatever the outcome.</param>
    /// <param name="length">How many bytes <paramref name="content"/> holds, or -1 when the writer does not know it yet.</param>
    /// <param name="maxLength">
    /// The most bytes <paramref name="content"/> holds when <paramref name="length"/> is -1, or -1 for no
    /// bound. Content that runs past it is refused, and nothing is written.
    /// </param>
    /// <param name="condition">When the put may write.</param>
    /// <param name="cancellationToken">Cancels the put, leaving the key as it was or holding the new bytes whole.</param>
    /// <returns>Whether the bytes were written, or which condition failed.</returns>
    /// <exception cref="NotSupportedException">The condition is one the store cannot hold (<see cref="ObjectStoreCapabilities.ReplacesOnTokenMatch"/>).</exception>
    /// <exception cref="ObjectStoreException">The store failed, or cannot tell whether the conditional put landed.</exception>
    ValueTask<PutOutcome> PutAsync(string key, PipeReader content, long length, long maxLength, PutCondition condition, CancellationToken cancellationToken) =>
        PutAsync(key, content, length, condition, cancellationToken);

    /// <summary>
    /// Writes an object from bytes already in memory, atomically, if <paramref name="condition"/> holds.
    /// A store that sends them sends them where they lie, and sends them again from there when it must
    /// retry, so that nothing is copied; the default streams them through
    /// <see cref="PutAsync(string, PipeReader, long, PutCondition, CancellationToken)"/>.
    /// </summary>
    /// <param name="key">The object's key.</param>
    /// <param name="content">The bytes, which the caller keeps unchanged until the put completes.</param>
    /// <param name="condition">When the put may write.</param>
    /// <param name="cancellationToken">Cancels the put, leaving the key as it was or holding the new bytes whole.</param>
    /// <returns>Whether the bytes were written, or which condition failed.</returns>
    /// <exception cref="NotSupportedException">The condition is one the store cannot hold (<see cref="ObjectStoreCapabilities.ReplacesOnTokenMatch"/>).</exception>
    /// <exception cref="ObjectStoreException">The store failed, or cannot tell whether the conditional put landed.</exception>
    ValueTask<PutOutcome> PutAsync(string key, ReadOnlySequence<byte> content, PutCondition condition, CancellationToken cancellationToken) =>
        PutAsync(key, PipeReader.Create(content), content.Length, condition, cancellationToken);

    /// <summary>Deletes objects. An absent key is not an error; a store whose service caps a batch splits it.</summary>
    /// <param name="keys">The keys, in any order.</param>
    /// <param name="cancellationToken">Cancels the requests not yet sent.</param>
    /// <returns>A task that completes when every key is absent.</returns>
    ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken);

    /// <summary>
    /// The objects under a prefix, in ordinal order of their keys, after <paramref name="startAfter"/>,
    /// each with its head. The store asks for its pages as the enumeration advances, so a caller that
    /// stops at the first key pays for one page.
    /// </summary>
    /// <param name="prefix">The prefix; empty lists the whole store.</param>
    /// <param name="startAfter">The key to resume after, or <see langword="null"/> from the start.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The objects.</returns>
    IAsyncEnumerable<ObjectListing> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken);
}

/// <summary>What a store does in one request.</summary>
[Flags]
public enum ObjectStoreCapabilities
{
    /// <summary>None of the below.</summary>
    None = 0,

    /// <summary><see cref="IObjectStore.ReadTailAsync"/> is one request, its answer saying the object's length.</summary>
    ReadsSuffix = 1,

    /// <summary>A listing is in ordinal order and resumes after a key, so that the first keys under a prefix cost one page.</summary>
    ListsInOrder = 2,

    /// <summary>A put replaces an object only while it still has a token (<see cref="PutCondition.IfMatch"/>).</summary>
    ReplacesOnTokenMatch = 8,

    /// <summary>
    /// The store keeps an object under a retention date or a legal hold when its bucket says so, as S3
    /// Object Lock does, refuses to delete or overwrite it until then, and reports the lock in a head.
    /// </summary>
    KeepsLocks = 16,

    /// <summary>
    /// A put with <see cref="PutCondition.IfAbsent"/> creates the key only while it is free, in one
    /// step: of callers racing to create one key, one hears <see cref="PutOutcome.Created"/> and every
    /// other <see cref="PutOutcome.Exists"/>. A dataset commits by such a put, so it writes only to a
    /// store that declares this; a store that checked and then wrote would let two commits of one
    /// version both believe they had landed.
    /// </summary>
    CreatesConditionally = 32,
}

/// <summary>What one read brought: the bytes written into the caller's memory, the object's length and its token.</summary>
/// <param name="BytesRead">How many bytes were written into the destination, from its start.</param>
/// <param name="ObjectLength">The whole object's length, as the same answer gave it.</param>
/// <param name="Token">The object's token, as <see cref="ObjectHead.Token"/> defines it.</param>
public readonly record struct ObjectRead(int BytesRead, long ObjectLength, string Token);

/// <summary>An object a listing names, with its head.</summary>
/// <param name="Key">The object's key.</param>
/// <param name="Head">Its length, token and creation time, and its lock when <paramref name="LockReported"/>.</param>
/// <param name="LockReported">
/// Whether the listing says the object's lock, so that <see cref="ObjectHead.RetainUntil"/> and
/// <see cref="ObjectHead.LegalHold"/> mean what they say, which a store that keeps no locks does by
/// saying none; otherwise only a head tells.
/// </param>
public readonly record struct ObjectListing(string Key, ObjectHead Head, bool LockReported);

/// <summary>An object's length, token and creation time, and the lock the store keeps it under.</summary>
/// <param name="Length">Its bytes.</param>
/// <param name="Token">A value that changes whenever the bytes under the key change.</param>
/// <param name="LastModified">
/// When the store wrote it, by the store's clock, which a caller compares with the store's other dates
/// rather than with its own clock, since two writers' clocks need not agree. A store may date an object
/// written in parts by the start of its upload, as S3 does, earlier than the moment it appeared.
/// </param>
public readonly record struct ObjectHead(long Length, string Token, DateTimeOffset LastModified)
{
    /// <summary>
    /// The date before which the store refuses to delete or overwrite the object, when it keeps it under
    /// a retention lock: S3 Object Lock, Azure immutable blob storage, a Cloud Storage retention policy;
    /// null when it keeps none.
    /// </summary>
    public DateTimeOffset? RetainUntil { get; init; }

    /// <summary>Whether the object is under a legal hold, which no date lifts: the store refuses to delete it until the hold is released.</summary>
    public bool LegalHold { get; init; }

    /// <summary>Whether the store would refuse to delete the object at <paramref name="now"/>, by the store's clock.</summary>
    /// <param name="now">The moment asked about.</param>
    /// <returns>True under a legal hold, or before the retention date.</returns>
    public bool IsLockedAt(DateTimeOffset now) => LegalHold || RetainUntil > now;
}

/// <summary>When a put may write: always, only when the key is free, or only while the object still has a token.</summary>
public readonly record struct PutCondition
{
    private PutCondition(PutConditionKind kind, string? token)
    {
        Kind = kind;
        Token = token;
    }

    /// <summary>Whether the put writes whatever is there, only when nothing is, or only over one object.</summary>
    public PutConditionKind Kind { get; }

    /// <summary>The token the object must still have, for <see cref="PutConditionKind.IfMatch"/>.</summary>
    public string? Token { get; }

    /// <summary>The put writes whatever the key holds, replacing it, as a file written to a path replaces it.</summary>
    public static PutCondition None => default;

    /// <summary>The put writes only when no object has the key: a creation, which <see cref="PutOutcome.Exists"/> refuses.</summary>
    public static PutCondition IfAbsent => new PutCondition(PutConditionKind.IfAbsent, null);

    /// <summary>The put writes only while the object still has <paramref name="token"/>, which <see cref="PutOutcome.Changed"/> refuses.</summary>
    /// <param name="token">The token the object was read with.</param>
    /// <returns>The condition.</returns>
    public static PutCondition IfMatch(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return new PutCondition(PutConditionKind.IfMatch, token);
    }
}

/// <summary>The kinds of <see cref="PutCondition"/>.</summary>
public enum PutConditionKind
{
    /// <summary>Always.</summary>
    None = 0,

    /// <summary>Only when no object has the key.</summary>
    IfAbsent = 1,

    /// <summary>Only while the object has a token.</summary>
    IfMatch = 2,
}

/// <summary>What <see cref="IObjectStore.PutAsync(string, PipeReader, long, PutCondition, CancellationToken)"/> did.</summary>
public enum PutOutcome
{
    /// <summary>The bytes were written.</summary>
    Created = 0,

    /// <summary>The key was taken, and the put asked for it free. Nothing was written.</summary>
    Exists = 1,

    /// <summary>The object no longer had the token the put asked for, or no longer existed. Nothing was written.</summary>
    Changed = 2,
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

    /// <summary>The exception naming the key that is not there.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The exception.</returns>
    public static ObjectNotFoundException For(string key) =>
        new ObjectNotFoundException($"No object at '{key}'.") { Key = key };
}

/// <summary>A store refused or failed an operation, or a caller found its answers inconsistent.</summary>
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

    /// <summary>
    /// Whether a conditional put may have landed without its writer learning it: the answer was lost,
    /// and the head asked afterwards failed too. The caller decides, since neither outcome is proven.
    /// </summary>
    public bool OutcomeUnknown { get; init; }

    /// <summary>The HTTP status of the service's refusal, or 0 when no service answered.</summary>
    public int StatusCode { get; init; }

    /// <summary>The service's code for the refusal, <c>AccessDenied</c> or <c>SlowDown</c>, when it named one.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>The id the service gave the refused request, which its support asks for, when it gave one.</summary>
    public string? RequestId { get; init; }
}
