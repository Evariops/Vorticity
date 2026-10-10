using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Parquet.Geospatial;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Reading;
using Vorticity.Parquet.Schema;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;
using Vorticity.Types.Numerics;

namespace Vorticity.Parquet.Verification;

/// <summary>
/// What a file says of itself, held to its rows: the metadata oracle. For any file, this package's
/// or another writer's, each flat column's chunk statistics recomputed from its values, its page
/// index's null pages, null counts, bounds and boundary order from its pages' values, its Bloom
/// filter asked for every value it holds, its pages' rows and values counted, its claim that every
/// data page is dictionary codes walked, and its checksums verified. A claim the rows bear out says
/// nothing; one they refute is a finding.
/// </summary>
/// <remarks>
/// A file's statistics and indexes are believed by its scans (the third class of fields, which only
/// correctness rests on): this is how a file from the real world is checked without a reference.
/// A column under a list, whose statistics are its elements', is counted but not bounded.
/// </remarks>
internal static class ParquetVerifier
{
    /// <summary>The findings on the file at <paramref name="path"/>: none for a file that says only what is true.</summary>
    internal static async ValueTask<List<ParquetFinding>> VerifyAsync(string path, VortexSession session, CancellationToken cancellationToken)
    {
        ParquetFile file = await session.OpenParquetAsync(path, null, cancellationToken).ConfigureAwait(false);
        await using (file.ConfigureAwait(false))
        {
            return await VerifyAsync(file, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The findings on <paramref name="opened"/>, its pages held to their checksums whatever it was opened with.</summary>
    internal static async ValueTask<List<ParquetFinding>> VerifyAsync(ParquetFile opened, CancellationToken cancellationToken)
    {
        List<ParquetFinding> findings = [];
        ParquetFile file = opened.Checked();
        {
            ParquetFooter footer = file.Footer;
            long rows = 0;
            foreach (RowGroupEntry group in footer.RowGroups)
            {
                rows += group.RowCount;
            }

            if (rows != footer.RowCount)
            {
                findings.Add(new ParquetFinding(-1, null, "num_rows", $"the file counts {footer.RowCount} rows where its row groups hold {rows}"));
            }

            for (int group = 0; group < footer.RowGroups.Length; group++)
            {
                // An empty row group has no pages to say anything of, as the scans skip it.
                if (footer.RowGroups[group].RowCount == 0)
                {
                    continue;
                }

                foreach (ParquetColumn column in file.Compiled.Columns)
                {
                    try
                    {
                        await VerifyChunkAsync(file, group, column, findings, cancellationToken).ConfigureAwait(false);
                    }
                    catch (ParquetFormatException e)
                    {
                        findings.Add(Finding(group, column, "pages", e.Message));
                    }
                }
            }
        }

        return findings;
    }

    private static ParquetFinding Finding(int group, ParquetColumn column, string structure, string message) =>
        new ParquetFinding(group, column.DottedPath, structure, message);

    private static async ValueTask VerifyChunkAsync(ParquetFile file, int group, ParquetColumn column, List<ParquetFinding> findings, CancellationToken cancellationToken)
    {
        ParquetFooter footer = file.Footer;
        RowGroupEntry entry = footer.RowGroups[group];
        ColumnChunkMetadata chunk = footer.Chunk(group, column.Ordinal);
        if (chunk.IsEncrypted)
        {
            // An encrypted chunk's pages and indexes are modules this check does not decrypt: its statistics
            // alone are held to its values, which a scan decrypts, where its key is given.
            if (!chunk.Hidden && file.Footer.Decryptor is not null && column.MaxRepetitionLevel == 0 && column.Form != LeafForm.Null)
            {
                ChunkValues encrypted = await ValuesAsync(file, group, column, cancellationToken).ConfigureAwait(false);
                Statistics(group, column, chunk, footer, encrypted, findings);
                Geospatial(group, column, chunk, footer, encrypted, findings);
            }

            return;
        }

        (long start, int length) = file.ChunkRange(chunk);
        using SegmentRequestSet requests = new();
        int slot = requests.Add(new SegmentSpec((ulong)start, (uint)length, 0, 0, 0));
        await file.Reader.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
        Walk(requests.GetBuffer(slot).Span, group, column, chunk, entry.RowCount, findings);
        requests.Release();

        if (column.MaxRepetitionLevel > 0 || column.Form == LeafForm.Null)
        {
            return;
        }

        ChunkValues values = await ValuesAsync(file, group, column, cancellationToken).ConfigureAwait(false);
        Statistics(group, column, chunk, footer, values, findings);
        Geospatial(group, column, chunk, footer, values, findings);
        await PageIndexAsync(file, group, column, chunk, values, findings, cancellationToken).ConfigureAwait(false);
        await BloomAsync(file, group, column, chunk, values, findings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The chunk's pages by their headers: their rows and values against the chunk's, and every data page's encoding against its claim.</summary>
    private static void Walk(ReadOnlySpan<byte> bytes, int group, ParquetColumn column, ColumnChunkMetadata chunk, long groupRows, List<ParquetFinding> findings)
    {
        long values = 0;
        long rows = 0;
        bool rowsKnown = true;
        bool allCodes = true;
        int at = 0;
        while (at < bytes.Length)
        {
            PageHeader header = PageHeader.Read(bytes[at..]);
            int body = at + header.HeaderLength;
            if (header.CompressedPageSize < 0 || header.CompressedPageSize > bytes.Length - body)
            {
                findings.Add(Finding(group, column, "pages", "a page runs past its chunk"));
                return;
            }

            switch (header.Type)
            {
                case PageType.DataPage:
                    values += header.ValueCount;
                    rowsKnown &= column.MaxRepetitionLevel == 0;
                    rows += header.ValueCount;
                    allCodes &= header.Encoding is ParquetEncoding.RleDictionary or ParquetEncoding.PlainDictionary;
                    break;
                case PageType.DataPageV2:
                    values += header.ValueCount;
                    rows += header.RowCount;
                    allCodes &= header.Encoding is ParquetEncoding.RleDictionary or ParquetEncoding.PlainDictionary;
                    break;
            }

            at = body + header.CompressedPageSize;
        }

        if (values != chunk.ValueCount)
        {
            findings.Add(Finding(group, column, "num_values", $"the chunk counts {chunk.ValueCount} values where its pages hold {values}"));
        }

        if (rowsKnown && rows != groupRows)
        {
            findings.Add(Finding(group, column, "pages", $"the pages hold {rows} rows of a row group of {groupRows}"));
        }

        if (chunk.HasEncodingStats && chunk.AllDataPagesDictionary && !allCodes)
        {
            findings.Add(Finding(group, column, "encoding_stats", "every data page is said dictionary codes, and one is not"));
        }
    }

    /// <summary>A flat column's values in one row group, row by row: each a literal, or null, or none this build compares.</summary>
    private sealed class ChunkValues(long rows)
    {
        internal FilterLiteral[] Values { get; } = new FilterLiteral[rows];

        internal bool[] Valid { get; } = new bool[rows];

        /// <summary>Whether a valid value had no literal: a type no bound of this build covers.</summary>
        internal bool Unbounded { get; set; }
    }

    private static async ValueTask<ChunkValues> ValuesAsync(ParquetFile file, int group, ParquetColumn column, CancellationToken cancellationToken)
    {
        ParquetFooter footer = file.Footer;
        RowGroupEntry entry = footer.RowGroups[group];
        ChunkValues values = new ChunkValues(entry.RowCount);
        int field = TopField(file.Compiled, column);
        bool[] groups = new bool[footer.RowGroups.Length];
        groups[group] = true;
        ScanSpec spec = new ScanSpec { Rows = new RowRange(entry.FirstRow, entry.FirstRow + entry.RowCount) };
        FieldExpr path = new FieldExpr(column.DottedPath);
        bool decimals = ColumnBounds.IsDecimal(column);
        ParquetBatches batches = new ParquetBatches(file, spec, [field], groups, new ScanCounters(), cancellationToken, storage: true);
        await using (batches.ConfigureAwait(false))
        {
            while (await batches.MoveNextAsync().ConfigureAwait(false))
            {
                RecordBatch batch = batches.Current;
                int offset = (int)(batch.StartRow - entry.FirstRow);
                Read(batch, path, decimals, offset, values);
            }
        }

        return values;
    }

    private static void Read(RecordBatch batch, FieldExpr path, bool decimals, int offset, ChunkValues values)
    {
        CanonicalArena arena = batch.Arena;
        int node = FilterEvaluator.Resolve(arena, batch.RootIndex, path, batch.RowCount);
        ValidityMask mask = ValidityMask.From(arena, arena.GetNode(node).Validity);
        for (int row = 0; row < batch.RowCount; row++)
        {
            bool valid = mask.IsValid(row);
            values.Valid[offset + row] = valid;
            if (!valid)
            {
                continue;
            }

            bool read = decimals ? LiteralReader.TryReadDecimal(arena, node, row, out FilterLiteral value) : LiteralReader.TryRead(arena, node, row, out value);
            if (read)
            {
                values.Values[offset + row] = value;
            }
            else
            {
                values.Unbounded = true;
            }
        }
    }

    private static int TopField(ParquetSchema schema, ParquetColumn column)
    {
        for (int field = 0; field < schema.Fields.Length; field++)
        {
            ParquetField top = schema.Fields[field];
            if (column.Ordinal >= top.FirstLeaf && column.Ordinal < top.FirstLeaf + top.LeafCount)
            {
                return field;
            }
        }

        throw new InvalidOperationException($"No field holds the column '{column.DottedPath}'.");
    }

    /// <summary>The extremes and counts of rows <paramref name="from"/> to <paramref name="to"/>, NaN skipped and counted apart.</summary>
    private static (FilterLiteral Min, FilterLiteral Max, bool Bounded, long Nulls, long NaNs) Summarize(ChunkValues values, long from, long to, bool decimals)
    {
        FilterLiteral min = default;
        FilterLiteral max = default;
        bool has = false;
        bool bounded = !values.Unbounded;
        long nulls = 0;
        long nans = 0;
        for (long row = from; row < to; row++)
        {
            if (!values.Valid[row])
            {
                nulls++;
                continue;
            }

            FilterLiteral value = values.Values[row];
            if (value.Kind == FilterLiteralKind.Float && double.IsNaN(value.FloatValue))
            {
                nans++;
                continue;
            }

            if (!has)
            {
                min = max = value;
                has = true;
                continue;
            }

            if (TryOrder(value, min, decimals, out int low) && TryOrder(value, max, decimals, out int high))
            {
                min = low < 0 ? value : min;
                max = high > 0 ? value : max;
            }
            else
            {
                bounded = false;
            }
        }

        return (min, max, bounded && has, nulls, nans);
    }

    private static bool TryOrder(FilterLiteral a, FilterLiteral b, bool decimals, out int order)
    {
        if (decimals)
        {
            order = 0;
            if (!ComparisonKernels.TryDecimal(a, out Int256 x) || !ComparisonKernels.TryDecimal(b, out Int256 y))
            {
                return false;
            }

            order = x.CompareTo(y);
            return true;
        }

        return ZonePruner.TryCompare(a, b, out order);
    }

    /// <summary>The chunk's statistics against its values: its null and NaN counts, and its bounds, equal to the extremes where exact and around them otherwise.</summary>
    private static void Statistics(int group, ParquetColumn column, ColumnChunkMetadata chunk, ParquetFooter footer, ChunkValues values, List<ParquetFinding> findings)
    {
        ZoneBounds bounds = ColumnBounds.Of(column, chunk.Statistics, chunk.Source.Span);
        bool decimals = ColumnBounds.IsDecimal(column);
        (FilterLiteral min, FilterLiteral max, bool bounded, long nulls, long nans) = Summarize(values, 0, values.Valid.Length, decimals);
        if (bounds.HasNullCount && bounds.NullCount != nulls)
        {
            findings.Add(Finding(group, column, "null_count", $"{bounds.NullCount} where the rows hold {nulls}"));
        }

        if (bounds.HasNanCount && bounds.NanCount != nans)
        {
            findings.Add(Finding(group, column, "nan_count", $"{bounds.NanCount} where the rows hold {nans}"));
        }

        if (!bounded)
        {
            return;
        }

        Bound(group, column, "min_value", bounds.HasMin, bounds.Min, min, lower: true, bounds.IsExact, decimals, findings);
        Bound(group, column, "max_value", bounds.HasMax, bounds.Max, max, lower: false, bounds.IsExact, decimals, findings);
    }

    /// <summary>
    /// A GEOMETRY's or GEOGRAPHY's statistics against its values: no bound NaN, every coordinate in
    /// the box, X across the antimeridian where the box wraps, and every value's type in a list
    /// that is not empty. A GEOGRAPHY's edges may pass outside its vertices, which this does not
    /// follow: its box is held to the vertices alone, within <see cref="SphereSlack"/> degrees, as a
    /// box worked out on the sphere takes its vertices through unit vectors and back, which costs
    /// them a few units in the last place.
    /// </summary>
    /// <summary>How far, in degrees, a GEOGRAPHY's vertex may lie outside its box: about a tenth of a millimetre.</summary>
    private const double SphereSlack = 1e-9;

    private static void Geospatial(int group, ParquetColumn column, ColumnChunkMetadata chunk, ParquetFooter footer, ChunkValues values, List<ParquetFinding> findings)
    {
        if (!chunk.GeospatialStatistics.IsPresent || values.Unbounded)
        {
            return;
        }

        GeospatialStatistics declared;
        try
        {
            declared = GeospatialStatistics.Read(chunk.GeospatialStatistics.Of(chunk.Source.Span));
        }
        catch (ParquetFormatException e)
        {
            findings.Add(Finding(group, column, "geospatial_statistics", e.Message));
            return;
        }

        double slack = column.Logical.Kind == LogicalTypeKind.Geography ? SphereSlack : 0;
        WkbBounds found = new();
        if (declared.HasBox && declared.XMin > declared.XMax)
        {
            // A box that wraps holds no X between its east end and its west end: each X is held to that gap.
            found.Gap(declared.XMax + slack, declared.XMin - slack);
        }

        for (int row = 0; row < values.Valid.Length; row++)
        {
            if (values.Valid[row])
            {
                found.Add(values.Values[row].BytesValue);
            }
        }

        if (found.Unknown)
        {
            findings.Add(Finding(group, column, "geospatial_statistics", "a value is not ISO WKB of the seven types the standard lists"));
            return;
        }

        if (declared.Types is { Length: > 0 } types)
        {
            for (int bit = 0; bit < 28; bit++)
            {
                int code = (1000 * (bit / 7)) + (bit % 7) + 1;
                if ((found.TypeBits & (1u << bit)) != 0 && Array.IndexOf(types, code) < 0)
                {
                    findings.Add(Finding(group, column, "geospatial_types", $"[{string.Join(", ", types)}] where a value is of type {code}"));
                }
            }
        }

        if (!declared.HasBox)
        {
            return;
        }

        Span<double> bounds = stackalloc double[8];
        found.Bounds(bounds);
        if (double.IsNaN(declared.XMin) || double.IsNaN(declared.XMax) || double.IsNaN(declared.YMin) || double.IsNaN(declared.YMax)
            || (declared.HasZ && (double.IsNaN(declared.ZMin) || double.IsNaN(declared.ZMax)))
            || (declared.HasM && (double.IsNaN(declared.MMin) || double.IsNaN(declared.MMax))))
        {
            findings.Add(Finding(group, column, "bbox", "a bound is NaN"));
            return;
        }

        bool x = declared.XMin > declared.XMax ? found.InGap == 0 : bounds[0] > bounds[1] || (bounds[0] >= declared.XMin - slack && bounds[1] <= declared.XMax + slack);
        bool y = bounds[2] > bounds[3] || (bounds[2] >= declared.YMin - slack && bounds[3] <= declared.YMax + slack);
        bool z = !declared.HasZ || bounds[4] > bounds[5] || (bounds[4] >= declared.ZMin && bounds[5] <= declared.ZMax);
        bool m = !declared.HasM || bounds[6] > bounds[7] || (bounds[6] >= declared.MMin && bounds[7] <= declared.MMax);
        if (!(x && y && z && m))
        {
            findings.Add(Finding(group, column, "bbox", string.Create(
                CultureInfo.InvariantCulture,
                $"x [{declared.XMin}, {declared.XMax}] y [{declared.YMin}, {declared.YMax}] where the values reach x [{bounds[0]}, {bounds[1]}] y [{bounds[2]}, {bounds[3]}] z [{bounds[4]}, {bounds[5]}] m [{bounds[6]}, {bounds[7]}]")));
        }
    }

    /// <summary>A bound against the extreme of the values it bounds: equal to it when exact, on its outer side otherwise.</summary>
    private static void Bound(int group, ParquetColumn column, string structure, bool has, FilterLiteral bound, FilterLiteral extreme, bool lower, bool exact, bool decimals, List<ParquetFinding> findings)
    {
        if (!has || !TryOrder(bound, extreme, decimals, out int order))
        {
            return;
        }

        if (exact ? order != 0 : (lower ? order > 0 : order < 0))
        {
            findings.Add(Finding(group, column, structure, $"{bound} {(exact ? "exact" : "bound")} where the rows' {(lower ? "least" : "greatest")} is {extreme}"));
        }
    }

    /// <summary>The column index against its pages' values: null pages, null counts, bounds around each page and the boundary order.</summary>
    private static async ValueTask PageIndexAsync(ParquetFile file, int group, ParquetColumn column, ColumnChunkMetadata chunk, ChunkValues values, List<ParquetFinding> findings, CancellationToken cancellationToken)
    {
        if (!file.Holds(chunk.ColumnIndexOffset, chunk.ColumnIndexLength) || !file.Holds(chunk.OffsetIndexOffset, chunk.OffsetIndexLength))
        {
            return;
        }

        long rows = values.Valid.Length;
        using SegmentRequestSet requests = new();
        int columnSlot = requests.Add(new SegmentSpec((ulong)chunk.ColumnIndexOffset, (uint)chunk.ColumnIndexLength, 0, 0, 0));
        int offsetSlot = requests.Add(new SegmentSpec((ulong)chunk.OffsetIndexOffset, (uint)chunk.OffsetIndexLength, 0, 0, 0));
        await file.Reader.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
        PageLocation[] pages = OffsetIndex.Read(requests.GetBuffer(offsetSlot).Span, rows);
        ColumnIndex index = ColumnIndex.Read(requests.GetBuffer(columnSlot).Span.ToArray(), pages.Length);
        requests.Release();
        bool decimals = ColumnBounds.IsDecimal(column);
        FilterLiteral previousMin = default;
        FilterLiteral previousMax = default;
        bool previous = false;
        for (int page = 0; page < pages.Length; page++)
        {
            long from = pages[page].FirstRow;
            long to = page + 1 < pages.Length ? pages[page + 1].FirstRow : rows;
            (FilterLiteral min, FilterLiteral max, bool bounded, long nulls, long _) = Summarize(values, from, to, decimals);
            bool allNull = nulls == to - from;
            if (index.NullPages[page] != allNull)
            {
                findings.Add(Finding(group, column, "column index", $"page {page} is {(index.NullPages[page] ? "" : "not ")}said null where it holds {to - from - nulls} values"));
            }

            if (index.NullCounts is { } counts && counts[page] != nulls)
            {
                findings.Add(Finding(group, column, "column index", $"page {page} counts {counts[page]} nulls where it holds {nulls}"));
            }

            if (index.NullPages[page] || !bounded
                || !ColumnBounds.TryLiteral(column, index.Min(page), out FilterLiteral low) || !ColumnBounds.TryLiteral(column, index.Max(page), out FilterLiteral high))
            {
                continue;
            }

            Bound(group, column, $"column index, page {page}, min", true, low, min, lower: true, exact: false, decimals, findings);
            Bound(group, column, $"column index, page {page}, max", true, high, max, lower: false, exact: false, decimals, findings);
            if (previous && index.Order != BoundaryOrder.Unordered
                && TryOrder(low, previousMin, decimals, out int byMin) && TryOrder(high, previousMax, decimals, out int byMax)
                && (index.Order == BoundaryOrder.Ascending ? byMin < 0 || byMax < 0 : byMin > 0 || byMax > 0))
            {
                findings.Add(Finding(group, column, "column index", $"page {page}'s bounds break the {index.Order} order"));
            }

            previousMin = low;
            previousMax = high;
            previous = true;
        }
    }

    /// <summary>The Bloom filter asked for every value the chunk holds: one it says is absent is a false negative.</summary>
    private static async ValueTask BloomAsync(ParquetFile file, int group, ParquetColumn column, ColumnChunkMetadata chunk, ChunkValues values, List<ParquetFinding> findings, CancellationToken cancellationToken)
    {
        if (chunk.BloomFilterOffset < 0 || values.Unbounded)
        {
            return;
        }

        uint[]? words = await BloomPruning.ReadFilterAsync(file, chunk, metrics: null, cancellationToken).ConfigureAwait(false);
        if (words is null)
        {
            findings.Add(Finding(group, column, "bloom filter", "the footer points at no filter this build reads"));
            return;
        }

        Span<byte> plain = stackalloc byte[32];
        for (long row = 0; row < values.Valid.Length; row++)
        {
            if (!values.Valid[row])
            {
                continue;
            }

            if (BloomPruning.Holds(column, values.Values[row], words, plain) == false)
            {
                findings.Add(Finding(group, column, "bloom filter", $"row {row}'s value {values.Values[row]} is said absent"));
                return;
            }
        }
    }
}
