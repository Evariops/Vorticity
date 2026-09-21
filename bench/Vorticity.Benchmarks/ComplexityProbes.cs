// Three costs that grow with something they should not, measured so a fix has a before.
//
// They are curves rather than gates, so `explore`: the answer wanted from each is a SHAPE -- what
// happens to the time when the input grows -- and a shape does not belong in a run that has to stay
// under a minute. The three were first measured by throwaway programs outside the repository, which
// means nothing could re-measure them after a change; that is what this class exists to end.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>The three growth curves a complexity fix has to be measured against.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class ComplexityProbes
{
    /// <summary>Rows of the file the <c>IN</c> probe filters.</summary>
    private const int FilterRows = 1_000_000;

    /// <summary>Rows of the batch the window probe slices.</summary>
    private const int WindowRows = 500_000;

    /// <summary>Rows the window probe asks for, which is all of them.</summary>
    private const int WindowLength = WindowRows;

    /// <summary>Distinct sixteen-byte literals the hash probe inserts.</summary>
    private const int LiteralCount = 16_000;

    /// <summary>The column the <c>IN</c> probe filters on.</summary>
    private const string Field = "v";

    /// <summary>
    /// How many literals the <c>IN</c> is given, per case.
    /// </summary>
    /// <remarks>
    /// They are decades apart because the point is the exponent, not the constant: a kernel that
    /// evaluates one comparison per literal shows a line, and one that tests membership once per row
    /// shows a floor.
    ///
    /// The curve runs to sixteen thousand because the small end cannot tell the two shapes apart.
    /// A scan of a million rows costs about 0,8 ms before any candidate is looked at, so at 512 that
    /// floor is most of the reading and a residual term in the candidate count hides under it. Four
    /// thousand and sixteen thousand are where such a term stops hiding.
    ///
    /// Arguments of one benchmark rather than parameters of the class, because the other two probes
    /// do not depend on them: as parameters they ran once over per count and printed identical rows,
    /// which is both slower and an invitation to read a curve where there is none.
    /// </remarks>
    public static IEnumerable<int> LiteralCounts => [1, 8, 64, 512, 4_096, 16_384];

    /// <summary>Rows per zone of the map the pruning probe asks, which is one block.</summary>
    private const int ZoneLength = 1_024;

    /// <summary>
    /// How many rows the take probe asks for, scattered over every chunk of the file.
    /// </summary>
    /// <remarks>
    /// The published <c>take</c> scenario asks for sixty-four rows, which is too few to tell a
    /// selection walked once per chunk from one searched: at sixty-four the walk is a cache line.
    /// These counts run to a tenth of the file because that is where a per-chunk pass over the
    /// whole selection stops hiding under the gather it precedes.
    /// </remarks>
    public static IEnumerable<int> TakeCounts => [64, 1_024, 10_000, 100_000];

    /// <summary>Columns of the wide file, each one chunk larger than the batch that reads it.</summary>
    private const int WideColumns = 1_000;

    /// <summary>Rows of that file, which is one chunk.</summary>
    private const int WideRows = 1_024;

    /// <summary>The batch it is read in, small enough that every column is retained.</summary>
    private const int WideBatch = 64;

    /// <summary>
    /// How many of the wide file's columns the retention probe projects.
    /// </summary>
    /// <remarks>
    /// A retained chunk is looked up once per column and per batch, so a lookup that walks the
    /// retained entries costs the square of the count. The three points are far enough apart to
    /// tell that square from a line.
    /// </remarks>
    public static IEnumerable<int> ColumnCounts => [64, 256, 1_000];

    private string _path = string.Empty;
    private string _widePath = string.Empty;
    private Dictionary<int, string[]> _projections = [];
    private Dictionary<int, FilterLiteral[]> _needles = [];
    private Dictionary<int, ZonePruner> _pruners = [];
    private Dictionary<int, long[]> _takes = [];
    private CanonicalArena _arena = new CanonicalArena();
    private RecordBatch? _batch;
    private FilterLiteral[] _keys = [];

    /// <summary>Writes the file, builds the batch, and mints the literals.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-probe-{Guid.NewGuid():N}.vortex");
        WriteFilterFileAsync(_path).GetAwaiter().GetResult();

        _widePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-wide-{Guid.NewGuid():N}.vortex");
        WriteWideFileAsync(_widePath).GetAwaiter().GetResult();

        _projections = [];
        foreach (int count in ColumnCounts)
        {
            string[] paths = new string[count];
            for (int i = 0; i < count; i++)
            {
                paths[i] = WideField(i);
            }

            _projections[count] = paths;
        }

        _needles = [];
        foreach (int count in LiteralCounts)
        {
            FilterLiteral[] needles = new FilterLiteral[count];
            for (int i = 0; i < count; i++)
            {
                // Spread over the whole column rather than clustered, so no zone could prune them
                // even if pruning were left on, and every one of them is a value the column holds.
                needles[i] = FilterLiteral.From((i + 1L) * (FilterRows / (count + 1L)));
            }

            _needles[count] = needles;
        }

        // Every candidate sits above the column, so no zone can hold one and the pruner has to
        // look at all of them before it can say so. That is the case the cost per zone shows in:
        // with a candidate the map can find, the first zone answers and the count never matters.
        _pruners = [];
        ZoneBounds[] zones = new ZoneBounds[(FilterRows + ZoneLength - 1) / ZoneLength];
        for (int zone = 0; zone < zones.Length; zone++)
        {
            long low = (long)zone * ZoneLength;
            zones[zone] = ZoneBounds.Create(
                FilterLiteral.From(low), true,
                FilterLiteral.From(Math.Min(low + ZoneLength, FilterRows) - 1), true,
                exact: true, nullCount: 0, hasNullCount: true);
        }

        ZoneColumn map = new ZoneColumn(Expr.Field(Field), ZoneLength, FilterRows, zones);
        foreach (int count in LiteralCounts)
        {
            FilterLiteral[] absent = new FilterLiteral[count];
            for (int i = 0; i < count; i++)
            {
                // Below every zone, and not merely past the last one: a candidate above the column
                // still falls inside the final zone unless its bounds stop where the rows do, and
                // a probe that lets the first candidate answer measures nothing.
                absent[i] = FilterLiteral.From(-(i + 1L));
            }

            _pruners[count] = new ZonePruner(Expr.In(Expr.Field(Field), absent), [map]);
        }

        _takes = [];
        foreach (int count in TakeCounts)
        {
            long[] rows = new long[count];
            for (int i = 0; i < count; i++)
            {
                rows[i] = (long)i * (FilterRows / count);
            }

            _takes[count] = rows;
        }

        _arena = new CanonicalArena();
        DType i64 = new DTypeArena().Primitive(PType.I64, Nullability.NonNullable);
        int column = Longs(_arena, i64, WindowRows);
        _batch = new RecordBatch(_arena, column, 0);

        _keys = new FilterLiteral[LiteralCount];
        Span<byte> value = stackalloc byte[16];
        for (int i = 0; i < LiteralCount; i++)
        {
            // Sixteen bytes that differ only past the first eight: the shape of a UUID column, and
            // the shape a hash of the LENGTH cannot tell apart.
            MemoryMarshal.Write(value, 0x5645_4E44_4F52_0000UL);
            MemoryMarshal.Write(value[8..], (ulong)i);
            _keys[i] = FilterLiteral.From(value);
        }
    }

    /// <summary>Deletes the file the setup wrote.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _batch?.Dispose();
        try
        {
            System.IO.File.Delete(_path);
            System.IO.File.Delete(_widePath);
        }
        catch (System.IO.IOException)
        {
            // A probe that fails to tidy up is still a probe; the directory is the system's.
        }
    }

    /// <summary>Counts the rows an <c>IN</c> of <see cref="Literals"/> values keeps.</summary>
    /// <remarks>
    /// Pruning and indexes are off so the number is the comparison kernel's and nothing else's: with
    /// them on, a narrow set of literals is answered by the zone map and the curve measures the
    /// pruner instead.
    /// </remarks>
    [Benchmark]
    [ArgumentsSource(nameof(LiteralCounts))]
    public long FilteredCount(int literals)
    {
        return FilteredCountAsync(literals).GetAwaiter().GetResult();
    }

    /// <summary>Counts the same rows with pruning left on.</summary>
    /// <remarks>
    /// The mirror of <see cref="FilteredCount"/>, and it prices the whole read rather than the
    /// pruning: its candidates are values the column holds, so the first zone answers the map and
    /// what grows with their count is the rows the filter keeps. The pruning's own cost is
    /// <see cref="ZonePrune"/>. Indexes stay off so nothing but the zone map stands between the
    /// plan and the kernel.
    /// </remarks>
    [Benchmark]
    [ArgumentsSource(nameof(LiteralCounts))]
    public long PrunedCount(int literals)
    {
        return PrunedCountAsync(literals).GetAwaiter().GetResult();
    }

    /// <summary>Asks a thousand-zone map to rule out an <c>IN</c> it can rule out.</summary>
    /// <remarks>
    /// The zone map on its own, with no scan around it, because a scan hides the question: its
    /// candidates are values the column holds, so the first zone answers and what grows with their
    /// count is the rows the filter keeps, not the pruning. Here no zone can hold a candidate, so
    /// the pruner has to look at every zone before it may say no -- which is the shape the growth
    /// lives in, and the one a filter over a key the file does not carry meets in practice.
    /// </remarks>
    [Benchmark]
    [ArgumentsSource(nameof(LiteralCounts))]
    public bool ZonePrune(int literals)
    {
        return _pruners[literals].MayMatch(new RowRange(0, FilterRows));
    }

    /// <summary>Takes rows scattered over every chunk of the file.</summary>
    /// <remarks>
    /// A chunked reader re-bases the selection into each chunk's own row space, and what that costs
    /// per chunk is what this measures against the count asked for. The rows are evenly spread, so
    /// every chunk wants some and none is skipped.
    /// </remarks>
    [Benchmark]
    [ArgumentsSource(nameof(TakeCounts))]
    public long ChunkedTake(int rows)
    {
        return ChunkedTakeAsync(rows).GetAwaiter().GetResult();
    }

    /// <summary>Reads a wide file whose every column is a chunk larger than the batch.</summary>
    /// <remarks>
    /// Each such column is decoded once and borrowed by every batch, and each batch asks the scan
    /// context for the decode it holds. What that lookup costs is what this measures against the
    /// number of columns holding one.
    /// </remarks>
    [Benchmark]
    [ArgumentsSource(nameof(ColumnCounts))]
    public long RetainedLookup(int columns)
    {
        return RetainedLookupAsync(columns).GetAwaiter().GetResult();
    }

    /// <summary>Windows a batch of half a million rows.</summary>
    /// <remarks>
    /// Not parameterized: the question is whether the cost is proportional to the rows windowed or
    /// to the nodes of the batch, and one large window answers it. A slice that borrows costs a
    /// record per node; a gather costs a row.
    /// </remarks>
    [Benchmark]
    public int Window()
    {
        RecordBatch batch = _batch ?? throw new InvalidOperationException("The setup has not run.");
        using RecordBatch window = batch.Window(0, WindowLength);
        return window.RowCount;
    }

    /// <summary>Fills a dictionary with sixteen thousand distinct literals.</summary>
    /// <remarks>
    /// The insertion and not a lookup, because the cost being looked for is the one paid when every
    /// key lands in the same bucket: the dictionary degenerates to a list and each insertion walks
    /// it. A hash over the content makes this flat.
    /// </remarks>
    [Benchmark]
    public int LiteralDictionary()
    {
        Dictionary<FilterLiteral, int> slots = [];
        for (int i = 0; i < _keys.Length; i++)
        {
            slots.TryAdd(_keys[i], i);
        }

        return slots.Count;
    }

    private async Task<long> FilteredCountAsync(int literals)
    {
        await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None);
        return await file.ScanBuilder()
            .WithPruning(false)
            .WithIndexes(false)
            .Where(Expr.In(Expr.Field(Field), _needles[literals]))
            .CountAsync(CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static string WideField(int index) => $"c{index:D4}";

    private static async Task WriteWideFileAsync(string path)
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        string[] names = new string[WideColumns];
        DType[] fields = new DType[WideColumns];
        for (int i = 0; i < WideColumns; i++)
        {
            names[i] = WideField(i);
            fields[i] = i64;
        }

        DType schema = types.Struct(names, fields, Nullability.NonNullable);
        VortexWriteOptions options = new VortexWriteOptions { Compress = false };
        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);

        CanonicalArena arena = new CanonicalArena();
        int[] columns = new int[WideColumns];
        for (int i = 0; i < WideColumns; i++)
        {
            columns[i] = Longs(arena, i64, WideRows);
        }

        int root = arena.AddStruct(schema, WideRows, Validity.NonNullable, columns);
        using RecordBatch record = new RecordBatch(arena, root, 0);
        await writer.WriteAsync(record, CancellationToken.None).ConfigureAwait(false);
        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<long> RetainedLookupAsync(int columns)
    {
        await using VortexFile file = await VortexFile.OpenAsync(_widePath, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder()
            .Project(_projections[columns])
            .WithMaxBatchRows(WideBatch)
            .ExecuteAsync()
            .WithCancellation(CancellationToken.None)
            .ConfigureAwait(false))
        {
            rows += batch.RowCount;
            batch.Dispose();
        }

        return rows;
    }

    private async Task<long> ChunkedTakeAsync(int rows)
    {
        await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None);
        long taken = 0;
        await foreach (RecordBatch batch in file.ScanBuilder()
            .Take(_takes[rows]).ExecuteAsync()
            .WithCancellation(CancellationToken.None)
            .ConfigureAwait(false))
        {
            taken += batch.RowCount;
            batch.Dispose();
        }

        return taken;
    }

    private async Task<long> PrunedCountAsync(int literals)
    {
        await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None);
        return await file.ScanBuilder()
            .WithPruning(true)
            .WithIndexes(false)
            .Where(Expr.In(Expr.Field(Field), _needles[literals]))
            .CountAsync(CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task WriteFilterFileAsync(string path)
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct([Field], [i64], Nullability.NonNullable);

        // Uncompressed on purpose: a compressed column would put a decoder in front of the kernel
        // and the curve would be the decoder's as much as the comparison's.
        VortexWriteOptions options = new VortexWriteOptions { Compress = false };
        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
        const int batch = 8_192;
        for (int start = 0; start < FilterRows; start += batch)
        {
            int rows = Math.Min(batch, FilterRows - start);
            CanonicalArena arena = new CanonicalArena();
            int column = Longs(arena, i64, rows, start);
            int root = arena.AddStruct(schema, rows, Validity.NonNullable, [column]);
            using RecordBatch record = new RecordBatch(arena, root, start);
            await writer.WriteAsync(record, CancellationToken.None).ConfigureAwait(false);
        }

        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static int Longs(CanonicalArena arena, DType dtype, int count, int start = 0)
    {
        VortexBuffer buffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < count; i++)
        {
            values[i] = start + i;
        }

        return arena.AddPrimitive(dtype, count, Validity.NonNullable, PType.I64, buffer);
    }

}
