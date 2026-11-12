using System;
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
        LeafReader reader = new LeafReader(page);
        List<TreeEntry> entries = new List<TreeEntry>(Math.Min(reader.Count, 1 << 16));
        while (reader.TryRead(out TreeEntry entry))
        {
            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// A leaf page's entries read one at a time where they lie: a walk that passes a page makes no list of its entries,
    /// which cost a walk 40 bytes an object, and stops reading where it stops walking.
    /// </summary>
    internal struct LeafReader
    {
        private readonly ReadOnlyMemory<byte> _page;
        private int _at;
        private int _left;

        /// <summary>A reader of <paramref name="page"/>'s entries, from its first.</summary>
        /// <exception cref="CommitFormatException">The bytes are not a leaf page.</exception>
        internal LeafReader(ReadOnlyMemory<byte> page)
        {
            if (KindOf(page.Span) != TreePageKind.Leaf)
            {
                throw new CommitFormatException("This page is not a leaf page.");
            }

            _page = page;
            _at = 1;
            Count = checked((int)ReadVarint(page.Span, ref _at));
            _left = Count;
        }

        /// <summary>How many entries the page says it holds.</summary>
        internal readonly int Count { get; }

        /// <summary>The entry after the last one read; false past the last, once the page is checked to end there.</summary>
        /// <exception cref="CommitFormatException">The page is cut short, or has bytes past its last entry.</exception>
        internal bool TryRead(out TreeEntry entry)
        {
            if (_left == 0)
            {
                if (_at != _page.Length)
                {
                    throw new CommitFormatException(
                        $"A leaf page of {_page.Length} bytes has {_page.Length - _at} bytes after its {Count} entries.");
                }

                entry = default;
                return false;
            }

            ReadOnlyMemory<byte> key = ReadBytes(_page, ref _at);
            long rows = (long)ReadVarint(_page.Span, ref _at);
            ReadOnlyMemory<byte> value = ReadBytes(_page, ref _at);
            entry = new TreeEntry(key, value, rows);
            _left--;
            return true;
        }
    }

    /// <summary>Reads an internal page's entries, in key order.</summary>
    public static IReadOnlyList<InternalEntry> ReadInternal(ReadOnlyMemory<byte> page)
    {
        InternalReader reader = new InternalReader(page);
        List<InternalEntry> entries = new List<InternalEntry>(Math.Min(reader.Count, 1 << 16));
        while (reader.TryRead(out InternalEntry entry))
        {
            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>An internal page's entries read one at a time where they lie, as <see cref="LeafReader"/> reads a leaf's.</summary>
    internal struct InternalReader
    {
        private readonly ReadOnlyMemory<byte> _page;
        private int _at;
        private int _left;

        /// <summary>A reader of <paramref name="page"/>'s entries, from its first.</summary>
        /// <exception cref="CommitFormatException">The bytes are not an internal page.</exception>
        internal InternalReader(ReadOnlyMemory<byte> page)
        {
            if (KindOf(page.Span) != TreePageKind.Internal)
            {
                throw new CommitFormatException("This page is not an internal page.");
            }

            _page = page;
            _at = 1;
            Count = checked((int)ReadVarint(page.Span, ref _at));
            _left = Count;
        }

        /// <summary>How many entries the page says it holds.</summary>
        internal readonly int Count { get; }

        /// <summary>The entry after the last one read; false past the last, once the page is checked to end there.</summary>
        /// <exception cref="CommitFormatException">The page is cut short, or has bytes past its last entry.</exception>
        internal bool TryRead(out InternalEntry entry)
        {
            if (_left == 0)
            {
                if (_at != _page.Length)
                {
                    throw new CommitFormatException(
                        $"An internal page of {_page.Length} bytes has {_page.Length - _at} bytes after its {Count} entries.");
                }

                entry = default;
                return false;
            }

            ReadOnlyMemory<byte> min = ReadBytes(_page, ref _at);
            ReadOnlyMemory<byte> max = ReadBytes(_page, ref _at);
            long rows = (long)ReadVarint(_page.Span, ref _at);
            if (_at + PageReference.Bytes > _page.Length)
            {
                throw new CommitFormatException("An internal entry's reference is cut short.");
            }

            PageReference child = PageReference.Read(_page.Span[_at..]);
            _at += PageReference.Bytes;
            ReadOnlyMemory<byte> summary = ReadBytes(_page, ref _at);
            entry = new InternalEntry(min, max, rows, child, summary);
            _left--;
            return true;
        }
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
