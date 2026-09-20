// A compacted fragment - docs/13-dataset.md §6.4: "An entry with more than K fragments is compacted
// by merging them (index bytes only) into one fragment in a new commit".
//
// A BUNDLE OF CONTAINERS, NOT A REBUILT INDEX, and what was measured decides it. Two of the index
// kinds cannot be merged from their own bytes into one run. A Bloom tree's upper filters are sized
// from the exact union of their blocks' hashes, which leaf filters do not give back; and a run's
// payload -- fence pages, filter-tree children -- holds offsets into its own container, which a
// copy into another container would have to find and rewrite, kind by kind. What §6.4 asks for is
// the cost: one fragment, one ranged read, index bytes only. So the K containers are copied as they
// are, one after another, each on its own alignment, behind a table that says where each begins;
// the reader splits them back into containers and reads each as the fragment it was. Every offset
// in every part still counts from that part's own first byte, which is why nothing is rewritten.
//
//   "VXFB" | padding | part 0 | padding | part 1 | ... | (u64 offset, u64 length) x n | u32 n | u32 version | "VXFB"
//
// WHAT IT DOES NOT MERGE is the runs: an entry indexed by n block ranges keeps n runs, one per
// range, where a rebuild would write one. A pruner reads a run per range at no extra request, the
// bundle being one read; a key cursor merges them as it merges any runs. Bundling a bundle flattens
// it, so the parts never nest.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Vorticity.Dataset;

/// <summary>Several index containers carried as one fragment (§6.4).</summary>
/// <remarks>
/// Internal: a bundle is how the indexer packs containers into one fragment, and no public member
/// hands one to a caller or takes one back.
/// </remarks>
internal static class FragmentBundle
{
    /// <summary>The magic that opens and closes a bundle.</summary>
    private static ReadOnlySpan<byte> Magic => "VXFB"u8;

    private const uint Version = 1;

    /// <summary>The trailer: the part count, the version, the magic.</summary>
    private const int TrailerBytes = sizeof(uint) + sizeof(uint) + 4;

    /// <summary>One table row: a part's offset and length.</summary>
    private const int RowBytes = sizeof(ulong) + sizeof(ulong);

    /// <summary>Where every part starts: the alignment a container's own regions assume.</summary>
    private const int Alignment = VortexLimits.MaxAlignment;

    /// <summary>Whether <paramref name="fragment"/> is a bundle rather than one container.</summary>
    /// <param name="fragment">A fragment's bytes.</param>
    /// <returns>Whether it ends with the bundle's magic.</returns>
    public static bool IsBundle(ReadOnlySpan<byte> fragment) =>
        fragment.Length >= Magic.Length + TrailerBytes && fragment[^Magic.Length..].SequenceEqual(Magic);

    /// <summary>The containers a fragment carries: itself, or a bundle's parts.</summary>
    /// <param name="fragment">A fragment's bytes, checked against its reference already.</param>
    /// <returns>The containers, each one whole.</returns>
    /// <exception cref="CommitFormatException">A bundle whose table does not describe its bytes.</exception>
    public static IReadOnlyList<ReadOnlyMemory<byte>> Unpack(ReadOnlyMemory<byte> fragment)
    {
        ReadOnlySpan<byte> bytes = fragment.Span;
        if (!IsBundle(bytes))
        {
            return [fragment];
        }

        ReadOnlySpan<byte> trailer = bytes[^TrailerBytes..];
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(trailer[sizeof(uint)..]);
        if (version != Version)
        {
            throw new CommitFormatException($"A fragment bundle of version {version} is not one this library reads.");
        }

        long tableStart = bytes.Length - TrailerBytes - ((long)count * RowBytes);
        if (count == 0 || tableStart < Magic.Length)
        {
            throw new CommitFormatException($"A fragment bundle of {bytes.Length} bytes cannot hold {count} part(s).");
        }

        List<ReadOnlyMemory<byte>> parts = new List<ReadOnlyMemory<byte>>((int)count);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> row = bytes.Slice((int)tableStart + (i * RowBytes), RowBytes);
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(row);
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(row[sizeof(ulong)..]);
            if (offset < (ulong)Magic.Length || offset + length > (ulong)tableStart || length == 0)
            {
                throw new CommitFormatException(
                    $"Part {i} of a fragment bundle lies at {offset}+{length}, outside the {tableStart} bytes before its table.");
            }

            parts.Add(fragment.Slice((int)offset, (int)length));
        }

        return parts;
    }

    /// <summary>One fragment carrying every container of <paramref name="fragments"/>, flattened.</summary>
    /// <param name="fragments">Fragments: containers, or bundles whose parts are taken one by one.</param>
    /// <returns>The bundle's bytes.</returns>
    /// <exception cref="ArgumentException">No container is given.</exception>
    public static byte[] Pack(IReadOnlyList<ReadOnlyMemory<byte>> fragments)
    {
        ArgumentNullException.ThrowIfNull(fragments);
        List<ReadOnlyMemory<byte>> parts = [];
        foreach (ReadOnlyMemory<byte> fragment in fragments)
        {
            parts.AddRange(Unpack(fragment));
        }

        if (parts.Count == 0)
        {
            throw new ArgumentException("A bundle carries at least one container.", nameof(fragments));
        }

        long[] offsets = new long[parts.Count];
        long at = Align(Magic.Length);
        for (int i = 0; i < parts.Count; i++)
        {
            offsets[i] = at;
            at = Align(at + parts[i].Length);
        }

        long length = at + ((long)parts.Count * RowBytes) + TrailerBytes;
        byte[] bundle = new byte[checked((int)length)];
        Magic.CopyTo(bundle);
        Span<byte> table = bundle.AsSpan((int)at);
        for (int i = 0; i < parts.Count; i++)
        {
            parts[i].Span.CopyTo(bundle.AsSpan((int)offsets[i]));
            BinaryPrimitives.WriteUInt64LittleEndian(table[(i * RowBytes)..], (ulong)offsets[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(table[((i * RowBytes) + sizeof(ulong))..], (ulong)parts[i].Length);
        }

        Span<byte> trailer = bundle.AsSpan(bundle.Length - TrailerBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, (uint)parts.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[sizeof(uint)..], Version);
        Magic.CopyTo(trailer[(2 * sizeof(uint))..]);
        return bundle;
    }

    private static long Align(long at) => (at + Alignment - 1) & ~((long)Alignment - 1);
}
