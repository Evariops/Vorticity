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
/// </remarks>
/// <param name="Schema">The file's schema, a struct of its columns, as <see cref="DTypeFlatBuffers"/> writes it.</param>
/// <param name="Encodings">The array encodings the file's blobs name, by their index.</param>
/// <param name="Slices">The file's slice table.</param>
/// <param name="ChunkDirectories">Each top-level column's chunk directory, the regions of its index entry.</param>
/// <param name="PartStart">Where the synopsis part starts in the file.</param>
/// <param name="PartEnd">Where it ends.</param>
/// <param name="Tables">Each synopsis table, and its chunks: their regions, the buckets they count over and their rows.</param>
/// <param name="Columns">
/// Each synopsis column, and its chunks: what the entries of earlier writers carry, which no reader asks for; a writer
/// leaves them out, as they made most of a large object's entry.
/// </param>
/// <param name="PathDirectories">
/// Each variant column's path directory, and its region: what a question on a path the tables do not name
/// reads first, before that path's regions.
/// </param>
internal sealed record SynopsisContents(
    byte[] Schema,
    IReadOnlyList<string> Encodings,
    SliceTable Slices,
    IReadOnlyList<IndexSegment> ChunkDirectories,
    long PartStart,
    long PartEnd,
    IReadOnlyList<(SynopsisTableInfo Table, IReadOnlyList<TableChunk> Chunks)> Tables,
    IReadOnlyList<(int Field, ChunkSpan[] Chunks)> Columns,
    IReadOnlyList<(int Field, IndexSegment Region)>? PathDirectories = null)
{
    /// <summary>
    /// The region of the file's index directory: what a lookup of one value reads first, the runs of the
    /// file's identifiers and every other index in it, with no open of the file. Null when unknown.
    /// </summary>
    internal IndexSegment? DirectoryRegion { get; init; }

    private const int TagIndexDirectory = 11;
    private const int TagPathDirectory = 10;
    private const int TagDirectoryField = 1;
    private const int TagDirectoryRegion = 2;

    private const int Version = 1;
    private const int TagVersion = 1;
    private const int TagEncoding = 2;
    private const int TagSlices = 3;
    private const int TagChunkDirectory = 4;
    private const int TagPartStart = 5;
    private const int TagPartEnd = 6;
    private const int TagTable = 7;
    private const int TagColumn = 8;
    private const int TagSchema = 9;
    private const int TagTableInfo = 1;
    private const int TagTableRegion = 2;
    private const int TagTableChunk = 3;
    private const int TagChunkRegion = 1;
    private const int TagChunkFirst = 2;
    private const int TagChunkLast = 3;
    private const int TagChunkRows = 4;
    private const int TagChunkLeastKey = 5;
    private const int TagChunkGreatestKey = 6;
    private const int TagColumnField = 1;
    private const int TagColumnChunks = 2;

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
    /// built the map again for every object of every question.
    /// </summary>
    internal IReadOnlyDictionary<(ulong Offset, uint Length), IndexSegment> DeclaredRegions
    {
        get
        {
            if (System.Threading.Volatile.Read(ref _declared) is { } known)
            {
                return known;
            }

            Dictionary<(ulong, uint), IndexSegment> regions = [];
            foreach (IndexSegment region in SynopsisCacheReader.Declared(this))
            {
                regions[(region.Offset, region.Length)] = region;
            }

            return System.Threading.Interlocked.CompareExchange(ref _declared, regions, null) ?? regions;
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

    /// <summary>The row types <see cref="TableRowTypes"/> built, their arena, which nothing adds to after, and their fields' names; null until then.</summary>
    private System.Runtime.CompilerServices.StrongBox<(DTypeArena Arena, DType[] Rows, string[][] Names)>? _rowTypes;

    /// <summary>
    /// The row type of each table, by its place among <see cref="Tables"/>, built the first time a read of the tables
    /// asks for them into an arena of their own, which only readers share: each question over the object built them
    /// again into an arena of its own, a tenth of what a warm question answered from the tables allocated.
    /// </summary>
    internal (DTypeArena Arena, DType[] Rows, string[][] Names) TableRowTypes
    {
        get
        {
            if (System.Threading.Volatile.Read(ref _rowTypes) is { } known)
            {
                return known.Value;
            }

            DTypeArena arena = new DTypeArena();
            DType schema = SchemaType;
            DType[] rows = new DType[Tables.Count];
            string[][] names = new string[rows.Length][];
            for (int t = 0; t < rows.Length; t++)
            {
                rows[t] = Tables[t].Table.RowType(schema, Slices.Paths, arena);
                names[t] = new string[rows[t].FieldCount];
                for (int f = 0; f < names[t].Length; f++)
                {
                    names[t][f] = rows[t].GetFieldName(f);
                }
            }

            System.Runtime.CompilerServices.StrongBox<(DTypeArena, DType[], string[][])> built = new((arena, rows, names));
            return (System.Threading.Interlocked.CompareExchange(ref _rowTypes, built, null) ?? built).Value;
        }
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
                }
            }

            foreach ((int field, ChunkSpan[] chunks) in Columns)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagColumn);
                writer.WriteUInt32Always(TagColumnField, (uint)field);
                writer.WriteBytes(TagColumnChunks, new SliceChunks([], [chunks]).ColumnBytes(0));
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

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>The contents <paramref name="bytes"/> hold, or null when they do not decode or are of a later version.</summary>
    internal static SynopsisContents? From(ReadOnlySpan<byte> bytes) => From(new ReadOnlyMemory<byte>(bytes.ToArray()));

    /// <summary>
    /// The contents <paramref name="bytes"/> hold, or null when they do not decode or are of a later version; the chunks of
    /// the synopsis columns left in the bytes, which no reader of the contents asks for, parsed when one does
    /// (<see cref="ColumnChunks"/>).
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
            List<(int, ReadOnlyMemory<byte>)> columns = [];
            List<(int, IndexSegment)> pathDirectories = [];
            IndexSegment? indexDirectory = null;
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
                        if (Table(reader.ReadMessage()) is not { } table)
                        {
                            return null;
                        }

                        tables.Add(table);
                        break;
                    case (TagColumn, ProtoWireType.LengthDelimited):
                        ReadOnlySpan<byte> message = reader.ReadLengthDelimited();
                        if (ColumnField(new ProtoReader(message)) is not { } column)
                        {
                            return null;
                        }

                        columns.Add((column, bytes.Slice(reader.Position - message.Length, message.Length)));
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
                    default:
                        reader.SkipField(wire);
                        break;
                }
            }

            return version == Version && slices is not null && schema is not null && end >= start
                ? new SynopsisContents(schema, encodings, slices, directories, start, end, tables, new ColumnChunks([.. columns]), pathDirectories)
                {
                    DirectoryRegion = indexDirectory,
                }
                : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// A table and its chunks; a table of contents written before tables came in chunks names one region, which
    /// is then the table's one chunk, over every bucket.
    /// </summary>
    private static (SynopsisTableInfo, IReadOnlyList<TableChunk>)? Table(ProtoReader reader)
    {
        SynopsisTableInfo? table = null;
        IndexSegment? region = null;
        List<TableChunk> chunks = [];
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field, wire)
            {
                case (TagTableInfo, ProtoWireType.LengthDelimited):
                    table = SynopsisTableInfo.From(reader.ReadLengthDelimited());
                    break;
                case (TagTableRegion, ProtoWireType.LengthDelimited):
                    region = IndexDirectory.ReadSegment(reader.ReadMessage());
                    break;
                case (TagTableChunk, ProtoWireType.LengthDelimited):
                    if (Chunk(reader.ReadMessage()) is not { } chunk)
                    {
                        return null;
                    }

                    chunks.Add(chunk);
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

        if (chunks.Count == 0 && region is { } whole)
        {
            chunks.Add(new TableChunk(whole, long.MinValue, long.MaxValue, table.Rows));
        }

        return chunks.Count > 0 ? (table, chunks) : null;
    }

    private static TableChunk? Chunk(ProtoReader reader)
    {
        IndexSegment? region = null;
        long first = long.MinValue;
        long last = long.MaxValue;
        int rows = -1;
        byte[]? least = null;
        byte[]? greatest = null;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field, wire)
            {
                case (TagChunkLeastKey, ProtoWireType.LengthDelimited):
                    least = reader.ReadLengthDelimited().ToArray();
                    break;
                case (TagChunkGreatestKey, ProtoWireType.LengthDelimited):
                    greatest = reader.ReadLengthDelimited().ToArray();
                    break;
                case (TagChunkRegion, ProtoWireType.LengthDelimited):
                    region = IndexDirectory.ReadSegment(reader.ReadMessage());
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

        // A chunk that says one bound and not the other says neither.
        return region is { } at && rows >= 0 && first <= last
            ? new TableChunk(at, first, last, rows) { Keys = least is null || greatest is null ? null : new ValueBounds(least, greatest) }
            : null;
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

    /// <summary>The field of the synopsis column a message names, when it names one and its chunks; null otherwise. Its chunks are not read.</summary>
    private static int? ColumnField(ProtoReader reader)
    {
        int? field = null;
        bool chunks = false;
        while (reader.TryReadTag(out int tag, out ProtoWireType wire))
        {
            switch (tag, wire)
            {
                case (TagColumnField, ProtoWireType.Varint):
                    field = checked((int)reader.ReadVarint32());
                    break;
                case (TagColumnChunks, ProtoWireType.LengthDelimited):
                    reader.SkipField(wire);
                    chunks = true;
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return chunks ? field : null;
    }

    /// <summary>
    /// The chunks of each synopsis column, as the bytes of the contents hold them, each column's parsed the first time it
    /// is asked for: no reader of the contents asks for them, and parsed at every open of an object they were most of
    /// what its contents held, a megabyte and a half of an hour of 180 M rows, kept as long as its entry. A column whose
    /// chunks do not decode has none.
    /// </summary>
    internal sealed class ColumnChunks((int Field, ReadOnlyMemory<byte> Message)[] columns) : IReadOnlyList<(int Field, ChunkSpan[] Chunks)>
    {
        private readonly ChunkSpan[]?[] _parsed = new ChunkSpan[]?[columns.Length];

        public int Count => columns.Length;

        public (int Field, ChunkSpan[] Chunks) this[int index]
        {
            get
            {
                ref ChunkSpan[]? parsed = ref _parsed[index];
                if (System.Threading.Volatile.Read(ref parsed) is not { } chunks)
                {
                    chunks = Column(new ProtoReader(columns[index].Message.Span))?.Item2 ?? [];
                    System.Threading.Volatile.Write(ref parsed, chunks);
                }

                return (columns[index].Field, chunks);
            }
        }

        public IEnumerator<(int Field, ChunkSpan[] Chunks)> GetEnumerator()
        {
            for (int i = 0; i < columns.Length; i++)
            {
                yield return this[i];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static (int, ChunkSpan[])? Column(ProtoReader reader)
    {
        int? field = null;
        ChunkSpan[]? chunks = null;
        while (reader.TryReadTag(out int tag, out ProtoWireType wire))
        {
            switch (tag, wire)
            {
                case (TagColumnField, ProtoWireType.Varint):
                    field = checked((int)reader.ReadVarint32());
                    break;
                case (TagColumnChunks, ProtoWireType.LengthDelimited):
                    chunks = SliceChunks.ColumnFrom(reader.ReadLengthDelimited());
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return field is { } at && chunks is not null ? (at, chunks) : null;
    }
}

/// <summary>
/// A chunk of a synopsis table: the region that holds its rows, the least and the greatest bucket they count over,
/// and how many there are. A table's rows lie in the order of their buckets, or of their hours and then their values
/// for a table of a column's values per slice, so its chunks follow each other in time, and a question reads only the
/// chunks of the buckets it folds; a chunk of a table of values says the least and the greatest value it holds too,
/// so that a question of some values reads only the chunks that may hold them.
/// </summary>
/// <param name="Region">Where the chunk's rows lie, with their checksum.</param>
/// <param name="FirstBucket">The least bucket of its rows: a slice's start, an hour's, or 0 for the object.</param>
/// <param name="LastBucket">The greatest bucket of its rows.</param>
/// <param name="Rows">Its rows.</param>
internal readonly record struct TableChunk(IndexSegment Region, long FirstBucket, long LastBucket, int Rows)
{
    /// <summary>
    /// For a chunk of a table of values, the least and the greatest value of its key its rows hold; null when the chunk
    /// does not say. One reference, where two took a chunk's copies past the bytes a warm question allocates.
    /// </summary>
    internal ValueBounds? Keys { get; init; }

    /// <summary>Whether the chunk may hold a row of a bucket from <paramref name="first"/> to <paramref name="last"/>.</summary>
    internal bool Overlaps(long first, long last) => FirstBucket <= last && LastBucket >= first;
}

/// <summary>
/// The least and the greatest value of a table's key a chunk holds, a null aside, each encoded as a run's records hold
/// a component: a byte that says it holds one, then its bytes.
/// </summary>
/// <param name="Least">The least value.</param>
/// <param name="Greatest">The greatest value.</param>
internal sealed record ValueBounds(byte[] Least, byte[] Greatest);
