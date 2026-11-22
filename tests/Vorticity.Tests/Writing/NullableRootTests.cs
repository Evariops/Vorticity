// A file whose root is a nullable struct, as Vortex Rust 0.93 writes one: its rows may each be absent, the columns holding
// some value beneath an absent row. The writer keeps that validity as the root struct's first child, before the columns, and
// carries no statistics, zone maps or indexes for columns whose values beneath an absent row would answer for it. What
// cannot hold such a root refuses it by name: a Parquet file, an append, a dataset.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.Layouts;
using Vorticity.Parquet;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class NullableRootTests : IDisposable
{
    private const string Source = "types/struct_nested_deep_nullable_r8193";

    private readonly string _directory = TestScratch.Create("vx-nullable-root");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// Rows written whole, then with absent rows among them, many times over, then every one absent, read back as they were
    /// written: the root's validity crosses from none kept to a bitmap, which then grows.
    /// </summary>
    [Fact]
    public async Task ANullableRootReadsBackAsItWasWritten()
    {
        string path = Path.Combine(_directory, "copy.vortex");
        List<string> expected = [];
        await using (VortexFile source = await VortexFile.OpenAsync(Corpus.Path(Source), Ct))
        {
            Assert.True(source.Schema.RootIsNullable);
            await using VortexFileWriter writer = VortexSession.Default.CreateWriter(path, source.Schema);
            await foreach (RecordBatch batch in source.Scan().ToBatchesAsync(Ct))
            {
                using (batch)
                {
                    await WriteAsAsync(writer, batch, Validity.AllValid, expected);
                    for (int i = 0; i < 12; i++)
                    {
                        Describe(batch, expected);
                        await writer.WriteAsync(batch, Ct);
                    }

                    await WriteAsAsync(writer, batch, Validity.AllInvalid, expected);
                }
            }

            await writer.CompleteAsync(Ct);
        }

        List<string> actual = [];
        await using VortexFile written = await VortexFile.OpenAsync(path, Ct);
        await foreach (RecordBatch batch in written.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                Describe(batch, actual);
            }
        }

        // Each row is told with its presence, which the columns of a batch do not carry: the first batch's rows are all
        // present, the last one's all absent, and the source's own lie between.
        Assert.Equal(14 * 8193, expected.Count);
        Assert.All(expected.Take(8193), row => Assert.Equal('+', row[0]));
        Assert.All(expected.TakeLast(8193), row => Assert.Equal('-', row[0]));
        Assert.Contains(expected.Skip(8193).Take(8193), row => row[0] == '-');
        Assert.Equal(expected, actual);
    }

    /// <summary>The validity is the root struct's first child, a bool, and no column below carries a zone map or a statistic.</summary>
    [Fact]
    public async Task TheValidityIsTheFirstChildAndTheColumnsCarryNoStatistics()
    {
        string path = await CopyAsync();
        await using VortexFile written = await VortexFile.OpenAsync(path, Ct);
        LayoutNode root = written.LayoutTree.Root;
        Assert.Equal(LayoutEncodingId.Struct, root.Encoding);
        Assert.Equal(written.Schema.Count + 1, root.ChildCount);
        Assert.Equal(DTypeKind.Bool, root.GetChild(0).DType.Kind);
        Assert.False(root.GetChild(0).DType.IsNullable);
        for (int child = 1; child < root.ChildCount; child++)
        {
            Assert.NotEqual(LayoutEncodingId.Zoned, root.GetChild(child).Encoding);
        }

        Assert.False(written.HasFileStatistics);
    }

    /// <summary>The schema of such a file says its root is nullable, and is no schema a caller builds of the same fields.</summary>
    [Fact]
    public async Task TheSchemaKeepsTheRootNullable()
    {
        await using VortexFile source = await VortexFile.OpenAsync(Corpus.Path(Source), Ct);
        VortexSchema schema = source.Schema;
        Assert.EndsWith("}?", schema.ToString(), StringComparison.Ordinal);
        Assert.NotEqual(VortexSchema.Create([.. schema]), schema);

        await using VortexFileWriter writer = VortexSession.Default.CreateWriter(Path.Combine(_directory, "schema.vortex"), schema);
        Assert.Equal(schema, writer.Schema);
    }

    /// <summary>A Parquet file's rows are its root group's, which no row is absent from: the writer is refused, and nothing is left behind.</summary>
    [Fact]
    public async Task AParquetWriterRefusesANullableRoot()
    {
        string path = Path.Combine(_directory, "refused.parquet");
        await using VortexFile source = await VortexFile.OpenAsync(Corpus.Path(Source), Ct);
        VortexSchemaException refusal = Assert.Throws<VortexSchemaException>(
            () => VortexSession.Default.CreateParquetWriter(path, source.Schema));
        Assert.Contains("nullable root", refusal.Message, StringComparison.Ordinal);
        Assert.False(System.IO.File.Exists(path));
    }

    /// <summary>An append carries the columns' chunks on, not the root's validity beside them: it is refused, and the file stays as it was.</summary>
    [Fact]
    public async Task AnAppendRefusesANullableRoot()
    {
        string path = await CopyAsync();
        byte[] before = await System.IO.File.ReadAllBytesAsync(path, Ct);
        VortexUnsupportedException refusal = await Assert.ThrowsAsync<VortexUnsupportedException>(
            async () => await VortexFileWriter.AppendAsync(path, cancellationToken: Ct));
        Assert.Contains("nullable struct", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(before, await System.IO.File.ReadAllBytesAsync(path, Ct));
    }

    /// <summary>An index asked for beneath a nullable root is refused when the writer is made.</summary>
    [Fact]
    public async Task AnIndexIsRefusedBeneathANullableRoot()
    {
        await using VortexFile source = await VortexFile.OpenAsync(Corpus.Path(Source), Ct);
        ArgumentException refusal = Assert.Throws<ArgumentException>(() => VortexSession.Default.CreateWriter(
            Path.Combine(_directory, "indexed.vortex"), source.Schema, new VortexWriteOptions { Indexes = IndexPolicy.Auto }));
        Assert.Contains("nullable struct", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A dataset's rows are records, never absent: a dataset of such a schema is refused.</summary>
    [Fact]
    public async Task ADatasetRefusesANullableRoot()
    {
        await using VortexFile source = await VortexFile.OpenAsync(Corpus.Path(Source), Ct);
        await using MemoryObjectStore store = new MemoryObjectStore();
        ArgumentException refusal = await Assert.ThrowsAsync<ArgumentException>(
            async () => await VortexDataset.CreateAsync(store, source.Schema, cancellationToken: Ct));
        Assert.Contains("nullable struct", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>Writes <paramref name="batch"/>'s rows under a root of <paramref name="validity"/> instead of its own.</summary>
    private static async Task WriteAsAsync(VortexFileWriter writer, RecordBatch batch, Validity validity, List<string> expected)
    {
        CanonicalArena arena = batch.Arena;
        CanonicalNode root = arena.GetNode(batch.RootIndex);
        int[] fields = new int[root.DType.FieldCount];
        for (int field = 0; field < fields.Length; field++)
        {
            fields[field] = root.GetFieldIndex(field);
        }

        using RecordBatch rewrapped = new RecordBatch(arena, arena.AddStruct(root.DType, root.Length, validity, fields), 0);
        Describe(rewrapped, expected);
        await writer.WriteAsync(rewrapped, Ct);
    }

    /// <summary>Each row of <paramref name="batch"/>, led by <c>+</c> when its root is present and <c>-</c> when absent.</summary>
    private static void Describe(RecordBatch batch, List<string> into)
    {
        int first = into.Count;
        Values.DescribeRows(batch, into);
        CanonicalArena arena = batch.Arena;
        Validity validity = arena.GetNode(batch.RootIndex).Validity;
        for (int row = 0; row < batch.RowCount; row++)
        {
            bool present = validity.Kind switch
            {
                ValidityKind.NonNullable or ValidityKind.AllValid => true,
                ValidityKind.AllInvalid => false,
                _ => CanonicalSupport.BitAt(
                    arena.GetNode(validity.CanonicalNodeIndex).Bits.Span, arena.GetNode(validity.CanonicalNodeIndex).BitOffset + row),
            };
            into[first + row] = (present ? "+" : "-") + into[first + row];
        }
    }

    /// <summary>The source corpus file, rewritten by this writer under its own schema.</summary>
    private async Task<string> CopyAsync()
    {
        string path = Path.Combine(_directory, "copy.vortex");
        await using VortexFile source = await VortexFile.OpenAsync(Corpus.Path(Source), Ct);
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter(path, source.Schema);
        await foreach (RecordBatch batch in source.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                await writer.WriteAsync(batch, Ct);
            }
        }

        await writer.CompleteAsync(Ct);
        return path;
    }
}
