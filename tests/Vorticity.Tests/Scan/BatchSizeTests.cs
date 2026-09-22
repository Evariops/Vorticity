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
/// The size of a batch: by what the scan reads when it only reads, by the zone when it filters,
/// never above a cap; and the budget and the estimate that decide it.
/// </summary>
public sealed class BatchSizeTests
{
    private const int Zone = 8192;

    /// <summary>One chunk of thirty-two zones of one i64 column.</summary>
    private const int Rows = 32 * Zone;

    [Fact]
    public async Task AScanThatOnlyReadsIsBatchedByWhatItReads()
    {
        string path = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
            long expected = BatchBudget.Rows(file.DType, FieldMask.All, Zone, FlatLayoutReader.WindowRows, BatchBudget.Bytes);

            List<int> plain = await SizesAsync(file.ScanBuilder());
            List<int> raised = await SizesAsync(file.ScanBuilder().WithMaxBatchRows(int.MaxValue));
            List<int> capped = await SizesAsync(file.ScanBuilder().WithMaxBatchRows(Zone));

            Assert.True(expected > Zone, "a single i64 column fits more than a zone in any budget");
            Assert.Equal(Math.Min(expected, Rows), plain[0]);
            Assert.All(plain, size => Assert.True(size <= expected));
            Assert.Equal(plain, raised);
            Assert.All(capped, size => Assert.True(size <= Zone));
            Assert.Equal(Rows, Sum(plain));
            Assert.Equal(Rows, Sum(capped));
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
            VortexExpr all = Expr.Gt(Expr.Field("v"), Expr.Literal(FilterLiteral.From(-1L)));

            List<int> sizes = await SizesAsync(file.ScanBuilder().Where(all));

            Assert.All(sizes, size => Assert.True(size <= Zone));
            Assert.Equal(Rows, Sum(sizes));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void RowsDoubleTheZoneWhileABatchFitsTheBudgetAndAWindow()
    {
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(["v"], [types.Primitive(PType.I64, Nullability.NonNullable)], Nullability.NonNullable);

        Assert.Equal(Zone, BatchBudget.Rows(schema, FieldMask.All, Zone, 1 << 17, budget: 8 * Zone));
        Assert.Equal(2 * Zone, BatchBudget.Rows(schema, FieldMask.All, Zone, 1 << 17, budget: (16 * Zone) + 7));
        Assert.Equal(4 * Zone, BatchBudget.Rows(schema, FieldMask.All, Zone, 1 << 17, budget: 32 * Zone));
        Assert.Equal(1 << 17, BatchBudget.Rows(schema, FieldMask.All, Zone, 1 << 17, budget: 1L << 40));
        Assert.Equal(Zone, BatchBudget.Rows(schema, FieldMask.All, Zone, 1 << 17, budget: 1));
        Assert.Equal(3 * Zone, BatchBudget.Rows(schema, FieldMask.All, 3 * Zone, 5 * Zone, budget: 1L << 40));
    }

    [Fact]
    public void TheEstimateReadsTheProjectedDTypes()
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType i32 = types.Primitive(PType.I32, Nullability.Nullable);
        DType text = types.Utf8(Nullability.NonNullable);
        DType flag = types.Bool(Nullability.NonNullable);
        DType schema = types.Struct(["a", "b", "c", "d"], [i64, i32, text, flag], Nullability.NonNullable);

        Assert.Equal(64, BatchBudget.BitsPerRow(i64, FieldMask.All));
        Assert.Equal(33, BatchBudget.BitsPerRow(i32, FieldMask.All));
        Assert.Equal(256, BatchBudget.BitsPerRow(text, FieldMask.All));
        Assert.Equal(1, BatchBudget.BitsPerRow(flag, FieldMask.All));
        Assert.Equal(64 + 33 + 256 + 1, BatchBudget.BitsPerRow(schema, FieldMask.All));
        Assert.Equal(64, BatchBudget.BitsPerRow(schema, new FieldMaskBuilder().IncludeField(0).Build()));
        Assert.Equal(33 + 1, BatchBudget.BitsPerRow(schema, new FieldMaskBuilder().IncludeField(1).IncludeField(3).Build()));
        Assert.Equal(0, BatchBudget.BitsPerRow(schema, FieldMask.Empty));
    }

    [Fact]
    public void ALinuxCacheDirectoryGivesItsSecondLevelAndItsSharers()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vorticity-cache-{Guid.NewGuid():N}");
        try
        {
            Index(root, 0, level: "1", type: "Data", size: "48K", shared: "0-1");
            Index(root, 1, level: "1", type: "Instruction", size: "32K", shared: "0-1");
            Index(root, 2, level: "2", type: "Unified", size: "2048K", shared: "0-1");
            Index(root, 3, level: "3", type: "Unified", size: "36M", shared: "0-23");

            Assert.Equal((2L << 20, 2L), BatchBudget.Linux(root));
            Assert.Equal(default, BatchBudget.Linux(Path.Combine(root, "absent")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("2048K", 2L << 20)]
    [InlineData("1M", 1L << 20)]
    [InlineData("512", 512L)]
    [InlineData("big", 0L)]
    [InlineData("", 0L)]
    public void ASysfsSizeIsReadWithItsUnit(string text, long bytes) => Assert.Equal(bytes, BatchBudget.Size(text));

    [Theory]
    [InlineData("0", 1L)]
    [InlineData("0-3", 4L)]
    [InlineData("0-3,8-11", 8L)]
    [InlineData("0,64", 2L)]
    [InlineData("3-1", 0L)]
    [InlineData("x", 0L)]
    public void ASysfsCpuListIsCounted(string list, long cpus) => Assert.Equal(cpus, BatchBudget.CpuCount(list));

    [Fact]
    public void TheSystemAnswersOnMacOS()
    {
        Assert.True(BatchBudget.Bytes > 0);
        if (OperatingSystem.IsMacOS())
        {
            (long size, long sharers) = BatchBudget.Darwin();
            Assert.True(size > 0, "sysctl should report an L2 on a Mac");
            Assert.True(sharers > 0, "sysctl should report the cores that share it");
            Assert.Equal(size / sharers / 2, BatchBudget.Bytes);
        }
    }

    private static void Index(string root, int index, string level, string type, string size, string shared)
    {
        string directory = Path.Combine(root, $"index{index}");
        Directory.CreateDirectory(directory);
        System.IO.File.WriteAllText(Path.Combine(directory, "level"), level + "\n");
        System.IO.File.WriteAllText(Path.Combine(directory, "type"), type + "\n");
        System.IO.File.WriteAllText(Path.Combine(directory, "size"), size + "\n");
        System.IO.File.WriteAllText(Path.Combine(directory, "shared_cpu_list"), shared + "\n");
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
