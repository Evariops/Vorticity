// The dictionary probe, from the read side: a reader answers `x = v` by decoding the values
// child alone and never the codes.
//
// WHAT THESE TESTS PIN, and why the shapes look the way they do: the writer's entry says which
// chunks are dictionaries and carries no payload, so every claim the pruner makes comes from the
// chunk's own values. The columns here give each chunk THE SAME first and last value and one
// distinctive value of its own, so a zone map can prune nothing at all and every block killed is
// the dictionary's doing -- if the probe were removed, these tests would still pass on the rows and
// fail on the blocks.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class DictionaryProbeTests
{
    private const int Rows = 20_480;
    private const int Block = 1_024;

    /// <summary>Rows sharing one distinctive label, a stretch wide enough to cover a chunk.</summary>
    private const int Group = 2_048;

    /// <summary>The label of a row: two values every chunk holds, and one that names its stretch.</summary>
    private static string Label(int row) => (row % 4) switch
    {
        0 => "aaa",
        1 => "zzz",
        _ => $"mmm-{row / Group:D3}",
    };

    [Fact]
    public async Task AValueNoChunkHoldsPrunesEveryBlock()
    {
        string path = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path);
            VortexExpr filter = Expr.Eq(Expr.Field("label"), Expr.Literal(FilterLiteral.From("mmm-999")));
            ScanPlan plan = await file.ScanBuilder().Where(filter).ExplainAsync();

            Assert.Equal(0, plan.LiveBlocks);
            Assert.Equal(0, await file.ScanBuilder().Where(filter).CountAsync());
            Assert.Contains(plan.Pruning, step => step.Structure == "locating index" && step.BlocksPruned == plan.Blocks);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AValueOfOneChunkLeavesThatChunkAndKillsTheRest()
    {
        string path = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path);
            VortexExpr filter = Expr.Eq(Expr.Field("label"), Expr.Literal(FilterLiteral.From("mmm-003")));
            ScanPlan plan = await file.ScanBuilder().Where(filter).ExplainAsync();

            // The zone map cannot help: every chunk's bounds are "aaa" and "zzz".
            Assert.All(plan.Pruning.Where(s => s.Structure == "zone map"), s => Assert.Equal(0, s.BlocksPruned));
            Assert.InRange(plan.LiveBlocks, 1, plan.Blocks - 1);

            List<string> labels = await LabelsAsync(file, filter);
            Assert.Equal(Group / 2, labels.Count);
            Assert.All(labels, label => Assert.Equal("mmm-003", label));
            Assert.Equal(labels, await LabelsAsync(file, filter, prune: false));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AValueEveryChunkHoldsPrunesNothingAndAnswersTheSame()
    {
        string path = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path);
            VortexExpr filter = Expr.Eq(Expr.Field("label"), Expr.Literal(FilterLiteral.From("aaa")));
            ScanPlan plan = await file.ScanBuilder().Where(filter).ExplainAsync();

            Assert.Equal(plan.Blocks, plan.LiveBlocks);
            Assert.Equal(Rows / 4, await file.ScanBuilder().Where(filter).CountAsync());
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AnInIsAbsentOnlyWhenNoChunkHoldsAnyOfItsValues()
    {
        string path = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path);
            FilterLiteral[] absent = [.. new[] { "mmm-900", "mmm-901", "mmm-902" }.Select(FilterLiteral.From)];
            ScanPlan none = await file.ScanBuilder().Where(Expr.In(Expr.Field("label"), absent)).ExplainAsync();
            Assert.Equal(0, none.LiveBlocks);

            FilterLiteral[] one = [.. new[] { "mmm-900", "mmm-004", "mmm-902" }.Select(FilterLiteral.From)];
            VortexExpr filter = Expr.In(Expr.Field("label"), one);
            ScanPlan some = await file.ScanBuilder().Where(filter).ExplainAsync();
            Assert.InRange(some.LiveBlocks, 1, some.Blocks - 1);
            Assert.Equal(Group / 2, await file.ScanBuilder().Where(filter).CountAsync());
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AColumnWithoutADictionaryPrunesNothing()
    {
        // `other` is every row's own string: the chooser never dictionaries it, so the probe has no
        // entry for it and the filter falls through to the scan -- with the right answer.
        string path = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path);
            VortexExpr filter = Expr.Eq(Expr.Field("other"), Expr.Literal(FilterLiteral.From("other-00000000")));
            ScanPlan plan = await file.ScanBuilder().Where(filter).ExplainAsync();

            Assert.DoesNotContain(plan.Pruning, step => step.Structure == "locating index" && step.BlocksPruned > 0);
            Assert.Equal(1, await file.ScanBuilder().Where(filter).CountAsync());
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task TheTwoZerosOfAFloatAreOneValueToProbeFor()
    {
        // A dictionary holds -0.0; a filter asking for 0.0 must not be told the chunk lacks it.
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        DType schema = types.Struct(["key"], [f64], Nullability.NonNullable);
        VortexBuffer buffer = arena.Allocate(Rows * sizeof(double), sizeof(double), out Span<byte> bytes);
        Span<double> values = MemoryMarshal.Cast<byte, double>(bytes);
        for (int row = 0; row < Rows; row++)
        {
            values[row] = (row % 4) switch
            {
                0 => -0.0,
                1 => 1.5,
                _ => 2.5,
            };
        }

        int column = arena.AddPrimitive(f64, Rows, Validity.NonNullable, PType.F64, buffer);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [column]);
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-dictprobe-{Guid.NewGuid():N}.vortex");
        try
        {
            Decoders.EnsureRegistered();
            await using (VortexFileWriter writer = VortexFileWriter.Create(path, schema, Options(["key"])))
            {
                using RecordBatch batch = new RecordBatch(arena, root, 0);
                await writer.WriteAsync(batch);
                await writer.CompleteAsync();
            }

            await using VortexFile file = await VortexFile.OpenAsync(path);
            VortexExpr filter = Expr.Eq(Expr.Field("key"), Expr.Literal(FilterLiteral.From(0.0)));
            ScanPlan plan = await file.ScanBuilder().Where(filter).ExplainAsync();

            Assert.Equal(plan.Blocks, plan.LiveBlocks);
            Assert.Equal(Rows / 4, await file.ScanBuilder().Where(filter).CountAsync());

            // And a float no chunk holds is still pruned away.
            VortexExpr missing = Expr.Eq(Expr.Field("key"), Expr.Literal(FilterLiteral.From(7.5)));
            Assert.Equal(0, (await file.ScanBuilder().Where(missing).ExplainAsync()).LiveBlocks);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static VortexWriteOptions Options(IEnumerable<string> dictionaries) => new VortexWriteOptions
    {
        RowBlockSize = Block,

        // Several chunks, so the probe has several dictionaries to tell apart: at sixteen canonical
        // bytes a view and two columns, this cuts a chunk every couple of thousand rows.
        DataBlockTargetBytes = 64 << 10,
        Identity = new Guid("32323232-3232-4232-8232-323232323232"),
        EncodingHints = dictionaries.ToDictionary(column => column, _ => EncodingHint.Dictionary),
    };

    private static async Task<string> WriteAsync()
    {
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType utf8 = types.Utf8(Nullability.NonNullable);
        DType schema = types.Struct(["label", "other"], [utf8, utf8], Nullability.NonNullable);
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-dictprobe-{Guid.NewGuid():N}.vortex");
        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, Options(["label"]));

        // ONE BATCH PER STRETCH, so the file holds several chunks and so several dictionaries: a
        // chunk is cut between batches, never inside one.
        for (int start = 0; start < Rows; start += Group)
        {
            CanonicalArena arena = new CanonicalArena();
            int label = Strings(arena, utf8, Group, row => Label(start + row));
            int other = Strings(arena, utf8, Group, row => $"other-{start + row:D8}");
            int root = arena.AddStruct(schema, Group, Validity.NonNullable, [label, other]);
            using RecordBatch batch = new RecordBatch(arena, root, start);
            await writer.WriteAsync(batch);
        }

        await writer.CompleteAsync();
        return path;
    }

    private static int Strings(CanonicalArena arena, DType utf8, int count, Func<int, string> of)
    {
        byte[][] values = new byte[count][];
        int heap = 0;
        for (int row = 0; row < count; row++)
        {
            values[row] = System.Text.Encoding.UTF8.GetBytes(of(row));
            heap += values[row].Length;
        }

        VortexBuffer data = arena.Allocate(heap, 1, out Span<byte> bytes);
        VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> raw);
        raw.Clear();
        int at = 0;
        for (int row = 0; row < count; row++)
        {
            byte[] value = values[row];
            Span<byte> view = raw.Slice(row * 16, 16);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view, value.Length);
            if (value.Length <= 12)
            {
                // A short value lives in the view itself, after its length.
                value.CopyTo(view[4..]);
                continue;
            }

            value.AsSpan(0, 4).CopyTo(view[4..]);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view[8..], 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view[12..], at);
            value.CopyTo(bytes[at..]);
            at += value.Length;
        }

        return arena.AddVarBinView(utf8, count, Validity.NonNullable, views, [data]);
    }

    /// <summary>The label of every row the filter selects, in file order.</summary>
    private static async Task<List<string>> LabelsAsync(VortexFile file, VortexExpr filter, bool prune = true)
    {
        List<string> labels = [];
        ScanBuilder scan = file.ScanBuilder().Where(filter);
        if (!prune)
        {
            scan = scan.WithPruning(false);
        }

        await foreach (RecordBatch batch in scan.ExecuteAsync())
        {
            BinaryColumn column = batch.Column(0).AsBinary();
            for (int row = 0; row < batch.RowCount; row++)
            {
                labels.Add(column.GetString(row) ?? string.Empty);
            }
        }

        return labels;
    }
}
