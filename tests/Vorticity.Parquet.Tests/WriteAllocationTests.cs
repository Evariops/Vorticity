using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// The writer allocates per file, per column and per page, not per row: a file of four times the
/// rows costs its pages' statistics more, and a schema four times as wide a ceiling per column more.
/// </summary>
/// <remarks>
/// A page keeps its bounds and its place for the column and offset indexes, which the file writes
/// once its row groups are done: their few hundred bytes a page are the file's, as the footer is. A
/// column's dictionary table rents its buffers from the shared array pool, whose capacity follows
/// the machine's cores: the columns are measured PLAIN, the table's cost left out of the ceiling.
/// The writer rents its blocks from a pool of the test's own, graded as the shared one is: the
/// blocks a read of large files leaves parked in the shared pool, until a sweep frees them, keep
/// its classes from widening to a wide schema's demand, and the writer's blocks would be counted
/// allocated again for what the process did before.
/// </remarks>
[Collection(nameof(ParquetAllocationCollection))]
public sealed class WriteAllocationTests : IDisposable
{
    private const int BatchRows = 8_192;

    /// <summary>The most a page of a column may cost, its bounds and its place kept for the indexes: measured 251 bytes on 2026-10-10.</summary>
    private const long PageCeiling = 288;

    /// <summary>The most a column may cost beyond the first, its writer's state and its chunk's metadata: measured 6 606 bytes on 2026-10-10.</summary>
    private const long ColumnCeiling = 7_168;

    private readonly string _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vx-write-alloc-{Guid.NewGuid():N}.parquet");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => System.IO.File.Delete(_path);

    [Fact]
    public async Task FourTimesTheRowsCostOnlyTheirPages()
    {
        Assert.SkipWhen(Debug, "Allocation figures are Release figures: run `dotnet test -c Release`.");
        await using VortexSession session = Isolated();
        (long few, long many) = await FloorsAsync(() => MeasureAsync(session, Mixed, 16), () => MeasureAsync(session, Mixed, 64));
        long pages = 48L * Mixed.Count;
        Assert.True(
            many - few <= pages * PageCeiling,
            string.Create(CultureInfo.InvariantCulture, $"48 batches more cost {many - few} bytes, {(many - few) / pages} a page, past {PageCeiling} ({many} against {few})"));
    }

    [Fact]
    public async Task FourTimesTheColumnsCostACeilingPerColumn()
    {
        Assert.SkipWhen(Debug, "Allocation figures are Release figures: run `dotnet test -c Release`.");
        await using VortexSession session = Isolated();
        (long narrow, long wide) = await FloorsAsync(() => MeasureAsync(session, Longs(50), 2), () => MeasureAsync(session, Longs(200), 2));
        Assert.True(
            wide - narrow <= 150 * ColumnCeiling,
            string.Create(CultureInfo.InvariantCulture, $"150 columns more cost {wide - narrow} bytes, {(wide - narrow) / 150} a column, past {ColumnCeiling} ({wide} against {narrow})"));
    }

    private static bool Debug => typeof(ParquetFile).Assembly.GetCustomAttribute<DebuggableAttribute>() is { IsJITOptimizerDisabled: true };

    /// <summary>A session over a pool of its own, graded as the shared pool is.</summary>
    private static VortexSession Isolated() =>
        VortexSession.Create(options => options.MemoryPool = new AlignedMemoryPool(AlignedBufferPool.Graded(32 * 1024 * 1024, 8, demandBudget: 256L * 1024 * 1024)));

    /// <summary>A schema of every kind of column the writer stages: integers, floats, text, bytes, booleans, nulls and lists.</summary>
    private static VortexSchema Mixed =>
    [
        ("id", VortexType.Int64),
        ("measure", VortexType.Float64),
        ("label", VortexType.Utf8),
        ("blob", VortexType.Binary),
        ("flag", VortexType.Bool),
        ("sparse", VortexType.Int32.Nullable),
        ("tags", VortexType.List(VortexType.Int32)),
    ];

    private static VortexSchema Longs(int columns) => [.. Enumerable.Range(0, columns).Select(c => ($"c{c}", VortexType.Int64))];

    /// <summary>The smallest of five measures of each, taken alternately once warm, so that the shared pools are in the same state for both.</summary>
    private static async Task<(long A, long B)> FloorsAsync(Func<Task<long>> a, Func<Task<long>> b)
    {
        for (int warm = 0; warm < 3; warm++)
        {
            await a();
            await b();
        }

        long floorA = long.MaxValue;
        long floorB = long.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            floorA = Math.Min(floorA, await a());
            floorB = Math.Min(floorB, await b());
        }

        return (floorA, floorB);
    }

    /// <summary>What writing <paramref name="batches"/> batches of <paramref name="schema"/> to a file allocates, on every thread, each column PLAIN.</summary>
    private async Task<long> MeasureAsync(VortexSession session, VortexSchema schema, int batches)
    {
        Dictionary<string, ParquetEncodingHint> hints = schema.Where(f => f.Type.Kind != VortexTypeKind.List).ToDictionary(f => f.Name, _ => ParquetEncodingHint.Plain);
        long before = GC.GetTotalAllocatedBytes(precise: true);
        await using (ParquetFileWriter writer = session.CreateParquetWriter(_path, schema, new ParquetWriteOptions { RowGroupRows = 64 * BatchRows, Hints = hints }))
        {
            ColumnsBuilder builder = writer.Builder();
            for (int batch = 0; batch < batches; batch++)
            {
                for (int row = 0; row < BatchRows; row++)
                {
                    Append(builder, schema, ((long)batch * BatchRows) + row);
                }

                await writer.WriteAsync(builder, Ct);
            }

            await writer.CompleteAsync(Ct);
        }

        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    private static void Append(ColumnsBuilder builder, VortexSchema schema, long row)
    {
        for (int c = 0; c < schema.Count; c++)
        {
            switch (schema[c].Name)
            {
                case "measure":
                    builder.Column<double>(c).Append(20.0 + (row % 977));
                    break;
                case "label":
                    builder.Column<string>(c).Append(Labels[row % Labels.Length]);
                    break;
                case "blob":
                    builder.Column<ReadOnlyMemory<byte>>(c).Append(Blobs[row % Blobs.Length]);
                    break;
                case "flag":
                    builder.Column<bool>(c).Append(row % 100 < 90);
                    break;
                case "sparse":
                    if (row % 5 == 0)
                    {
                        builder.Column<int?>(c).AppendNull();
                    }
                    else
                    {
                        builder.Column<int?>(c).Append((int)(row % 1_000));
                    }

                    break;
                case "tags":
                    ColumnBuilder<ReadOnlyMemory<int>> tags = builder.Column<ReadOnlyMemory<int>>(c);
                    tags.BeginList();
                    for (int t = 0; t < row % 3; t++)
                    {
                        tags.Elements.Append((int)row + t);
                    }

                    tags.EndList();
                    break;
                default:
                    builder.Column<long>(c).Append(row + c);
                    break;
            }
        }
    }

    private static readonly string[] Labels = [.. Enumerable.Range(0, 64).Select(i => $"label-{i:D3}")];

    private static readonly byte[][] Blobs = [.. Enumerable.Range(0, 32).Select(i => Enumerable.Range(0, i % 12).Select(b => (byte)(b * i)).ToArray())];
}
