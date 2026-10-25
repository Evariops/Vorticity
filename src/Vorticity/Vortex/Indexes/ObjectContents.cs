using System;
using System.Collections.Generic;
using System.Text;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;
using Vorticity.Types.Serialization;

namespace Vorticity.Indexes;

/// <summary>
/// The table of contents of a sliced file, which a dataset's entry carries so that a reader of a few of its slices, of a
/// key of its variant columns or of a value by its locating indexes needs no open of the file, not even its index
/// directory: the file's schema and the encodings its array blobs name, its slice table and the region of each column's
/// chunk directory, its variant columns' path directories, and the regions of its index directory and of the entries
/// of its locating indexes.
/// </summary>
/// <remarks>
/// A table of contents a later library cannot read, or one an earlier one wrote with synopsis tables, is ignored, and
/// the file is read through its directory.
/// <para>
/// A class, not a record: it keeps what its readers load and decode, page tables and the schema, which an equality of
/// its members or a copy of it would compare or share.
/// </para>
/// </remarks>
/// <param name="schema">The file's schema, a struct of its columns, as <see cref="DTypeFlatBuffers"/> writes it.</param>
/// <param name="encodings">The array encodings the file's blobs name, by their index.</param>
/// <param name="slices">The file's slice table.</param>
/// <param name="chunkDirectories">Each top-level column's chunk directory, the regions of its index entry.</param>
/// <param name="pathDirectories">
/// Each variant column's path directory, and its region: what a question on a path reads first, before that path's
/// regions.
/// </param>
internal sealed class ObjectContents(
    byte[] schema,
    IReadOnlyList<string> encodings,
    SliceTable slices,
    IReadOnlyList<IndexSegment> chunkDirectories,
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

    /// <summary>
    /// Each variant column's path directory, and its region: what a question on a path reads first, before that path's
    /// regions.
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
    /// directory, those on its columns alone. Null when the contents name none, and a lookup reads the whole directory.
    /// </summary>
    internal IReadOnlyList<LocatingEntry>? Locating { get; init; }

    /// <summary>
    /// Each top-level column's sheet (<see cref="ColumnSheet"/>), and its region: what a question reads of the object to
    /// learn whether a value lies in a column, before any row. Empty when the object keeps none.
    /// </summary>
    internal IReadOnlyList<(int Field, IndexSegment Region)> Sheets { get; init; } = [];

    /// <summary>The region of column <paramref name="field"/>'s sheet, or null when the object keeps none of it.</summary>
    internal IndexSegment? SheetOf(int field) => RegionOf(Sheets, field);

    /// <summary>
    /// Each top-level column's counts (<see cref="ColumnCounts"/>), and its region: what a question on that column
    /// alone reads of the object instead of its rows. Empty when the object keeps none.
    /// </summary>
    internal IReadOnlyList<(int Field, IndexSegment Region)> Counts { get; init; } = [];

    /// <summary>The region of column <paramref name="field"/>'s counts, or null when the object keeps none of it.</summary>
    internal IndexSegment? CountsOf(int field) => RegionOf(Counts, field);

    /// <summary>
    /// Each top-level column's sketches of its counts (<see cref="ColumnSketches"/>), and its region, right after the
    /// counts': what a question of quantiles on that column reads beside them. Empty when the object keeps none.
    /// </summary>
    internal IReadOnlyList<(int Field, IndexSegment Region)> Sketches { get; init; } = [];

    /// <summary>The region of the sketches of column <paramref name="field"/>'s counts, or null when the object keeps none of it.</summary>
    internal IndexSegment? SketchesOf(int field) => RegionOf(Sketches, field);

    /// <summary>
    /// The object's blocks (<see cref="BlockTable"/>), on its key's first column, and their region: what a question reads to
    /// learn the runs of rows of each value of that column. Empty when the object keeps none.
    /// </summary>
    internal IReadOnlyList<(int Field, IndexSegment Region)> Blocks { get; init; } = [];

    /// <summary>The region of the blocks on column <paramref name="field"/>, or null when the object keeps none on it.</summary>
    internal IndexSegment? BlocksOf(int field) => RegionOf(Blocks, field);

    /// <summary>
    /// The statistics of each column over the object's blocks (<see cref="ColumnBlocks"/>) and their regions: what a
    /// question reads to answer a block from its columns' values without its rows. Empty when the object keeps none.
    /// </summary>
    internal IReadOnlyList<(int Field, IndexSegment Region)> BlockStats { get; init; } = [];

    /// <summary>The region of column <paramref name="field"/>'s statistics over the blocks, or null when the object keeps none of it.</summary>
    internal IndexSegment? BlockStatsOf(int field) => RegionOf(BlockStats, field);

    /// <summary>
    /// Where each value of each column lives (<see cref="ColumnWhere"/>), and their regions: what a filter on a value reads
    /// to read the value's rows alone. Empty when the object keeps none.
    /// </summary>
    internal IReadOnlyList<(int Field, IndexSegment Region)> Wheres { get; init; } = [];

    /// <summary>The region of where column <paramref name="field"/>'s values live, or null when the object keeps none of it.</summary>
    internal IndexSegment? WhereOf(int field) => RegionOf(Wheres, field);

    /// <summary>
    /// The bytes of each top-level column's data in the object, by the column's place: what reading its rows costs, which a
    /// planner weighs against the synopses that would answer instead. Empty when the object does not say.
    /// </summary>
    internal IReadOnlyList<long> ColumnBytes { get; init; } = [];

    private static IndexSegment? RegionOf(IReadOnlyList<(int Field, IndexSegment Region)> regions, int field)
    {
        foreach ((int column, IndexSegment region) in regions)
        {
            if (column == field)
            {
                return region;
            }
        }

        return null;
    }

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
    internal ObjectContents Naming(IndexSegment directory, IReadOnlyList<LocatingEntry>? locating) =>
        new ObjectContents(Schema, Encodings, Slices, ChunkDirectories, PathDirectories)
        {
            DirectoryRegion = directory,
            Locating = locating,
            Paging = Paging,
            Sheets = Sheets,
            Counts = Counts,
            Sketches = Sketches,
            Blocks = Blocks,
            BlockStats = BlockStats,
            Wheres = Wheres,
            ColumnBytes = ColumnBytes,
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

    // The version of the contents. Version 1 held the synopsis part's extent and the synopsis tables, tags 5 to 8, which
    // this library neither writes nor reads: such contents are ignored, the file read through its directory.
    private const int Version = 2;
    private const int TagVersion = 1;
    private const int TagEncoding = 2;
    private const int TagSlices = 3;
    private const int TagChunkDirectory = 4;
    private const int TagSchema = 9;
    private const int TagPathDirectory = 10;
    private const int TagDirectoryField = 1;
    private const int TagDirectoryRegion = 2;
    private const int TagIndexDirectory = 11;

    // An entry of a locating index: the column of the root it lies on, its kind, the region of its message.
    private const int TagLocating = 12;
    private const int TagLocatingColumn = 1;
    private const int TagLocatingKind = 2;
    private const int TagLocatingRegion = 3;

    // The paging of the chunk directories: the entries of a page, the chunks of a paged column, the first row of each page,
    // and the paged columns, the last two as deltas, packed.
    private const int TagPaging = 13;
    private const int TagPagingEntries = 1;
    private const int TagPagingCount = 2;
    private const int TagPagingRows = 3;
    private const int TagPagingColumns = 4;

    // A column's sheet: its field and its region, as a path directory's.
    private const int TagSheet = 14;

    // A column's counts: its field and its region, as a path directory's.
    private const int TagCounts = 15;

    // The sketches of a column's counts: its field and its region, as a path directory's.
    private const int TagSketches = 16;

    // The object's blocks: the field of its key's first column and their region, as a path directory's.
    private const int TagBlocks = 17;

    // A column's statistics over the blocks: its field and its region, as a path directory's.
    private const int TagBlockStats = 18;

    // Where a column's values live: its field and its region, as a path directory's.
    private const int TagWhere = 19;

    // The bytes of each top-level column's data, packed, by the column's place.
    private const int TagColumnBytes = 20;

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
    /// built the first time an object's read through the cache asks for them, and shared by every later read.
    /// </summary>
    internal IReadOnlyDictionary<(ulong Offset, uint Length), IndexSegment> DeclaredRegions
    {
        get
        {
            if (System.Threading.Volatile.Read(ref _declared) is { } known)
            {
                return known;
            }

            Dictionary<(ulong Offset, uint Length), IndexSegment> regions = [];
            foreach (IndexSegment region in Declared())
            {
                regions[(region.Offset, region.Length)] = region;
            }

            return System.Threading.Interlocked.CompareExchange(ref _declared, regions, null) ?? regions;
        }
    }

    /// <summary>
    /// The regions of the object the contents declare with a checksum, which the synopsis cache may keep: its chunk
    /// directories, its path directories, its index directory and the entries of its locating indexes in it.
    /// </summary>
    internal List<IndexSegment> Declared()
    {
        List<IndexSegment> regions = [];
        void Declare(IndexSegment region)
        {
            if (region.Checksum is not null)
            {
                regions.Add(region);
            }
        }

        foreach (IndexSegment region in ChunkDirectories)
        {
            Declare(region);
        }

        foreach ((int _, IndexSegment region) in PathDirectories ?? [])
        {
            Declare(region);
        }

        foreach ((int _, IndexSegment region) in Sheets)
        {
            Declare(region);
        }

        foreach ((int _, IndexSegment region) in Counts)
        {
            Declare(region);
        }

        foreach ((int _, IndexSegment region) in Sketches)
        {
            Declare(region);
        }

        foreach ((int _, IndexSegment region) in Blocks)
        {
            Declare(region);
        }

        foreach ((int _, IndexSegment region) in BlockStats)
        {
            Declare(region);
        }

        foreach ((int _, IndexSegment region) in Wheres)
        {
            Declare(region);
        }

        if (DirectoryRegion is { } directory)
        {
            Declare(directory);
        }

        foreach (LocatingEntry located in Locating ?? [])
        {
            Declare(located.Region);
        }

        return regions;
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

            writer.WriteBytes(TagSchema, Schema);
            foreach ((int field, IndexSegment region) in PathDirectories ?? [])
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagPathDirectory);
                writer.WriteUInt32Always(TagDirectoryField, (uint)field);
                IndexDirectory.WriteSegment(ref writer, TagDirectoryRegion, region);
            }

            foreach ((int field, IndexSegment region) in Sheets)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagSheet);
                writer.WriteUInt32Always(TagDirectoryField, (uint)field);
                IndexDirectory.WriteSegment(ref writer, TagDirectoryRegion, region);
            }

            foreach ((int field, IndexSegment region) in Counts)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagCounts);
                writer.WriteUInt32Always(TagDirectoryField, (uint)field);
                IndexDirectory.WriteSegment(ref writer, TagDirectoryRegion, region);
            }

            foreach ((int field, IndexSegment region) in Sketches)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagSketches);
                writer.WriteUInt32Always(TagDirectoryField, (uint)field);
                IndexDirectory.WriteSegment(ref writer, TagDirectoryRegion, region);
            }

            foreach ((int field, IndexSegment region) in Blocks)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagBlocks);
                writer.WriteUInt32Always(TagDirectoryField, (uint)field);
                IndexDirectory.WriteSegment(ref writer, TagDirectoryRegion, region);
            }

            foreach ((int field, IndexSegment region) in BlockStats)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagBlockStats);
                writer.WriteUInt32Always(TagDirectoryField, (uint)field);
                IndexDirectory.WriteSegment(ref writer, TagDirectoryRegion, region);
            }

            foreach ((int field, IndexSegment region) in Wheres)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagWhere);
                writer.WriteUInt32Always(TagDirectoryField, (uint)field);
                IndexDirectory.WriteSegment(ref writer, TagDirectoryRegion, region);
            }

            if (ColumnBytes.Count > 0)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagColumnBytes);
                foreach (long bytes in ColumnBytes)
                {
                    writer.WriteVarint((ulong)bytes);
                }
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

    /// <summary>The contents <paramref name="bytes"/> hold, or null when they do not decode or are of another version.</summary>
    internal static ObjectContents? From(ReadOnlySpan<byte> bytes) => From(new ReadOnlyMemory<byte>(bytes.ToArray()));

    /// <summary>The contents <paramref name="bytes"/> hold, or null when they do not decode or are of another version.</summary>
    internal static ObjectContents? From(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            ProtoReader reader = new ProtoReader(bytes.Span);
            ulong version = 0;
            List<string> encodings = [];
            SliceTable? slices = null;
            List<IndexSegment> directories = [];
            List<(int, IndexSegment)> pathDirectories = [];
            List<(int, IndexSegment)> sheets = [];
            List<(int, IndexSegment)> counts = [];
            List<(int, IndexSegment)> sketches = [];
            List<(int, IndexSegment)> blocks = [];
            List<(int, IndexSegment)> blockStats = [];
            List<(int, IndexSegment)> wheres = [];
            List<long> columnBytes = [];
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
                        if (version != Version)
                        {
                            return null;
                        }

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
                    case (TagSheet, ProtoWireType.LengthDelimited):
                        if (Directory(reader.ReadMessage()) is not { } sheet)
                        {
                            return null;
                        }

                        sheets.Add(sheet);
                        break;
                    case (TagCounts, ProtoWireType.LengthDelimited):
                        if (Directory(reader.ReadMessage()) is not { } counted)
                        {
                            return null;
                        }

                        counts.Add(counted);
                        break;
                    case (TagSketches, ProtoWireType.LengthDelimited):
                        if (Directory(reader.ReadMessage()) is not { } sketched)
                        {
                            return null;
                        }

                        sketches.Add(sketched);
                        break;
                    case (TagBlocks, ProtoWireType.LengthDelimited):
                        if (Directory(reader.ReadMessage()) is not { } blocked)
                        {
                            return null;
                        }

                        blocks.Add(blocked);
                        break;
                    case (TagBlockStats, ProtoWireType.LengthDelimited):
                        if (Directory(reader.ReadMessage()) is not { } stated)
                        {
                            return null;
                        }

                        blockStats.Add(stated);
                        break;
                    case (TagColumnBytes, ProtoWireType.LengthDelimited):
                        ProtoReader packed = reader.ReadMessage();
                        while (!packed.End)
                        {
                            columnBytes.Add(checked((long)packed.ReadVarint()));
                        }

                        break;
                    case (TagWhere, ProtoWireType.LengthDelimited):
                        if (Directory(reader.ReadMessage()) is not { } placed)
                        {
                            return null;
                        }

                        wheres.Add(placed);
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
            return version == Version && slices is not null && schema is not null
                && (locating is null || (indexDirectory is { } named && locating.TrueForAll(l => l.Region.Offset >= named.Offset
                    && l.Region.Offset + l.Region.Length <= named.Offset + named.Length)))
                && (paging is null || (slices.Slices.Count > 0 && paging.PageRows[^1] < slices.Rows && Array.TrueForAll(paging.Columns, c =>
                    c < directories.Count && directories[c].Length == SliceChunks.TableBytes(paging.PageRows.Length))))
                ? new ObjectContents(schema, encodings, slices, directories, pathDirectories)
                {
                    DirectoryRegion = indexDirectory,
                    Locating = locating,
                    Paging = paging,
                    Sheets = sheets,
                    Counts = counts,
                    Sketches = sketches,
                    Blocks = blocks,
                    BlockStats = blockStats,
                    Wheres = wheres,
                    ColumnBytes = columnBytes,
                }
                : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException)
        {
            return null;
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
/// (<see cref="ObjectContents.Paging"/>): every paged column holds the same chunks, at the same rows, in pages alike.
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
/// contents names it (<see cref="ObjectContents.Locating"/>).
/// </summary>
/// <param name="Column">The field of the root the index lies on, the first of its path; <see cref="IndexDirectory.RootColumn"/> for the root.</param>
/// <param name="Kind">The index's kind.</param>
/// <param name="Region">Where the entry's message lies in the file, with its checksum.</param>
internal readonly record struct LocatingEntry(uint Column, string Kind, IndexSegment Region);
