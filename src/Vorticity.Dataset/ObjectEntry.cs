// What a leaf entry says about one data object - docs/13-dataset.md §4.2, the parts the protocol of
// §8 needs to reason about.
//
// WHY IT IS NOT ALL OF §4.2 YET. That section lists the key range, the object's key, size and row
// count, its identity, its bounded summaries and its index descriptor. The tree needs none of them
// (13 §4.1: the boundary rule looks at the KEY alone), and the commit protocol needs four: which
// object the entry is, whether it is still the same object, how many rows it has, and which
// fragments have been attached to it. The summaries are the dataset's business and arrive with it.
//
// CANONICAL, like a page and for the same reason: a leaf entry is inside a content-addressed page,
// so two encodings of one entry would be two trees. Varints, in order, no optional fields.
using System;
using System.Collections.Generic;
using System.Text;

namespace Vorticity.Dataset;

/// <summary>One data object, as its leaf entry describes it.</summary>
/// <param name="Key">Its object key in the store, e.g. <c>data/&lt;uid&gt;.vortex</c>.</param>
/// <param name="Uid">
/// The identity its postscript carries (§7): a version of the bytes, not of the file. An append
/// mints a new one, which is how a stale fragment is refused before any length is compared.
/// </param>
/// <param name="Rows">Its rows.</param>
/// <param name="Bytes">Its bytes in the store.</param>
/// <param name="Hash">The XXH3-128 its writer computed while writing (§7).</param>
/// <param name="Fragments">
/// The index fragments attached to it (§6.4), in the order they were attached; at most K after
/// fragment compaction.
/// </param>
public sealed record ObjectEntry(
    string Key,
    UInt128 Uid,
    long Rows,
    long Bytes,
    UInt128 Hash,
    IReadOnlyList<PageReference> Fragments)
{
    /// <summary>An entry with no fragment yet.</summary>
    /// <param name="key">The object's key.</param>
    /// <param name="uid">Its identity.</param>
    /// <param name="rows">Its rows.</param>
    /// <param name="bytes">Its bytes.</param>
    /// <param name="hash">Its content hash.</param>
    public ObjectEntry(string key, UInt128 uid, long rows, long bytes, UInt128 hash)
        : this(key, uid, rows, bytes, hash, [])
    {
    }

    /// <summary>The entry with <paramref name="fragment"/> attached.</summary>
    /// <param name="fragment">The fragment's reference.</param>
    /// <returns>The new entry.</returns>
    public ObjectEntry With(PageReference fragment) =>
        this with { Fragments = [.. Fragments, fragment] };

    /// <summary>Whether a fragment with this reference is already attached.</summary>
    /// <param name="fragment">The reference.</param>
    /// <returns>Whether the entry holds it.</returns>
    public bool Holds(PageReference fragment)
    {
        foreach (PageReference held in Fragments)
        {
            if (held == fragment)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The entry's canonical bytes, as a leaf page carries them.</summary>
    /// <returns>The bytes.</returns>
    public byte[] ToBytes()
    {
        byte[] key = Encoding.UTF8.GetBytes(Key);
        int bytes = TreePage.VarintBytes((ulong)key.Length) + key.Length
            + 16 + TreePage.VarintBytes((ulong)Rows) + TreePage.VarintBytes((ulong)Bytes) + 16
            + TreePage.VarintBytes((ulong)Fragments.Count) + (Fragments.Count * PageReference.Bytes);
        byte[] value = new byte[bytes];
        Span<byte> at = value;
        at = Write(at, (ulong)key.Length);
        key.CopyTo(at);
        at = at[key.Length..];
        at = WriteHash(at, Uid);
        at = Write(at, (ulong)Rows);
        at = Write(at, (ulong)Bytes);
        at = WriteHash(at, Hash);
        at = Write(at, (ulong)Fragments.Count);
        foreach (PageReference fragment in Fragments)
        {
            fragment.Write(at);
            at = at[PageReference.Bytes..];
        }

        return at.IsEmpty ? value : throw new CommitFormatException("An object entry was mis-sized.");
    }

    /// <summary>Reads an entry written by <see cref="ToBytes"/>.</summary>
    /// <param name="value">The bytes.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="CommitFormatException">The bytes are not an entry.</exception>
    public static ObjectEntry FromBytes(ReadOnlySpan<byte> value)
    {
        int at = 0;
        int length = checked((int)Read(value, ref at));
        if (at + length > value.Length)
        {
            throw new CommitFormatException("An object entry's key runs past its bytes.");
        }

        string key = Encoding.UTF8.GetString(value.Slice(at, length));
        at += length;
        UInt128 uid = ReadHash(value, ref at);
        long rows = (long)Read(value, ref at);
        long bytes = (long)Read(value, ref at);
        UInt128 hash = ReadHash(value, ref at);
        int fragments = checked((int)Read(value, ref at));
        List<PageReference> references = new List<PageReference>(Math.Min(fragments, 1 << 10));
        for (int i = 0; i < fragments; i++)
        {
            if (at + PageReference.Bytes > value.Length)
            {
                throw new CommitFormatException("An object entry's fragment list is cut short.");
            }

            references.Add(PageReference.Read(value[at..]));
            at += PageReference.Bytes;
        }

        if (at != value.Length)
        {
            throw new CommitFormatException($"An object entry has {value.Length - at} bytes left over.");
        }

        return new ObjectEntry(key, uid, rows, bytes, hash, references);
    }

    private static Span<byte> Write(Span<byte> destination, ulong value)
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

    private static Span<byte> WriteHash(Span<byte> destination, UInt128 value)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(destination, (ulong)(value >> 64));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], (ulong)value);
        return destination[16..];
    }

    private static ulong Read(ReadOnlySpan<byte> value, ref int at)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            if (at >= value.Length)
            {
                throw new CommitFormatException("An object entry ends inside a varint.");
            }

            byte b = value[at++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
            if (shift > 63)
            {
                throw new CommitFormatException("A varint of more than ten bytes is not a varint.");
            }
        }
    }

    private static UInt128 ReadHash(ReadOnlySpan<byte> value, ref int at)
    {
        if (at + 16 > value.Length)
        {
            throw new CommitFormatException("An object entry's hash is cut short.");
        }

        UInt128 hash = ((UInt128)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(value[at..]) << 64)
            | System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(value[(at + 8)..]);
        at += 16;
        return hash;
    }
}
