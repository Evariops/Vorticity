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
/// <remarks>
/// Immutable but for what is learned of its deletion vector: its bytes, read from where they lie,
/// and the vector they decode to. That state is shared by every entry made from this one that names
/// the same vector, a fragment attached or the vector moved out of line, so the first read or decode
/// serves them all.
/// </remarks>
internal sealed class ObjectEntry : IEquatable<ObjectEntry>
{
    /// <summary>The vector as far as it is known; null when no row is deleted.</summary>
    private readonly Vector? _vector;

    /// <summary>Where the vector lies when it is out of line, in the commit object that wrote it; none when it is in the entry.</summary>
    private readonly PageReference _vectorAt;

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

    /// <summary>An entry whose rows are all live.</summary>
    /// <param name="key">Its object key in the store, e.g. <c>data/&lt;uid&gt;.vortex</c>.</param>
    /// <param name="uid">
    /// The identity its postscript carries: a version of the bytes, not of the file. An append mints a
    /// new one, which is how a stale fragment is refused before any length is compared.
    /// </param>
    /// <param name="rows">Its rows.</param>
    /// <param name="bytes">Its bytes in the store.</param>
    /// <param name="hash">The XXH3-128 its writer computed while writing.</param>
    /// <param name="fragments">The index fragments attached to it, in the order they were attached.</param>
    /// <param name="summaries">
    /// Its bounded summaries: per summarised column, <c>min</c>, <c>max</c> and <c>null_count</c>, over
    /// a bounded number of columns so that the entry has a bounded size whatever the schema.
    /// </param>
    public ObjectEntry(
        string key, UInt128 uid, long rows, long bytes, UInt128 hash, IReadOnlyList<PageReference> fragments, ObjectSummaries summaries)
        : this(key, uid, rows, bytes, hash, fragments, summaries, 0, PageReference.None, null)
    {
    }

