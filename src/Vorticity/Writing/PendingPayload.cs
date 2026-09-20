using System;
using System.Buffers;
using Vorticity.Arrays;
using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>Lays a payload's array itself, where a delegate and its closure would be two more objects.</summary>
internal interface IPayloadLayout
{
    /// <summary>Lays the array into the arena and returns its node.</summary>
    int Lay(CanonicalArena arena, DTypeArena types);
}

/// <summary>
/// One index payload waiting to be written: how to build its array, and where it landed. The array
/// is laid only when the file writer can take it, between two chunks, so a builder may hold its
/// results in whatever shape it likes until then. The estimate is the uncompressed size, an upper
/// bound that judges a builder before its payloads go out, and the writer replaces it with the
/// real length once placed. A payload never laid leaves its rentals to the collector.
/// </summary>
internal sealed class PendingPayload(
    Func<CanonicalArena, DTypeArena, int>? build, bool compress, long estimate, Action? release = null)
{
    /// <summary>What lays the array: a delegate, or an <see cref="IPayloadLayout"/>; null once laid.</summary>
    private object? _build = build;
    private Action? _release = release;

    internal PendingPayload(IPayloadLayout layout, bool compress, long estimate)
        : this((Func<CanonicalArena, DTypeArena, int>?)null, compress, estimate) => _build = layout;

    /// <summary>Lays the array into the arena and returns its node; a payload is laid once.</summary>
    internal int Build(CanonicalArena arena, DTypeArena types)
    {
        object lay = _build ?? throw new InvalidOperationException("An index payload is laid once.");
        _build = null;
        int node = lay is IPayloadLayout layout
            ? layout.Lay(arena, types)
            : ((Func<CanonicalArena, DTypeArena, int>)lay)(arena, types);
        Action? release = _release;
        _release = null;
        release?.Invoke();
        return node;
    }

    /// <summary>Whether the column compressor may choose its encoding.</summary>
    internal bool Compress { get; } = compress;

    /// <summary>Its uncompressed bytes, until the real length is known.</summary>
    internal long Estimate { get; } = estimate;

    /// <summary>The builder whose bytes it counts toward.</summary>
    internal IndexBuilder? Owner { get; set; }

    /// <summary>Where the blob landed, once written.</summary>
    internal IndexSegment? Segment { get; set; }

    /// <summary>The serialized dtype of the array written, once written.</summary>
    internal byte[]? DType { get; set; }

    internal static PendingPayload U32(uint[] words, bool compress) =>
        new PendingPayload(
            (arena, types) => LayU32(arena, types, words, words.Length),
            compress,
            (long)words.Length * sizeof(uint));

    /// <summary>
    /// A payload over the first <paramref name="length"/> words of a rented array, given back to
    /// <see cref="ArrayPool{T}.Shared"/> once laid.
    /// </summary>
    internal static PendingPayload RentedU32(uint[] rented, int length, bool compress) =>
        new PendingPayload(
            (arena, types) => LayU32(arena, types, rented, length),
            compress,
            (long)length * sizeof(uint),
            () => ArrayPool<uint>.Shared.Return(rented));

    /// <summary>
    /// A payload over the first <paramref name="length"/> words of a rented <see cref="ulong"/>
    /// array, laid at 64 bits: a run's rows when it spans 2³² rows or more.
    /// </summary>
    internal static PendingPayload RentedU64(ulong[] rented, int length, bool compress) =>
        new PendingPayload(
            (arena, types) =>
            {
                Buffers.VortexBuffer buffer = arena.AllocateUninitialized(
                    length * sizeof(ulong), sizeof(ulong), out Span<byte> bytes);
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(rented.AsSpan(0, length)).CopyTo(bytes);
                return arena.AddPrimitive(
                    types.Primitive(PType.U64, Nullability.NonNullable), length,
                    Validity.NonNullable, PType.U64, buffer);
            },
            compress,
            (long)length * sizeof(ulong),
            () => ArrayPool<ulong>.Shared.Return(rented));

    /// <summary>
    /// A payload over the first <paramref name="length"/> words of a rented <see cref="ulong"/>
    /// array whose values all fit 32 bits, laid at 32 bits.
    /// </summary>
    internal static PendingPayload NarrowedU32(ulong[] rented, int length, bool compress) =>
        new PendingPayload(
            (arena, types) =>
            {
                Buffers.VortexBuffer buffer = arena.AllocateUninitialized(
                    length * sizeof(uint), sizeof(uint), out Span<byte> bytes);
                Span<uint> words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes);
                ReadOnlySpan<ulong> wide = rented.AsSpan(0, length);
                for (int i = 0; i < wide.Length; i++)
                {
                    words[i] = checked((uint)wide[i]);
                }

                return arena.AddPrimitive(
                    types.Primitive(PType.U32, Nullability.NonNullable), length,
                    Validity.NonNullable, PType.U32, buffer);
            },
            compress,
            (long)length * sizeof(uint),
            () => ArrayPool<ulong>.Shared.Return(rented));

    private static int LayU32(CanonicalArena arena, DTypeArena types, uint[] words, int length)
    {
        Buffers.VortexBuffer buffer = arena.AllocateUninitialized(
            length * sizeof(uint), sizeof(uint), out Span<byte> bytes);
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan(0, length)).CopyTo(bytes);
        return arena.AddPrimitive(
            types.Primitive(PType.U32, Nullability.NonNullable), length,
            Validity.NonNullable, PType.U32, buffer);
    }
}
