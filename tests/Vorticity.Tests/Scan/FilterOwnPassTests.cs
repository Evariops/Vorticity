using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Scan;

/// <summary>
/// A filter the zone maps cannot answer is read in a pass of its own, then the projection over the
/// rows it kept: a split it keeps nothing of decodes no other column, one it keeps a few rows of
/// decodes those rows alone, and one it keeps most of is read whole and gathered.
/// </summary>
public sealed class FilterOwnPassTests
{
    private const int Rows = 12 * 8_192;

    private static readonly string[] Names = ["c0", "c1", "c2", "c3"];

    public static TheoryData<string, string> Cases
    {
        get
        {
            // Each share twice: a comparison, which the column's encoding may answer, and the same
            // comparison and a bound every row meets, which no encoding is asked and the evaluator
            // answers over the filter's column.
            TheoryData<string, string> cases = new TheoryData<string, string>();
            foreach (string filter in new[]
            {
                "none", "sparse", "quarter", "half", "every", "none&", "sparse&", "quarter&", "half&", "every&",
            })
            {
                foreach (string projection in new[] { "", "c0,c2", "c1", "c1,c3" })
                {
                    cases.Add(filter, projection);
                }
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryColumnAskedHoldsTheRowsTheFilterKeeps(string filter, string projection)
    {
        Decoders.EnsureRegistered();
        string path = Write();
        try
        {
            string[] columns = projection.Length == 0 ? Names : projection.Split(',');
            List<long> got = [];
            await using (VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None))
            {
                ScanBuilder scan = file.ScanBuilder().Where(Filter(filter));
                if (projection.Length != 0)
                {
                    scan = scan.Project(columns);
                }

                await foreach (RecordBatch batch in scan.ExecuteAsync().WithCancellation(CancellationToken.None))
                {
                    for (int row = 0; row < batch.RowCount; row++)
                    {
                        foreach (string column in columns)
                        {
                            got.Add(batch.Column(Encoding.UTF8.GetBytes(column)).AsPrimitive<long>().Values[row]);
                        }
                    }
                }
            }

            List<long> expected = [];
            for (long row = 0; row < Rows; row++)
            {
                if (Keeps(filter, Value(1, row)))
                {
                    foreach (string column in columns)
                    {
                        expected.Add(Value(column[1] - '0', row));
                    }
                }
            }

            Assert.Equal(expected.Count, got.Count);
            Assert.Equal(expected, got);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("none&")]
    public async Task ASplitTheFilterKeepsNothingOfDecodesOnlyTheFiltersColumn(string filter)
    {
        Decoders.EnsureRegistered();
        string path = Write();
        try
        {
            ScanCounters metrics = new ScanCounters();
            long rows = 0;
            await using (VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None))
            {
                await foreach (RecordBatch batch in file.ScanBuilder().Where(Filter(filter)).WithMetrics(metrics).ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    rows += batch.RowCount;
                }
            }

            // The filter's column, every row of it, and the zone map it is pruned with, a row a
            // zone: nothing of the three columns the filter keeps no row of.
            Assert.Equal(0, rows);
            Assert.Equal(Rows + (Rows / 8_192), metrics.ValuesDecoded);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// On four lanes, a filter the zone maps cannot answer keeps what one lane keeps and decodes what
    /// one lane decodes: the filter's column once for its window, whatever the lanes' first splits
    /// ask of its encoding, and the projection a zone at a time on each lane.
    /// </summary>
    [Theory]
    [InlineData("half")]
    [InlineData("sparse")]
    [InlineData("none")]
    public async Task OnLanesAFilterKeepsAndDecodesWhatOneLaneDoes(string filter)
    {
        Decoders.EnsureRegistered();
        string path = Write();
        try
        {
            (List<long> oneLane, long oneLaneDecoded) = await ReadAsync(path, filter, 1);
            (List<long> fourLanes, long fourLanesDecoded) = await ReadAsync(path, filter, 4);

            Assert.Equal(oneLane, fourLanes);
            Assert.Equal(oneLaneDecoded, fourLanesDecoded);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ARunKeptOfEveryZoneReadsEachChunkOnceAndSlicesIt()
    {
        Decoders.EnsureRegistered();
        string path = Write();
        try
        {
            // The first 2 000 rows of every zone: too few of a split to read the projection whole,
            // but every row of what they span, in a chunk of twelve batches that no window smaller
            // than the chunk cuts.
            ScanCounters metrics = new ScanCounters();
            List<long> got = [];
            await using (VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None))
            {
                VortexExpr run = Expr.Lt(Expr.Field("c3"), Expr.Literal(FilterLiteral.From(2_000L)));
                await foreach (RecordBatch batch in file.ScanBuilder().Where(run).Project("c0", "c1", "c2").WithMetrics(metrics).ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    for (int row = 0; row < batch.RowCount; row++)
                    {
                        for (int column = 0; column < 3; column++)
                        {
                            got.Add(batch.Column(Encoding.UTF8.GetBytes(Names[column])).AsPrimitive<long>().Values[row]);
                        }
                    }
                }
            }

            List<long> expected = [];
            for (long row = 0; row < Rows; row++)
            {
                if (Value(3, row) < 2_000)
                {
                    for (int column = 0; column < 3; column++)
                    {
                        expected.Add(Value(column, row));
                    }
                }
            }

            Assert.Equal(expected, got);

            // The filter's column once, for its own pass, and its zone map; then each projected
            // column's chunk once, whole, every batch slicing its run out of it.
            Assert.Equal(Rows + (Rows / 8_192) + (3L * Rows), metrics.ValuesDecoded);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void AScanReadsWholeOnceMostRowsAreKeptAndGoesBackOnlyPastAMargin()
    {
        FilterEvaluator evaluator = new FilterEvaluator(Filter("half"));

        // A fifth kept, split after split: the kept rows alone.
        for (int split = 0; split < 20; split++)
        {
            Assert.False(evaluator.KeepsMost(200, 1_000));
        }

        // Two fifths from here: read whole once the smoothed share is past a third.
        bool whole = false;
        int splits = 0;
        while (!whole && splits < 50)
        {
            whole = evaluator.KeepsMost(400, 1_000);
            splits++;
        }

        Assert.True(whole, "two fifths kept should end up read whole");
        Assert.True(splits > 1, "one split should not turn the scan around");

        // Three tenths, around the crossing: the choice holds either way of it.
        for (int split = 0; split < 40; split++)
        {
            Assert.True(evaluator.KeepsMost(300, 1_000));
        }

        // A tenth: back to the kept rows alone once the share is under the lower margin.
        bool alone = false;
        for (int split = 0; split < 50 && !alone; split++)
        {
            alone = !evaluator.KeepsMost(100, 1_000);
        }

        Assert.True(alone, "a tenth kept should end up read over the kept rows");
        for (int split = 0; split < 40; split++)
        {
            Assert.False(evaluator.KeepsMost(300, 1_000));
        }
    }

    /// <summary>
    /// The values: c1 even and spread over [0, 4096) in every zone, so no zone map settles a band of
    /// it; c3 the row's place in its zone, so a band of it keeps a run of every zone.
    /// </summary>
    private static long Value(int column, long row) => column switch
    {
        0 => row,
        1 => (long)((((ulong)row * 2_654_435_761UL) >> 12) & 0xFFE),
        2 => row * 3,
        _ => row % 8_192,
    };

    private static VortexExpr Filter(string name)
    {
        FieldExpr c1 = Expr.Field("c1");
        VortexExpr comparison = name.TrimEnd('&') switch
        {
            "none" => Expr.Eq(c1, Expr.Literal(FilterLiteral.From(7L))),
            "sparse" => Expr.Lt(c1, Expr.Literal(FilterLiteral.From(40L))),
            "quarter" => Expr.Lt(c1, Expr.Literal(FilterLiteral.From(1_000L))),
            "half" => Expr.Lt(c1, Expr.Literal(FilterLiteral.From(2_000L))),
            _ => Expr.Ne(c1, Expr.Literal(FilterLiteral.From(7L))),
        };

        return name.EndsWith('&')
            ? Expr.And(comparison, Expr.Ge(c1, Expr.Literal(FilterLiteral.From(0L))))
            : comparison;
    }

    private static bool Keeps(string name, long c1) => name.TrimEnd('&') switch
    {
        "none" => c1 == 7,
        "sparse" => c1 < 40,
        "quarter" => c1 < 1_000,
        "half" => c1 < 2_000,
        _ => c1 != 7,
    };

    /// <summary>Every column of every row the filter keeps, at a degree, and the values the scan decoded.</summary>
    private static async Task<(List<long> Values, long Decoded)> ReadAsync(string path, string filter, int degree)
    {
        ScanCounters metrics = new ScanCounters();
        List<long> values = [];
        await using (VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None))
        {
            await foreach (RecordBatch batch in file.ScanBuilder().Where(Filter(filter)).WithDegreeOfParallelism(degree)
                .WithMetrics(metrics).ExecuteAsync().WithCancellation(CancellationToken.None))
            {
                for (int row = 0; row < batch.RowCount; row++)
                {
                    foreach (string column in Names)
                    {
                        values.Add(batch.Column(Encoding.UTF8.GetBytes(column)).AsPrimitive<long>().Values[row]);
                    }
                }
            }
        }

        return (values, metrics.ValuesDecoded);
    }

    private static string Write()
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(Names, [i64, i64, i64, i64], Nullability.NonNullable);

        int[] columns = new int[Names.Length];
        for (int c = 0; c < columns.Length; c++)
        {
            VortexBuffer values = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> destination);
            Span<long> longs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(destination);
            for (int row = 0; row < Rows; row++)
            {
                longs[row] = Value(c, row);
            }

            columns[c] = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, values);
        }

        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-ownpass-{Guid.NewGuid():N}.vortex");
        WriteAsync(path, schema, arena, arena.AddStruct(schema, Rows, Validity.NonNullable, columns)).GetAwaiter().GetResult();
        return path;
    }

    private static async Task WriteAsync(string path, DType schema, CanonicalArena arena, int root)
    {
        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, new VortexWriteOptions());
        using (RecordBatch batch = new RecordBatch(arena, root, 0))
        {
            await writer.WriteAsync(batch, CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
    }
}
