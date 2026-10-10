using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Layouts;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Layouts;

/// <summary>
/// A chunk larger than a window is read a window at a time, row for row what a whole decode
/// gives, with every value decoded once: a progression, packed integers, a dictionary of few
/// strings, bits with nulls and a dictionary of many doubles whose values every window borrows,
/// side by side in one table; and a column of runs kept encoded, whose windows start inside a run.
/// </summary>
public sealed class WindowedChunkTests
{
    private const int Rows = 300_000;

    private static readonly string[] Labels = ["alpha", "beta", "gamma", "delta", "epsilon"];

    public static TheoryData<int> Prefetch => [0, 2];

    [Theory]
    [MemberData(nameof(Prefetch))]
    public async Task AChunkLargerThanAWindowReadsBackRowForRowAndDecodesEachValueOnce(int prefetch)
    {
        Assert.True(Rows > 2 * FlatLayoutReader.WindowRows, "the table must span several windows");
        string path = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, TestContext.Current.CancellationToken);
            Scan<Wide> scan = file.Scan<Wide>().With(new ScanOptions { Prefetch = prefetch });
            long seen = 0;
            Wide[] buffer = new Wide[FlatLayoutReader.WindowRows];
            await foreach (Columns<Wide> columns in scan.WithCancellation(TestContext.Current.CancellationToken))
            {
                Span<Wide> rows = buffer.AsSpan(0, columns.RowCount);
                Wide.ReadRows(columns, rows);
                for (int i = 0; i < rows.Length; i++)
                {
                    Assert.Equal(Row(checked((int)(seen + i))), rows[i]);
                }

                seen += rows.Length;
            }

            Assert.Equal(Rows, seen);
            Assert.Equal(5L * Rows, scan.Counters.ValuesDecoded);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// A run-end column kept encoded for a typed scan, read a window at a time, with windows that
    /// start inside a run: each batch holds the runs its rows fall in, rebased to its first row,
    /// and every value is decoded once.
    /// </summary>
    [Fact]
    public async Task ARunEndChunkKeptEncodedReadsBackInWindowsRunForRun()
    {
        Assert.True(FlatLayoutReader.WindowRows % RunLength != 0, "a window must start inside a run");
        string path = await WriteRunsAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, TestContext.Current.CancellationToken);
            Scan<Runs> scan = file.Scan<Runs>();
            long seen = 0;
            await foreach (Columns<Runs> columns in scan.WithCancellation(TestContext.Current.CancellationToken))
            {
                Column<int> day = columns.Column<int>(0);
                Assert.Equal(ColumnEncoding.RunEnd, day.Encoding);
                RunEndView<int> runs = day.AsRunEnd();
                ReadOnlySpan<int> values = runs.Values.Values;
                int row = 0;
                for (int run = 0; run < runs.RunCount; run++)
                {
                    for (; row < runs.RunEnds[run]; row++)
                    {
                        Assert.Equal(Day(checked((int)(seen + row))), values[run]);
                    }
                }

                Assert.Equal(columns.RowCount, row);
                seen += row;
            }

            Assert.Equal(Rows, seen);
            Assert.Equal(Rows, scan.Counters.ValuesDecoded);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// An encoding overrides the range decode and the question about it together: one without the
    /// other is a window never taken, or a decode that throws where the reader was told it would not.
    /// </summary>
    [Fact]
    public void EveryEncodingThatDecodesARangeSaysSo()
    {
        Vorticity.Tests.Scan.Decoders.EnsureRegistered();
        System.Text.StringBuilder wrong = new System.Text.StringBuilder();
        int ranged = 0;
        foreach (ArrayEncodingId id in Enum.GetValues<ArrayEncodingId>())
        {
            if (id == ArrayEncodingId.Unknown || !ArrayDecoderTable.IsImplemented(id))
            {
                continue;
            }

            ArrayDecoder decoder = ArrayDecoderTable.Get(id, id.ToString());
            bool decodes = Overrides(decoder, nameof(ArrayDecoder.DecodeRange));
            bool says = Overrides(decoder, nameof(ArrayDecoder.DecodesRange));
            if (decodes != says)
            {
                wrong.Append(id.ToString()).Append(": overrides DecodeRange = ").Append(decodes)
                    .Append(", DecodesRange = ").Append(says).Append('\n');
            }

            if (decodes)
            {
                ranged++;
            }
        }

        Assert.Equal(string.Empty, wrong.ToString());
        Assert.True(ranged >= 14, $"only {ranged} encodings decode a range");
    }

