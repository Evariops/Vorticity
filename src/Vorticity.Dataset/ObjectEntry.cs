using System;
using System.Collections.Generic;
using System.Text;

namespace Vorticity.Dataset;

/// <summary>
/// One data object, as its leaf entry describes it. Its encoding is canonical, since an entry lies
/// inside a content-addressed page and two encodings of one entry would be two trees.
/// </summary>
/// <param name="Key">Its object key in the store, e.g. <c>data/&lt;uid&gt;.vortex</c>.</param>
/// <param name="Uid">
/// The identity its postscript carries: a version of the bytes, not of the file. An append mints a
/// new one, which is how a stale fragment is refused before any length is compared.
/// </param>
/// <param name="Rows">Its rows.</param>
/// <param name="Bytes">Its bytes in the store.</param>
/// <param name="Hash">The XXH3-128 its writer computed while writing.</param>
/// <param name="Fragments">The index fragments attached to it, in the order they were attached.</param>
/// <param name="Summaries">
/// Its bounded summaries: per summarised column, <c>min</c>, <c>max</c> and <c>null_count</c>, over
/// a bounded number of columns so that the entry has a bounded size whatever the schema.
/// </param>
internal sealed record ObjectEntry(
    string Key,
    UInt128 Uid,
    long Rows,
    long Bytes,
    UInt128 Hash,
    IReadOnlyList<PageReference> Fragments,
    ObjectSummaries Summaries)
{
    /// <summary>An entry with no fragment yet; null summaries means none.</summary>
    public ObjectEntry(
        string key, UInt128 uid, long rows, long bytes, UInt128 hash, ObjectSummaries? summaries = null)
        : this(key, uid, rows, bytes, hash, [], summaries ?? ObjectSummaries.Empty)
    {
    }

    /// <summary>An entry with fragments and no summaries.</summary>
    public ObjectEntry(
        string key, UInt128 uid, long rows, long bytes, UInt128 hash, IReadOnlyList<PageReference> fragments)
        : this(key, uid, rows, bytes, hash, fragments, ObjectSummaries.Empty)
    {
    }

    /// <summary>
    /// Whether two entries describe the same object in the same state. Written out because the
    /// generated equality would compare <see cref="Fragments"/> by reference, so two entries read
    /// out of two pages with the same fragments would come back unequal.
    /// </summary>
    public bool Equals(ObjectEntry? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null
            || !string.Equals(Key, other.Key, StringComparison.Ordinal)
            || Uid != other.Uid
            || Rows != other.Rows
            || Bytes != other.Bytes
            || Hash != other.Hash
            || Fragments.Count != other.Fragments.Count
            || !Summaries.Equals(other.Summaries))
        {
            return false;
        }

        for (int i = 0; i < Fragments.Count; i++)
        {
            if (Fragments[i] != other.Fragments[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(Key, StringComparer.Ordinal);
        hash.Add(Uid);
        hash.Add(Rows);
        hash.Add(Bytes);
        hash.Add(Hash);
        hash.Add(Fragments.Count);
        foreach (PageReference fragment in Fragments)
        {
            hash.Add(fragment);
        }

        hash.Add(Summaries);
        return hash.ToHashCode();
    }

    /// <summary>The entry with <paramref name="fragment"/> attached.</summary>
    public ObjectEntry With(PageReference fragment) =>
        this with { Fragments = [.. Fragments, fragment] };

    /// <summary>
    /// Whether a fragment of the same content as this reference's is attached. A fragment is its
    /// content: the same index bytes written by two commits are one fragment, and an entry never
    /// holds them twice.
    /// </summary>
    public bool Holds(PageReference fragment)
    {
        foreach (PageReference held in Fragments)
        {
            if (SameContent(held, fragment))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The entry without the fragment of this reference's content.</summary>
    public ObjectEntry Without(PageReference fragment)
    {
        List<PageReference> kept = new List<PageReference>(Fragments.Count);
        foreach (PageReference held in Fragments)
        {
            if (!SameContent(held, fragment))
            {
                kept.Add(held);
            }
        }

        return this with { Fragments = kept };
    }

    private static bool SameContent(PageReference left, PageReference right) =>
        left.Length == right.Length && left.Hash == right.Hash;

    /// <summary>Whether a fragment attached to this entry lies in one of <paramref name="versions"/>.</summary>
    public bool NamesAny(HashSet<ulong> versions)
    {
        for (int i = 0; i < Fragments.Count; i++)
        {
            if (versions.Contains(Fragments[i].Version))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an entry's bytes name a fragment that lies in one of <paramref name="versions"/>,
    /// read in place: the fields before the fragments are skipped as <see cref="SummaryOf"/> skips
    /// them, and each reference's version is its first eight bytes, so nothing is allocated.
    /// </summary>
    /// <exception cref="CommitFormatException">The bytes are not an entry.</exception>
    public static bool NamesAny(ReadOnlySpan<byte> value, HashSet<ulong> versions)
    {
        int at = 0;
        Skip(value, ref at, (long)Read(value, ref at));
        Skip(value, ref at, 16);
        Read(value, ref at);
        Read(value, ref at);
        Skip(value, ref at, 16);
        ulong fragments = Read(value, ref at);
        if (fragments > (ulong)((value.Length - at) / PageReference.Bytes))
        {
            throw new CommitFormatException("An object entry's fragment list is cut short.");
        }

        ReadOnlySpan<byte> references = value.Slice(at, (int)fragments * PageReference.Bytes);
        for (int i = 0; i < references.Length; i += PageReference.Bytes)
        {
            if (versions.Contains(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(references[i..])))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a fragment of exactly these bytes is already attached, wherever it lies. By content,
    /// because an uncommitted fragment has no reference to compare.
    /// </summary>
    public bool Holds(ReadOnlySpan<byte> fragment)
    {
        UInt128 hash = System.IO.Hashing.XxHash128.HashToUInt128(fragment);
        foreach (PageReference held in Fragments)
        {
            if (held.Length == fragment.Length && held.Hash == hash)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The entry's canonical bytes, as a leaf page carries them.</summary>
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

    /// <summary>
    /// The summaries an entry's bytes carry, as a slice of <paramref name="value"/>: the fields
    /// before them are varints and fixed-width hashes, so reaching them is a skip rather than a
    /// parse, and nothing is allocated.
    /// </summary>
    /// <exception cref="CommitFormatException">The bytes are not an entry.</exception>
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

    /// <summary>
    /// Moves past <paramref name="bytes"/> bytes, or says the entry is not one. The count is a long
    /// because it comes from a varint this code did not write, and an int would wrap to a small
    /// positive number that skips somewhere plausible.
    /// </summary>
    private static void Skip(ReadOnlySpan<byte> value, ref int at, long bytes)
    {
        if (bytes < 0 || at + bytes > value.Length)
        {
            throw new CommitFormatException("An object entry is cut short.");
        }

        at += (int)bytes;
    }

    /// <summary>Reads an entry written by <see cref="ToBytes"/>.</summary>
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
