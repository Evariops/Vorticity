// One index payload waiting to be written: how to build its array, and where it landed.
//
// A BUILDER DESCRIBES, THE INDEX WRITER WRITES. Every kind's payload is an ordinary array blob
// (docs/10-indexes.md §4.2), built into the index writer's own arena at the moment the file writer
// can take it -- between two chunks -- and dropped from the arena as soon as its bytes are out. So a
// builder holds its results in whatever shape it likes and hands over a delegate that lays them
// into canonical nodes; the arena, the dtype arena and the compression decision stay in one place.
//
// AN ESTIMATE UNTIL IT IS WRITTEN. `Auto` judges a builder by its bytes before its payloads have
// gone out (10 §5.5), so a payload carries its uncompressed size -- an upper bound, since the
// compressor only shrinks it -- and the writer replaces it with the real length on placement.
//
// LAID ONCE, THEN LET GO. The arrays a payload holds are copied into the arena when it is laid, and
// nothing reads them afterwards: the delegate is dropped, and a builder that rented them from a
// pool gets them back through `release`. A payload never laid -- its builder abandoned, the write
// cancelled -- leaves its rentals to the collector, which a pool tolerates.
using System;
using System.Buffers;
using Vorticity.Arrays;
using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>One payload array of one run.</summary>
/// <param name="build">Lays the array into the arena and returns its node.</param>
/// <param name="compress">Whether the column compressor may choose its encoding.</param>
/// <param name="estimate">Its uncompressed bytes, until the real length is known.</param>
/// <param name="release">Gives back what <paramref name="build"/> read, once it has run.</param>
internal sealed class PendingPayload(
    Func<CanonicalArena, DTypeArena, int> build, bool compress, long estimate, Action? release = null)
{
    private Func<CanonicalArena, DTypeArena, int>? _build = build;
    private Action? _release = release;

    /// <summary>Lays the array into the arena and returns its node; a payload is laid once.</summary>
    /// <param name="arena">The arena to lay it into.</param>
    /// <param name="types">The dtype arena.</param>
    internal int Build(CanonicalArena arena, DTypeArena types)
    {
        Func<CanonicalArena, DTypeArena, int> lay = _build
            ?? throw new InvalidOperationException("An index payload is laid once.");
        _build = null;
        int node = lay(arena, types);
        Action? release = _release;
        _release = null;
        release?.Invoke();
        return node;
    }

    /// <summary>Whether the column compressor may choose its encoding.</summary>
    internal bool Compress { get; } = compress;

    /// <summary>Its uncompressed bytes.</summary>
    internal long Estimate { get; } = estimate;

    /// <summary>The builder whose bytes it counts toward.</summary>
    internal IndexBuilder? Owner { get; set; }

    /// <summary>Where the blob landed, once written.</summary>
    internal IndexSegment? Segment { get; set; }

    /// <summary>The serialized dtype of the array written, once written.</summary>
    internal byte[]? DType { get; set; }

    /// <summary>A payload over 32-bit words, uncompressed or not.</summary>
    /// <param name="words">The array.</param>
    /// <param name="compress">Whether to let the compressor bit-pack it.</param>
    internal static PendingPayload U32(uint[] words, bool compress) =>
        new PendingPayload(
            (arena, types) => LayU32(arena, types, words, words.Length),
            compress,
            (long)words.Length * sizeof(uint));

    /// <summary>
    /// A payload over the first <paramref name="length"/> words of an array rented from
    /// <see cref="ArrayPool{T}.Shared"/>, given back once laid.
    /// </summary>
    /// <param name="rented">The rented array.</param>
    /// <param name="length">The words it holds.</param>
    /// <param name="compress">Whether to let the compressor bit-pack it.</param>
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
    /// <param name="rented">The rented array.</param>
    /// <param name="length">The words it holds.</param>
    /// <param name="compress">Whether to let the compressor bit-pack it.</param>
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
    /// <param name="rented">The rented array.</param>
    /// <param name="length">The words it holds.</param>
    /// <param name="compress">Whether to let the compressor bit-pack it.</param>
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
