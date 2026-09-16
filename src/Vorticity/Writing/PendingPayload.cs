// One index payload waiting to be written: how to build its array, and where it landed.
//
// A BUILDER DESCRIBES, THE INDEX WRITER WRITES. Every kind's payload is an ordinary array blob
// (docs/10-indexes.md §4.2), built into the index writer's own arena at the moment the file writer
// can take it -- between two chunks -- and dropped from the arena as soon as its bytes are out. So a
// builder holds its results in whatever shape it likes and hands over a delegate that lays them
// into canonical nodes; the arena, the dtype arena and the compression decision stay in one place.
using System;
using Vorticity.Arrays;
using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>One payload array of one run.</summary>
/// <param name="build">Lays the array into the arena and returns its node.</param>
/// <param name="compress">Whether the column compressor may choose its encoding.</param>
internal sealed class PendingPayload(Func<CanonicalArena, DTypeArena, int> build, bool compress)
{
    /// <summary>Lays the array into the arena and returns its node.</summary>
    internal Func<CanonicalArena, DTypeArena, int> Build { get; } = build;

    /// <summary>Whether the column compressor may choose its encoding.</summary>
    internal bool Compress { get; } = compress;

    /// <summary>Where the blob landed, once written.</summary>
    internal IndexSegment? Segment { get; set; }

    /// <summary>The serialized dtype of the array written, once written.</summary>
    internal byte[]? DType { get; set; }

    /// <summary>A payload over 32-bit words, uncompressed or not.</summary>
    /// <param name="words">The array.</param>
    /// <param name="compress">Whether to let the compressor bit-pack it.</param>
    internal static PendingPayload U32(uint[] words, bool compress) =>
        new PendingPayload(
            (arena, types) =>
            {
                Buffers.VortexBuffer buffer = arena.AllocateUninitialized(
                    words.Length * sizeof(uint), sizeof(uint), out Span<byte> bytes);
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan()).CopyTo(bytes);
                return arena.AddPrimitive(
                    types.Primitive(PType.U32, Nullability.NonNullable), words.Length,
                    Validity.NonNullable, PType.U32, buffer);
            },
            compress);
}
