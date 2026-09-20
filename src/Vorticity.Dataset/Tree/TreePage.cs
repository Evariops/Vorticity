using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Vorticity.Dataset;

/// <summary>What a page holds.</summary>
internal enum TreePageKind
{
    /// <summary>Data objects.</summary>
    Leaf = 0,

    /// <summary>Child pages.</summary>
    Internal = 1,
}

/// <summary>
/// Reads and writes the bytes of a tree page. The encoding is canonical — nothing optional,
/// nothing writable two ways — because the pages are content-addressed.
/// </summary>
internal static class TreePage
{
    /// <summary>The kind byte and the entry count's largest varint.</summary>
    internal const int HeaderBytes = 1 + 5;

    /// <summary>Writes a leaf page from its entries, in key order.</summary>
    public static byte[] WriteLeaf(IReadOnlyList<TreeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        int bytes = 1 + VarintBytes((ulong)entries.Count);
        foreach (TreeEntry entry in entries)
        {
            bytes += entry.Bytes;
        }

        byte[] page = new byte[bytes];
        Span<byte> at = page;
        at[0] = (byte)TreePageKind.Leaf;
        at = at[1..];
        at = WriteVarint(at, (ulong)entries.Count);
        foreach (TreeEntry entry in entries)
        {
            at = WriteBytes(at, entry.Key.Span);
            at = WriteVarint(at, (ulong)entry.Rows);
            at = WriteBytes(at, entry.Value.Span);
        }

        return at.IsEmpty ? page : throw new CommitFormatException("A leaf page was mis-sized.");
    }

    /// <summary>Writes an internal page from its entries, in key order.</summary>
    public static byte[] WriteInternal(IReadOnlyList<InternalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        int bytes = 1 + VarintBytes((ulong)entries.Count);
        foreach (InternalEntry entry in entries)
        {
            bytes += entry.Bytes;
        }

        byte[] page = new byte[bytes];
        Span<byte> at = page;
        at[0] = (byte)TreePageKind.Internal;
        at = at[1..];
        at = WriteVarint(at, (ulong)entries.Count);
        foreach (InternalEntry entry in entries)
        {
            at = WriteBytes(at, entry.MinKey.Span);
            at = WriteBytes(at, entry.MaxKey.Span);
            at = WriteVarint(at, (ulong)entry.Rows);
            entry.Child.Write(at);
            at = at[PageReference.Bytes..];
            at = WriteBytes(at, entry.Summary.Span);
        }

        return at.IsEmpty ? page : throw new CommitFormatException("An internal page was mis-sized.");
    }

    /// <summary>What kind of page these bytes are.</summary>
    public static TreePageKind KindOf(ReadOnlySpan<byte> page)
    {
        if (page.IsEmpty)
        {
            throw new CommitFormatException("An empty page has no kind.");
        }

        return page[0] switch
        {
            (byte)TreePageKind.Leaf => TreePageKind.Leaf,
            (byte)TreePageKind.Internal => TreePageKind.Internal,
            _ => throw new CommitFormatException($"A page of kind {page[0]} is not a page."),
        };
    }

    /// <summary>Reads a leaf page's entries, in key order.</summary>
    public static IReadOnlyList<TreeEntry> ReadLeaf(ReadOnlyMemory<byte> page)
    {
        if (KindOf(page.Span) != TreePageKind.Leaf)
        {
            throw new CommitFormatException("This page is not a leaf page.");
        }

        int at = 1;
        int count = checked((int)ReadVarint(page.Span, ref at));
        List<TreeEntry> entries = new List<TreeEntry>(Math.Min(count, 1 << 16));
        for (int i = 0; i < count; i++)
        {
            ReadOnlyMemory<byte> key = ReadBytes(page, ref at);
            long rows = (long)ReadVarint(page.Span, ref at);
            ReadOnlyMemory<byte> value = ReadBytes(page, ref at);
            entries.Add(new TreeEntry(key, value, rows));
        }

        if (at != page.Length)
        {
            throw new CommitFormatException(
                $"A leaf page of {page.Length} bytes has {page.Length - at} bytes after its {count} entries.");
        }

        return entries;
    }

    /// <summary>Reads an internal page's entries, in key order.</summary>
    public static IReadOnlyList<InternalEntry> ReadInternal(ReadOnlyMemory<byte> page)
    {
        if (KindOf(page.Span) != TreePageKind.Internal)
        {
            throw new CommitFormatException("This page is not an internal page.");
        }

        int at = 1;
        int count = checked((int)ReadVarint(page.Span, ref at));
        List<InternalEntry> entries = new List<InternalEntry>(Math.Min(count, 1 << 16));
        for (int i = 0; i < count; i++)
        {
            ReadOnlyMemory<byte> min = ReadBytes(page, ref at);
            ReadOnlyMemory<byte> max = ReadBytes(page, ref at);
            long rows = (long)ReadVarint(page.Span, ref at);
            if (at + PageReference.Bytes > page.Length)
            {
                throw new CommitFormatException("An internal entry's reference is cut short.");
            }

            PageReference child = PageReference.Read(page.Span[at..]);
            at += PageReference.Bytes;
            ReadOnlyMemory<byte> summary = ReadBytes(page, ref at);
            entries.Add(new InternalEntry(min, max, rows, child, summary));
        }

        if (at != page.Length)
        {
            throw new CommitFormatException(
                $"An internal page of {page.Length} bytes has {page.Length - at} bytes after its {count} entries.");
        }

        return entries;
    }

    /// <summary>The bytes a varint of this value takes, 1 to 10.</summary>
    internal static int VarintBytes(ulong value)
    {
        int bytes = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            bytes++;
        }

        return bytes;
    }

    /// <summary>Writes an unsigned LEB128 varint and returns the rest of the destination.</summary>
    internal static Span<byte> WriteVarint(Span<byte> destination, ulong value)
    {
        int at = 0;
        while (value >= 0x80)
        {
            destination[at++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[at++] = (byte)value;
        return destination[at..];
    }

    private static Span<byte> WriteBytes(Span<byte> destination, ReadOnlySpan<byte> value)
    {
        destination = WriteVarint(destination, (ulong)value.Length);
        value.CopyTo(destination);
        return destination[value.Length..];
    }

    /// <summary>
    /// Reads an unsigned LEB128 varint, bounds-checked, moving <c>at</c> past it. <c>what</c> names
    /// the bytes in the message raised when they end inside the varint.
    /// </summary>
    internal static ulong ReadVarint(ReadOnlySpan<byte> page, ref int at, string what = "A page")
    {
        ulong value = 0;
        int shift = 0;
        while (true)
        {
            if (at >= page.Length)
            {
                throw new CommitFormatException($"{what} ends inside a varint.");
            }

            byte b = page[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
            if (shift > 63)
            {
                throw new CommitFormatException("A varint of more than ten bytes is not a varint.");
            }
        }
    }

    private static ReadOnlyMemory<byte> ReadBytes(ReadOnlyMemory<byte> page, ref int at)
    {
        ulong length = ReadVarint(page.Span, ref at);
        if (length > (ulong)(page.Length - at))
        {
            throw new CommitFormatException(
                $"A field of {length} bytes needs more than the {page.Length - at} the page has left.");
        }

        ReadOnlyMemory<byte> value = page.Slice(at, (int)length);
        at += (int)length;
        return value;
    }

    /// <summary>Compares two keys as <c>memcmp</c> does, which is the tree's order.</summary>
    public static int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) => left.SequenceCompareTo(right);
}
