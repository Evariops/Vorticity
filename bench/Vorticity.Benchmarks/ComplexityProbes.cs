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
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scan;
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

    private string _path = string.Empty;
    private Dictionary<int, FilterLiteral[]> _needles = [];
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
        return await file.Scan()
            .WithPruning(false)
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
