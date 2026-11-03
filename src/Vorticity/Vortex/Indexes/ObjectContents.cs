using System;
using System.Collections.Generic;
using System.Text;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;
using Vorticity.Types.Serialization;
using Vorticity.Types.Variants;

namespace Vorticity.Indexes;

/// <summary>
/// The table of contents of a file, which a dataset's entry carries so that a reader of a few of its rows, of a
/// key of its variant columns or of a value by its locating indexes needs no open of the file, not even its index
/// directory: the file's schema and the encodings its array blobs name, its rows, its key's sparse index and the region of
/// each column's chunk directory, its variant columns' path directories, and the regions of its index directory and of the entries
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
/// <param name="rows">The file's rows.</param>
/// <param name="chunkDirectories">Each top-level column's chunk directory, the regions of its index entry.</param>
/// <param name="pathDirectories">
/// Each variant column's path directory, and its region: what a question on a path reads first, before that path's
/// regions.
/// </param>
internal sealed class ObjectContents(
    byte[] schema,
    IReadOnlyList<string> encodings,
    long rows,
    IReadOnlyList<IndexSegment> chunkDirectories,
    IReadOnlyList<(int Field, IndexSegment Region)>? pathDirectories = null)
{
    /// <summary>The file's schema, a struct of its columns, as <see cref="DTypeFlatBuffers"/> writes it.</summary>
    internal byte[] Schema { get; } = schema;

    /// <summary>The array encodings the file's blobs name, by their index.</summary>
    internal IReadOnlyList<string> Encodings { get; } = encodings;

    /// <summary>The file's rows.</summary>
    internal long Rows { get; } = rows;

    /// <summary>
    /// The region of the table of the file's sparse index (<see cref="GranuleKeys"/>), which names its pages; null for a
    /// file written without a key.
    /// </summary>
    internal IndexSegment? KeyIndex { get; init; }

    /// <summary>The region of the pages of the file's sparse index; null when its table keeps its one page.</summary>
    internal IndexSegment? KeyPages { get; init; }

    // The file's sparse index once a read of it loaded its table, whose pages it then keeps as reads take them; null before.
    private GranuleKeys? _keys;

    /// <summary>The file's sparse index, once a read loaded its table (<see cref="KeepKeys"/>); null before.</summary>
    internal GranuleKeys? Keys => System.Threading.Volatile.Read(ref _keys);

    /// <summary>Keeps <paramref name="keys"/>, read and checked, as the file's sparse index; the first kept stays.</summary>
    internal GranuleKeys KeepKeys(GranuleKeys keys) => System.Threading.Interlocked.CompareExchange(ref _keys, keys, null) ?? keys;

    /// <summary>
    /// Forgets <paramref name="keys"/> when it is the file's sparse index kept, which a handle's budget let go: the next read
    /// reads it again, and a search that holds it finishes with it.
    /// </summary>
    internal void ForgetKeys(GranuleKeys keys) => System.Threading.Interlocked.CompareExchange(ref _keys, null, keys);

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
    /// The statistics of each column over the object's granules (<see cref="Indexes.GranuleStats"/>) and their regions: what a
    /// question reads to answer a granule from its columns' values without its rows. Empty when the object keeps none.
    /// </summary>
    internal IReadOnlyList<(int Field, IndexSegment Region)> GranuleStats { get; init; } = [];

    /// <summary>The region of column <paramref name="field"/>'s statistics over the granules, or null when the object keeps none of it.</summary>
    internal IndexSegment? GranuleStatsOf(int field) => RegionOf(GranuleStats, field);

    /// <summary>
    /// Each measure's histograms over the object's granules (<see cref="Indexes.GranuleHistograms"/>) and their regions: what a
    /// quantile reads beside the statistics to answer a granule without its rows. Empty when the object keeps none.
    /// </summary>
    internal IReadOnlyList<(int Field, IndexSegment Region)> GranuleHistograms { get; init; } = [];

    /// <summary>The region of measure <paramref name="field"/>'s histograms over the granules, or null when the object keeps none of it.</summary>
    internal IndexSegment? GranuleHistogramsOf(int field) => RegionOf(GranuleHistograms, field);

    /// <summary>
    /// Where each value of each column lies, granule by granule (<see cref="ColumnLocate"/>), and their regions: what a filter
    /// on a value reads to read the granules of its rows alone. Empty when the object keeps none.
    /// </summary>
    internal IReadOnlyList<(int Field, IndexSegment Region)> Locates { get; init; } = [];

    /// <summary>The region of where column <paramref name="field"/>'s values lie, or null when the object keeps none of it.</summary>
    internal IndexSegment? LocateOf(int field) => RegionOf(Locates, field);

    /// <summary>
    /// Where each value of each shredded key of a variant column lies, read in its class (spec E, 3.3): the variant column,
    /// the key, the class and the region. Empty when the object keeps none.
    /// </summary>
    internal IReadOnlyList<(int Field, string Key, PathClass Class, IndexSegment Region)> PathLocates { get; init; } = [];

    /// <summary>
    /// The statistics over the granules of each shredded key read in its class (<see cref="GranuleStats"/>), its values listed
    /// granule by granule as a column's, the rows that hold none of the class counted as nulls: by the variant column's
    /// place, the key and the class. Empty when the object keeps none.
    /// </summary>
    internal IReadOnlyList<(int Field, string Key, PathClass Class, IndexSegment Region)> PathGranuleStats { get; init; } = [];

    /// <summary>The rows of the granules where each of <see cref="PathGranuleStats"/> holds one value, or nulls alone, by the same places.</summary>
    internal IReadOnlyList<long> PathSingleRows { get; init; } = [];

    /// <summary>The statistics over the granules of key <paramref name="key"/> of the variant column <paramref name="field"/>, read as <paramref name="class"/>, and the rows of its granules of one value; null for none.</summary>
    internal (IndexSegment Region, long SingleRows)? PathGranuleStatsOf(int field, string key, PathClass @class)
    {
        for (int i = 0; i < PathGranuleStats.Count; i++)
        {
            (int column, string named, PathClass held, IndexSegment region) = PathGranuleStats[i];
            if (column == field && held == @class && string.Equals(named, key, StringComparison.Ordinal))
            {
                return (region, i < PathSingleRows.Count ? PathSingleRows[i] : 0);
            }
        }

        return null;
    }

    /// <summary>The region of where key <paramref name="key"/> of the variant column <paramref name="field"/>, read as <paramref name="class"/>, lies; null for none.</summary>
    internal IndexSegment? PathLocateOf(int field, string key, PathClass @class)
    {
        // By place rather than through the list's enumerator, which an interface boxes: a plan asks it of every object.
        for (int i = 0; i < PathLocates.Count; i++)
        {
            (int column, string named, PathClass held, IndexSegment region) = PathLocates[i];
            if (column == field && held == @class && string.Equals(named, key, StringComparison.Ordinal))
            {
                return region;
            }
        }

        return null;
    }

    /// <summary>
    /// The bytes of each top-level column's data in the object, by the column's place: what reading its rows costs, which a
    /// planner weighs against the synopses that would answer instead. Empty when the object does not say.
    /// </summary>
    internal IReadOnlyList<long> ColumnBytes { get; init; } = [];

    /// <summary>
    /// The chunks of each top-level column's data in the object, by the column's place: the requests reading its rows
    /// takes, one a chunk, and the chunks a read of a few rows spares. Empty when the object does not say.
    /// </summary>
    internal IReadOnlyList<long> ColumnChunks { get; init; } = [];

    /// <summary>
    /// The bytes of each top-level column's chunks that a read of a part of one reads whole, by the column's place: a
    /// dictionary's values, patches, runs, which every row of the chunk may need. What a read of a few granules pays for each
    /// chunk it touches, past its granules' share. Empty when the object does not say, whose parts are priced by their share.
    /// </summary>
    internal IReadOnlyList<long> ColumnWholeBytes { get; init; } = [];

    /// <summary>
    /// What each top-level column's values cost the processor (spec B, 2.1), by the column's place: for each encoding class
    /// its values lie under (<see cref="IO.EncodingClass"/>), the units the class's price counts, values for most, runs for
    /// run-end, present values for sparse, over the object's rows in 256ths, rounded up. Each column's entries follow their
    /// count, each <c>units &lt;&lt; 3 | class</c>. Empty when the object does not say.
    /// </summary>
    internal uint[] ColumnClasses { get; init; } = [];

    // Where each column's entries of ColumnClasses start, past their count; null until a read asks.
    private int[]? _classStarts;

    /// <summary>
    /// Column <paramref name="field"/>'s entries of <see cref="ColumnClasses"/>, each <c>units &lt;&lt; 3 | class</c>, the
    /// units in 256ths of the object's rows; empty when the object does not say.
    /// </summary>
    internal ReadOnlySpan<uint> ClassesOf(int field)
    {
        int[] starts = System.Threading.Volatile.Read(ref _classStarts) ?? StartClasses();
        return field >= 0 && field + 1 < starts.Length
            ? ColumnClasses.AsSpan(starts[field], starts[field + 1] - starts[field] - 1)
            : default;
    }

    /// <summary>Where each column's entries start, and one past the last column's count; a span the entries fall short of ends the walk.</summary>
    private int[] StartClasses()
    {
        List<int> starts = [];
        int at = 0;
        while (at < ColumnClasses.Length)
        {
            int count = (int)Math.Min(ColumnClasses[at], (uint)(ColumnClasses.Length - at - 1));
            starts.Add(at + 1);
            at += count + 1;
        }

        starts.Add(at + 1);
        int[] built = [.. starts];
        System.Threading.Volatile.Write(ref _classStarts, built);
        return built;
    }

    /// <summary>
    /// The rows of the object's granules (<see cref="GranuleStats"/>) that hold one value of the key's indexed prefix: those a
    /// question grouped by its components may answer from their statistics, where a granule that holds
    /// several reads its rows. -1 when the object does not say.
    /// </summary>
    internal long PureGranuleRows { get; init; } = -1;

    /// <summary>
    /// The rows of the object's granules whose statistics list each top-level column's values (<see cref="GranuleStats"/>),
    /// at most <see cref="GranuleStats.ListedValues"/> of them a granule, by the column's place: those whose groups by the
    /// column, or whose rows of a value of it, the statistics answer. Empty when the object does not say; 0 for a column
    /// whose statistics list none.
    /// </summary>
    internal IReadOnlyList<long> ListedRows { get; init; } = [];

    /// <summary>The rows of the granules whose statistics list column <paramref name="field"/>'s values (<see cref="ListedRows"/>).</summary>
    internal long ListedRowsOf(int field) => (uint)field < (uint)ListedRows.Count ? ListedRows[field] : 0;

    /// <summary>
    /// The rows of the object's granules where each top-level column holds one value, or nulls alone, as their statistics list
    /// them, by the column's place: those where the column does not vary, which a question that crosses it with another
    /// answers from the other's statistics. Empty when the object does not say; 0 for a column whose statistics list none,
    /// or that varies in every granule.
    /// </summary>
    internal IReadOnlyList<long> SingleRows { get; init; } = [];

    /// <summary>Whether the object says, column by column, the rows of the granules where each holds one value (<see cref="SingleRows"/>).</summary>
    internal bool CountsSingleRows => SingleRows.Count > 0;

    /// <summary>The rows of the granules where column <paramref name="field"/> holds one value (<see cref="SingleRows"/>).</summary>
    internal long SingleRowsOf(int field) => (uint)field < (uint)SingleRows.Count ? SingleRows[field] : 0;

    /// <summary>
    /// The values each component of the sort key that is a function of a column takes in each granule, with their rows
    /// (<see cref="GranuleKeyRuns"/>), by the component's place in the key, and the rows of the granules where it holds one
    /// value; and the runs of the whole indexed prefix, at <see cref="GranuleKeyRuns.WholePrefix"/>, with the rows of the
    /// granules they describe. Empty when the object keeps none.
    /// </summary>
    internal IReadOnlyList<(int Component, IndexSegment Region, long SingleRows)> KeyRuns { get; init; } = [];

    /// <summary>The region of the values of the key's component <paramref name="component"/> in each granule, and the rows of its granules of one value; null for none.</summary>
    internal (IndexSegment Region, long SingleRows)? KeyRunsOf(int component)
    {
        foreach ((int held, IndexSegment region, long single) in KeyRuns)
        {
            if (held == component)
            {
                return (region, single);
            }
        }

        return null;
    }

    /// <summary>
    /// What each column's counts carry (<see cref="Counts"/>), by its field: the fields of the measures their records hold,
    /// and whether a budget left values out, counted as one group of the others. Empty when the object does not say.
    /// </summary>
    internal IReadOnlyList<(int Field, int[] Measures, bool LeavesOut)> Carried { get; init; } = [];

    /// <summary>What the counts of column <paramref name="field"/> carry (<see cref="Carried"/>); null when the object does not say.</summary>
    internal (int[] Measures, bool LeavesOut)? CarriedOf(int field)
    {
        for (int i = 0; i < Carried.Count; i++)
        {
            (int at, int[] measures, bool leavesOut) = Carried[i];
            if (at == field)
            {
                return (measures, leavesOut);
            }
        }

        return null;
    }

    /// <summary>
    /// Each top-level column's geometry (<see cref="ColumnMarks"/>), the regions of its index entry, in column order: what a
    /// read of a few granules reads before their bytes alone. Empty when the object keeps none, and its chunks read whole.
    /// </summary>
    internal IReadOnlyList<IndexSegment> Marks { get; init; } = [];

    /// <summary>
    /// Each top-level column's object dictionary, by the column's place, null for a column whose chunks hold its values:
    /// what a read of its chunks gathers their codes through. Empty when no column has one.
    /// </summary>
    internal IReadOnlyList<ColumnDictionary?> Dictionaries { get; init; } = [];

    private static IndexSegment? RegionOf(IReadOnlyList<(int Field, IndexSegment Region)> regions, int field)
    {
        for (int i = 0; i < regions.Count; i++)
        {
            if (regions[i].Field == field)
            {
                return regions[i].Region;
            }
        }

        return null;
    }

    /// <summary>
    /// How the paged columns' chunk directories lie (<see cref="SliceChunks.PagedVersion"/>): each such column names its page
    /// table in <see cref="ChunkDirectories"/>, and the first row of each page, which every paged column shares, says which
    /// pages a span's rows lie in, with no read. Null when no column's directory is paged.
    /// </summary>
    internal ChunkPaging? Paging { get; init; }

    /// <summary>
    /// These contents naming the region of the file's index directory <paramref name="directory"/> and the entries of
    /// the indexes that locate a column's values in it, <paramref name="locating"/>: new contents, which hold nothing a
    /// reader of these loaded.
    /// </summary>
    internal ObjectContents Naming(IndexSegment directory, IReadOnlyList<LocatingEntry>? locating) =>
        new ObjectContents(Schema, Encodings, Rows, ChunkDirectories, PathDirectories)
        {
            DirectoryRegion = directory,
            Locating = locating,
            Paging = Paging,
            Sheets = Sheets,
            Counts = Counts,
            Sketches = Sketches,
            GranuleStats = GranuleStats,
            GranuleHistograms = GranuleHistograms,
            Locates = Locates,
            PathLocates = PathLocates,
            PathGranuleStats = PathGranuleStats,
            PathSingleRows = PathSingleRows,
            ColumnBytes = ColumnBytes,
            ColumnChunks = ColumnChunks,
            ColumnWholeBytes = ColumnWholeBytes,
            ColumnClasses = ColumnClasses,
            PureGranuleRows = PureGranuleRows,
            ListedRows = ListedRows,
            SingleRows = SingleRows,
            KeyRuns = KeyRuns,
            Carried = Carried,
            Marks = Marks,
            Dictionaries = Dictionaries,
            KeyIndex = KeyIndex,
            KeyPages = KeyPages,
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

    // 3 held the slice table, retired with it: never reused.
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
    private const int TagLocatingKey = 4;
    private const int TagLocatingBytes = 5;

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

    // 17 held the region of the blocks' keys, retired: never reused.

    // A column's statistics over the granules: its field and its region, as a path directory's.
    private const int TagGranuleStats = 18;

    // 19 held where a column's values lived, by the ranges of runs of the key's prefix: retired, never reused.

    // The bytes of each top-level column's data, packed, by the column's place.
    private const int TagColumnBytes = 20;

    // The chunks of each top-level column's data, packed, by the column's place.
    private const int TagColumnChunks = 21;

    // The rows of the granules that hold one value of the key's indexed prefix.
    private const int TagPureGranuleRows = 22;

    // A column's geometry, its region, one a column in column order.
    private const int TagMarks = 23;

    // A column's object dictionary, a message a column that has one: its field, its segment, its values and codes.
    private const int TagDictionary = 24;

    // The file's rows.
    private const int TagRows = 25;

    // The region of the table of the file's sparse index.
    private const int TagKeyIndex = 26;

    // The region of the pages of the file's sparse index.
    private const int TagKeyPages = 27;

    // What each top-level column's values cost the processor, packed: a count, then its entries, a column after the other.
    private const int TagColumnClasses = 28;

    // A measure's histograms over the granules: its field and its region, as a path directory's.
    private const int TagGranuleHistograms = 29;

    // Where a column's values lie, granule by granule: its field and its region, as a path directory's.
    private const int TagLocate = 30;

    // Where a shredded key's values lie, read in its class: its variant column's field, its region, its key and its class.
    private const int TagPathLocate = 31;
    private const int TagPathLocateKey = 3;
    private const int TagPathLocateClass = 4;

    // The rows of the granules whose statistics list each top-level column's values, packed, by the column's place.
    private const int TagListedRows = 32;

    // The rows of the granules where each top-level column holds one value, packed, by the column's place.
    private const int TagSingleRows = 34;

    // The values a component of the key that is a function of a column takes in each granule: its place, as a directory's
    // field, its region, and the rows of its granules of one value.
    private const int TagKeyRuns = 35;
    private const int TagKeyRunsSingle = 3;

    // The runs of the key's whole indexed prefix in each granule: their region, and the rows of the granules they describe.
    private const int TagPrefixRuns = 38;

    // The bytes of each top-level column's chunks that a read of a part of one reads whole, packed, by the column's place.
    private const int TagColumnWholeBytes = 39;

    // The statistics over the granules of a shredded key read in its class, as a path's locate is named; then the rows of the
    // granules where each holds one value, packed, in their order.
    private const int TagPathGranuleStats = 36;
    private const int TagPathSingleRows = 37;

    // What a column's counts carry: its field, the fields of their measures, packed, and whether they leave values out.
    private const int TagCarried = 33;
    private const int TagCarriedMeasures = 2;
    private const int TagCarriedLeavesOut = 3;

    /// <summary>The file's schema, read into <paramref name="types"/>; a struct of its columns.</summary>
    /// <exception cref="VortexFormatException">The schema's bytes do not decode.</exception>
    internal DType SchemaIn(DTypeArena types) => DTypeFlatBuffers.Read(Schema, types);

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

        foreach (IndexSegment region in Marks)
        {
            Declare(region);
        }

        if (KeyIndex is { } keyIndex)
        {
            Declare(keyIndex);
        }

        if (KeyPages is { } keyPages)
        {
            Declare(keyPages);
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

        foreach ((int _, IndexSegment region) in GranuleStats)
        {
            Declare(region);
        }

        foreach ((int _, IndexSegment region) in GranuleHistograms)
        {
            Declare(region);
        }

        foreach ((int _, IndexSegment region, long _) in KeyRuns)
        {
            Declare(region);
        }

        foreach ((int _, IndexSegment region) in Locates)
        {
            Declare(region);
        }

        foreach ((int _, string _, PathClass _, IndexSegment region) in PathLocates)
        {
            Declare(region);
        }

        foreach ((int _, string _, PathClass _, IndexSegment region) in PathGranuleStats)
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

            writer.WriteUInt64Always(TagRows, (ulong)Rows);
            if (KeyIndex is { } keyIndex)
            {
                IndexDirectory.WriteSegment(ref writer, TagKeyIndex, keyIndex);
            }

            if (KeyPages is { } keyPages)
            {
                IndexDirectory.WriteSegment(ref writer, TagKeyPages, keyPages);
            }

            foreach (IndexSegment directory in ChunkDirectories)
            {
                IndexDirectory.WriteSegment(ref writer, TagChunkDirectory, directory);
            }

            foreach (IndexSegment marks in Marks)
            {
                IndexDirectory.WriteSegment(ref writer, TagMarks, marks);
            }

            for (int field = 0; field < Dictionaries.Count; field++)
            {
                if (Dictionaries[field] is { } dictionary)
                {
                    using ProtoWriter.MessageScope message = writer.BeginMessage(TagDictionary);
                    dictionary.Write(ref writer, field);
                }
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

            foreach ((int field, IndexSegment region) in GranuleStats)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagGranuleStats);
                writer.WriteUInt32Always(TagDirectoryField, (uint)field);
                IndexDirectory.WriteSegment(ref writer, TagDirectoryRegion, region);
            }

            foreach ((int field, IndexSegment region) in GranuleHistograms)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagGranuleHistograms);
                writer.WriteUInt32Always(TagDirectoryField, (uint)field);
                IndexDirectory.WriteSegment(ref writer, TagDirectoryRegion, region);
            }

            foreach ((int component, IndexSegment region, long single) in KeyRuns)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(component == GranuleKeyRuns.WholePrefix ? TagPrefixRuns : TagKeyRuns);
                if (component != GranuleKeyRuns.WholePrefix)
                {
                    writer.WriteUInt32Always(TagDirectoryField, (uint)component);
                }

                IndexDirectory.WriteSegment(ref writer, TagDirectoryRegion, region);
                writer.WriteUInt64Always(TagKeyRunsSingle, (ulong)single);
            }

            foreach ((int field, IndexSegment region) in Locates)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagLocate);
                writer.WriteUInt32Always(TagDirectoryField, (uint)field);
                IndexDirectory.WriteSegment(ref writer, TagDirectoryRegion, region);
            }

            foreach ((int field, string key, PathClass @class, IndexSegment region) in PathLocates)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagPathLocate);
                writer.WriteUInt32Always(TagDirectoryField, (uint)field);
                IndexDirectory.WriteSegment(ref writer, TagDirectoryRegion, region);
                writer.WriteString(TagPathLocateKey, key);
                writer.WriteUInt32Always(TagPathLocateClass, (uint)@class);
            }

            foreach ((int field, string key, PathClass @class, IndexSegment region) in PathGranuleStats)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagPathGranuleStats);
                writer.WriteUInt32Always(TagDirectoryField, (uint)field);
                IndexDirectory.WriteSegment(ref writer, TagDirectoryRegion, region);
                writer.WriteString(TagPathLocateKey, key);
                writer.WriteUInt32Always(TagPathLocateClass, (uint)@class);
            }

            if (PathSingleRows.Count > 0)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagPathSingleRows);
                foreach (long rows in PathSingleRows)
                {
                    writer.WriteVarint((ulong)rows);
                }
            }

            if (ColumnBytes.Count > 0)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagColumnBytes);
                foreach (long bytes in ColumnBytes)
                {
                    writer.WriteVarint((ulong)bytes);
                }
            }

            if (ColumnChunks.Count > 0)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagColumnChunks);
                foreach (long chunks in ColumnChunks)
                {
                    writer.WriteVarint((ulong)chunks);
                }
            }

            if (ColumnWholeBytes.Count > 0)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagColumnWholeBytes);
                foreach (long bytes in ColumnWholeBytes)
                {
                    writer.WriteVarint((ulong)bytes);
                }
            }

            if (ColumnClasses.Length > 0)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagColumnClasses);
                foreach (uint entry in ColumnClasses)
                {
                    writer.WriteVarint(entry);
                }
            }

            if (PureGranuleRows >= 0)
            {
                writer.WriteUInt64Always(TagPureGranuleRows, (ulong)PureGranuleRows);
            }

            if (ListedRows.Count > 0)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagListedRows);
                foreach (long rows in ListedRows)
                {
                    writer.WriteVarint((ulong)rows);
                }
            }

            if (SingleRows.Count > 0)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagSingleRows);
                foreach (long rows in SingleRows)
                {
                    writer.WriteVarint((ulong)rows);
                }
            }

            foreach ((int field, int[] measures, bool leavesOut) in Carried)
            {
                using ProtoWriter.MessageScope message = writer.BeginMessage(TagCarried);
                writer.WriteUInt32Always(TagDirectoryField, (uint)field);
                if (measures.Length > 0)
                {
                    using ProtoWriter.MessageScope packed = writer.BeginMessage(TagCarriedMeasures);
                    foreach (int measure in measures)
                    {
                        writer.WriteVarint((ulong)measure);
                    }
                }

                if (leavesOut)
                {
                    writer.WriteUInt32Always(TagCarriedLeavesOut, 1);
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
                if (located.Key is { } key)
                {
                    writer.WriteStringAlways(TagLocatingKey, key);
                }

                if (located.Bytes > 0)
                {
                    writer.WriteUInt64Always(TagLocatingBytes, (ulong)located.Bytes);
                }
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
            long? rows = null;
            IndexSegment? keyIndex = null;
            IndexSegment? keyPages = null;
            List<IndexSegment> directories = [];
            List<(int, IndexSegment)> pathDirectories = [];
            List<(int, IndexSegment)> sheets = [];
            List<(int, IndexSegment)> counts = [];
            List<(int, IndexSegment)> sketches = [];
            List<(int, IndexSegment)> granuleStats = [];
            List<(int, IndexSegment)> granuleHistograms = [];
            List<(int, IndexSegment)> locates = [];
            List<(int, string, PathClass, IndexSegment)> pathLocates = [];
            List<long> columnBytes = [];
            List<long> columnChunks = [];
            long[] columnWholeBytes = [];
            List<uint> columnClasses = [];
            long pureGranuleRows = -1;
            List<long> listedRows = [];
            List<long> singleRows = [];
            List<(int, IndexSegment, long)> keyRuns = [];
            List<(int, string, PathClass, IndexSegment)> pathGranuleStats = [];
            List<long> pathSingleRows = [];
            List<(int, int[], bool)> carried = [];
            List<IndexSegment> marks = [];
            List<(int Field, ColumnDictionary Dictionary)> dictionaries = [];
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
                    case (TagRows, ProtoWireType.Varint):
                        rows = checked((long)reader.ReadVarint());
                        break;
                    case (TagKeyPages, ProtoWireType.LengthDelimited):
                        keyPages = IndexDirectory.ReadSegment(reader.ReadMessage());
                        break;
                    case (TagKeyIndex, ProtoWireType.LengthDelimited):
                        keyIndex = IndexDirectory.ReadSegment(reader.ReadMessage());
                        break;
                    case (TagChunkDirectory, ProtoWireType.LengthDelimited):
                        directories.Add(IndexDirectory.ReadSegment(reader.ReadMessage()));
                        break;
                    case (TagMarks, ProtoWireType.LengthDelimited):
                        marks.Add(IndexDirectory.ReadSegment(reader.ReadMessage()));
                        break;
                    case (TagDictionary, ProtoWireType.LengthDelimited):
                        if (ColumnDictionary.Read(reader.ReadMessage()) is not { } column)
                        {
                            return null;
                        }

                        dictionaries.Add(column);
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
                    case (TagGranuleStats, ProtoWireType.LengthDelimited):
                        if (Directory(reader.ReadMessage()) is not { } stated)
                        {
                            return null;
                        }

                        granuleStats.Add(stated);
                        break;
                    case (TagGranuleHistograms, ProtoWireType.LengthDelimited):
                        if (Directory(reader.ReadMessage()) is not { } histogrammed)
                        {
                            return null;
                        }

                        granuleHistograms.Add(histogrammed);
                        break;
                    case (TagColumnBytes, ProtoWireType.LengthDelimited):
                        ProtoReader packed = reader.ReadMessage();
                        while (!packed.End)
                        {
                            columnBytes.Add(checked((long)packed.ReadVarint()));
                        }

                        break;
                    case (TagColumnChunks, ProtoWireType.LengthDelimited):
                        ProtoReader chunked = reader.ReadMessage();
                        while (!chunked.End)
                        {
                            columnChunks.Add(checked((long)chunked.ReadVarint()));
                        }

                        break;
                    case (TagColumnWholeBytes, ProtoWireType.LengthDelimited):
                        // Counted first, then read into an array of its size, no list grown entry by entry.
                        ProtoReader wholes = reader.ReadMessage();
                        ProtoReader ahead = wholes;
                        int entries = 0;
                        for (; !ahead.End; entries++)
                        {
                            ahead.ReadVarint();
                        }

                        columnWholeBytes = new long[entries];
                        for (int i = 0; i < entries; i++)
                        {
                            columnWholeBytes[i] = checked((long)wholes.ReadVarint());
                        }

                        break;
                    case (TagColumnClasses, ProtoWireType.LengthDelimited):
                        ProtoReader classed = reader.ReadMessage();
                        while (!classed.End)
                        {
                            columnClasses.Add(checked((uint)classed.ReadVarint()));
                        }

                        break;
                    case (TagPureGranuleRows, ProtoWireType.Varint):
                        pureGranuleRows = checked((long)reader.ReadVarint());
                        break;
                    case (TagCarried, ProtoWireType.LengthDelimited):
                        if (CarriedBy(reader.ReadMessage()) is not { } carrying)
                        {
                            return null;
                        }

                        carried.Add(carrying);
                        break;
                    case (TagListedRows, ProtoWireType.LengthDelimited):
                        ProtoReader listing = reader.ReadMessage();
                        while (!listing.End)
                        {
                            listedRows.Add(checked((long)listing.ReadVarint()));
                        }

                        break;
                    case (TagSingleRows, ProtoWireType.LengthDelimited):
                        ProtoReader singles = reader.ReadMessage();
                        while (!singles.End)
                        {
                            singleRows.Add(checked((long)singles.ReadVarint()));
                        }

                        break;
                    case (TagKeyRuns or TagPrefixRuns, ProtoWireType.LengthDelimited):
                        if (KeyRunsEntry(reader.ReadMessage(), whole: field == TagPrefixRuns) is not { } runs)
                        {
                            return null;
                        }

                        keyRuns.Add(runs);
                        break;
                    case (TagLocate, ProtoWireType.LengthDelimited):
                        if (Directory(reader.ReadMessage()) is not { } lying)
                        {
                            return null;
                        }

                        locates.Add(lying);
                        break;
                    case (TagPathLocate, ProtoWireType.LengthDelimited):
                        if (PathLocated(reader.ReadMessage()) is not { } pathLying)
                        {
                            return null;
                        }

                        pathLocates.Add(pathLying);
                        break;
                    case (TagPathGranuleStats, ProtoWireType.LengthDelimited):
                        if (PathLocated(reader.ReadMessage()) is not { } pathStated)
                        {
                            return null;
                        }

                        pathGranuleStats.Add(pathStated);
                        break;
                    case (TagPathSingleRows, ProtoWireType.LengthDelimited):
                        ProtoReader pathSingles = reader.ReadMessage();
                        while (!pathSingles.End)
                        {
                            pathSingleRows.Add(checked((long)pathSingles.ReadVarint()));
                        }

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
            // directory the contents name, and its pages cover the file's rows.
            // A dictionary names a column the directories name.
            return version == Version && rows is { } held && schema is not null
                && dictionaries.TrueForAll(d => d.Field < directories.Count)
                && (locating is null || (indexDirectory is { } named && locating.TrueForAll(l => l.Region.Offset >= named.Offset
                    && l.Region.Offset + l.Region.Length <= named.Offset + named.Length)))
                && (paging is null || (held > 0 && paging.PageRows[^1] < held && Array.TrueForAll(paging.Columns, c =>
                    c < directories.Count && directories[c].Length == SliceChunks.TableBytes(paging.PageRows.Length))))
                ? new ObjectContents(schema, encodings, held, directories, pathDirectories)
                {
                    KeyIndex = keyIndex,
                    KeyPages = keyPages,
                    DirectoryRegion = indexDirectory,
                    Locating = locating,
                    Paging = paging,
                    Sheets = sheets,
                    Counts = counts,
                    Sketches = sketches,
                    GranuleStats = granuleStats,
                    GranuleHistograms = granuleHistograms,
                    Locates = locates,
                    PathLocates = pathLocates,
                    PathGranuleStats = pathGranuleStats,
                    PathSingleRows = pathSingleRows,
                    ColumnBytes = columnBytes,
                    ColumnChunks = columnChunks,
                    ColumnWholeBytes = columnWholeBytes,
                    ColumnClasses = [.. columnClasses],
                    PureGranuleRows = pureGranuleRows,
                    ListedRows = listedRows,
                    SingleRows = singleRows,
                    KeyRuns = keyRuns,
                    Carried = carried,
                    Marks = marks.Count == directories.Count ? marks : [],
                    Dictionaries = Placed(dictionaries, directories.Count),
                }
                : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>The dictionaries by the place of their column among <paramref name="columns"/>; none when no column has one.</summary>
    private static ColumnDictionary?[] Placed(List<(int Field, ColumnDictionary Dictionary)> named, int columns)
    {
        if (named.Count == 0)
        {
            return [];
        }

        ColumnDictionary?[] placed = new ColumnDictionary?[columns];
        foreach ((int field, ColumnDictionary dictionary) in named)
        {
            placed[field] = dictionary;
        }

        return placed;
    }

    /// <summary>A shredded key's index of where its values lie, as its message names it; null when it lacks a part.</summary>
    /// <summary>What a column's counts carry, as <see cref="TagCarried"/> holds it; null for a message without its field.</summary>
    private static (int, int[], bool)? CarriedBy(ProtoReader reader)
    {
        int? field = null;
        List<int> measures = [];
        bool leavesOut = false;
        while (reader.TryReadTag(out int tag, out ProtoWireType wire))
        {
            switch (tag, wire)
            {
                case (TagDirectoryField, ProtoWireType.Varint):
                    field = checked((int)reader.ReadVarint32());
                    break;
                case (TagCarriedMeasures, ProtoWireType.LengthDelimited):
                    ProtoReader packed = reader.ReadMessage();
                    while (!packed.End)
                    {
                        measures.Add(checked((int)packed.ReadVarint32()));
                    }

                    break;
                case (TagCarriedLeavesOut, ProtoWireType.Varint):
                    leavesOut = reader.ReadVarint32() != 0;
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return field is { } at ? (at, [.. measures], leavesOut) : null;
    }

    private static (int, string, PathClass, IndexSegment)? PathLocated(ProtoReader reader)
    {
        int? field = null;
        IndexSegment? region = null;
        string? key = null;
        uint? @class = null;
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
                case (TagPathLocateKey, ProtoWireType.LengthDelimited):
                    key = System.Text.Encoding.UTF8.GetString(reader.ReadLengthDelimited());
                    break;
                case (TagPathLocateClass, ProtoWireType.Varint):
                    @class = reader.ReadVarint32();
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return field is { } at && region is { } segment && key is not null && @class is { } held && held <= (uint)PathClass.Other
            ? (at, key, (PathClass)held, segment)
            : null;
    }

    /// <summary>
    /// A component's values over the granules: its place, its region and the rows of its granules of one value; or, when
    /// <paramref name="whole"/>, the runs of the whole prefix, at <see cref="GranuleKeyRuns.WholePrefix"/>, with the rows of
    /// the granules they describe. Null when they do not read.
    /// </summary>
    private static (int, IndexSegment, long)? KeyRunsEntry(ProtoReader reader, bool whole)
    {
        int component = -1;
        IndexSegment? region = null;
        long single = 0;
        while (reader.TryReadTag(out int tag, out ProtoWireType wire))
        {
            switch (tag, wire)
            {
                case (TagDirectoryField, ProtoWireType.Varint):
                    component = checked((int)reader.ReadVarint32());
                    break;
                case (TagDirectoryRegion, ProtoWireType.LengthDelimited):
                    region = IndexDirectory.ReadSegment(reader.ReadMessage());
                    break;
                case (TagKeyRunsSingle, ProtoWireType.Varint):
                    single = checked((long)reader.ReadVarint());
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return whole && region is { } prefix ? (GranuleKeyRuns.WholePrefix, prefix, single)
            : component >= 0 && region is { } held ? (component, held, single) : null;
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
        string? key = null;
        long bytes = 0;
        while (reader.TryReadTag(out int tag, out ProtoWireType wire))
        {
            switch (tag, wire)
            {
                case (TagLocatingKey, ProtoWireType.LengthDelimited):
                    key = Encoding.UTF8.GetString(reader.ReadLengthDelimited());
                    break;
                case (TagLocatingBytes, ProtoWireType.Varint):
                    bytes = checked((long)reader.ReadVarint());
                    break;
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

        return column is { } on && kind is not null && region is { Checksum: not null } segment ? new LocatingEntry(on, kind, segment) { Key = key, Bytes = bytes } : null;
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
internal readonly record struct LocatingEntry(uint Column, string Kind, IndexSegment Region)
{
    /// <summary>The key of the variant column <see cref="Column"/> the index lies on, the first of its path; null for a column, or unknown.</summary>
    internal string? Key { get; init; }

    /// <summary>
    /// The bytes of the index's segments, every level of a Bloom filter's tree: what placing a value through it reads at most,
    /// past its entry; 0 when unknown, an object written before its table of contents said.
    /// </summary>
    internal long Bytes { get; init; }
}
