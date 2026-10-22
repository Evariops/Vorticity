using System;
using System.Collections.Generic;
using System.Text;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;
using Vorticity.Types.Serialization;

namespace Vorticity.Indexes;

/// <summary>
/// The table of contents of a sliced file's synopsis, which a dataset's entry carries so that a reader of
/// the synopsis or of a few slices needs no open of the file, not even its index directory: the encodings
/// its array blobs name, its slice table and the region of each column's chunk directory, the synopsis
/// part's extent, and each synopsis table with its region.
/// </summary>
/// <remarks>
/// A table's rows decode at the struct <see cref="SynopsisTableInfo.RowType"/> rebuilds from the file's
/// schema, which the contents hold once, so that they hold no table's dtype: the file's schema, not the
/// dataset's, which may have changed since. A table of contents a later library cannot read is ignored,
/// and the file is read through its directory.
/// <para>
/// A class, not a record: it keeps what its readers load and decode, page tables, row types, states structs,
/// which an equality of its members or a copy of it would compare or share.
/// </para>
/// </remarks>
/// <param name="schema">The file's schema, a struct of its columns, as <see cref="DTypeFlatBuffers"/> writes it.</param>
/// <param name="encodings">The array encodings the file's blobs name, by their index.</param>
/// <param name="slices">The file's slice table.</param>
/// <param name="chunkDirectories">Each top-level column's chunk directory, the regions of its index entry.</param>
/// <param name="partStart">Where the synopsis part starts in the file.</param>
/// <param name="partEnd">Where it ends.</param>
/// <param name="tables">Each synopsis table, and its chunks: their regions, the buckets they count over and their rows.</param>
/// <param name="pathDirectories">
/// Each variant column's path directory, and its region: what a question on a path the tables do not name
/// reads first, before that path's regions.
/// </param>
internal sealed class SynopsisContents(
    byte[] schema,
    IReadOnlyList<string> encodings,
    SliceTable slices,
    IReadOnlyList<IndexSegment> chunkDirectories,
    long partStart,
    long partEnd,
    IReadOnlyList<(SynopsisTableInfo Table, IReadOnlyList<TableChunk> Chunks)> tables,
    IReadOnlyList<(int Field, IndexSegment Region)>? pathDirectories = null)
{
    /// <summary>The file's schema, a struct of its columns, as <see cref="DTypeFlatBuffers"/> writes it.</summary>
    internal byte[] Schema { get; } = schema;

    /// <summary>The array encodings the file's blobs name, by their index.</summary>
    internal IReadOnlyList<string> Encodings { get; } = encodings;

    /// <summary>The file's slice table.</summary>
    internal SliceTable Slices { get; } = slices;

    /// <summary>Each top-level column's chunk directory, the regions of its index entry.</summary>
    internal IReadOnlyList<IndexSegment> ChunkDirectories { get; } = chunkDirectories;

    /// <summary>Where the synopsis part starts in the file.</summary>
    internal long PartStart { get; } = partStart;

    /// <summary>Where the synopsis part ends.</summary>
    internal long PartEnd { get; } = partEnd;

    /// <summary>Each synopsis table, and its chunks: their regions, the buckets they count over and their rows.</summary>
    internal IReadOnlyList<(SynopsisTableInfo Table, IReadOnlyList<TableChunk> Chunks)> Tables { get; } = tables;

    /// <summary>
    /// Each variant column's path directory, and its region: what a question on a path the tables do not name
    /// reads first, before that path's regions.
    /// </summary>
    internal IReadOnlyList<(int Field, IndexSegment Region)>? PathDirectories { get; } = pathDirectories;

    /// <summary>
    /// The region of the file's index directory: what a lookup of one value reads first, the runs of the
    /// file's identifiers and every other index in it, with no open of the file. Null when unknown.
    /// </summary>
    internal IndexSegment? DirectoryRegion { get; init; }

    /// <summary>
    /// The entries of the index directory of the indexes that locate a column's values (<see cref="IndexKinds.Locates"/>),
    /// each the region of its message within the directory, with its checksum: what a lookup by a value reads of the
    /// directory, those on its columns alone, where the whole directory of a day's object held every chunk of its synopsis
    /// tables. Null when the contents name none, and a lookup reads the whole directory.
    /// </summary>
    internal IReadOnlyList<LocatingEntry>? Locating { get; init; }

    /// <summary>
    /// How the paged columns' chunk directories lie (<see cref="SliceChunks.PagedVersion"/>): each such column names its page
    /// table in <see cref="ChunkDirectories"/>, and the first row of each page, which every paged column shares, says which
    /// pages a slice's rows lie in, with no read. Null when no column's directory is paged.
    /// </summary>
    internal ChunkPaging? Paging { get; init; }

    /// <summary>
    /// These contents naming the region of the file's index directory <paramref name="directory"/> and the entries of
    /// the indexes that locate a column's values in it, <paramref name="locating"/>: new contents, which hold nothing a
    /// reader of these loaded.
    /// </summary>
    internal SynopsisContents Naming(IndexSegment directory, IReadOnlyList<LocatingEntry>? locating) =>
        new SynopsisContents(Schema, Encodings, Slices, ChunkDirectories, PartStart, PartEnd, Tables, PathDirectories)
        {
            DirectoryRegion = directory,
            Locating = locating,
            Paging = Paging,
        };

    // The page tables of the paged columns a read loaded, by column; null until one is.
    private SliceChunks.PageTable?[]? _pageTables;

    /// <summary>
    /// Column <paramref name="column"/>'s page table, once a read of it loaded and checked it (<see cref="KeepPageTable"/>):
    /// what a later read asks the column's pages by, with their checksums, which the synopsis cache keeps them by. Null before.
    /// </summary>
    internal SliceChunks.PageTable? PageTable(int column) =>
        System.Threading.Volatile.Read(ref _pageTables) is { } tables ? System.Threading.Volatile.Read(ref tables[column]) : null;

    /// <summary>Keeps <paramref name="table"/>, read and checked, as column <paramref name="column"/>'s page table; a read beside that kept one first leaves it.</summary>
    internal void KeepPageTable(int column, SliceChunks.PageTable table)
    {
        SliceChunks.PageTable?[] tables = System.Threading.Volatile.Read(ref _pageTables)
            ?? System.Threading.Interlocked.CompareExchange(ref _pageTables, new SliceChunks.PageTable?[ChunkDirectories.Count], null)
            ?? _pageTables!;
        System.Threading.Interlocked.CompareExchange(ref tables[column], table, null);
    }

    /// <summary>
    /// The bytes of a table's list at most for its entry to keep its chunks in its place (<see cref="TableChunks.List"/>):
    /// a few chunks, whose list would cost a question a round trip for some bytes. The tables of an hour's object all keep
    /// theirs, where their lists doubled the requests of a question over twelve of them; of a day's 44 tables, the 33 of
    /// one to seven chunks, whose lists take 3.1 KB, keep theirs, and the larger ones keep their lists apart.
    /// </summary>
    internal const int InlineListBytes = 512;

    private const int TagIndexDirectory = 11;

    // The paging of the chunk directories: the entries of a page, the chunks of a paged column, the first row of each page,
    // and the paged columns, the last two as deltas, packed.
    private const int TagPaging = 13;
    private const int TagPagingEntries = 1;
    private const int TagPagingCount = 2;
    private const int TagPagingRows = 3;
    private const int TagPagingColumns = 4;
    private const int TagPathDirectory = 10;
    private const int TagDirectoryField = 1;
    private const int TagDirectoryRegion = 2;

    // An entry of a locating index: the column of the root it lies on, its kind, the region of its message.
    private const int TagLocating = 12;
    private const int TagLocatingColumn = 1;
    private const int TagLocatingKind = 2;
    private const int TagLocatingRegion = 3;

    private const int Version = 1;
    private const int TagVersion = 1;
    private const int TagEncoding = 2;
    private const int TagSlices = 3;
    private const int TagChunkDirectory = 4;
    private const int TagPartStart = 5;
    private const int TagPartEnd = 6;
    private const int TagTable = 7;

    // Tag 8 held the synopsis columns of the branch's first writers, which no reader asked for: skipped, as an unknown tag is.
    private const int TagSchema = 9;
    private const int TagTableInfo = 1;

    // Tag 2 of a table held the one region of the branch's first tables, before they came in chunks: skipped, as an
    // unknown tag is.
    private const int TagTableChunk = 3;

    // A table whose chunks are listed apart, in a region of the object (TableChunks.List): the region, then the chunks'
    // count, the bytes of their keys' bounds and how many of them say a checksum, all known before the list is read.
    private const int TagTableList = 4;
    private const int TagTableCount = 5;
    private const int TagTableKeyBytes = 6;
    private const int TagTableDeclared = 7;
    private const int TagChunkRegion = 1;
    private const int TagChunkFirst = 2;
    private const int TagChunkLast = 3;
    private const int TagChunkRows = 4;
    private const int TagChunkLeastKey = 5;
    private const int TagChunkGreatestKey = 6;

    // The region of a chunk's distinct sketches, which a table of sketches lays apart from the rest of its rows.
    private const int TagChunkDistinct = 7;

    /// <summary>The file's schema, read into <paramref name="types"/>; a struct of its columns.</summary>
    /// <exception cref="VortexFormatException">The schema's bytes do not decode.</exception>
    internal DType SchemaIn(DTypeArena types) => DTypeFlatBuffers.Read(Schema, types);

    /// <summary>
    /// The file's encoding table, resolved the first time a read of its regions asks for it: the table a context
    /// that decodes them is rebound to, and the ids its messages name.
    /// </summary>
    internal (Arrays.ArrayEncodingId[] Resolved, string[] Ids) EncodingTable
    {
        get
        {
            if (System.Threading.Volatile.Read(ref _encodingTableBox) is { } known)
            {
                return known.Value;
            }

            string[] ids = [.. Encodings];
            System.Runtime.CompilerServices.StrongBox<(Arrays.ArrayEncodingId[], string[])> resolved = new((Arrays.ScanContext.Resolve(ids), ids));
            return (System.Threading.Interlocked.CompareExchange(ref _encodingTableBox, resolved, null) ?? resolved).Value;
        }
    }

    private System.Runtime.CompilerServices.StrongBox<(Arrays.ArrayEncodingId[], string[])>? _encodingTableBox;

    /// <summary>The regions the contents declare with a checksum, by offset and length; null until asked.</summary>
    private IReadOnlyDictionary<(ulong Offset, uint Length), IndexSegment>? _declared;

    /// <summary>
    /// The regions the contents declare with a checksum, by their offset and length, which the synopsis cache may keep:
    /// built the first time an object's read through the cache asks for them, and shared by every later read, which
    /// built the map again for every object of every question. A table's chunks are looked for among those of the tables
    /// a question parsed (<see cref="DeclaredRegionMap"/>).
    /// </summary>
    internal IReadOnlyDictionary<(ulong Offset, uint Length), IndexSegment> DeclaredRegions
    {
        get
        {
            if (System.Threading.Volatile.Read(ref _declared) is { } known)
            {
                return known;
            }

            DeclaredRegionMap regions = new DeclaredRegionMap(this);
            return System.Threading.Interlocked.CompareExchange(ref _declared, regions, null) ?? regions;
        }
    }

    /// <summary>
    /// The regions the contents declare with a checksum: their directories' held at once, and a table's chunks' looked for
    /// among the chunks of the tables a question parsed, the only regions of a table a read through the cache asks for, as
    /// a question learns them from its chunks; every table's parsed only when the map is walked whole.
    /// </summary>
    private sealed class DeclaredRegionMap : IReadOnlyDictionary<(ulong Offset, uint Length), IndexSegment>
    {
        private readonly SynopsisContents _contents;
        private readonly Dictionary<(ulong, uint), IndexSegment> _directories = [];
        private readonly int _count;
        private Dictionary<(ulong, uint), IndexSegment>? _whole;

        internal DeclaredRegionMap(SynopsisContents contents)
        {
            _contents = contents;
            foreach (IndexSegment region in contents.ChunkDirectories)
            {
                Declare(region);
            }

            foreach ((int _, IndexSegment region) in contents.PathDirectories ?? [])
            {
                Declare(region);
            }

            if (contents.DirectoryRegion is { } directory)
            {
                Declare(directory);
            }

            // The entries of the locating indexes, which a lookup reads in place of the whole directory.
            foreach (LocatingEntry located in contents.Locating ?? [])
            {
                Declare(located.Region);
            }

            // A table's list of chunks apart from the entry is a region the cache keeps, as its chunks are.
            foreach ((SynopsisTableInfo _, IReadOnlyList<TableChunk> chunks) in contents.Tables)
            {
                if (chunks is TableChunks { List: { } list })
                {
                    Declare(list);
                }
            }

            int count = _directories.Count;
            for (int t = 0; t < contents.Tables.Count; t++)
            {
                IReadOnlyList<TableChunk> chunks = contents.Tables[t].Chunks;
                if (chunks is TableChunks lazy)
                {
                    count += lazy.Declared;
                    continue;
                }

                for (int c = 0; c < chunks.Count; c++)
                {
                    count += Declares(chunks[c].Region, chunks[c].Distinct);
                }
            }

            _count = count;
        }

        /// <summary>Every region the contents declare, the chunks of a list no read loaded yet among them.</summary>
        public int Count => _count;

        public IndexSegment this[(ulong Offset, uint Length) key] =>
            TryGetValue(key, out IndexSegment region) ? region : throw new KeyNotFoundException();

        public IEnumerable<(ulong Offset, uint Length)> Keys => Whole.Keys;

        public IEnumerable<IndexSegment> Values => Whole.Values;

        public bool ContainsKey((ulong Offset, uint Length) key) => TryGetValue(key, out _);

        public bool TryGetValue((ulong Offset, uint Length) key, out IndexSegment value)
        {
            if (_directories.TryGetValue(key, out value))
            {
                return true;
            }

            IReadOnlyList<(SynopsisTableInfo Table, IReadOnlyList<TableChunk> Chunks)> tables = _contents.Tables;
            for (int t = 0; t < tables.Count; t++)
            {
                // A table no question parsed holds no region a read asks for yet.
                IReadOnlyList<TableChunk> chunks = tables[t].Chunks;
                if (chunks is TableChunks { Parsed: null })
                {
                    continue;
                }

                if (chunks is TableChunks parsed ? parsed.TryFind(key.Offset, key.Length, out value) : TryFind(chunks, key.Offset, key.Length, out value))
                {
                    return true;
                }
            }

            value = default;
            return false;
        }

        /// <summary>
        /// The region of <paramref name="chunks"/> at <paramref name="offset"/>, of <paramref name="length"/> bytes, with a checksum,
        /// a chunk's own or its distinct sketches', looked for one by one.
        /// </summary>
        internal static bool TryFind(IReadOnlyList<TableChunk> chunks, ulong offset, uint length, out IndexSegment region)
        {
            for (int c = 0; c < chunks.Count; c++)
            {
                TableChunk chunk = chunks[c];
                foreach (IndexSegment? held in (ReadOnlySpan<IndexSegment?>)[chunk.Region, chunk.Distinct])
                {
                    if (held is { } found && found.Offset == offset && found.Length == length && found.Checksum is not null)
                    {
                        region = found;
                        return true;
                    }
                }
            }

            region = default;
            return false;
        }

        public IEnumerator<KeyValuePair<(ulong Offset, uint Length), IndexSegment>> GetEnumerator() => Whole.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private void Declare(IndexSegment region)
        {
            if (region.Checksum is not null)
            {
                _directories[(region.Offset, region.Length)] = region;
            }
        }

        /// <summary>
        /// Every region, every table's chunks parsed: what a walk of the whole map asks for. The chunks of a list no read
        /// loaded yet are not among them, nor counted apart: the map keeps the walk once every list is.
        /// </summary>
        private Dictionary<(ulong, uint), IndexSegment> Whole
        {
            get
            {
                if (System.Threading.Volatile.Read(ref _whole) is { } known)
                {
                    return known;
                }

                Dictionary<(ulong, uint), IndexSegment> regions = new Dictionary<(ulong, uint), IndexSegment>(_count);
                foreach (IndexSegment region in SynopsisCacheReader.Declared(_contents))
                {
                    regions[(region.Offset, region.Length)] = region;
                }

                foreach ((SynopsisTableInfo _, IReadOnlyList<TableChunk> chunks) in _contents.Tables)
                {
                    if (chunks is TableChunks { Loaded: false })
                    {
                        return regions;
                    }
                }

                return System.Threading.Interlocked.CompareExchange(ref _whole, regions, null) ?? regions;
            }
        }
    }

    /// <summary>The schema <see cref="SchemaType"/> read, in an arena of its own that nothing adds to; null until then.</summary>
    private System.Runtime.CompilerServices.StrongBox<DType>? _schema;

    /// <summary>
    /// The file's schema, read the first time it is asked for into an arena of its own, which only readers
    /// share: the contents of an entry are parsed once, and each question over the object asked for its schema
    /// again.
    /// </summary>
    /// <exception cref="VortexFormatException">The schema's bytes do not decode.</exception>
    internal DType SchemaType
    {
        get
        {
            if (System.Threading.Volatile.Read(ref _schema) is { } known)
            {
                return known.Value;
            }

            System.Runtime.CompilerServices.StrongBox<DType> read = new System.Runtime.CompilerServices.StrongBox<DType>(SchemaIn(new DTypeArena()));
            return (System.Threading.Interlocked.CompareExchange(ref _schema, read, null) ?? read).Value;
        }
    }

    /// <summary>
    /// The row types <see cref="TableRowTypes"/> built, their arena, which nothing adds to after, their fields' names, and
    /// the halves <see cref="TableApartTypes"/> names; null until then.
    /// </summary>
    private System.Runtime.CompilerServices.StrongBox<(DTypeArena Arena, DType[] Rows, string[][] Names, DType[] Mains, DType[] Distincts)>? _rowTypes;

    /// <summary>
    /// The row type of each table, by its place among <see cref="Tables"/>, built the first time a read of the tables
    /// asks for them into an arena of their own, which only readers share: each question over the object built them
    /// again into an arena of its own, a tenth of what a warm question answered from the tables allocated.
    /// </summary>
    internal (DTypeArena Arena, DType[] Rows, string[][] Names) TableRowTypes
    {
        get
        {
            (DTypeArena arena, DType[] rows, string[][] names, _, _) = RowTypes();
            return (arena, rows, names);
        }
    }

    /// <summary>
    /// For each table of sketches that holds distinct sketches, by its place among <see cref="Tables"/>, the struct of the
    /// first fields of its row type and that of its distinct sketches, its last (<see cref="SynopsisTableInfo.RowHalves"/>): what
    /// a chunk that lays the sketches apart decodes from its two regions. A default type for the other tables.
    /// </summary>
    internal (DType[] Mains, DType[] Distincts) TableApartTypes
    {
        get
        {
            (_, _, _, DType[] mains, DType[] distincts) = RowTypes();
            return (mains, distincts);
        }
    }

    private (DTypeArena Arena, DType[] Rows, string[][] Names, DType[] Mains, DType[] Distincts) RowTypes()
    {
        if (System.Threading.Volatile.Read(ref _rowTypes) is { } known)
        {
            return known.Value;
        }

        DTypeArena arena = new DTypeArena();
        DType schema = SchemaType;
        DType[] rows = new DType[Tables.Count];
        string[][] names = new string[rows.Length][];
        DType[] mains = new DType[rows.Length];
        DType[] distincts = new DType[rows.Length];
        for (int t = 0; t < rows.Length; t++)
        {
            SynopsisTableInfo table = Tables[t].Table;
            rows[t] = table.RowType(schema, Slices.Paths, arena);
            names[t] = new string[rows[t].FieldCount];
            for (int f = 0; f < names[t].Length; f++)
            {
                names[t][f] = rows[t].GetFieldName(f);
            }

            if (table.Distinct.Length > 0)
            {
                (mains[t], distincts[t]) = SynopsisTableInfo.RowHalves(rows[t], table.Distinct.Length, arena);
            }
        }

        System.Runtime.CompilerServices.StrongBox<(DTypeArena, DType[], string[][], DType[], DType[])> built = new((arena, rows, names, mains, distincts));
        return (System.Threading.Interlocked.CompareExchange(ref _rowTypes, built, null) ?? built).Value;
    }

    // Per table, the struct its batches of states were last read under.
    private StatesStruct?[]? _states;

    /// <summary>
    /// The struct the batches of states of table <paramref name="table"/> were last read under, when it was its fields
    /// <paramref name="fields"/> keeps, the bucket named <paramref name="instant"/>: what a question asked again of the
    /// object finds built, where it built the names and types of its columns and interned them at each answer.
    /// </summary>
    internal bool TryStatesStruct(int table, Layouts.FieldMask fields, string instant, out DType root)
    {
        if (System.Threading.Volatile.Read(ref _states) is { } kept && kept[table] is { } last && last.Fields.Is(fields)
            && ReferenceEquals(last.Instant, instant))
        {
            root = last.Root;
            return true;
        }

        root = default;
        return false;
    }

    /// <summary>Keeps <paramref name="root"/>, of the arena of <see cref="TableRowTypes"/>, as the struct table <paramref name="table"/> was read under (<see cref="TryStatesStruct"/>).</summary>
    internal void KeepStatesStruct(int table, Layouts.FieldMask fields, string instant, DType root)
    {
        StatesStruct?[] kept = System.Threading.Volatile.Read(ref _states)
            ?? System.Threading.Interlocked.CompareExchange(ref _states, new StatesStruct?[Tables.Count], null) ?? _states!;
        System.Threading.Volatile.Write(ref kept[table], new StatesStruct(fields, instant, root));
    }

    /// <summary>A struct a table's batches of states were read under: its fields, and the instant its bucket was named.</summary>
    private sealed record StatesStruct(Layouts.FieldMask Fields, string Instant, DType Root);

    /// <summary>The contents as an entry carries them: tagged fields, in the order of their tags.</summary>
    internal byte[] ToBytes()
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteUInt64Always(TagVersion, Version);
            foreach (string encoding in Encodings)
            {
                writer.WriteString(TagEncoding, encoding);
            }

            writer.WriteBytes(TagSlices, Slices.ToBytes());
            foreach (IndexSegment directory in ChunkDirectories)
            {
                IndexDirectory.WriteSegment(ref writer, TagChunkDirectory, directory);
            }

            writer.WriteUInt64Always(TagPartStart, (ulong)PartStart);
            writer.WriteUInt64Always(TagPartEnd, (ulong)PartEnd);
            foreach ((SynopsisTableInfo table, IReadOnlyList<TableChunk> chunks) in Tables)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagTable);
                writer.WriteBytes(TagTableInfo, table.OptionBytes());
                if (chunks is TableChunks { List: { } list } listed)
                {
                    IndexDirectory.WriteSegment(ref writer, TagTableList, list);
                    writer.WriteUInt64Always(TagTableCount, (ulong)listed.Count);
                    writer.WriteUInt64Always(TagTableKeyBytes, (ulong)listed.KeyBytes);
                    writer.WriteUInt64Always(TagTableDeclared, (ulong)listed.Declared);
                }
                else
                {
                    WriteChunks(ref writer, chunks);
                }
            }

            writer.WriteBytes(TagSchema, Schema);
            foreach ((int field, IndexSegment region) in PathDirectories ?? [])
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagPathDirectory);
                writer.WriteUInt32Always(TagDirectoryField, (uint)field);
                IndexDirectory.WriteSegment(ref writer, TagDirectoryRegion, region);
            }

            if (DirectoryRegion is { } directoryRegion)
            {
                IndexDirectory.WriteSegment(ref writer, TagIndexDirectory, directoryRegion);
            }

            foreach (LocatingEntry located in Locating ?? [])
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagLocating);
                writer.WriteUInt32Always(TagLocatingColumn, located.Column);
                writer.WriteStringAlways(TagLocatingKind, located.Kind);
                IndexDirectory.WriteSegment(ref writer, TagLocatingRegion, located.Region);
            }

            if (Paging is { } paging)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagPaging);
                writer.WriteUInt64Always(TagPagingEntries, (ulong)paging.PageEntries);
                writer.WriteUInt64Always(TagPagingCount, (ulong)paging.Count);
                writer.WriteBytesAlways(TagPagingRows, Deltas(paging.PageRows));
                writer.WriteBytesAlways(TagPagingColumns, Deltas(Array.ConvertAll(paging.Columns, static c => (long)c)));
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// The list of <paramref name="chunks"/> a writer lays in the object, apart from the entry (<see cref="TableChunks.List"/>):
    /// the chunk fields of a table's message, alone.
    /// </summary>
    internal static byte[] ListBytes(IReadOnlyList<TableChunk> chunks)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            WriteChunks(ref writer, chunks);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>Writes <paramref name="chunks"/> as the chunk fields of a table's message, or of its list.</summary>
    private static void WriteChunks(ref ProtoWriter writer, IReadOnlyList<TableChunk> chunks)
    {
        foreach (TableChunk chunk in chunks)
        {
            using ProtoWriter.MessageScope part = writer.BeginMessage(TagTableChunk);
            IndexDirectory.WriteSegment(ref writer, TagChunkRegion, chunk.Region);
            writer.WriteUInt64Always(TagChunkFirst, unchecked((ulong)chunk.FirstBucket));
            writer.WriteUInt64Always(TagChunkLast, unchecked((ulong)chunk.LastBucket));
            writer.WriteUInt64Always(TagChunkRows, (ulong)chunk.Rows);
            if (chunk.Keys is { } keys)
            {
                writer.WriteBytes(TagChunkLeastKey, keys.Least);
                writer.WriteBytes(TagChunkGreatestKey, keys.Greatest);
            }

            if (chunk.Distinct is { } distinct)
            {
                IndexDirectory.WriteSegment(ref writer, TagChunkDistinct, distinct);
            }
        }
    }

    /// <summary>
    /// The tables whose chunks are listed apart and were not loaded yet, among those <paramref name="reads"/> reads, every
    /// one when it is null: what a read of their chunks reads first, in one round trip (<see cref="TableChunks.Load"/>).
    /// Null for none.
    /// </summary>
    internal List<TableChunks>? Unloaded(ISynopsisReads? reads)
    {
        List<TableChunks>? unloaded = null;
        for (int t = 0; t < Tables.Count; t++)
        {
            (SynopsisTableInfo table, IReadOnlyList<TableChunk> chunks) = Tables[t];
            if (chunks is TableChunks { Loaded: false } listed && (reads is null || reads.ReadsTable(table)))
            {
                (unloaded ??= []).Add(listed);
            }
        }

        return unloaded;
    }

    /// <summary>Whether a table <paramref name="reads"/> reads, every one when it is null, waits for its list (<see cref="Unloaded"/>).</summary>
    internal bool HasUnloaded(ISynopsisReads? reads)
    {
        for (int t = 0; t < Tables.Count; t++)
        {
            (SynopsisTableInfo table, IReadOnlyList<TableChunk> chunks) = Tables[t];
            if (chunks is TableChunks { Loaded: false } && (reads is null || reads.ReadsTable(table)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The contents <paramref name="bytes"/> hold, or null when they do not decode or are of a later version.</summary>
    internal static SynopsisContents? From(ReadOnlySpan<byte> bytes) => From(new ReadOnlyMemory<byte>(bytes.ToArray()));

    /// <summary>
    /// The contents <paramref name="bytes"/> hold, or null when they do not decode or are of a later version.
    /// </summary>
    internal static SynopsisContents? From(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            ProtoReader reader = new ProtoReader(bytes.Span);
            ulong version = 0;
            List<string> encodings = [];
            SliceTable? slices = null;
            List<IndexSegment> directories = [];
            long start = 0;
            long end = 0;
            List<(SynopsisTableInfo, IReadOnlyList<TableChunk>)> tables = [];
            List<(int, IndexSegment)> pathDirectories = [];
            IndexSegment? indexDirectory = null;
            List<LocatingEntry>? locating = null;
            ChunkPaging? paging = null;
            byte[]? schema = null;
            while (reader.TryReadTag(out int field, out ProtoWireType wire))
            {
                switch (field, wire)
                {
                    case (TagVersion, ProtoWireType.Varint):
                        version = reader.ReadVarint();
                        break;
                    case (TagEncoding, ProtoWireType.LengthDelimited):
                        encodings.Add(Encoding.UTF8.GetString(reader.ReadLengthDelimited()));
                        break;
                    case (TagSlices, ProtoWireType.LengthDelimited):
                        slices = SliceTable.FromBytes(reader.ReadLengthDelimited());
                        break;
                    case (TagChunkDirectory, ProtoWireType.LengthDelimited):
                        directories.Add(IndexDirectory.ReadSegment(reader.ReadMessage()));
                        break;
                    case (TagPartStart, ProtoWireType.Varint):
                        start = checked((long)reader.ReadVarint());
                        break;
                    case (TagPartEnd, ProtoWireType.Varint):
                        end = checked((long)reader.ReadVarint());
                        break;
                    case (TagTable, ProtoWireType.LengthDelimited):
                        ReadOnlySpan<byte> held = reader.ReadLengthDelimited();
                        if (Table(bytes.Slice(reader.Position - held.Length, held.Length)) is not { } table)
                        {
                            return null;
                        }

                        tables.Add(table);
                        break;
                    case (TagSchema, ProtoWireType.LengthDelimited):
                        schema = reader.ReadLengthDelimited().ToArray();
                        break;
                    case (TagIndexDirectory, ProtoWireType.LengthDelimited):
                        indexDirectory = IndexDirectory.ReadSegment(reader.ReadMessage());
                        break;
                    case (TagPathDirectory, ProtoWireType.LengthDelimited):
                        if (Directory(reader.ReadMessage()) is not { } directory)
                        {
                            return null;
                        }

                        pathDirectories.Add(directory);
                        break;
                    case (TagLocating, ProtoWireType.LengthDelimited):
                        if (Located(reader.ReadMessage()) is not { } located)
                        {
                            return null;
                        }

                        (locating ??= []).Add(located);
                        break;
                    case (TagPaging, ProtoWireType.LengthDelimited):
                        if ((paging = Paged(reader.ReadMessage())) is null)
                        {
                            return null;
                        }

                        break;
                    default:
                        reader.SkipField(wire);
                        break;
                }
            }

            // The entries of a directory lie within it; a paged column's page table, whose length its pages give, is a chunk
            // directory the contents name, and its pages cover the slices' rows.
            return version == Version && slices is not null && schema is not null && end >= start
                && (locating is null || (indexDirectory is { } named && locating.TrueForAll(l => l.Region.Offset >= named.Offset
                    && l.Region.Offset + l.Region.Length <= named.Offset + named.Length)))
                && (paging is null || (slices.Slices.Count > 0 && paging.PageRows[^1] < slices.Rows && Array.TrueForAll(paging.Columns, c =>
                    c < directories.Count && directories[c].Length == SliceChunks.TableBytes(paging.PageRows.Length))))
                ? new SynopsisContents(schema, encodings, slices, directories, start, end, tables, pathDirectories)
                {
                    DirectoryRegion = indexDirectory,
                    Locating = locating,
                    Paging = paging,
                }
                : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// A table and its chunks, which the message <paramref name="message"/> holds, its chunks checked and left in the bytes
    /// (<see cref="TableChunks"/>), or listed apart in a region of the object the message names, read when a question loads
    /// it; null for a table of no chunk.
    /// </summary>
    private static (SynopsisTableInfo, IReadOnlyList<TableChunk>)? Table(ReadOnlyMemory<byte> message)
    {
        ProtoReader reader = new ProtoReader(message.Span);
        SynopsisTableInfo? table = null;
        int count = 0;
        int keyBytes = 0;
        int declared = 0;
        IndexSegment? list = null;
        int listed = 0;
        int listedKeyBytes = 0;
        int listedDeclared = 0;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field, wire)
            {
                case (TagTableInfo, ProtoWireType.LengthDelimited):
                    table = SynopsisTableInfo.From(reader.ReadLengthDelimited());
                    break;
                case (TagTableList, ProtoWireType.LengthDelimited):
                    list = IndexDirectory.ReadSegment(reader.ReadMessage());
                    break;
                case (TagTableCount, ProtoWireType.Varint):
                    listed = checked((int)reader.ReadVarint());
                    break;
                case (TagTableKeyBytes, ProtoWireType.Varint):
                    listedKeyBytes = checked((int)reader.ReadVarint());
                    break;
                case (TagTableDeclared, ProtoWireType.Varint):
                    listedDeclared = checked((int)reader.ReadVarint());
                    break;
                case (TagTableChunk, ProtoWireType.LengthDelimited):
                    if (!Chunk(reader.ReadMessage(), out TableChunk chunk, out ReadOnlySpan<byte> least, out ReadOnlySpan<byte> greatest, out IndexSegment? distinct))
                    {
                        return null;
                    }

                    count++;
                    keyBytes += least.Length + greatest.Length;
                    declared += Declares(chunk.Region, distinct);
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        if (table is null)
        {
            return null;
        }

        // A table listed apart names its list, with a checksum, and holds no chunk of its own; a chunk declares its region
        // and that of its distinct sketches at most.
        if (list is { } region)
        {
            return count == 0 && listed > 0 && region.Checksum is not null && listedDeclared <= 2L * listed
                ? (table, new TableChunks(region, listed, listedKeyBytes, listedDeclared))
                : null;
        }

        return count > 0 ? (table, new TableChunks(message, count, keyBytes, declared)) : null;
    }

    /// <summary>The regions a chunk declares with a checksum, which the synopsis cache may keep: its own, and its distinct sketches'.</summary>
    private static int Declares(IndexSegment region, IndexSegment? distinct) =>
        (region.Checksum is null ? 0 : 1) + (distinct is { Checksum: not null } ? 1 : 0);

    /// <summary>
    /// The chunk <paramref name="reader"/> holds, the bounds of its key, both empty when it says none, and the region of
    /// its distinct sketches, null when it lays none apart: false when it names no region, or no rows, or bounds of its
    /// buckets that cross.
    /// </summary>
    private static bool Chunk(
        ProtoReader reader, out TableChunk chunk, out ReadOnlySpan<byte> least, out ReadOnlySpan<byte> greatest, out IndexSegment? distinct)
    {
        IndexSegment? region = null;
        long first = long.MinValue;
        long last = long.MaxValue;
        int rows = -1;
        least = default;
        greatest = default;
        distinct = null;
        bool knowsLeast = false;
        bool knowsGreatest = false;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field, wire)
            {
                case (TagChunkLeastKey, ProtoWireType.LengthDelimited):
                    least = reader.ReadLengthDelimited();
                    knowsLeast = true;
                    break;
                case (TagChunkGreatestKey, ProtoWireType.LengthDelimited):
                    greatest = reader.ReadLengthDelimited();
                    knowsGreatest = true;
                    break;
                case (TagChunkRegion, ProtoWireType.LengthDelimited):
                    region = IndexDirectory.ReadSegment(reader.ReadMessage());
                    break;
                case (TagChunkDistinct, ProtoWireType.LengthDelimited):
                    distinct = IndexDirectory.ReadSegment(reader.ReadMessage());
                    break;
                case (TagChunkFirst, ProtoWireType.Varint):
                    first = unchecked((long)reader.ReadVarint());
                    break;
                case (TagChunkLast, ProtoWireType.Varint):
                    last = unchecked((long)reader.ReadVarint());
                    break;
                case (TagChunkRows, ProtoWireType.Varint):
                    rows = checked((int)reader.ReadVarint());
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        // A chunk that says one bound and not the other says neither, nor one that says an empty one, which no encoded
        // value is.
        if (!knowsLeast || !knowsGreatest || least.IsEmpty || greatest.IsEmpty)
        {
            least = default;
            greatest = default;
        }

        // Its distinct sketches lie past its own region, as a writer lays them, which a search of the chunks' regions by their
        // offsets counts on (TableChunks.TryFind).
        chunk = region is { } at ? new TableChunk(at, first, last, rows) : default;
        return region is { } own && rows >= 0 && first <= last && (distinct is not { } apart || apart.Offset >= own.Offset + own.Length);
    }

    /// <summary>
    /// The chunks of a table, parsed the first time one is asked for: from the bytes of the contents, checked when the
    /// contents were, or from their list, a region of the object the entry names (<see cref="List"/>), read and checked when
    /// a question loads it (<see cref="Load"/>). A question reads the chunks of the tables it answers from, a few of an
    /// object's: the chunks of every table were most of what a cold question allocated, 13 MB over the 22 objects of days the
    /// design's tables of values per slice cut in 47 836 chunks, and most of a day's entry, 0.32 MB. Their count is known
    /// before.
    /// </summary>
    internal sealed class TableChunks : IReadOnlyList<TableChunk>
    {
        private readonly ReadOnlyMemory<byte> _message;
        private readonly int _count;
        private readonly int _keyBytes;
        private readonly int _declared;

        // The list read from the object, once loaded; null before, and for chunks the contents hold.
        private byte[]? _list;

        private TableChunk[]? _parsed;

        /// <summary>The chunks the message of a table in the contents holds, <paramref name="count"/> of them.</summary>
        internal TableChunks(ReadOnlyMemory<byte> message, int count, int keyBytes, int declared)
        {
            _message = message;
            _count = count;
            _keyBytes = keyBytes;
            _declared = declared;
        }

        /// <summary>The <paramref name="count"/> chunks <paramref name="list"/> lists, read when a question loads it.</summary>
        internal TableChunks(IndexSegment list, int count, int keyBytes, int declared)
            : this(ReadOnlyMemory<byte>.Empty, count, keyBytes, declared)
        {
            List = list;
        }

        /// <summary>The chunks a writer listed in <paramref name="list"/>: known, with no read.</summary>
        internal TableChunks(IndexSegment list, TableChunk[] chunks)
            : this(list, chunks.Length, KeyBytesOf(chunks), DeclaredOf(chunks))
        {
            bool ordered = true;
            for (int i = 1; i < chunks.Length && ordered; i++)
            {
                ordered = chunks[i - 1].Region.Offset < chunks[i].Region.Offset;
            }

            _ordered = ordered;
            _parsed = chunks;
        }

        public int Count => _count;

        /// <summary>The regions of the chunks that say a checksum, their distinct sketches' among them, which the synopsis cache may keep.</summary>
        internal int Declared => _declared;

        /// <summary>The bytes of the chunks' keys' bounds, which the parse lays in one store.</summary>
        internal int KeyBytes => _keyBytes;

        /// <summary>The region of the object that lists the chunks, apart from the entry; null for chunks the contents hold.</summary>
        internal IndexSegment? List { get; }

        /// <summary>Whether the chunks may be parsed: the contents hold them, or their list was loaded.</summary>
        internal bool Loaded => List is null || System.Threading.Volatile.Read(ref _list) is not null || Parsed is not null;

        /// <summary>The chunks, when they were parsed; null before.</summary>
        internal TableChunk[]? Parsed => System.Threading.Volatile.Read(ref _parsed);

        public TableChunk this[int index] => (Parsed ?? Parse())[index];

        public IEnumerator<TableChunk> GetEnumerator() => ((IEnumerable<TableChunk>)(Parsed ?? Parse())).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// Takes the list <paramref name="bytes"/> read of the object: false, and nothing taken, when they do not hold the list's
        /// checksum, or hold chunks other than the entry says, in count, in bytes of keys or in checksums. True once loaded.
        /// </summary>
        internal bool Load(ReadOnlySpan<byte> bytes)
        {
            if (Loaded)
            {
                return true;
            }

            if (List is not { } list || !list.Holds(bytes))
            {
                return false;
            }

            if (Counted(bytes) is not (int count, int keyBytes, int declared) || count != _count || keyBytes != _keyBytes || declared != _declared)
            {
                return false;
            }

            System.Threading.Interlocked.CompareExchange(ref _list, bytes.ToArray(), null);
            return true;
        }

        /// <summary>
        /// The chunks the list <paramref name="bytes"/>, read of its region <paramref name="list"/>, holds, loaded: what a reader
        /// with no table of contents to say what the list holds reads of a file's own directory; null when the bytes do not
        /// hold the list's checksum, or hold no chunk, or one that does not parse.
        /// </summary>
        internal static TableChunks? Listed(IndexSegment list, ReadOnlySpan<byte> bytes)
        {
            if (!list.Holds(bytes) || Counted(bytes) is not (int count and > 0, int keyBytes, int declared))
            {
                return null;
            }

            TableChunks chunks = new TableChunks(list, count, keyBytes, declared);
            return chunks.Load(bytes) ? chunks : null;
        }

        /// <summary>The chunks a list's <paramref name="bytes"/> hold, the bytes of their keys' bounds and the regions they declare; null when one does not parse.</summary>
        private static (int Count, int KeyBytes, int Declared)? Counted(ReadOnlySpan<byte> bytes)
        {
            int count = 0;
            int keyBytes = 0;
            int declared = 0;
            try
            {
                ProtoReader reader = new ProtoReader(bytes);
                while (reader.TryReadTag(out int field, out ProtoWireType wire))
                {
                    if (field != TagTableChunk || wire != ProtoWireType.LengthDelimited
                        || !Chunk(reader.ReadMessage(), out TableChunk chunk, out ReadOnlySpan<byte> least, out ReadOnlySpan<byte> greatest, out IndexSegment? distinct))
                    {
                        return null;
                    }

                    count++;
                    keyBytes += least.Length + greatest.Length;
                    declared += Declares(chunk.Region, distinct);
                }
            }
            catch (Exception malformed) when (malformed is VortexFormatException or OverflowException)
            {
                // A list that holds its checksum yet does not parse, as a table of contents another writer forged: no chunks.
                return null;
            }

            return (count, keyBytes, declared);
        }

        private static int KeyBytesOf(TableChunk[] chunks)
        {
            int bytes = 0;
            foreach (TableChunk chunk in chunks)
            {
                bytes += chunk.Keys is { } keys ? keys.Least.Length + keys.Greatest.Length : 0;
            }

            return bytes;
        }

        private static int DeclaredOf(TableChunk[] chunks)
        {
            int declared = 0;
            foreach (TableChunk chunk in chunks)
            {
                declared += Declares(chunk.Region, chunk.Distinct);
            }

            return declared;
        }

        /// <summary>Whether the parsed chunks lie in the order of their regions' offsets, as a writer lays them: a region is then looked for by halves.</summary>
        private bool _ordered;

        /// <summary>
        /// The region of the chunks at <paramref name="offset"/>, of <paramref name="length"/> bytes, with a checksum, a chunk's
        /// own or its distinct sketches': by halves over chunks in the order of their offsets, one by one otherwise.
        /// </summary>
        internal bool TryFind(ulong offset, uint length, out IndexSegment region)
        {
            TableChunk[] chunks = Parsed ?? Parse();
            if (!System.Threading.Volatile.Read(ref _ordered))
            {
                return DeclaredRegionMap.TryFind(chunks, offset, length, out region);
            }

            int low = 0;
            int high = chunks.Length - 1;
            while (low <= high)
            {
                int middle = (int)((uint)(low + high) >> 1);
                ulong at = chunks[middle].Region.Offset;
                if (at < offset)
                {
                    low = middle + 1;
                }
                else if (at > offset)
                {
                    high = middle - 1;
                }
                else
                {
                    region = chunks[middle].Region;
                    return region.Length == length && region.Checksum is not null;
                }
            }

            // No chunk's own: the distinct sketches of the last chunk before it, which lie past that chunk's region.
            if (high >= 0 && chunks[high].Distinct is { } distinct && distinct.Offset == offset)
            {
                region = distinct;
                return region.Length == length && region.Checksum is not null;
            }

            region = default;
            return false;
        }

        /// <summary>
        /// Parses the chunks, their keys and the regions of their distinct sketches in one store, once: a reader that parses
        /// them beside another keeps the first's.
        /// </summary>
        /// <exception cref="InvalidOperationException">The chunks are listed apart, and no question loaded their list.</exception>
        private TableChunk[] Parse()
        {
            ReadOnlySpan<byte> message = List is null
                ? _message.Span
                : System.Threading.Volatile.Read(ref _list)
                    ?? throw new InvalidOperationException("The chunks of a synopsis table are listed in its object, and no read loaded the list.");
            int count = _count;
            int keyBytes = _keyBytes;
            TableChunk[] chunks = new TableChunk[count];
            byte[] bytes = keyBytes == 0 ? [] : new byte[keyBytes];
            int[] offsets = keyBytes == 0 ? [] : new int[(2 * count) + 1];
            TableBounds? keys = keyBytes == 0 ? null : new TableBounds(bytes, offsets);
            ProtoReader reader = new ProtoReader(message);
            int c = 0;
            int at = 0;
            while (reader.TryReadTag(out int field, out ProtoWireType wire))
            {
                if (field != TagTableChunk || wire != ProtoWireType.LengthDelimited || c == count)
                {
                    reader.SkipField(wire);
                    continue;
                }

                Chunk(reader.ReadMessage(), out TableChunk chunk, out ReadOnlySpan<byte> least, out ReadOnlySpan<byte> greatest, out IndexSegment? distinct);
                if (keyBytes != 0)
                {
                    offsets[2 * c] = at;
                    least.CopyTo(bytes.AsSpan(at));
                    at += least.Length;
                    offsets[(2 * c) + 1] = at;
                    greatest.CopyTo(bytes.AsSpan(at));
                    at += greatest.Length;
                    offsets[(2 * c) + 2] = at;
                }

                if (distinct is { } apart)
                {
                    keys ??= new TableBounds([], []);
                    keys.HoldDistinct(c, count, apart);
                }

                chunks[c] = keys is null ? chunk : new TableChunk(chunk.Region, chunk.FirstBucket, chunk.LastBucket, chunk.Rows, keys, c);
                c++;
            }

            bool ordered = true;
            for (int i = 1; i < chunks.Length && ordered; i++)
            {
                ordered = chunks[i - 1].Region.Offset < chunks[i].Region.Offset;
            }

            // The order is known before the chunks are: a reader that finds them parsed finds it.
            System.Threading.Volatile.Write(ref _ordered, ordered);
            return System.Threading.Interlocked.CompareExchange(ref _parsed, chunks, null) ?? chunks;
        }
    }

    private static (int, IndexSegment)? Directory(ProtoReader reader)
    {
        int? field = null;
        IndexSegment? region = null;
        while (reader.TryReadTag(out int tag, out ProtoWireType wire))
        {
            switch (tag, wire)
            {
                case (TagDirectoryField, ProtoWireType.Varint):
                    field = checked((int)reader.ReadVarint32());
                    break;
                case (TagDirectoryRegion, ProtoWireType.LengthDelimited):
                    region = IndexDirectory.ReadSegment(reader.ReadMessage());
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return field is { } at && region is { } segment ? (at, segment) : null;
    }

    /// <summary>The entry of a locating index the message names: null when it names no column, kind or region with a checksum.</summary>
    private static LocatingEntry? Located(ProtoReader reader)
    {
        uint? column = null;
        string? kind = null;
        IndexSegment? region = null;
        while (reader.TryReadTag(out int tag, out ProtoWireType wire))
        {
            switch (tag, wire)
            {
                case (TagLocatingColumn, ProtoWireType.Varint):
                    column = reader.ReadVarint32();
                    break;
                case (TagLocatingKind, ProtoWireType.LengthDelimited):
                    kind = Encoding.UTF8.GetString(reader.ReadLengthDelimited());
                    break;
                case (TagLocatingRegion, ProtoWireType.LengthDelimited):
                    region = IndexDirectory.ReadSegment(reader.ReadMessage());
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return column is { } on && kind is not null && region is { Checksum: not null } segment ? new LocatingEntry(on, kind, segment) : null;
    }

    /// <summary>
    /// The paging the message names: null when a count is not positive, the pages' first rows do not start at 0 and rise, their
    /// number is not the pages the chunks fill, or the columns do not rise.
    /// </summary>
    private static ChunkPaging? Paged(ProtoReader reader)
    {
        long entries = 0;
        long count = 0;
        long[]? rows = null;
        long[]? columns = null;
        while (reader.TryReadTag(out int tag, out ProtoWireType wire))
        {
            switch (tag, wire)
            {
                case (TagPagingEntries, ProtoWireType.Varint):
                    entries = checked((long)reader.ReadVarint());
                    break;
                case (TagPagingCount, ProtoWireType.Varint):
                    count = checked((long)reader.ReadVarint());
                    break;
                case (TagPagingRows, ProtoWireType.LengthDelimited):
                    rows = FromDeltas(reader.ReadLengthDelimited());
                    break;
                case (TagPagingColumns, ProtoWireType.LengthDelimited):
                    columns = FromDeltas(reader.ReadLengthDelimited());
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        if (entries is <= 0 or > int.MaxValue || count is <= 0 or > int.MaxValue || rows is not { Length: > 0 } || columns is not { Length: > 0 }
            || rows.Length != (count + entries - 1) / entries || rows[0] != 0 || columns[^1] > int.MaxValue)
        {
            return null;
        }

        for (int i = 1; i < rows.Length; i++)
        {
            if (rows[i] <= rows[i - 1])
            {
                return null;
            }
        }

        for (int i = 1; i < columns.Length; i++)
        {
            if (columns[i] <= columns[i - 1])
            {
                return null;
            }
        }

        return new ChunkPaging((int)entries, (int)count, rows, Array.ConvertAll(columns, static c => (int)c));
    }

    /// <summary><paramref name="values"/>, which do not fall, as the varints of each one's rise over the one before, the first's over 0.</summary>
    private static byte[] Deltas(long[] values)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            long previous = 0;
            foreach (long value in values)
            {
                writer.WriteVarint(checked((ulong)(value - previous)));
                previous = value;
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>The values <see cref="Deltas"/> wrote.</summary>
    private static long[] FromDeltas(ReadOnlySpan<byte> bytes)
    {
        List<long> values = [];
        ProtoReader reader = new ProtoReader(bytes);
        long value = 0;
        while (!reader.End)
        {
            value = checked(value + checked((long)reader.ReadVarint()));
            values.Add(value);
        }

        return [.. values];
    }
}

/// <summary>
/// How a file's paged chunk directories lie (<see cref="SliceChunks.PagedVersion"/>), as its table of contents names it
/// (<see cref="SynopsisContents.Paging"/>): every paged column holds the same chunks, at the same rows, in pages alike.
/// </summary>
/// <param name="PageEntries">The chunks of a full page.</param>
/// <param name="Count">The chunks of a paged column.</param>
/// <param name="PageRows">The first row of each page, rising from 0.</param>
/// <param name="Columns">The paged columns, rising.</param>
internal sealed record ChunkPaging(int PageEntries, int Count, long[] PageRows, int[] Columns)
{
    /// <summary>Whether column <paramref name="column"/>'s directory is paged.</summary>
    internal bool Pages(int column) => Array.BinarySearch(Columns, column) >= 0;

    /// <summary>Where page <paramref name="page"/> of a paged column lies past its page table, and its bytes.</summary>
    internal (long Offset, int Length) Page(int page) =>
        ((long)page * PageEntries * SliceChunks.EntryBytes, Math.Min(PageEntries, Count - (page * PageEntries)) * SliceChunks.EntryBytes);

    /// <summary>The first and the last page that hold a row of <c>[first, first + rows)</c>, which a span of a row at least reaches.</summary>
    internal (int First, int Last) PagesOf(long first, long rows) => (PageOf(first), PageOf(first + Math.Max(rows, 1) - 1));

    /// <summary>The page that holds row <paramref name="row"/>: the last whose first row is not past it.</summary>
    private int PageOf(long row)
    {
        int at = Array.BinarySearch(PageRows, row);
        return at >= 0 ? at : Math.Max(~at - 1, 0);
    }
}

/// <summary>
/// An entry of a file's index directory that locates a column's values (<see cref="IndexKinds.Locates"/>), as its table of
/// contents names it (<see cref="SynopsisContents.Locating"/>).
/// </summary>
/// <param name="Column">The field of the root the index lies on, the first of its path; <see cref="IndexDirectory.RootColumn"/> for the root.</param>
/// <param name="Kind">The index's kind.</param>
/// <param name="Region">Where the entry's message lies in the file, with its checksum.</param>
internal readonly record struct LocatingEntry(uint Column, string Kind, IndexSegment Region);

/// <summary>
/// A chunk of a synopsis table: the region that holds its rows, the least and the greatest bucket they count over,
/// and how many there are. A table's rows lie in the order of their buckets, or of their hours and then their values
/// for a table of a column's values per slice, so its chunks follow each other in time, and a question reads only the
/// chunks of the buckets it folds; a chunk of a table of values says the least and the greatest value it holds too,
/// so that a question of some values reads only the chunks that may hold them. A chunk of a table of sketches may lay
/// its distinct sketches in a region of their own, which a question that counts no distinct value does not read.
/// </summary>
/// <param name="Region">Where the chunk's rows lie, with their checksum.</param>
/// <param name="FirstBucket">The least bucket of its rows: a slice's start, an hour's, or 0 for the object.</param>
/// <param name="LastBucket">The greatest bucket of its rows.</param>
/// <param name="Rows">Its rows.</param>
internal readonly record struct TableChunk(IndexSegment Region, long FirstBucket, long LastBucket, int Rows)
{
    // The chunk's place among those whose keys and distinct sketches' regions the store holds, then the store: an index
    // and one reference, where the copies of two took a chunk past the bytes a warm question allocates.
    private readonly int _keyIndex;
    private readonly TableBounds? _keys;

    /// <summary>The chunk whose bounds and region of distinct sketches are those of chunk <paramref name="index"/> of <paramref name="store"/>.</summary>
    internal TableChunk(IndexSegment region, long firstBucket, long lastBucket, int rows, TableBounds? store, int index)
        : this(region, firstBucket, lastBucket, rows)
    {
        _keys = store;
        _keyIndex = index;
    }

    /// <summary>
    /// For a chunk of a table of values, the least and the greatest value of its key its rows hold; null when the chunk
    /// does not say.
    /// </summary>
    internal ValueBounds? Keys
    {
        get => _keys?.Bounds(_keyIndex);
        init => (_keys, _keyIndex) = value is { } bounds ? (bounds.Store, bounds.Chunk) : (null, 0);
    }

    /// <summary>
    /// For a chunk of a table of sketches that lays its distinct sketches apart, the region that holds them, past its own:
    /// its rows' last fields (<see cref="SynopsisTableInfo.Distinct"/>), the others in <see cref="Region"/>; null when its
    /// region holds them, or it holds none.
    /// </summary>
    internal IndexSegment? Distinct => _keys?.Distinct(_keyIndex);

    /// <summary>Whether the chunk may hold a row of a bucket from <paramref name="first"/> to <paramref name="last"/>.</summary>
    internal bool Overlaps(long first, long last) => FirstBucket <= last && LastBucket >= first;
}

/// <summary>
/// The least and the greatest value of a table's key a chunk holds, a null aside, each encoded as a run's records hold
/// a component: a byte that says it holds one, then its bytes. A view of the store the chunks of a table share.
/// </summary>
internal readonly struct ValueBounds
{
    /// <summary>The bounds <paramref name="least"/> and <paramref name="greatest"/>, in a store of their own.</summary>
    internal ValueBounds(byte[] least, byte[] greatest)
        : this(TableBounds.Of(least, greatest), 0)
    {
    }

    /// <summary>The bounds of chunk <paramref name="chunk"/> of <paramref name="store"/>.</summary>
    internal ValueBounds(TableBounds store, int chunk)
    {
        Store = store;
        Chunk = chunk;
    }

    /// <summary>The store that holds the bounds.</summary>
    internal TableBounds Store { get; }

    /// <summary>The chunk's place among those of the store.</summary>
    internal int Chunk { get; }

    /// <summary>The least value.</summary>
    internal ReadOnlySpan<byte> Least => Store.Least(Chunk);

    /// <summary>The greatest value.</summary>
    internal ReadOnlySpan<byte> Greatest => Store.Greatest(Chunk);
}

/// <summary>
/// The least and the greatest value of the key of each chunk of a table of values, in one buffer the table's chunks share:
/// chunk <c>i</c>'s least lies from offset <c>2i</c> to <c>2i + 1</c>, its greatest from <c>2i + 1</c> to <c>2i + 2</c>, both
/// empty when the chunk says none, as an encoded value is never empty. For a table of sketches whose chunks lay their
/// distinct sketches apart, the region of each chunk's (<see cref="TableChunk.Distinct"/>), the buffer empty.
/// </summary>
internal sealed class TableBounds(byte[] bytes, int[] offsets)
{
    // The region of each chunk's distinct sketches, null for a table whose chunks lay none apart.
    private IndexSegment?[]? _distinct;

    /// <summary>A store of one chunk's bounds.</summary>
    internal static TableBounds Of(byte[] least, byte[] greatest) => new([.. least, .. greatest], [0, least.Length, least.Length + greatest.Length]);

    /// <summary>A store of one chunk's bounds, none when either is null, and of the region of its distinct sketches, none when it is null.</summary>
    internal static TableBounds? Of(byte[]? least, byte[]? greatest, IndexSegment? distinct)
    {
        TableBounds? store = least is null || greatest is null ? null : Of(least, greatest);
        if (distinct is { } apart)
        {
            store ??= new TableBounds([], []);
            store.HoldDistinct(0, 1, apart);
        }

        return store;
    }

    /// <summary>The bounds of chunk <paramref name="chunk"/>, null when it says none.</summary>
    internal ValueBounds? Bounds(int chunk) =>
        offsets.Length == 0 || offsets[2 * chunk] == offsets[(2 * chunk) + 1] ? null : new ValueBounds(this, chunk);

    /// <summary>The region of chunk <paramref name="chunk"/>'s distinct sketches, null when it lays none apart.</summary>
    internal IndexSegment? Distinct(int chunk) => _distinct?[chunk];

    /// <summary>Holds <paramref name="region"/> as the distinct sketches of chunk <paramref name="chunk"/> of <paramref name="count"/>, before the store is shared.</summary>
    internal void HoldDistinct(int chunk, int count, IndexSegment region) => (_distinct ??= new IndexSegment?[count])[chunk] = region;

    internal ReadOnlySpan<byte> Least(int chunk) => bytes.AsSpan(offsets[2 * chunk], offsets[(2 * chunk) + 1] - offsets[2 * chunk]);

    internal ReadOnlySpan<byte> Greatest(int chunk) => bytes.AsSpan(offsets[(2 * chunk) + 1], offsets[(2 * chunk) + 2] - offsets[(2 * chunk) + 1]);
}