    private ObjectEntry(
        string key,
        UInt128 uid,
        long rows,
        long bytes,
        UInt128 hash,
        IReadOnlyList<PageReference> fragments,
        ObjectSummaries summaries,
        long deletedRows,
        PageReference vectorAt,
        Vector? vector)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fragments);
        ArgumentNullException.ThrowIfNull(summaries);
        Key = key;
        Uid = uid;
        Rows = rows;
        Bytes = bytes;
        Hash = hash;
        Fragments = fragments;
        Summaries = summaries;
        DeletedRows = deletedRows;
        _vectorAt = vectorAt;
        _vector = vector;
    }

    /// <summary>Its object key in the store, e.g. <c>data/&lt;uid&gt;.vortex</c>.</summary>
    public string Key { get; }

    /// <summary>The identity its postscript carries: a version of the bytes, not of the file.</summary>
    public UInt128 Uid { get; }

    /// <summary>Its rows, those <see cref="Deletions"/> names left out.</summary>
    public long Rows { get; }

    /// <summary>Its bytes in the store.</summary>
    public long Bytes { get; }

    /// <summary>The XXH3-128 its writer computed while writing.</summary>
    public UInt128 Hash { get; }

    /// <summary>The index fragments attached to it, in the order they were attached.</summary>
    public IReadOnlyList<PageReference> Fragments { get; }

    /// <summary>Its bounded summaries.</summary>
    public ObjectSummaries Summaries { get; }

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
    public DeletionVector Deletions => _vector is null ? DeletionVector.Empty : _vector.Decoded ?? Decode(_vector);

    /// <summary>
    /// Where the vector lies when it is out of line: in the commit object that wrote it, as an index
    /// fragment does, rather than in the entry and so in every page that holds the entry. None when
    /// the vector is in the entry, or there is none.
    /// </summary>
    public PageReference VectorAt => _vectorAt;

    /// <summary>Whether <see cref="Deletions"/> can be read without a request: the vector is in the entry, or was read from where it lies.</summary>
    public bool IsResolved => _vector is null || _vector.InHand;

    /// <summary>How many rows <see cref="Deletions"/> names, known without decoding it.</summary>
    public long DeletedRows { get; }

    /// <summary>The rows the object's file holds, the deleted ones included.</summary>
    public long PhysicalRows => Rows + DeletedRows;

    /// <summary>Whether a delete took rows out of the object without rewriting it.</summary>
    public bool HasDeletions => DeletedRows > 0;

    /// <summary>The bytes <see cref="Deletions"/> takes encoded, in the entry or where it lies, known without decoding it.</summary>
    public int VectorBytes => _vector is null ? 0 : _vectorAt.Exists ? _vectorAt.Length : _vector.Length;

    /// <summary>The bytes the vector takes inside the entry: 0 when it lies out of line, or there is none.</summary>
    public int InlineVectorBytes => _vectorAt.Exists ? 0 : VectorBytes;

    /// <summary>
    /// Whether two entries take the same rows out: one vector has one encoding, so the same length
    /// and hash of its bytes, wherever they lie, and without reading one that lies out of line.
    /// </summary>
    public bool SameDeletions(ObjectEntry other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (DeletedRows != other.DeletedRows)
        {
            return false;
        }

        if (_vector is null || ReferenceEquals(_vector, other._vector))
        {
            return true;
        }

        // Two vectors in hand compare as runs, which encodes neither.
        if (_vector.Decoded is { } mine && other._vector!.Decoded is { } theirs)
        {
            return mine.Equals(theirs);
        }

        return VectorIdentity() == other.VectorIdentity();
    }

    /// <summary>The length and the XXH3-128 of the vector's bytes, which a reference to them carries.</summary>
    private (int Length, UInt128 Hash) VectorIdentity() =>
        _vectorAt.Exists ? (_vectorAt.Length, _vectorAt.Hash) : _vector!.Encoded()!.Identity;

    /// <summary>The vector's bytes, encoded as the entry or where it lies holds them.</summary>
    /// <exception cref="InvalidOperationException">The vector lies out of line and was not read yet.</exception>
    public ReadOnlyMemory<byte> EncodedDeletions => _vector is null ? default : (_vector.Encoded() ?? throw Unresolved()).Bytes;

    /// <summary>
    /// The entry with its vector out of line, at <paramref name="at"/>, where the commit writing it put
    /// the vector's bytes; the vector itself, decoded or not, stays at hand.
    /// </summary>
    /// <exception cref="InvalidOperationException">The vector lies out of line and was not read yet.</exception>
    public ObjectEntry WithVectorAt(PageReference at)
    {
        if (_vector?.Encoded() is null)
        {
            throw _vector is null
                ? new InvalidOperationException("An entry with no deleted row has no vector to place.")
                : Unresolved();
        }

        return new ObjectEntry(Key, Uid, Rows, Bytes, Hash, Fragments, Summaries, DeletedRows, at, _vector);
    }

    /// <summary>
    /// Reads the vector from where it lies, when it lies out of line and was not read yet, through the
    /// version's pages: in the region the read opening its commit holds, in the handle's cache, or one
    /// ranged read. The reference checks the bytes; every entry sharing the vector keeps them.
    /// </summary>
    internal async ValueTask ResolveAsync(IPageSource pages, CancellationToken cancellationToken)
    {
        if (IsResolved)
        {
            return;
        }

        _vector!.Fetched(await pages.ReadPageAsync(_vectorAt, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Whether two entries describe the same object in the same state. Fragments compare as the
    /// references they are, so two entries read out of two pages with the same fragments are equal.
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
    public override bool Equals(object? obj) => Equals(obj as ObjectEntry);

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

    /// <summary>What the entry says of its object, without reading or decoding its vector.</summary>
    public override string ToString() => FormattableString.Invariant(
        $"{Key} (rows {Rows}, deleted {DeletedRows}{(_vectorAt.Exists ? " out of line" : string.Empty)}, bytes {Bytes}, fragments {Fragments.Count})");

    /// <summary>
    /// The entry of the same object with <paramref name="deletions"/> as its deleted rows: its live
    /// rows counted again, and its summaries loosened into bounds, since a minimum, a maximum or a
    /// count of nulls taken over every row of the file may describe a row that is gone.
    /// </summary>
    public ObjectEntry WithDeletions(DeletionVector deletions)
    {
        ArgumentNullException.ThrowIfNull(deletions);
        return new ObjectEntry(
            Key,
            Uid,
            PhysicalRows - deletions.Count,
            Bytes,
            Hash,
            Fragments,
            Summaries.Loosened(),
            deletions.Count,
            PageReference.None,
            deletions.IsEmpty ? null : new Vector(deletions));
    }

    private static InvalidOperationException Unresolved() =>
        new InvalidOperationException(
            "The entry's deletion vector lies in a commit object and was not read: resolve it before asking for its rows.");

    /// <summary>The entry with <paramref name="fragment"/> attached.</summary>
    public ObjectEntry With(PageReference fragment) => WithFragments([.. Fragments, fragment]);

    /// <summary>The entry with <paramref name="fragments"/> attached in place of its own.</summary>
    public ObjectEntry WithFragments(IReadOnlyList<PageReference> fragments) =>
        new ObjectEntry(Key, Uid, Rows, Bytes, Hash, fragments, Summaries, DeletedRows, _vectorAt, _vector);

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

        return WithFragments(kept);
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
        int keyBytes = Encoding.UTF8.GetByteCount(Key);
        byte[] summaries = Summaries.ToBytes();

        // Out of line, the vector's place stands where its bytes would: a length of zero, which no
        // vector in the entry has, then the reference.
        bool outOfLine = HasDeletions && _vectorAt.Exists;
        ReadOnlyMemory<byte> deletions = !HasDeletions || outOfLine ? default : _vector!.Encoded()!.Bytes;
        int bytes = TreePage.VarintBytes((ulong)keyBytes) + keyBytes
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
        at = Write(at, (ulong)keyBytes);
        at = at[Encoding.UTF8.GetBytes(Key, at)..];
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
    public static ObjectEntry FromBytes(ReadOnlyMemory<byte> value) => Parse(value.Span, value, copy: false);

    /// <summary>Reads an entry written by <see cref="ToBytes"/>, copying the bytes of its deletion vector.</summary>
    /// <exception cref="CommitFormatException">The bytes are not an entry.</exception>
    public static ObjectEntry FromBytes(ReadOnlySpan<byte> value) => Parse(value, default, copy: true);

    /// <summary>
    /// The vector <paramref name="vector"/>'s bytes hold, checked against the rows it names: each is
    /// one of the object's own, and as many as the entry says. Kept for every entry sharing it.
    /// </summary>
    private DeletionVector Decode(Vector vector)
    {
        DeletionVector deletions = DeletionVector.FromBytes((vector.Encoded() ?? throw Unresolved()).Bytes.Span);
        if (deletions.Count != DeletedRows || deletions.EndOf(deletions.Runs - 1) > PhysicalRows)
        {
            throw new CommitFormatException("An object entry deletes other rows than it counts, or a row past the object's own.");
        }

        return vector.Decoded = deletions;
    }

    /// <summary>
    /// The entry <paramref name="value"/> holds, its vector's bytes a slice of
    /// <paramref name="memory"/>, the same bytes, or with <paramref name="copy"/> a copy of them.
    /// </summary>
    private static ObjectEntry Parse(ReadOnlySpan<byte> value, ReadOnlyMemory<byte> memory, bool copy)
    {
        int at = 0;
        int length = Length(Read(value, ref at), "key");
        if (at + length > value.Length)
        {
            throw new CommitFormatException("An object entry's key runs past its bytes.");
        }

        string key = Encoding.UTF8.GetString(value.Slice(at, length));
        at += length;
        UInt128 uid = ReadHash(value, ref at);
        long rows = Count(Read(value, ref at));
        long bytes = Count(Read(value, ref at));
        UInt128 hash = ReadHash(value, ref at);
        ulong listed = Read(value, ref at);
        if (listed > (ulong)((value.Length - at) / PageReference.Bytes))
        {
            throw new CommitFormatException("An object entry's fragment list is cut short.");
        }

        PageReference[] references = listed == 0 ? [] : new PageReference[(int)listed];
        for (int i = 0; i < references.Length; i++)
        {
            references[i] = PageReference.Read(value[at..]);
            at += PageReference.Bytes;
        }

        int summaries = Length(Read(value, ref at), "summaries");
        if (at + summaries > value.Length)
        {
            throw new CommitFormatException("An object entry's summaries run past its bytes.");
        }

        ObjectSummaries bounds = ObjectSummaries.FromBytes(value.Slice(at, summaries));
        at += summaries;
        if (at == value.Length)
        {
            return new ObjectEntry(key, uid, rows, bytes, hash, references, bounds);
        }

        long deleted = Count(Read(value, ref at));
        int encoded = Length(Read(value, ref at), "deletion vector");
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

            return new ObjectEntry(key, uid, rows, bytes, hash, references, bounds, deleted, lies, new Vector());
        }

        if (at + encoded != value.Length)
        {
            throw new CommitFormatException("An object entry's deleted rows do not end where it does.");
        }

        ReadOnlyMemory<byte> vector = copy ? value.Slice(at, encoded).ToArray() : memory.Slice(at, encoded);
        return new ObjectEntry(key, uid, rows, bytes, hash, references, bounds, deleted, PageReference.None, new Vector(vector));
    }

    /// <summary>A length a varint states, which is not one past an int on a format this code wrote.</summary>
    private static int Length(ulong value, string what) =>
        value > int.MaxValue ? throw new CommitFormatException($"An object entry's {what} is longer than an entry can be.") : (int)value;

    /// <summary>A count a varint states, which is not one past a long on a format this code wrote.</summary>
    private static long Count(ulong value) =>
        value > long.MaxValue ? throw new CommitFormatException("An object entry counts its rows or its bytes past a long.") : (long)value;

    /// <summary>
    /// An entry's deletion vector as far as it is known: its bytes once in hand, the vector they
    /// decode to once asked for, and their identity once compared. Either may be learned by any
    /// entry that shares it, on any thread: each is published whole, and two threads that learn one
    /// at once learn the same.
    /// </summary>
    private sealed class Vector
    {
        private DeletionVector? _decoded;
        private EncodedVector? _encoded;

        /// <summary>A vector given whole, encoded when its bytes are first asked for.</summary>
        internal Vector(DeletionVector decoded) => _decoded = decoded;

        /// <summary>A vector whose bytes are in hand, decoded when it is first asked for.</summary>
        internal Vector(ReadOnlyMemory<byte> bytes) => _encoded = new EncodedVector(bytes);

        /// <summary>A vector that lies out of line and was not read yet.</summary>
        internal Vector()
        {
        }

        /// <summary>Whether its bytes or the vector itself are in hand.</summary>
        internal bool InHand => Volatile.Read(ref _encoded) is not null || Volatile.Read(ref _decoded) is not null;

        /// <summary>The vector, once decoded or given.</summary>
        internal DeletionVector? Decoded
        {
            get => Volatile.Read(ref _decoded);
            set => Volatile.Write(ref _decoded, value);
        }

        /// <summary>Its encoded length, known without encoding it; 0 while neither its bytes nor the vector are in hand.</summary>
        internal int Length => Volatile.Read(ref _encoded) is { } encoded ? encoded.Bytes.Length : Volatile.Read(ref _decoded)?.EncodedBytes ?? 0;

        /// <summary>Its bytes: those in hand, or the vector's encoding, made once; null while neither is in hand.</summary>
        internal EncodedVector? Encoded()
        {
            if (Volatile.Read(ref _encoded) is { } encoded)
            {
                return encoded;
            }

            if (Volatile.Read(ref _decoded) is not { } decoded)
            {
                return null;
            }

            encoded = new EncodedVector(decoded.ToBytes());
            return Interlocked.CompareExchange(ref _encoded, encoded, null) ?? encoded;
        }

        /// <summary>Takes the bytes read from where the vector lies.</summary>
        internal void Fetched(ReadOnlyMemory<byte> bytes) => Interlocked.CompareExchange(ref _encoded, new EncodedVector(bytes), null);
    }

    /// <summary>A vector's bytes, and their length and hash once asked for, which is what a reference to them carries.</summary>
    private sealed class EncodedVector(ReadOnlyMemory<byte> bytes)
    {
        private UInt128 _hash;
        private bool _hashed;

        internal ReadOnlyMemory<byte> Bytes { get; } = bytes;

        internal (int Length, UInt128 Hash) Identity
        {
            get
            {
                if (!Volatile.Read(ref _hashed))
                {
                    _hash = System.IO.Hashing.XxHash128.HashToUInt128(Bytes.Span);
                    Volatile.Write(ref _hashed, true);
                }

                return (Bytes.Length, _hash);
            }
        }
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
