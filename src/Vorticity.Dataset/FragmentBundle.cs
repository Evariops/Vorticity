using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Vorticity.Dataset;

/// <summary>
/// Several index containers carried as one fragment, so that many fragments cost one ranged read.
/// The containers are copied byte for byte behind a table of offsets rather than merged: every
/// offset inside a part still counts from that part's own first byte.
/// </summary>
internal static class FragmentBundle
{
    /// <summary>The magic that opens and closes a bundle.</summary>
    private static ReadOnlySpan<byte> Magic => "VXFB"u8;

    private const uint Version = 1;

    /// <summary>The trailer: the part count, the version, the magic.</summary>
    private const int TrailerBytes = sizeof(uint) + sizeof(uint) + 4;

    private const int RowBytes = sizeof(ulong) + sizeof(ulong);

    /// <summary>Every part starts here, because a container's own regions assume it.</summary>
    private const int Alignment = VortexLimits.MaxAlignment;

    /// <summary>Whether a fragment is a bundle rather than a single container.</summary>
    public static bool IsBundle(ReadOnlySpan<byte> fragment) =>
        fragment.Length >= Magic.Length + TrailerBytes && fragment[^Magic.Length..].SequenceEqual(Magic);

    /// <summary>The containers a fragment carries: itself, or a bundle's parts.</summary>
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

    /// <summary>
    /// One fragment carrying every container of the given fragments; bundling a bundle flattens it,
    /// so parts never nest.
    /// </summary>
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
