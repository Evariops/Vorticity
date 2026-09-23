using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Scan;

/// <summary>
/// The size of a batch: a window of whole zones when the scan only reads, the zone when it
/// filters, a window again over zones its zone maps prove whole, never above a cap.
/// </summary>
public sealed class BatchSizeTests
{
    private const int Zone = 8192;

    /// <summary>One chunk of thirty-two zones of one i64 column: two windows.</summary>
    private const int Rows = 32 * Zone;

    [Fact]
    public async Task AScanThatOnlyReadsIsBatchedByTheWindow()
    {
        string path = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);

            List<int> plain = await SizesAsync(file.ScanBuilder());
            List<int> raised = await SizesAsync(file.ScanBuilder().WithMaxBatchRows(int.MaxValue));
            List<int> capped = await SizesAsync(file.ScanBuilder().WithMaxBatchRows(Zone));

            Assert.Equal([FlatLayoutReader.WindowRows, FlatLayoutReader.WindowRows], plain);
            Assert.Equal(plain, raised);
            Assert.Equal(Rows / Zone, capped.Count);
            Assert.All(capped, size => Assert.Equal(Zone, size));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData((4 * Zone) + 5, 4 * Zone)]
    [InlineData(Zone, Zone)]
    [InlineData(100, Zone)]
    public async Task AWindowHoldsWholeZonesAndAtLeastOne(int windowRows, int batchRows)
    {
        string path = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);

            List<int> sizes = await SizesAsync(file.ScanBuilder().WithWindowRows(windowRows));

            Assert.All(sizes, size => Assert.Equal(batchRows, size));
            Assert.Equal(Rows, Sum(sizes));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AFilteredScanIsBatchedByTheZone()
    {
        string path = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);

            // Every zone holds values on both sides of the literal, so none is proven or pruned.
            VortexExpr half = Expr.Gt(Expr.Field("v"), Expr.Literal(FilterLiteral.From(50_000L)));

            List<int> sizes = await SizesAsync(file.ScanBuilder().Where(half));

            Assert.Equal(Rows / Zone, sizes.Count);
            Assert.All(sizes, size => Assert.True(size <= Zone));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AFilteredScanReadsZonesItsZoneMapsProveWholeByTheWindow()
    {
        string path = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
            VortexExpr all = Expr.Gt(Expr.Field("v"), Expr.Literal(FilterLiteral.From(-1L)));

            List<int> sizes = await SizesAsync(file.ScanBuilder().Where(all));
            List<int> capped = await SizesAsync(file.ScanBuilder().Where(all).WithMaxBatchRows(Zone));

            Assert.Equal([FlatLayoutReader.WindowRows, FlatLayoutReader.WindowRows], sizes);
            Assert.Equal(Rows / Zone, capped.Count);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static int Sum(List<int> sizes)
    {
        int total = 0;
        foreach (int size in sizes)
        {
            total += size;
        }

        return total;
    }

    private static async Task<List<int>> SizesAsync(ScanBuilder builder)
    {
        List<int> sizes = [];
        await foreach (RecordBatch batch in builder.ExecuteAsync().WithCancellation(CancellationToken.None))
        {
            sizes.Add(batch.RowCount);
        }

        return sizes;
    }

    /// <summary>One i64 column of <see cref="Rows"/> rows in one chunk, zoned every <see cref="Zone"/> rows.</summary>
    private static async Task<string> WriteAsync()
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(["v"], [i64], Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer values = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> destination);
        Span<long> longs = MemoryMarshal.Cast<byte, long>(destination);
        for (int i = 0; i < Rows; i++)
        {
            longs[i] = (i * 7919L) % 100_003;
        }

        int column = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, values);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [column]);
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-batchsize-{Guid.NewGuid():N}.vortex");
        VortexWriteOptions options = new VortexWriteOptions { RowBlockSize = Zone, DataBlockTargetBytes = 1L << 30 };
        await using (VortexFileWriter writer = VortexFileWriter.Create(path, schema, options))
        {
            using RecordBatch batch = new RecordBatch(arena, root, 0);
            await writer.WriteAsync(batch, CancellationToken.None);
            await writer.CompleteAsync(CancellationToken.None);
        }

        return path;
    }
}
