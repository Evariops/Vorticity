using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.IO;

/// <summary>
/// A caller-owned, reusable set of segment reads. Register everything a split needs, then issue
/// <b>one</b> <see cref="ISegmentReader.ReadManyAsync"/> — that single call is what makes
/// coalescing possible. It carries both the requests and their results because an async method
/// cannot take a <c>Span&lt;T&gt;</c> parameter, and, holding the owners, it is where the rule of
/// exactly one reference per segment per batch is enforced.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not thread-safe, by design.</b> A request set belongs to exactly one decode flow, the same
/// way a <c>ScanContext</c> does. Concurrent splits each get their own set.
/// <see cref="ISegmentReader"/> implementations, by contrast, must be thread-safe.
/// </para>
/// <para>
/// <b>Lifetime.</b> Every slot holds one reference to a <see cref="SegmentOwner"/> and
/// <see cref="Release"/> gives back exactly one per slot. Buffers obtained from
/// <see cref="GetBuffer"/> are invalid the moment <see cref="Release"/> runs.
/// </para>
/// </remarks>
internal sealed class SegmentRequestSet : IDisposable
{
    private static readonly ulong s_hashSeed = DrawHashSeed();

    private const byte StateEmpty = 0;

    /// <summary>Filled by the in-flight read; <see cref="AbandonPending"/> undoes it.</summary>
    private const byte StatePending = 1;

    /// <summary>Filled at <see cref="Add"/> time (a zero-length segment); survives a retry.</summary>
    private const byte StatePermanent = 2;

    private SegmentSpec[] _specs;
    private SegmentOwner?[] _owners;
    private VortexBuffer[] _buffers;
    private byte[] _states;

    /// <summary>Open-addressed map from (Offset, Length) to slot + 1. Zero means empty.</summary>
    private int[] _table;
    private int _mask;

    private int _count;
    private bool _populated;

