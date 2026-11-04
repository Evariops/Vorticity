// A variant shredded into more columns than a slice of its rows holds is rebuilt a slice at a time. Read whole, in
// windows and taken, each row reads back what was written, in the reference's two shredded spellings and in the
// writer's, and each read says how many slices it rebuilt the rows in: a window is decoded as a range of its chunk, so
// a read in windows slices each window, where a read of the whole chunk slices the chunk.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Types.Variants;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed partial class ShreddedVariantSliceTests
{
    private const int Rows = 5_000;

    private static readonly string[] Routes = ["/a", "/api/v1/orders", "/api/v1/orders/{id}/lines", "/health"];

    /// <summary>A budget no slice's columns fit in: every slice is the least, a block of 1 024 rows.</summary>
    private static readonly VortexOpenOptions Sliced = new VortexOpenOptions { Read = new VortexReadOptions { VariantSliceBytes = 1 } };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The reference's three chunks of 8 192 rows, shredded as its tree and as the Parquet layout, read a block of rows at
    /// a time as their plain twin reads: eight slices for each of the first two chunks, the third of which is not shredded.
    /// The Parquet layout's typed columns hold plain binary columns, which a range of its rows reads a range of.
    /// </summary>
    [Theory]
    [InlineData("chunks.shredded.vortex")]
    [InlineData("chunks.parquet.vortex")]
    public async Task TheReferenceShreddingsReadAsTheirPlainTwinASliceAtATime(string name)
    {
        string directory = DataDirectory();
        List<(long Row, Variant? Value)> plain;
        await using (VortexFile file = await VortexFile.OpenAsync(Path.Combine(directory, "chunks.plain.vortex"), Ct))
        {
            (plain, long none) = await ReadAsync(file, id: -1, static scan => scan);
            Assert.Equal(0, none);
        }

        await using VortexFile shredded = await VortexFile.OpenAsync(Path.Combine(directory, name), Sliced, Ct);
        (List<(long Row, Variant? Value)> read, long slices) = await ReadAsync(shredded, id: -1, static scan => scan);
        AssertSame(plain, read);
        Assert.Equal(2 * 8, slices);
    }

    /// <summary>
    /// The writer's shredding of documents of every shape, one chunk of 5 000 rows beside an id column cut at its block of
    /// 4 096. Read whole, each of the two batches reads its rows as a range of the chunk, the first in four slices and the
    /// 904 of the second, no more than a slice, whole; decoded whole, the chunk would take five. In windows of 1 536 rows
    /// within those batches, two slices each but the last window's 1 024 and the second batch's 904. Taken, a row of three,
    /// the 1 366 rows of the first block taken in two slices of their own, the 301 of the second whole. Every row reads
    /// back what was written.
    /// </summary>
    [Fact]
    public async Task TheWritersShreddingReadsBackWholeInWindowsAndTakenASliceAtATime()
    {
        Random random = new Random(11);
        Doc[] docs = [.. Enumerable.Range(0, Rows).Select(i => new Doc(i, Document(random)))];
        string path = await WriteAsync(docs);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Sliced, Ct);
            List<(long Row, Variant? Value)> expected = [.. docs.Select(static doc => (doc.Id, (Variant?)doc.Attributes))];

            (List<(long Row, Variant? Value)> whole, long wholeSlices) = await ReadAsync(file, id: 0, static scan => scan);
            AssertSame(expected, whole);
            Assert.Equal(4, wholeSlices);

            (List<(long Row, Variant? Value)> windows, long windowSlices) =
                await ReadAsync(file, id: 0, static scan => scan.WithWindowRows(1_536).WithMaxBatchRows(512));
            AssertSame(expected, windows);
            Assert.Equal(2 * 2, windowSlices);

            long[] wanted = [.. Enumerable.Range(0, Rows).Where(static row => row % 3 == 0).Select(static row => (long)row)];
            (List<(long Row, Variant? Value)> taken, long takenSlices) = await ReadAsync(file, id: 0, scan => scan.Take(wanted));
            AssertSame([.. wanted.Select(row => expected[(int)row])], taken);
            Assert.Equal(2, takenSlices);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static void AssertSame(List<(long Row, Variant? Value)> expected, List<(long Row, Variant? Value)> read)
    {
        Assert.Equal(expected.Count, read.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Row, read[i].Row);
            Assert.Equal(expected[i].Value is null, read[i].Value is null);
            if (expected[i].Value is { } value)
            {
                Assert.True(value.Equals(read[i].Value!.Value), $"row {expected[i].Row}: {value.ToJson()} read as {read[i].Value!.Value.ToJson()}");
            }
        }
    }

    /// <summary>
    /// Each row the scan <paramref name="shape"/> makes delivers, by its id, column <paramref name="id"/>, or its place when
    /// there is none (-1), and its variant, the column after; and the slices the scan rebuilt them in.
    /// </summary>
    private static async Task<(List<(long Row, Variant? Value)> Rows, long Slices)> ReadAsync(
        VortexFile file, int id, Func<ScanBuilder, ScanBuilder> shape)
    {
        ScanCounters counters = new ScanCounters();
        List<(long Row, Variant? Value)> rows = [];
        await foreach (RecordBatch batch in shape(file.ScanBuilder()).WithMetrics(counters).ExecuteAsync().WithCancellation(Ct))
        {
            using (batch)
            {
                VortexColumn variant = batch.Column(id + 1);
                StructColumn parts = variant.AsStruct();
                BinaryColumn metadata = parts.GetField(0).AsBinary();
                BinaryColumn value = parts.GetField(1).AsBinary();

                // A take delivers its rows in their blocks, with a selection.
                foreach (int row in batch.View.Selection)
                {
                    long at = id < 0 ? rows.Count : batch.Column(id).AsPrimitive<long>()[row];
                    rows.Add((at, variant.IsValid(row) ? Variant.FromBytes(metadata.GetSpan(row), value.GetSpan(row)) : null));
                }
            }
        }

        return (rows, counters.VariantSlices);
    }

    /// <summary>
    /// The documents in one chunk of their variant column, in blocks of 4 096 rows, under a shredding of every key but the
    /// list, whose encoding decodes no range of its rows.
    /// </summary>
    private static async Task<string> WriteAsync(Doc[] rows)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-sliced-{Guid.NewGuid():N}.vortex");
        Dictionary<string, VariantShredding> shredding = new()
        {
            [nameof(Doc.Attributes)] = VariantShredding.Object(
                ("status", VariantShredding.Primitive(VariantClass.Integer)),
                ("duration", VariantShredding.Primitive(VariantClass.Double)),
                ("route", VariantShredding.Primitive(VariantClass.String)),
                ("ok", VariantShredding.Primitive(VariantClass.Boolean)),
                ("user", VariantShredding.Object(("id", VariantShredding.Primitive(VariantClass.Integer)), ("name", VariantShredding.Primitive(VariantClass.String))))),
        };

        VortexWriteOptions options = new VortexWriteOptions
        {
            BlockRows = 4_096,
            ShreddedVariants = shredding,
            ColumnChunkTargetBytes = ImmutableDictionary<string, int>.Empty.Add(nameof(Doc.Attributes), 1 << 30),
        };
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Doc>(path, options);
        await writer.WriteAsync<Doc>(rows, Ct);
        await writer.CompleteAsync(Ct);
        return path;
    }

    /// <summary>A document of any shape, most often an object of the keys the shredding names, of their class or another.</summary>
    private static Variant Document(Random random)
    {
        switch (random.Next(20))
        {
            case 0:
                return Variant.Null;
            case 1:
                return Variant.From(random.NextInt64(-1_000, 1_000));
            case 2:
                return Variant.Array("a", 1L);
        }

        List<(string, Variant)> fields = [];
        void Maybe(string key, Func<Variant> value)
        {
            if (random.Next(10) < 7)
            {
                fields.Add((key, value()));
            }
        }

        Maybe("status", () => random.Next(8) == 0 ? Variant.From("502") : Variant.From(random.Next(600)));
        Maybe("duration", () => random.Next(6) == 0 ? Variant.From(random.Next(100)) : Variant.From(random.NextDouble() * 1_000));
        Maybe("route", () => Routes[random.Next(Routes.Length)]);
        Maybe("ok", () => random.Next(9) == 0 ? Variant.From("yes") : Variant.From(random.Next(2) == 0));
        Maybe("user", () => random.Next(5) == 0
            ? Variant.From("anonymous")
            : Variant.Object(("id", random.NextInt64(1, 1_000_000)), ("name", $"user-{random.Next(50)}")));
        Maybe("tags", () => Variant.Array([.. Enumerable.Range(0, random.Next(4)).Select(i => Variant.From($"tag-{random.Next(20)}"))]));
        Maybe($"k{random.Next(30)}", () => random.NextDouble());
        return Variant.Object(fields.ToArray());
    }

    /// <summary>Where the files the reference wrote lie, beside this test's sources.</summary>
    private static string DataDirectory([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "Data", "shredded-variants"));

    [VortexRecord]
    public partial record struct Doc(long Id, Variant Attributes);
}