    private static bool Overrides(ArrayDecoder decoder, string method)
    {
        System.Reflection.MethodInfo? found = decoder.GetType().GetMethod(
            method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        return found is not null && found.DeclaringType != typeof(ArrayDecoder);
    }

    /// <summary>Row <paramref name="row"/> of the table.</summary>
    private static Wide Row(int row) => new Wide(
        1_000 + row,
        (int)(((uint)row * 2_654_435_761u) >> 22),
        Labels[row % Labels.Length],
        row % 97 == 0 ? null : (row & 1) == 0,
        (row * 7_919L % 100_003) / 7.0);

    private static async Task<string> WriteAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "windows");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"wide-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");

        // A block target far above the table, so that a column's first chunk spans several windows.
        VortexWriteOptions options = new VortexWriteOptions { DataBlockTargetBytes = 64 << 20 };
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Wide>(path, options);
        Wide[] block = new Wide[writer.BlockRows];
        for (int start = 0; start < Rows; start += writer.BlockRows)
        {
            int count = Math.Min(writer.BlockRows, Rows - start);
            for (int i = 0; i < count; i++)
            {
                block[i] = Row(start + i);
            }

            await writer.WriteAsync<Wide>(block.AsSpan(0, count), CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
        return path;
    }

    /// <summary>Rows per run of the run-end table.</summary>
    private const int RunLength = 100;

    /// <summary>Row <paramref name="row"/>'s value in the run-end table: one value per run, each run's differing from its neighbours'.</summary>
    private static int Day(int row) => (int)((row / RunLength) * 2_654_435_761u >> 20);

    private static async Task<string> WriteRunsAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "windows");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"runs-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        VortexWriteOptions options = new VortexWriteOptions { DataBlockTargetBytes = 64 << 20 };
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Runs>(path, options);
        Runs[] block = new Runs[writer.BlockRows];
        for (int start = 0; start < Rows; start += writer.BlockRows)
        {
            int count = Math.Min(writer.BlockRows, Rows - start);
            for (int i = 0; i < count; i++)
            {
                block[i] = new Runs(Day(start + i));
            }

            await writer.WriteAsync<Runs>(block.AsSpan(0, count), CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
        return path;
    }

    /// <summary>One row of the run-end table.</summary>
    internal readonly record struct Runs(int Day) : IVortexRecord<Runs>
    {
        public static VortexSchema Schema { get; } = [("Day", VortexType.Int32)];

        public static void ReadRows(Columns<Runs> columns, Span<Runs> rows)
        {
            ReadOnlySpan<int> day = columns.Column<int>(0).Values;
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new Runs(day[i]);
            }
        }

        public static void WriteRows(ColumnsBuilder<Runs> builder, ReadOnlySpan<Runs> rows)
        {
            ColumnBuilder<int> day = builder.Column<int>(0);
            for (int i = 0; i < rows.Length; i++)
            {
                day.Append(rows[i].Day);
            }
        }
    }

    /// <summary>One row of the table, written by hand the way the generator would.</summary>
    internal readonly record struct Wide(long Sequence, int Packed, string Label, bool? Flag, double Measure)
        : IVortexRecord<Wide>
    {
        public static VortexSchema Schema { get; } =
        [
            ("Sequence", VortexType.Int64),
            ("Packed", VortexType.Int32),
            ("Label", VortexType.Utf8),
            ("Flag", VortexType.Bool.Nullable),
            ("Measure", VortexType.Float64),
        ];

        public static void ReadRows(Columns<Wide> columns, Span<Wide> rows)
        {
            ReadOnlySpan<long> sequence = columns.Column<long>(0).Values;
            ReadOnlySpan<int> packed = columns.Column<int>(1).Values;
            Column<string> label = columns.Column<string>(2);
            Column<bool?> flag = columns.Column<bool?>(3);
            ReadOnlySpan<double> measure = columns.Column<double>(4).Values;
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new Wide(sequence[i], packed[i], label.GetString(i)!, flag[i], measure[i]);
            }
        }

        public static void WriteRows(ColumnsBuilder<Wide> builder, ReadOnlySpan<Wide> rows)
        {
            ColumnBuilder<long> sequence = builder.Column<long>(0);
            ColumnBuilder<int> packed = builder.Column<int>(1);
            ColumnBuilder<string> label = builder.Column<string>(2);
            ColumnBuilder<bool?> flag = builder.Column<bool?>(3);
            ColumnBuilder<double> measure = builder.Column<double>(4);
            for (int i = 0; i < rows.Length; i++)
            {
                sequence.Append(rows[i].Sequence);
                packed.Append(rows[i].Packed);
                label.Append(rows[i].Label.AsSpan());
                flag.Append(rows[i].Flag);
                measure.Append(rows[i].Measure);
            }
        }
    }
}
