// What a leaf entry says about one data object - docs/13-dataset.md §4.2, the parts the protocol of
// §8 needs to reason about.
//
// WHAT IT CARRIES AND WHY. §4.2 lists the key range, the object's key, size and row count, its
// identity, its bounded summaries and its index descriptor. The tree itself needs none of them
// (13 §4.1: the boundary rule looks at the KEY alone), and the commit protocol needs four: which
// object the entry is, whether it is still the same object, how many rows it has, and which
// fragments have been attached to it. The SUMMARIES are here for a different reader: the scan, which
// must decide whether to open this object at all, and the parent node, which folds them into the
// union that lets it skip the whole subtree.
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
/// <param name="Summaries">
/// Its bounded summaries (§4.2): per summarised column, <c>min</c>, <c>max</c> and
/// <c>null_count</c>, over the first 32 columns by default, so that the entry has a bounded size
/// whatever the schema.
/// </param>
public sealed record ObjectEntry(
    string Key,
    UInt128 Uid,
    long Rows,
    long Bytes,
    UInt128 Hash,
    IReadOnlyList<PageReference> Fragments,
    ObjectSummaries Summaries)
{
    /// <summary>An entry with no fragment yet.</summary>
    /// <param name="key">The object's key.</param>
    /// <param name="uid">Its identity.</param>
    /// <param name="rows">Its rows.</param>
    /// <param name="bytes">Its bytes.</param>
    /// <param name="hash">Its content hash.</param>
    /// <param name="summaries">Its bounded summaries, or null for none.</param>
    public ObjectEntry(
        string key, UInt128 uid, long rows, long bytes, UInt128 hash, ObjectSummaries? summaries = null)
        : this(key, uid, rows, bytes, hash, [], summaries ?? ObjectSummaries.Empty)
    {
    }

    /// <summary>An entry with fragments and no summaries.</summary>
    /// <param name="key">The object's key.</param>
    /// <param name="uid">Its identity.</param>
    /// <param name="rows">Its rows.</param>
    /// <param name="bytes">Its bytes.</param>
    /// <param name="hash">Its content hash.</param>
    /// <param name="fragments">The fragments attached to it.</param>
    public ObjectEntry(
        string key, UInt128 uid, long rows, long bytes, UInt128 hash, IReadOnlyList<PageReference> fragments)
        : this(key, uid, rows, bytes, hash, fragments, ObjectSummaries.Empty)
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
        byte[] summaries = Summaries.ToBytes();
        int bytes = TreePage.VarintBytes((ulong)key.Length) + key.Length
            + 16 + TreePage.VarintBytes((ulong)Rows) + TreePage.VarintBytes((ulong)Bytes) + 16
            + TreePage.VarintBytes((ulong)Fragments.Count) + (Fragments.Count * PageReference.Bytes)
            + TreePage.VarintBytes((ulong)summaries.Length) + summaries.Length;
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

        at = Write(at, (ulong)summaries.Length);
        summaries.CopyTo(at);
        at = at[summaries.Length..];
        return at.IsEmpty ? value : throw new CommitFormatException("An object entry was mis-sized.");
    }

    /// <summary>The summaries an entry's bytes carry, without the rest of the entry being built.</summary>
    /// <param name="value">The entry's bytes, as a leaf page holds them.</param>
    /// <returns>Just the summaries' bytes, a slice of <paramref name="value"/>.</returns>
    /// <exception cref="CommitFormatException">The bytes are not an entry.</exception>
    /// <remarks>
    /// What <see cref="ISummaryFold.OfLeaf"/> needs and all it needs. The fields before the
    /// summaries are varints and fixed-width hashes, so reaching them is a skip rather than a parse,
    /// and no string, list or entry is allocated to get at them.
    /// </remarks>
    public static ReadOnlyMemory<byte> SummaryOf(ReadOnlyMemory<byte> value)
    {
        ReadOnlySpan<byte> span = value.Span;
        int at = 0;
        Skip(span, ref at, (long)Read(span, ref at));
        Skip(span, ref at, 16);
        Read(span, ref at);
        Read(span, ref at);
        Skip(span, ref at, 16);
        Skip(span, ref at, (long)Read(span, ref at) * PageReference.Bytes);
        int length = (int)Math.Min(Read(span, ref at), int.MaxValue);
        Skip(span, ref at, length);
        return value.Slice(at - length, length);
    }

    /// <summary>Moves past <paramref name="bytes"/> bytes, or says the entry is not one.</summary>
    /// <remarks>
    /// The count is a LONG because it comes from a varint in bytes this code did not write: a
    /// fragment count near <c>2^32</c> times a reference's size overflows an int, and an overflow
    /// that wraps to a small positive number is a skip that lands somewhere plausible. Widening is
    /// cheaper than reasoning about which wrap is harmless.
    /// </remarks>
    private static void Skip(ReadOnlySpan<byte> value, ref int at, long bytes)
    {
        if (bytes < 0 || at + bytes > value.Length)
        {
            throw new CommitFormatException("An object entry is cut short.");
        }

        at += (int)bytes;
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

        int summaries = checked((int)Read(value, ref at));
        if (at + summaries > value.Length)
        {
            throw new CommitFormatException("An object entry's summaries run past its bytes.");
        }

        ObjectSummaries bounds = ObjectSummaries.FromBytes(value.Slice(at, summaries));
        at += summaries;
        if (at != value.Length)
        {
            throw new CommitFormatException($"An object entry has {value.Length - at} bytes left over.");
        }

        return new ObjectEntry(key, uid, rows, bytes, hash, references, bounds);
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
