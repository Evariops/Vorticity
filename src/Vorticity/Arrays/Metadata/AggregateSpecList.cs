// The durable, reusable form of a zone map's repeated AggregateSpecProto entries.
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// Holds the repeated <c>AggregateSpecProto</c> entries decoded from a <see cref="ZonedMetadata"/>
/// without allocating per entry. Reused across zone maps; owned by the caller.
/// </summary>
/// <remarks>
/// <para>
/// Ids and options are copied into two growable arrays and addressed by offset, so an instance
/// reaches a steady state after the first few zone maps and allocates nothing thereafter.
/// </para>
/// <para>
/// <b>A <see cref="ZonedMetadata"/> is a view onto the list it was read into.</b>
/// <see cref="ZonedMetadata.Read"/> clears the list first, so reading a second zone map into the
/// same instance invalidates the first metadata's spec indices. Use one list per zone map you need
/// to keep, or consume each before reading the next.
/// </para>
/// </remarks>
public sealed class AggregateSpecList
{
    private byte[] _bytes;
    private Entry[] _entries;
    private int _count;
    private int _byteCount;

    /// <summary>Creates an empty list.</summary>
    /// <param name="initialCapacity">Number of entries to size the backing arrays for.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialCapacity"/> is negative.</exception>
    public AggregateSpecList(int initialCapacity = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        int entries = Math.Max(initialCapacity, 1);
        _entries = new Entry[entries];
        _bytes = new byte[entries * 32];
    }

    /// <summary>Number of aggregate specs currently held.</summary>
    public int Count => _count;

    /// <summary>Discards every entry, keeping the backing arrays for reuse.</summary>
    public void Clear()
    {
        _count = 0;
        _byteCount = 0;
    }

    /// <summary>
    /// Appends one spec, copying both payloads into the list's own storage and resolving the id
    /// once.
    /// </summary>
    /// <param name="idUtf8">The aggregate id as UTF-8.</param>
    /// <param name="options">The aggregate's options payload.</param>
    /// <returns>The index of the appended entry.</returns>
    public int Add(ReadOnlySpan<byte> idUtf8, ReadOnlySpan<byte> options)
    {
        EnsureEntryCapacity(_count + 1);
        EnsureByteCapacity((long)_byteCount + idUtf8.Length + options.Length);

        int idOffset = _byteCount;
        idUtf8.CopyTo(_bytes.AsSpan(idOffset));
        int optionsOffset = idOffset + idUtf8.Length;
        options.CopyTo(_bytes.AsSpan(optionsOffset));
        _byteCount = optionsOffset + options.Length;

        _entries[_count] = new Entry(
            idOffset, idUtf8.Length, optionsOffset, options.Length, AggregateRegistry.Resolve(idUtf8));
        return _count++;
    }

    /// <summary>The aggregate id of entry <paramref name="index"/>, as UTF-8.</summary>
    /// <param name="index">Zero-based entry index.</param>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside <c>[0, Count)</c>.</exception>
    public ReadOnlySpan<byte> GetIdUtf8(int index)
    {
        ref readonly Entry e = ref EntryAt(index);
        return new ReadOnlySpan<byte>(_bytes, e.IdOffset, e.IdLength);
    }

    /// <summary>The options payload of entry <paramref name="index"/>.</summary>
    /// <param name="index">Zero-based entry index.</param>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside <c>[0, Count)</c>.</exception>
    public ReadOnlySpan<byte> GetOptions(int index)
    {
        ref readonly Entry e = ref EntryAt(index);
        return new ReadOnlySpan<byte>(_bytes, e.OptionsOffset, e.OptionsLength);
    }

    /// <summary>
    /// The resolved aggregate of entry <paramref name="index"/>. Resolved once, when the entry was
    /// added; <see cref="AggregateId.Unknown"/> disables that aggregate's pruning and is never an
    /// error (docs/08-semantics.md §4).
    /// </summary>
    /// <param name="index">Zero-based entry index.</param>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside <c>[0, Count)</c>.</exception>
    public AggregateId GetAggregate(int index) => EntryAt(index).Aggregate;

    /// <summary>
    /// The bound length of entry <paramref name="index"/>, when it is a bounded aggregate with a
    /// usable options payload.
    /// </summary>
    /// <param name="index">Zero-based entry index.</param>
    /// <param name="boundLength">Receives the maximum bound length in bytes.</param>
    /// <returns>False when the entry is not bounded or its options are unusable.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside <c>[0, Count)</c>.</exception>
    public bool TryGetBoundLength(int index, out uint boundLength) =>
        AggregateRegistry.TryGetBoundLength(GetAggregate(index), GetOptions(index), out boundLength);

    private ref readonly Entry EntryAt(int index)
    {
        if ((uint)index >= (uint)_count)
        {
            ThrowIndex(index, _count);
        }

        return ref _entries[index];
    }

    private void EnsureEntryCapacity(int required)
    {
        if (required <= _entries.Length)
        {
            return;
        }

        // `size *= 2` on its own overflows to a negative int and then spins forever at 2^31. The
        // count is bounded by the metadata length in practice, but a growth loop that can hang is
        // exactly the trap docs/03-architecture.md §6 is about, so the ceiling is explicit.
        if (required > Array.MaxLength)
        {
            ThrowTooLarge(required);
        }

        long size = _entries.Length;
        while (size < required)
        {
            size = size >= Array.MaxLength / 2 ? Array.MaxLength : size * 2;
        }

        Array.Resize(ref _entries, (int)size);
    }

    private void EnsureByteCapacity(long required)
    {
        if (required <= _bytes.Length)
        {
            return;
        }

        // Every spec costs at least two bytes on the wire, so `required` is bounded by the metadata
        // payload the caller already holds: this cannot be driven past the input size.
        if (required > Array.MaxLength)
        {
            ThrowTooLarge(required);
        }

        long size = _bytes.Length;
        while (size < required)
        {
            size = size >= Array.MaxLength / 2 ? Array.MaxLength : size * 2;
        }

        Array.Resize(ref _bytes, (int)size);
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowIndex(int index, int count) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, $"Valid range is [0, {count}).");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTooLarge(long required) =>
        throw new VortexFormatException(
            $"A zone map's aggregate specs need {required} bytes, which exceeds the maximum array length.");

    private readonly struct Entry
    {
        internal Entry(int idOffset, int idLength, int optionsOffset, int optionsLength, AggregateId aggregate)
        {
            IdOffset = idOffset;
            IdLength = idLength;
            OptionsOffset = optionsOffset;
            OptionsLength = optionsLength;
            Aggregate = aggregate;
        }

        internal int IdOffset { get; }

        internal int IdLength { get; }

        internal int OptionsOffset { get; }

        internal int OptionsLength { get; }

        internal AggregateId Aggregate { get; }
    }
}
