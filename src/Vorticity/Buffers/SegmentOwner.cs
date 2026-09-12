// docs/03-architecture.md §3.1 and §3.5: "returned buffers are owned by the source until the
// batch that requested them is disposed. A caching source therefore refcounts; the reader never
// frees what it did not allocate." This is that refcount.
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Vorticity.Buffers;

/// <summary>
/// Reference-counted owner of the memory behind one or more <see cref="VortexBuffer"/> views.
/// </summary>
/// <remarks>
/// <para>
/// A freshly constructed owner starts at reference count 1, held by whoever created it. Each
/// <see cref="Retain"/> adds one, each <see cref="Release"/> removes one, and the transition to
/// zero frees the memory exactly once. <see cref="Retain"/> and <see cref="Release"/> are
/// thread-safe: two <c>RecordBatch</c>es sharing a coalesced read may release concurrently.
/// </para>
/// <para>
/// <b>Under-release is a bug, not a no-op.</b> A <see cref="Release"/> that would drive the count
/// below zero restores it and throws <see cref="ObjectDisposedException"/>: silently ignoring it
/// would let a later <see cref="Retain"/> resurrect freed memory. For the same reason,
/// <see cref="Retain"/> on an already-freed owner throws instead of returning a dangling view.
/// </para>
/// <para>
/// <b><see cref="Dispose"/> is idempotent</b> — it drops the creator's single initial reference
/// the first time and does nothing on any later call, as <see cref="IDisposable"/> requires. It
/// is <em>not</em> a synonym for an unguarded <see cref="Release"/>: use <see cref="Release"/>
/// to give back a reference obtained from <see cref="Retain"/>.
/// </para>
/// </remarks>
public abstract class SegmentOwner : IDisposable
{
    private int _refCount = 1;
    private int _disposeGuard;

    /// <summary>Initializes an owner with a reference count of 1 and an empty buffer.</summary>
    /// <remarks>Derived types publish the real memory by assigning <see cref="Buffer"/> once the
    /// allocation has succeeded, so a partially constructed owner never exposes a live pointer.
    /// </remarks>
    protected SegmentOwner()
    {
    }

    /// <summary>The view over the owned memory. Valid until the reference count reaches zero.</summary>
    public VortexBuffer Buffer { get; private protected set; }

    /// <summary>Length in bytes of <see cref="Buffer"/>.</summary>
    public int Length => Buffer.Length;

    /// <summary>The current reference count. Diagnostics and tests only.</summary>
    internal int RefCount => Volatile.Read(ref _refCount);

    /// <summary>Adds one reference and returns this owner, so a borrow reads as one expression.</summary>
    /// <returns>This instance.</returns>
    /// <exception cref="ObjectDisposedException">The owner has already been freed.</exception>
    public SegmentOwner Retain()
    {
        int updated = Interlocked.Increment(ref _refCount);

        // updated <= 1 means the previous value was <= 0: the memory is gone (or being freed) and
        // this call is a use-after-free. Put the counter back before throwing so a concurrent
        // Release still sees a coherent value.
        if (updated <= 1)
        {
            Interlocked.Decrement(ref _refCount);
            ThrowDisposed();
        }

        return this;
    }

    /// <summary>Removes one reference, freeing the memory when the last one goes.</summary>
    /// <exception cref="ObjectDisposedException">
    /// The count was already zero: the caller released a reference it did not hold.
    /// </exception>
    public void Release()
    {
        int updated = Interlocked.Decrement(ref _refCount);

        if (updated == 0)
        {
            FreeCore();
            return;
        }

        if (updated < 0)
        {
            Interlocked.Increment(ref _refCount);
            ThrowDisposed();
        }
    }

    /// <summary>
    /// Drops the initial reference created with this owner. Calling it more than once is safe and
    /// does nothing after the first call.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeGuard, 1) != 0)
        {
            return;
        }

        Release();
    }

    /// <summary>
    /// Releases the underlying memory. Called exactly once, on the transition to reference count
    /// zero. Implementations must be safe to run from a finalizer: no managed object graph
    /// traversal, no locks that a finalized peer could hold.
    /// </summary>
    protected abstract void FreeCore();

    /// <summary>
    /// Resets the count to 1 when a pooled owner is handed out again.
    /// </summary>
    /// <remarks>Only the pool calls this, and only on an owner it has exclusively popped from a
    /// bucket, so no other thread can hold a reference at that moment.</remarks>
    private protected void ResetRefCount()
    {
        Volatile.Write(ref _disposeGuard, 0);
        Volatile.Write(ref _refCount, 1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private void ThrowDisposed() =>
        throw new ObjectDisposedException(
            GetType().Name,
            "The segment owner's reference count already reached zero; its memory has been " +
            "released. Retain() a buffer before handing it to code that outlives the batch.");
}
