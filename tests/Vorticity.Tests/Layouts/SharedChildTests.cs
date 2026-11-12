using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Layouts;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Layouts;

/// <summary>
/// A dictionary of many values in a chunk read in windows decodes its values once for the chunk,
/// lent to every window that reads them, whatever runs the windows: once more for a context that
/// reaches the chunk while another decodes it, which decodes it too rather than hold its thread,
/// and never once a window.
/// </summary>
/// <remarks>
/// In the collection that runs alone, because the count it reads is the process's.
/// </remarks>
[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed class SharedChildTests
{
    private const int Rows = 300_000;

    /// <summary>Distinct tags: above what a window of the chunk holds rows of, so a copy per window would show.</summary>
    private const int Tags = 10_000;

    /// <summary>The read-ahead and the degree: one lane, a lane decoding ahead, four lanes.</summary>
    public static TheoryData<int, int> Lanes => new TheoryData<int, int>
    {
        { 0, 1 },
        { 2, 1 },
        { 0, 4 },
    };

    [Theory]
    [MemberData(nameof(Lanes))]
    public async Task ADictionarysValuesAreDecodedOnceForEveryWindowOfTheChunk(int prefetch, int degree)
    {
        // Twelve windows, more than the contexts that can reach the chunk at once, so that a copy per window would show.
        const int windowed = 12 * FlatLayoutReader.WindowRows;
        string path = await WriteAsync(rows: windowed);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, TestContext.Current.CancellationToken);
            Scan<Tagged> scan = file.Scan<Tagged>().With(new ScanOptions { Prefetch = prefetch, DegreeOfParallelism = degree });
            long before = ArrayDecodeContext.SharedChildrenDecoded;
            long seen = 0;
            Tagged[] buffer = new Tagged[FlatLayoutReader.WindowRows];
            await foreach (Columns<Tagged> columns in scan.WithCancellation(TestContext.Current.CancellationToken))
            {
                Span<Tagged> rows = buffer.AsSpan(0, columns.RowCount);
                Tagged.ReadRows(columns, rows);
                for (int i = 0; i < rows.Length; i++)
                {
                    Assert.Equal(Row(checked((int)(seen + i))), rows[i]);
                }

                seen += rows.Length;
            }

            Assert.Equal(windowed, seen);
            Assert.Equal(2L * windowed, scan.Counters.ValuesDecoded);

            // Once on one lane; past that, at most once a context the scan decodes on, those that reached the chunk while
            // another decoded it, every later window borrowing the copy published.
            Assert.InRange(ArrayDecodeContext.SharedChildrenDecoded - before, 1, prefetch + degree);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// A hundred spans of the chunk read together, each its own batch, as a lookup reads the rows its units hold: the
    /// dictionary's values decoded once for them all and lent to each, where each span decoded them again and kept them, a
    /// user's rows over a week of S2's logs past 16 GB.
    /// </summary>
    [Fact]
    public async Task ADictionarysValuesAreDecodedOnceForTheSpansOfARead()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = await WriteAsync(contents: true);
        try
        {
            byte[] bytes = await System.IO.File.ReadAllBytesAsync(path, ct);
            Vorticity.Types.DType schema;
            await using (VortexFile file = await VortexFile.OpenAsync(path, ct))
            {
                schema = file.DType;
            }

            global::Vorticity.Indexes.SpanReader reader = (await global::Vorticity.Indexes.SpanReader.OpenAsync(new global::Vorticity.Tests.File.TestSegmentSource(bytes), bytes.Length, schema, null, ct))!;
            RowRange[] spans = [.. Enumerable.Range(0, 100).Select(static s => RowRange.FromLength(s * 2_999L, 100))];
            long before = ArrayDecodeContext.SharedChildrenDecoded;
            RecordBatch[] batches = await reader.ReadSpansAsync(spans, null, ct);
            try
            {
                for (int s = 0; s < spans.Length; s++)
                {
                    Tagged[] rows = new Tagged[batches[s].RowCount];
                    Tagged.ReadRows(batches[s].As<Tagged>(), rows);
                    Assert.Equal(Enumerable.Range((int)spans[s].Start, 100).Select(Row), rows);
                }
            }
            finally
            {
                foreach (RecordBatch batch in batches)
                {
                    batch.Dispose();
                }
            }

            Assert.Equal(1, ArrayDecodeContext.SharedChildrenDecoded - before);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static Tagged Row(int row) =>
        new Tagged(row, "tag-" + (row * 7_919 % Tags).ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static async Task<string> WriteAsync(bool contents = false, int rows = Rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "windows");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"tagged-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");

        // One chunk for the whole column, several windows long, written as a dictionary.
        VortexWriteOptions options = new VortexWriteOptions
        {
            DataBlockTargetBytes = 64 << 20,
            CarriesContents = contents,
            Hints = ImmutableDictionary<string, EncodingHint>.Empty.Add("Tag", EncodingHint.Dictionary),
        };
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Tagged>(path, options);
        Tagged[] block = new Tagged[writer.BlockRows];
        for (int start = 0; start < rows; start += writer.BlockRows)
        {
            int count = Math.Min(writer.BlockRows, rows - start);
            for (int i = 0; i < count; i++)
            {
                block[i] = Row(start + i);
            }

            await writer.WriteAsync<Tagged>(block.AsSpan(0, count), CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
        return path;
    }

    /// <summary>One row: a position and one of <see cref="Tags"/> tags, written by hand as the generator would.</summary>
    internal readonly record struct Tagged(long Id, string Tag) : IVortexRecord<Tagged>
    {
        public static VortexSchema Schema { get; } =
        [
            ("Id", VortexType.Int64),
            ("Tag", VortexType.Utf8),
        ];

        public static void ReadRows(Columns<Tagged> columns, Span<Tagged> rows)
        {
            ReadOnlySpan<long> ids = columns.Column<long>(0).Values;
            Column<string> tags = columns.Column<string>(1);
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new Tagged(ids[i], tags.GetString(i)!);
            }
        }

        public static void WriteRows(ColumnsBuilder<Tagged> builder, ReadOnlySpan<Tagged> rows)
        {
            ColumnBuilder<long> ids = builder.Column<long>(0);
            ColumnBuilder<string> tags = builder.Column<string>(1);
            for (int i = 0; i < rows.Length; i++)
            {
                ids.Append(rows[i].Id);
                tags.Append(rows[i].Tag.AsSpan());
            }
        }
    }
}