    /// <summary>Creates an empty set.</summary>
    /// <param name="initialCapacity">Slots to preallocate. Must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialCapacity"/> is not positive.</exception>
    public SegmentRequestSet(int initialCapacity = 16)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);

        _specs = new SegmentSpec[initialCapacity];
        _owners = new SegmentOwner?[initialCapacity];
        _buffers = new VortexBuffer[initialCapacity];
        _states = new byte[initialCapacity];
        _table = new int[TableSizeFor(initialCapacity)];
        _mask = _table.Length - 1;
    }

    /// <summary>How many distinct segments are registered.</summary>
    public int Count => _count;

    /// <summary>True once a <see cref="ISegmentReader.ReadManyAsync"/> over this set completed.</summary>
    public bool IsPopulated => _populated;

    /// <summary>
    /// Registers a segment and returns its slot.
    /// </summary>
    /// <param name="spec">The locator, straight off the wire.</param>
    /// <returns>
    /// The slot index. Registering the same <c>(Offset, Length)</c> twice returns the <b>same</b>
    /// slot and reads it once: a <c>vortex.dict</c> layout legitimately points several children at
    /// one values segment, and reading it twice doubles both the I/O and the refcounting.
    /// </returns>
    /// <exception cref="VortexFormatException">
    /// The spec is malformed — see <see cref="SegmentIo.ValidateSpec"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A <em>new</em> segment is registered after the set was populated. Its slot could never be
    /// filled, so this is a caller bug, not a file problem.
    /// </exception>
    public int Add(in SegmentSpec spec)
    {
        SegmentIo.ValidateSpec(in spec, out _, out int length);

        int hash = HashOf(spec.Offset, spec.Length);
        int existing = Find(in spec, hash);
        if (existing >= 0)
        {
            // Two registrations of one segment can declare different alignments. Keep the
            // stronger claim while the buffer does not exist yet: the run buffer is 64-aligned
            // either way, so widening never over-reports for an honest file, and once populated
            // the published buffer already carries the exponent it was built with.
            if (!_populated && spec.AlignmentExponent > _specs[existing].AlignmentExponent)
            {
                ref SegmentSpec slotSpec = ref _specs[existing];
                slotSpec = new SegmentSpec(
                    slotSpec.Offset,
                    slotSpec.Length,
                    spec.AlignmentExponent,
                    slotSpec.Compression,
                    slotSpec.Encryption);
            }

            return existing;
        }

        if (_populated)
        {
            ThrowAddAfterPopulate();
        }

        if (_count == _specs.Length)
        {
            Grow();
        }

        int slot = _count++;
        _specs[slot] = spec;
        _owners[slot] = null;
        _buffers[slot] = VortexBuffer.Empty;

        if (length == 0)
        {
            // Nothing to read, ever. Filling it here means every source can simply skip
            // zero-length specs and Complete() still finds a full set.
            _owners[slot] = new EmptySegmentOwner();
            _states[slot] = StatePermanent;
        }
        else
        {
            _states[slot] = StateEmpty;
        }

        Insert(hash, slot);
        return slot;
    }

    /// <summary>The spec registered in <paramref name="slot"/>. Available before the read.</summary>
    /// <param name="slot">A slot returned by <see cref="Add"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is not a live slot.</exception>
    public SegmentSpec GetSpec(int slot)
    {
        CheckSlot(slot);
        return _specs[slot];
    }

    /// <summary>Whether <paramref name="slot"/> already holds its bytes.</summary>
    /// <param name="slot">A slot returned by <see cref="Add"/>.</param>
    /// <remarks>A source uses this to skip the zero-length segments it never needs to read.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is not a live slot.</exception>
    public bool IsFilled(int slot)
    {
        CheckSlot(slot);
        return _states[slot] != StateEmpty;
    }

    /// <summary>The bytes of <paramref name="slot"/>.</summary>
    /// <param name="slot">A slot returned by <see cref="Add"/>.</param>
    /// <returns>
    /// The segment's view, or <see cref="VortexBuffer.Empty"/> for a zero-length segment. Valid
    /// only until <see cref="Release"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is not a live slot.</exception>
    /// <exception cref="InvalidOperationException">The set has not been read yet.</exception>
    public VortexBuffer GetBuffer(int slot)
    {
        CheckSlot(slot);
        if (!_populated)
        {
            ThrowNotPopulated();
        }

        return _buffers[slot];
    }

    /// <summary>
    /// The owner whose segment holds the first byte of <paramref name="buffer"/>, or null when no
    /// segment of this set does, which is always the answer for an empty buffer or an unread set.
    /// </summary>
    /// <param name="buffer">A view that may lie inside one of this set's segments.</param>
    /// <returns>The owner, held by this set; no reference is taken.</returns>
    internal unsafe SegmentOwner? OwnerHolding(VortexBuffer buffer)
    {
        if (!_populated || buffer.Length == 0)
        {
            return null;
        }

        byte* at = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(buffer.Span));
        for (int slot = 0; slot < _count; slot++)
        {
            VortexBuffer segment = _buffers[slot];
            if (segment.Length == 0)
            {
                continue;
            }

            byte* start = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(segment.Span));
            if (at >= start && at < start + segment.Length)
            {
                return _owners[slot];
            }
        }

        return null;
    }

    /// <summary>The owner backing <paramref name="slot"/>.</summary>
    /// <param name="slot">A slot returned by <see cref="Add"/>.</param>
    /// <returns>
    /// The owner. The set holds the reference; <b>do not</b> <see cref="SegmentOwner.Release"/> it.
    /// <see cref="SegmentOwner.Retain"/> it only to outlive this set, and release that extra
    /// reference yourself.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is not a live slot.</exception>
    /// <exception cref="InvalidOperationException">The set has not been read yet.</exception>
    public SegmentOwner GetOwner(int slot)
    {
        CheckSlot(slot);
        if (!_populated)
        {
            ThrowNotPopulated();
        }

        // Every live slot has an owner once populated: Complete() refuses to run otherwise.
        return _owners[slot]!;
    }

    // ---- the population API, used by ISegmentSource implementations ---------------------------

    /// <summary>
    /// Fills <paramref name="slot"/> from an owner dedicated to it, <b>taking</b> the caller's
    /// reference.
    /// </summary>
    /// <param name="slot">A slot returned by <see cref="Add"/>.</param>
    /// <param name="owner">
    /// An owner whose <see cref="SegmentOwner.Buffer"/> is exactly this segment. Ownership passes
    /// to the set even when this method then throws, so a failing source can rely on
    /// <see cref="AbandonPending"/> to release it.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is not a live slot.</exception>
    /// <exception cref="InvalidOperationException">The set is populated, or the slot is filled.</exception>
    /// <exception cref="VortexFormatException">
    /// The buffer is not exactly <c>spec.Length</c> bytes. A source never returns a short buffer:
    /// a truncated read means the file is shorter than its own footer claims.
    /// </exception>
    public void SetResult(int slot, SegmentOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        CheckFillable(slot);

        VortexBuffer buffer = owner.Buffer;
        _owners[slot] = owner;
        _buffers[slot] = buffer;
        _states[slot] = StatePending;

        // Stored before validating, so the caller's `catch { AbandonPending(); throw; }` releases
        // it rather than leaking it.
        CheckLength(slot, buffer.Length);
    }

    /// <summary>
    /// Fills <paramref name="slot"/> with a view into memory owned by <paramref name="owner"/>,
    /// <b>retaining</b> it. The caller keeps its own reference and releases that separately.
    /// </summary>
    /// <param name="slot">A slot returned by <see cref="Add"/>.</param>
    /// <param name="owner">The owner of the enclosing block — a mapping, or a coalesced run.</param>
    /// <param name="buffer">The segment's view inside that block.</param>
    /// <remarks>
    /// This is the allocation-free path: one <see cref="SegmentOwner.Retain"/> per slot and no new
    /// owner object, which is how a coalesced read hands out N zero-copy segments.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is not a live slot.</exception>
    /// <exception cref="InvalidOperationException">The set is populated, or the slot is filled.</exception>
    /// <exception cref="VortexFormatException">The buffer is not exactly <c>spec.Length</c> bytes.</exception>
    public void SetSharedResult(int slot, SegmentOwner owner, VortexBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(owner);
        CheckFillable(slot);

        // Validated before the Retain: nothing has been taken yet, so there is nothing to unwind.
        CheckLength(slot, buffer.Length);

        _owners[slot] = owner.Retain();
        _buffers[slot] = buffer;
        _states[slot] = StatePending;
    }

    /// <summary>
    /// Declares the read complete. Every slot must be filled.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A slot is still empty: the source did not honour its own plan.
    /// </exception>
    public void Complete()
    {
        if (_populated)
        {
            return;
        }

        for (int i = 0; i < _count; i++)
        {
            if (_states[i] == StateEmpty)
            {
                ThrowIncomplete(i);
            }
        }

        _populated = true;
    }

    /// <summary>
    /// Releases everything acquired since the last <see cref="Release"/> or <see cref="Complete"/>
    /// and leaves the registrations intact, so the read can be retried.
    /// </summary>
    /// <remarks>
    /// This is the all-or-nothing rule of a batch read in one call: a source wraps its read in
    /// <c>try { … } catch { requests.AbandonPending(); throw; }</c>. It does nothing on
    /// an already-populated set, so a failure that arrives after <see cref="Complete"/> cannot
    /// destroy a good result.
    /// </remarks>
    public void AbandonPending()
    {
        if (_populated)
        {
            return;
        }

        ExceptionDispatchInfo? failure = null;

        for (int i = 0; i < _count; i++)
        {
            if (_states[i] != StatePending)
            {
                continue;
            }

            SegmentOwner? owner = _owners[i];

            // Cleared before the release so a re-entrant call cannot release twice.
            _owners[i] = null;
            _buffers[i] = VortexBuffer.Empty;
            _states[i] = StateEmpty;

            failure = ReleaseQuietly(owner, failure);
        }

        failure?.Throw();
    }

    /// <summary>Releases every owner this set holds and clears it for reuse. Idempotent.</summary>
    public void Release()
    {
        ExceptionDispatchInfo? failure = null;

        for (int i = 0; i < _count; i++)
        {
            SegmentOwner? owner = _owners[i];

            _owners[i] = null;
            _buffers[i] = VortexBuffer.Empty;
            _states[i] = StateEmpty;
            _specs[i] = default;

            failure = ReleaseQuietly(owner, failure);
        }

        _count = 0;
        _populated = false;
        Array.Clear(_table);

        failure?.Throw();
    }

    /// <inheritdoc cref="Release"/>
    public void Dispose() => Release();

    // ---- internals ----------------------------------------------------------------------------

    /// <summary>
    /// Releases <paramref name="owner"/> without letting a failure abort the loop: a leaked block
    /// is worse than a swallowed diagnostic, so the first exception is carried and rethrown once
    /// everything else has been given back.
    /// </summary>
    private static ExceptionDispatchInfo? ReleaseQuietly(SegmentOwner? owner, ExceptionDispatchInfo? failure)
    {
        if (owner is null)
        {
            return failure;
        }

        try
        {
            owner.Release();
        }
        catch (Exception ex)
        {
            return failure ?? ExceptionDispatchInfo.Capture(ex);
        }

        return failure;
    }

    private void CheckFillable(int slot)
    {
        CheckSlot(slot);

        if (_populated)
        {
            ThrowAlreadyPopulated();
        }

        if (_states[slot] != StateEmpty)
        {
            ThrowSlotFilled(slot);
        }
    }

    private void CheckLength(int slot, int actual)
    {
        if (actual != (int)_specs[slot].Length)
        {
            ThrowWrongLength(slot, (int)_specs[slot].Length, actual);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckSlot(int slot)
    {
        if ((uint)slot >= (uint)_count)
        {
            ThrowSlotRange(slot, _count);
        }
    }

    private int Find(in SegmentSpec spec, int hash)
    {
        int index = hash & _mask;

        // Bounded by the table size rather than `while (true)`: the load factor keeps a full table
        // unreachable, but an unbounded probe loop on attacker-influenced keys is exactly the
        // shape of hang this reader promises never to have.
        for (int probe = 0; probe <= _mask; probe++)
        {
            int entry = _table[index];
            if (entry == 0)
            {
                return -1;
            }

            int slot = entry - 1;
            if (_specs[slot].Offset == spec.Offset && _specs[slot].Length == spec.Length)
            {
                return slot;
            }

            index = (index + 1) & _mask;
        }

        return -1;
    }

    private void Insert(int hash, int slot)
    {
        int index = hash & _mask;

        for (int probe = 0; probe <= _mask; probe++)
        {
            if (_table[index] == 0)
            {
                _table[index] = slot + 1;
                return;
            }

            index = (index + 1) & _mask;
        }

        ThrowTableFull();
    }

    private void Grow()
    {
        int capacity = _specs.Length * 2;

        Array.Resize(ref _specs, capacity);
        Array.Resize(ref _owners, capacity);
        Array.Resize(ref _buffers, capacity);
        Array.Resize(ref _states, capacity);

        _table = new int[TableSizeFor(capacity)];
        _mask = _table.Length - 1;

        for (int i = 0; i < _count; i++)
        {
            Insert(HashOf(_specs[i].Offset, _specs[i].Length), i);
        }
    }

    /// <summary>Power of two at least four times the capacity: load factor stays under 0.25.</summary>
    private static int TableSizeFor(int capacity)
    {
        long wanted = (long)capacity * 4;
        int size = 8;
        while (size < wanted && size < (1 << 30))
        {
            size <<= 1;
        }

        return size;
    }

    /// <summary>
    /// Mixes the whole key. Offsets in one file share their high bits and differ only in the low
    /// ones, so a plain low-bits mask would pile every segment of a chunk into one probe chain.
    /// </summary>
    private static int HashOf(ulong offset, uint length)
    {
        ulong h = (offset ^ s_hashSeed) * 0x9E3779B97F4A7C15UL;
        h ^= h >> 29;
        h += length * 0xBF58476D1CE4E5B9UL;
        h ^= h >> 32;
        return (int)(h & 0x7FFFFFFF);
    }

    /// <summary>
    /// Draws the per-process hash seed.
    /// </summary>
    /// <remarks>
    /// Segment offsets come straight from an untrusted footer. With a fixed mix, a file could
    /// declare tens of thousands of segments whose offsets all collide in the final masked hash,
    /// turning this linear open-addressed table quadratic — a legal file that makes the reader
    /// hang, which is exactly the failure class the reader promises never to have. A seed the
    /// file cannot know makes such a collision set impossible to construct. It changes probe
    /// order only: slots are handed out by registration order, so every result stays
    /// deterministic.
    /// </remarks>
    private static ulong DrawHashSeed()
    {
        Span<byte> bytes = stackalloc byte[16];
        Guid.NewGuid().TryWriteBytes(bytes);
        return BitConverter.ToUInt64(bytes) | 1UL;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowSlotRange(int slot, int count) =>
        throw new ArgumentOutOfRangeException(
            nameof(slot), slot, $"The set holds {count} slots.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowNotPopulated() =>
        throw new InvalidOperationException(
            "The segment request set has not been read yet; call ISegmentSource.ReadManyAsync first.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowAlreadyPopulated() =>
        throw new InvalidOperationException(
            "The segment request set is already populated; Release() it before reading again.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowAddAfterPopulate() =>
        throw new InvalidOperationException(
            "Cannot register a new segment after the set has been populated; its slot could never " +
            "be filled. Release() the set and register the whole split again.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowSlotFilled(int slot) =>
        throw new InvalidOperationException($"Slot {slot} has already been filled.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowIncomplete(int slot) =>
        throw new InvalidOperationException(
            $"Slot {slot} was never filled; the segment source completed without reading it.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowTableFull() =>
        throw new InvalidOperationException(
            "The segment request set's dedup table is full; the set cannot hold more segments.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowWrongLength(int slot, int expected, int actual) =>
        throw new VortexFormatException(
            $"Slot {slot} expects {expected} bytes but the source supplied {actual}.");
}
