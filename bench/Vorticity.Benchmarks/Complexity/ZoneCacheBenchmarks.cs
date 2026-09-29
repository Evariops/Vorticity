// What keeping and finding a file's decoded zone maps costs, against the zoned columns.
//
// A file of `Columns` Int64 columns is opened from memory, a zone map is kept for as many layout
// nodes as it has columns, one after the other as the scans of a wide file's columns would keep
// them, and every one is then found twice. `Open` opens the file and nothing else, the part of
// `Keep` that is not the cache. Run in a checkout of the original and in the tree.
using System;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A file's zone map cache filled and consulted, against the columns kept.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class ZoneCacheBenchmarks
{
    /// <summary>Rows of the file.</summary>
    private const int Rows = 1_024;

    /// <summary>Columns of the file, and zone maps kept.</summary>
    [Params(1, 64, 1_024)]
    public int Columns { get; set; }

    private byte[] _bytes = [];
    private ZoneColumn _column = null!;

    /// <summary>Writes the file, and checks every map kept is found.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _bytes = WriteAsync().GetAwaiter().GetResult();
        _column = new ZoneColumn(Expr.Field("c0"), Rows, Rows, []);
        if (Keep() != 2L * Columns)
        {
            throw new InvalidOperationException("A kept zone map was not found.");
        }
    }

    /// <summary>The file opened, a zone map kept per column's node, and each found twice.</summary>
    [Benchmark]
    public long Keep() => KeepAsync().GetAwaiter().GetResult();

    /// <summary>The file opened, and nothing kept.</summary>
    [Benchmark(Baseline = true)]
    public long Open() => OpenAsync().GetAwaiter().GetResult();

    private async Task<long> KeepAsync()
    {
        long found = 0;
        await using VortexFile file = await VortexFile.OpenAsync(
            new MemorySegmentSource(_bytes), new VortexOpenOptions(), CancellationToken.None).ConfigureAwait(false);
        for (int node = 0; node < Columns; node++)
        {
            file.KeepZones(node, _column);
        }

        for (int round = 0; round < 2; round++)
        {
            for (int node = 0; node < Columns; node++)
            {
                found += file.DecodedZones(node) is null ? 0 : 1;
            }
        }

        return found;
    }

    private async Task<long> OpenAsync()
    {
        await using VortexFile file = await VortexFile.OpenAsync(
            new MemorySegmentSource(_bytes), new VortexOpenOptions(), CancellationToken.None).ConfigureAwait(false);
        return file.RowCount;
    }

    private async Task<byte[]> WriteAsync()
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        string[] names = new string[Columns];
        DType[] fields = new DType[Columns];
        for (int c = 0; c < Columns; c++)
        {
            names[c] = "c" + c.ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields[c] = i64;
        }

        DType schema = types.Struct(names, fields, Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();
        int[] columns = new int[Columns];
        for (int c = 0; c < Columns; c++)
        {
            VortexBuffer buffer = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
            Span<long> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes);
            for (int row = 0; row < Rows; row++)
            {
                values[row] = ((long)c * Rows) + row;
            }

            columns[c] = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, buffer);
        }

        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, columns);
        System.IO.MemoryStream written = new System.IO.MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(written), schema, new VortexWriteOptions()))
        {
            using (RecordBatch batch = new RecordBatch(arena, root, 0))
            {
                await writer.WriteAsync(batch).ConfigureAwait(false);
            }

            await writer.CompleteAsync().ConfigureAwait(false);
        }

        return written.ToArray();
    }
}
