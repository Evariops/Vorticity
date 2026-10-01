using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
/// <param name="Rows">Its rows, those <see cref="Deletions"/> names left out.</param>
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
    /// <summary>The vector, once decoded or given; null while only its bytes are held.</summary>
    private DeletionVector? _deletions = DeletionVector.Empty;

    /// <summary>The vector's bytes as the entry's page holds them, until they are decoded.</summary>
    private ReadOnlyMemory<byte> _encoded;

    /// <summary>Where the vector lies when it is out of line, in the commit object that wrote it; none when it is in the entry.</summary>
    private PageReference _vectorAt;

    /// <summary>The bytes of an out-of-line vector, once read from where it lies.</summary>
    private Fetched? _fetched;

    /// <summary>
    /// The rows a delete took out of the object without rewriting it, by their positions in the
    /// object; empty for an object whose rows are all live, which every object is until then.
    /// </summary>
    /// <remarks>
    /// An entry read from a page holds the vector's bytes and decodes them the first time they are
    /// asked for, which is when the object is read: a walk over a page parses every entry it
    /// passes, and needs of a vector only the count the entry carries beside it.
    /// </remarks>
    /// <exception cref="CommitFormatException">The vector's bytes are not a vector of the object's rows.</exception>
    /// <exception cref="InvalidOperationException">The vector lies out of line and was not read yet: see <see cref="ResolveAsync"/>.</exception>
    public DeletionVector Deletions
    {
        get => _deletions ??= Decode();
        init
        {
            _deletions = value;
            _encoded = default;
            _vectorAt = PageReference.None;
            _fetched = null;
            DeletedRows = value.Count;
        }
    }

    /// <summary>
    /// Where the vector lies when it is out of line: in the commit object that wrote it, as an index
    /// fragment does, rather than in the entry and so in every page that holds the entry. None when
    /// the vector is in the entry, or there is none.
    /// </summary>
    public PageReference VectorAt => _vectorAt;

    /// <summary>Whether <see cref="Deletions"/> can be read without a request: the vector is in the entry, or was read from where it lies.</summary>
    public bool IsResolved => !_vectorAt.Exists || _deletions is not null || Volatile.Read(ref _fetched) is not null;

    /// <summary>How many rows <see cref="Deletions"/> names, known without decoding it.</summary>
    public long DeletedRows { get; private init; }

    /// <summary>The rows the object's file holds, the deleted ones included.</summary>
    public long PhysicalRows => Rows + DeletedRows;

    /// <summary>Whether a delete took rows out of the object without rewriting it.</summary>
    public bool HasDeletions => DeletedRows > 0;

    /// <summary>The bytes <see cref="Deletions"/> takes encoded, in the entry or where it lies, known without decoding it.</summary>
    public int VectorBytes => !HasDeletions ? 0
        : _vectorAt.Exists ? _vectorAt.Length
        : _deletions is null ? _encoded.Length
        : _deletions.EncodedBytes;

    /// <summary>The bytes the vector takes inside the entry: 0 when it lies out of line, or there is none.</summary>
    public int InlineVectorBytes => _vectorAt.Exists ? 0 : VectorBytes;

    /// <summary>
    /// Whether two entries take the same rows out: one vector has one encoding, so the same length
    /// and hash of its bytes, wherever they lie, and without reading one that lies out of line.
    /// </summary>
    public bool SameDeletions(ObjectEntry other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return DeletedRows == other.DeletedRows && (DeletedRows == 0 || VectorIdentity() == other.VectorIdentity());
    }

    /// <summary>The length and the XXH3-128 of the vector's bytes, which a reference to them carries.</summary>
    private (int Length, UInt128 Hash) VectorIdentity()
    {
        if (_vectorAt.Exists)
        {
            return (_vectorAt.Length, _vectorAt.Hash);
        }

        ReadOnlyMemory<byte> bytes = _deletions is null ? _encoded : _deletions.ToBytes();
        return (bytes.Length, System.IO.Hashing.XxHash128.HashToUInt128(bytes.Span));
    }

    /// <summary>The vector's bytes, encoded as the entry or where it lies holds them.</summary>
    /// <exception cref="InvalidOperationException">The vector lies out of line and was not read yet.</exception>
    public ReadOnlyMemory<byte> EncodedDeletions =>
        !HasDeletions ? default
        : _deletions is not null ? _deletions.ToBytes()
        : _vectorAt.Exists ? (Volatile.Read(ref _fetched) ?? throw Unresolved()).Bytes
        : _encoded;

    /// <summary>
    /// The entry with its vector out of line, at <paramref name="at"/>, where the commit writing it put
    /// the vector's bytes; the vector itself, decoded or not, stays at hand.
    /// </summary>
    public ObjectEntry WithVectorAt(PageReference at)
    {
        ReadOnlyMemory<byte> bytes = EncodedDeletions;
        return this with { _vectorAt = at, _fetched = new Fetched(bytes), _encoded = default };
    }

    /// <summary>
    /// Reads the vector from where it lies, when it lies out of line and was not read yet, through the
    /// version's pages: in the region the read opening its commit holds, in the handle's cache, or one
    /// ranged read. The reference checks the bytes; the entry keeps them, whoever else holds it.
    /// </summary>
    internal async ValueTask ResolveAsync(IPageSource pages, CancellationToken cancellationToken)
    {
        if (IsResolved)
        {
            return;
        }

        ReadOnlyMemory<byte> bytes = await pages.ReadPageAsync(_vectorAt, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _fetched, new Fetched(bytes));
    }

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
            || !Summaries.Equals(other.Summaries)
            || !SameDeletions(other))
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
        hash.Add(DeletedRows);
        return hash.ToHashCode();
    }

    /// <summary>
    /// The entry of the same object with <paramref name="deletions"/> as its deleted rows: its live
    /// rows counted again, and its summaries loosened into bounds, since a minimum, a maximum or a
    /// count of nulls taken over every row of the file may describe a row that is gone.
    /// </summary>
    public ObjectEntry WithDeletions(DeletionVector deletions) =>
        this with { Rows = PhysicalRows - deletions.Count, Deletions = deletions, Summaries = Summaries.Loosened() };

    private static InvalidOperationException Unresolved() =>
        new InvalidOperationException(
            "The entry's deletion vector lies in a commit object and was not read: resolve it before asking for its rows.");

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

        return _vectorAt.Exists && versions.Contains(_vectorAt.Version);
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

        // Past the fragments and the summaries, a vector out of line ends the entry: its reference's
        // version is its first eight bytes.
        Skip(value, ref at, references.Length);
        Skip(value, ref at, (long)Read(value, ref at));
        if (at < value.Length)
        {
            Read(value, ref at);
            if (Read(value, ref at) == 0 && value.Length - at == PageReference.Bytes)
            {
                return versions.Contains(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(value[at..]));
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

        // Out of line, the vector's place stands where its bytes would: a length of zero, which no
        // vector in the entry has, then the reference.
        bool outOfLine = HasDeletions && _vectorAt.Exists;
        ReadOnlyMemory<byte> deletions = !HasDeletions || outOfLine ? default : _deletions is null ? _encoded : _deletions.ToBytes();
        int bytes = TreePage.VarintBytes((ulong)key.Length) + key.Length
            + 16 + TreePage.VarintBytes((ulong)Rows) + TreePage.VarintBytes((ulong)Bytes) + 16
            + TreePage.VarintBytes((ulong)Fragments.Count) + (Fragments.Count * PageReference.Bytes)
            + TreePage.VarintBytes((ulong)summaries.Length) + summaries.Length
            + (outOfLine
                ? TreePage.VarintBytes((ulong)DeletedRows) + 1 + PageReference.Bytes
                : HasDeletions
                    ? TreePage.VarintBytes((ulong)DeletedRows) + TreePage.VarintBytes((ulong)deletions.Length) + deletions.Length
                    : 0);
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

        // Last, and only when there are any, so that the entry of an object whose rows are all
        // live is the bytes it always was, and a reader that knows no deletions refuses one that has.
        // Their count comes first, so that a walk knows the object's rows without the vector.
        if (outOfLine)
        {
            at = Write(at, (ulong)DeletedRows);
            at = Write(at, 0);
            _vectorAt.Write(at);
            at = at[PageReference.Bytes..];
        }
        else if (HasDeletions)
        {
            at = Write(at, (ulong)DeletedRows);
            at = Write(at, (ulong)deletions.Length);
            deletions.Span.CopyTo(at);
            at = at[deletions.Length..];
        }

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
    /// The bytes, the live and the deleted rows, the fragment count and the deletion vector's size an
    /// entry's bytes carry, read in place as <see cref="SummaryOf"/> reads the summaries: what a page's
    /// tally folds, without an allocation.
    /// </summary>
    /// <exception cref="CommitFormatException">The bytes are not an entry.</exception>
    public static (long Bytes, long Rows, long DeletedRows, long Fragments, long VectorBytes) TallyOf(ReadOnlySpan<byte> value)
    {
        int at = 0;
        Skip(value, ref at, (long)Read(value, ref at));
        Skip(value, ref at, 16);
        long rows = (long)Read(value, ref at);
        long bytes = (long)Read(value, ref at);
        Skip(value, ref at, 16);
        ulong listed = Read(value, ref at);
        if (listed > (ulong)((value.Length - at) / PageReference.Bytes))
        {
            throw new CommitFormatException("An object entry's fragment list is cut short.");
        }

        long fragments = (long)listed;
        Skip(value, ref at, fragments * PageReference.Bytes);
        Skip(value, ref at, (long)Read(value, ref at));
        long deleted = at < value.Length ? (long)Read(value, ref at) : 0;
        long vector = at < value.Length ? (long)Read(value, ref at) : 0;
        if (deleted > 0 && vector == 0 && value.Length - at == PageReference.Bytes)
        {
            // Out of line: the vector's bytes are what its reference says it holds.
            vector = PageReference.Read(value[at..]).Length;
        }

        return bytes < 0 || rows < 0 || deleted < 0 || vector < 0
            ? throw new CommitFormatException("An object entry counts its bytes or its rows past a long.")
            : (bytes, rows, deleted, fragments, vector);
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

    /// <summary>
    /// Reads an entry written by <see cref="ToBytes"/>, holding the bytes of its deletion vector as
    /// a slice of <paramref name="value"/>, which a page never changes, until they are asked for.
    /// </summary>
    /// <exception cref="CommitFormatException">The bytes are not an entry.</exception>
    public static ObjectEntry FromBytes(ReadOnlyMemory<byte> value)
    {
        ObjectEntry entry = Parse(value.Span, out int start, out int length);
        return length == 0 || entry._vectorAt.Exists ? entry : entry with { _encoded = value.Slice(start, length), _deletions = null };
    }

    /// <summary>Reads an entry written by <see cref="ToBytes"/>, copying the bytes of its deletion vector.</summary>
    /// <exception cref="CommitFormatException">The bytes are not an entry.</exception>
    public static ObjectEntry FromBytes(ReadOnlySpan<byte> value)
    {
        ObjectEntry entry = Parse(value, out int start, out int length);
        return length == 0 || entry._vectorAt.Exists ? entry : entry with { _encoded = value.Slice(start, length).ToArray(), _deletions = null };
    }

    /// <summary>
    /// The vector the entry's bytes hold, or the bytes read from where it lies, checked against the
    /// rows it names: each is one of the object's own, and as many as the entry says.
    /// </summary>
    private DeletionVector Decode()
    {
        ReadOnlySpan<byte> bytes = _vectorAt.Exists ? (Volatile.Read(ref _fetched) ?? throw Unresolved()).Bytes.Span : _encoded.Span;
        DeletionVector deletions = DeletionVector.FromBytes(bytes);
        if (deletions.Count != DeletedRows || deletions.EndOf(deletions.Runs - 1) > PhysicalRows)
        {
            throw new CommitFormatException("An object entry deletes other rows than it counts, or a row past the object's own.");
        }

        return deletions;
    }

    /// <summary>
    /// Everything an entry's bytes hold but its deletion vector, which is at
    /// <paramref name="start"/> for <paramref name="vector"/> bytes, none when it has no deleted row.
    /// </summary>
    private static ObjectEntry Parse(ReadOnlySpan<byte> value, out int start, out int vector)
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
        ObjectEntry entry = new ObjectEntry(key, uid, rows, bytes, hash, references, bounds);
        start = at;
        vector = 0;
        if (at < value.Length)
        {
            long deleted = (long)Read(value, ref at);
            int encoded = checked((int)Read(value, ref at));
            if (deleted <= 0 || deleted > long.MaxValue - rows)
            {
                throw new CommitFormatException("An object entry's deleted rows are none, or more than a long holds.");
            }

            if (encoded == 0)
            {
                // A vector out of line: its reference, to the entry's end.
                if (value.Length - at != PageReference.Bytes)
                {
                    throw new CommitFormatException("An object entry's vector out of line is not one reference to its end.");
                }

                PageReference lies = PageReference.Read(value[at..]);
                if (!lies.Exists || lies.Length == 0)
                {
                    throw new CommitFormatException("An object entry's vector out of line names no bytes.");
                }

                start = at;
                vector = PageReference.Bytes;
                return entry with { DeletedRows = deleted, _vectorAt = lies, _deletions = null };
            }

            if (at + encoded != value.Length)
            {
                throw new CommitFormatException("An object entry's deleted rows do not end where it does.");
            }

            start = at;
            vector = encoded;
            entry = entry with { DeletedRows = deleted };
        }

        return entry;
    }

    /// <summary>The bytes of a vector read from where it lies, shared by every holder of the entry.</summary>
    private sealed class Fetched(ReadOnlyMemory<byte> bytes)
    {
        internal ReadOnlyMemory<byte> Bytes { get; } = bytes;
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
