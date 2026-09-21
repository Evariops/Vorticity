// RecordBatch and the columns it hands out: the boundary row counts, and every malformed shape
// throwing VortexFormatException and only that.
using System;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Columns;

public sealed class RecordBatchTests
{
    /// <summary>The corpus's boundary row counts: the FastLanes block, the row block, and ±1.</summary>
    public static TheoryData<int> RowCounts => [0, 1, 1023, 1024, 1025, 8191, 8192, 8193];

    [Theory]
    [MemberData(nameof(RowCounts))]
    public void NonStructRoot_IsOneUnnamedColumn(int rows)
    {
        using ColumnFixture f = new ColumnFixture();
        int[] values = new int[rows];
        for (int i = 0; i < rows; i++)
        {
            values[i] = i;
        }

        RecordBatch batch = f.Batch(f.Int32Node(values, Validity.NonNullable), startRow: 4096);

        Assert.False(batch.IsTabular);
        Assert.Equal(1, batch.FieldCount);
        Assert.Null(batch.GetFieldName(0));
        Assert.Equal(rows, batch.RowCount);
        Assert.Equal(4096L, batch.StartRow);
        Assert.Equal(DTypeKind.Primitive, batch.Schema.Kind);

        // Column(0) IS the root, not a synthetic wrapper.
        Assert.Equal(rows, batch.Column(0).Length);
        Assert.Equal(rows, batch.Root.Length);
        Assert.Equal(rows, batch.Column(0).AsPrimitive<int>().Values.Length);

        Assert.False(batch.TryGetFieldIndex("anything"u8, out int missing));
        Assert.Equal(-1, missing);
    }

    [Fact]
    public void NonStructRoot_RejectsAnyColumnButZero()
    {
        using ColumnFixture f = new ColumnFixture();
        RecordBatch batch = f.Batch(f.Int32Node([1, 2, 3], Validity.NonNullable));

        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = batch.Column(1).Length; });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = batch.Column(-1).Length; });
        Assert.Throws<ArgumentOutOfRangeException>(() => batch.GetFieldName(1));
        Assert.Throws<ArgumentException>(() => { _ = batch.Column("x"u8).Length; });
    }

    [Fact]
    public void StructRoot_ExposesFieldsByIndexAndByName()
    {
        using ColumnFixture f = new ColumnFixture();
        int a = f.Int32Node([1, 2], Validity.NonNullable);
        int b = f.Int32Node([3, 4], Validity.NonNullable);
        DType schema = f.Types.Struct(
            ["a", "b"],
            [f.Types.Primitive(PType.I32, Nullability.NonNullable), f.Types.Primitive(PType.I32, Nullability.NonNullable)],
            Nullability.NonNullable);
        RecordBatch batch = f.Batch(f.Arena.AddStruct(schema, 2, Validity.NonNullable, [a, b]));

        Assert.True(batch.IsTabular);
        Assert.Equal(2, batch.FieldCount);
        Assert.Equal("a", batch.GetFieldName(0));
        Assert.Equal("b", batch.GetFieldName(1));
        Assert.True(batch.TryGetFieldIndex("b"u8, out int bi));
        Assert.Equal(1, bi);
        Assert.Equal(3, batch.Column("b"u8).AsPrimitive<int>()[0]);
        Assert.Equal(1, batch.Column(0).AsPrimitive<int>()[0]);
    }

    [Fact]
    public void StructRoot_FieldCountMismatchIsMalformed()
    {
        using ColumnFixture f = new ColumnFixture();
        int a = f.Int32Node([1, 2], Validity.NonNullable);
        DType schema = f.Types.Struct(
            ["a", "b"],
            [f.Types.Primitive(PType.I32, Nullability.NonNullable), f.Types.Primitive(PType.I32, Nullability.NonNullable)],
            Nullability.NonNullable);

        // The dtype promises two fields; only one was decoded. Silently exposing field 1 as
        // whatever the arena's next child happens to be is exactly the wrong-value-passed-off-as-
        // right failure the quality bar forbids.
        int root = f.Arena.AddStruct(schema, 2, Validity.NonNullable, [a]);
        Assert.Throws<VortexFormatException>(() => new RecordBatch(f.Arena, root, 0));
    }

    [Fact]
    public void RootIndexOutsideTheArenaIsMalformed()
    {
        using ColumnFixture f = new ColumnFixture();
        Assert.Throws<VortexFormatException>(() => new RecordBatch(f.Arena, 0, 0));
        int node = f.Int32Node([1], Validity.NonNullable);
        Assert.Throws<VortexFormatException>(() => new RecordBatch(f.Arena, node + 1, 0));
        Assert.Throws<VortexFormatException>(() => new RecordBatch(f.Arena, -1, 0));
    }

    [Fact]
    public void ConstructorRejectsCallerErrors()
    {
        using ColumnFixture f = new ColumnFixture();
        int node = f.Int32Node([1], Validity.NonNullable);
        Assert.Throws<ArgumentNullException>(() => new RecordBatch((CanonicalArena)null!, 0, 0));
        Assert.Throws<ArgumentNullException>(() => new RecordBatch((ScanContext)null!, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecordBatch(f.Arena, node, -1));
    }

    [Fact]
    public void DisposeInvalidatesEveryEntryPoint()
    {
        using ColumnFixture f = new ColumnFixture();
        RecordBatch batch = f.Batch(f.Int32Node([1, 2, 3], Validity.NonNullable));

        // Read a span first, exactly as a caller would, and let it die with the scope: the
        // compiler will not let a ref struct or its span escape past the dispose below.
        {
            ReadOnlySpan<int> values = batch.Column(0).AsPrimitive<int>().Values;
            Assert.Equal(3, values.Length);
        }

        batch.Dispose();
        batch.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = batch.Schema; });
        Assert.Throws<ObjectDisposedException>(() => { _ = batch.RowCount; });
        Assert.Throws<ObjectDisposedException>(() => { _ = batch.StartRow; });
        Assert.Throws<ObjectDisposedException>(() => { _ = batch.IsTabular; });
        Assert.Throws<ObjectDisposedException>(() => { _ = batch.FieldCount; });
        Assert.Throws<ObjectDisposedException>(() => { _ = batch.Root.Length; });
        Assert.Throws<ObjectDisposedException>(() => { _ = batch.Column(0).Length; });
        Assert.Throws<ObjectDisposedException>(() => { _ = batch.Column("a"u8).Length; });
        Assert.Throws<ObjectDisposedException>(() => batch.GetFieldName(0));
        Assert.Throws<ObjectDisposedException>(() => batch.TryGetFieldIndex("a"u8, out _));
    }

    [Fact]
    public void DisposeThroughAScanContextResetsTheArenas()
    {
        using ScanContext context = new ScanContext(["vortex.primitive"], VortexReadOptions.Default);
        DTypeArena types = context.Types;
        Vorticity.Buffers.VortexBuffer values =
            context.Canonical.Allocate(12, 16, out Span<byte> destination);
        destination.Clear();

        int node = context.Canonical.AddPrimitive(
            types.Primitive(PType.I32, Nullability.NonNullable), 3, Validity.NonNullable, PType.I32, values);
        RecordBatch batch = new RecordBatch(context, node, 0);
        Assert.Equal(3, batch.RowCount);
        Assert.Equal(1, context.Canonical.NodeCount);

        batch.Dispose();

        // Disposing the batch resets the context so the next batch reuses the same arenas. A stale
        // column view therefore must not reach the arena at all.
        Assert.Equal(0, context.Canonical.NodeCount);
        Assert.Throws<ObjectDisposedException>(() => { _ = batch.Column(0).Length; });
    }

    [Fact]
    public void FieldNamesMayBeEmptyOrContainADot()
    {
        // types/struct_field_names does exactly this on purpose; the sidecar's dotted paths are
        // ambiguous for it by construction, so index access is the escape hatch.
        using ColumnFixture f = new ColumnFixture();
        int a = f.Int32Node([1], Validity.NonNullable);
        int b = f.Int32Node([2], Validity.NonNullable);
        int c = f.Int32Node([3], Validity.NonNullable);
        DType i32 = f.Types.Primitive(PType.I32, Nullability.NonNullable);
        DType schema = f.Types.Struct(["", "a.b", "a"], [i32, i32, i32], Nullability.NonNullable);
        RecordBatch batch = f.Batch(f.Arena.AddStruct(schema, 1, Validity.NonNullable, [a, b, c]));

        Assert.Equal(string.Empty, batch.GetFieldName(0));
        Assert.Equal("a.b", batch.GetFieldName(1));
        Assert.Equal("a", batch.GetFieldName(2));

        Assert.True(batch.TryGetFieldIndex(""u8, out int empty));
        Assert.Equal(0, empty);
        Assert.True(batch.TryGetFieldIndex("a.b"u8, out int dotted));
        Assert.Equal(1, dotted);
        Assert.True(batch.TryGetFieldIndex("a"u8, out int plain));
        Assert.Equal(2, plain);

        Assert.Equal(2, batch.Column("a.b"u8).AsPrimitive<int>()[0]);
        Assert.Equal(3, batch.Column("a"u8).AsPrimitive<int>()[0]);
    }

    [Fact]
    public void UnknownFieldNameNamesItself()
    {
        using ColumnFixture f = new ColumnFixture();
        int a = f.Int32Node([1], Validity.NonNullable);
        DType schema = f.Types.Struct(
            ["a"], [f.Types.Primitive(PType.I32, Nullability.NonNullable)], Nullability.NonNullable);
        RecordBatch batch = f.Batch(f.Arena.AddStruct(schema, 1, Validity.NonNullable, [a]));

        ArgumentException error =
            Assert.Throws<ArgumentException>(() => { _ = batch.Column("nope"u8).Length; });
        Assert.Contains("nope", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedStructNavigatesByIndexAndByName()
    {
        // The shape of types/struct_nested_deep: {a: i32, b: {c: i32, d: {e: i32, f: i32}}}.
        using ColumnFixture f = new ColumnFixture();
        DType i32 = f.Types.Primitive(PType.I32, Nullability.NonNullable);

        int e = f.Int32Node([50], Validity.NonNullable);
        int fld = f.Int32Node([60], Validity.NonNullable);
        DType dType = f.Types.Struct(["e", "f"], [i32, i32], Nullability.NonNullable);
        int d = f.Arena.AddStruct(dType, 1, Validity.NonNullable, [e, fld]);

        int c = f.Int32Node([30], Validity.NonNullable);
        DType bType = f.Types.Struct(["c", "d"], [i32, dType], Nullability.NonNullable);
        int b = f.Arena.AddStruct(bType, 1, Validity.NonNullable, [c, d]);

        int a = f.Int32Node([10], Validity.NonNullable);
        DType rootType = f.Types.Struct(["a", "b"], [i32, bType], Nullability.NonNullable);
        RecordBatch batch = f.Batch(f.Arena.AddStruct(rootType, 1, Validity.NonNullable, [a, b]));

        Assert.Equal(50, batch.Column(1).AsStruct().GetField(1).AsStruct().GetField(0).AsPrimitive<int>()[0]);
        Assert.Equal(
            60,
            batch.Column("b"u8).AsStruct().GetField("d"u8).AsStruct().GetField("f"u8).AsPrimitive<int>()[0]);
        Assert.Equal("d", batch.Column("b"u8).AsStruct().GetFieldName(1));
        Assert.True(
            Encoding.UTF8.GetString(batch.Column("b"u8).AsStruct().GetFieldNameUtf8(0)) == "c");
    }

    [Fact]
    public void StructColumnRejectsOutOfRangeFieldsAndUnknownNames()
    {
        using ColumnFixture f = new ColumnFixture();
        int a = f.Int32Node([1], Validity.NonNullable);
        DType schema = f.Types.Struct(
            ["a"], [f.Types.Primitive(PType.I32, Nullability.NonNullable)], Nullability.NonNullable);
        RecordBatch batch = f.Batch(f.Arena.AddStruct(schema, 1, Validity.NonNullable, [a]));

        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = batch.Root.AsStruct().GetField(1).Length; });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = batch.Root.AsStruct().GetField(-1).Length; });
        Assert.Throws<ArgumentOutOfRangeException>(() => batch.Root.AsStruct().GetFieldName(1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = batch.Root.AsStruct().GetFieldNameUtf8(1).Length;
        });
        Assert.Throws<ArgumentException>(() => { _ = batch.Root.AsStruct().GetField("z"u8).Length; });
        Assert.False(batch.Root.AsStruct().TryGetFieldIndex("z"u8, out int missing));
        Assert.Equal(-1, missing);
    }

    [Fact]
    public void WindowIsTheSameRowsWithTheirOwnStartRow()
    {
        // The seam a dataset's k-way merge needs: "these rows of that batch", so that the merge
        // can emit a run without writing a second gather.
        using ColumnFixture f = new ColumnFixture();
        int keys = f.Int64Node([10L, 20L, 30L, 40L, 50L], Validity.NonNullable);
        int flags = f.BoolNode([true, false, true, false, true]);
        DType schema = f.Types.Struct(
            ["key", "flag"],
            [
                f.Types.Primitive(PType.I64, Nullability.NonNullable),
                f.Types.Bool(Nullability.NonNullable),
            ],
            Nullability.NonNullable);
        RecordBatch batch = f.Batch(f.Arena.AddStruct(schema, 5, Validity.NonNullable, [keys, flags]), startRow: 400);

        using RecordBatch window = batch.Window(1, 3);
        Assert.Equal(3, window.RowCount);
        Assert.Equal(401L, window.StartRow);
        Assert.Equal(schema, window.Schema);
        Assert.Equal([20L, 30L, 40L], window.Column(0).AsPrimitive<long>().Values.ToArray());
        Assert.Equal([false, true, false], Bits(window.Column(1)));

        // The source is untouched: a window reads rows, it does not consume them.
        Assert.Equal(5, batch.RowCount);
        Assert.Equal(400L, batch.StartRow);
        Assert.Equal([10L, 20L, 30L, 40L, 50L], batch.Column(0).AsPrimitive<long>().Values.ToArray());

        // The degenerate ends, which a merge hits on its last run and on an empty input.
        using RecordBatch all = batch.Window(0, 5);
        Assert.Equal([10L, 20L, 30L, 40L, 50L], all.Column(0).AsPrimitive<long>().Values.ToArray());
        using RecordBatch none = batch.Window(5, 0);
        Assert.Equal(0, none.RowCount);
        Assert.Equal(405L, none.StartRow);
    }

    [Fact]
    public void WindowRefusesWhatIsNotInsideTheBatch()
    {
        using ColumnFixture f = new ColumnFixture();
        RecordBatch batch = f.Batch(f.Int32Node([1, 2, 3], Validity.NonNullable));

        Assert.Throws<ArgumentOutOfRangeException>(() => batch.Window(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => batch.Window(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => batch.Window(2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => batch.Window(4, 0));

        batch.Dispose();
        Assert.Throws<ObjectDisposedException>(() => batch.Window(0, 1));
    }

    [Fact]
    public void ProjectKeepsTheNamedColumnsAndCopiesNoValue()
    {
        // The seam a dataset's key-ordered merge needs: it compares rows by a key column the
        // caller may not have selected, and drops that column before handing the batch on — the
        // step the scan performs for a filter's columns, from outside the scan.
        using ColumnFixture f = new ColumnFixture();
        DType i64 = f.Types.Primitive(PType.I64, Nullability.NonNullable);
        DType i32 = f.Types.Primitive(PType.I32, Nullability.NonNullable);
        int keys = f.Int64Node([10L, 20L, 30L], Validity.NonNullable);
        int c = f.Int32Node([1, 2, 3], Validity.NonNullable);
        int d = f.Int32Node([4, 5, 6], Validity.NonNullable);
        DType inner = f.Types.Struct(["c", "d"], [i32, i32], Nullability.NonNullable);
        int nested = f.Arena.AddStruct(inner, 3, Validity.NonNullable, [c, d]);
        DType schema = f.Types.Struct(["key", "inner"], [i64, inner], Nullability.NonNullable);
        RecordBatch batch = f.Batch(
            f.Arena.AddStruct(schema, 3, Validity.NonNullable, [keys, nested]), startRow: 70);

        using RecordBatch dropped = batch.Project(Projection.Parse(batch.Schema, ["inner"]));
        Assert.Equal(f.Types.Struct(["inner"], [inner], Nullability.NonNullable), dropped.Schema);
        Assert.Equal(3, dropped.RowCount);
        Assert.Equal(70L, dropped.StartRow);
        Assert.Equal([4, 5, 6], dropped.Column(0).AsStruct().GetField("d"u8).AsPrimitive<int>().Values.ToArray());

        // A nested leaf: the struct above it is rebuilt around that one field.
        using RecordBatch leaf = batch.Project(Projection.Parse(batch.Schema, ["key", "inner.d"]));
        DType narrowed = f.Types.Struct(["d"], [i32], Nullability.NonNullable);
        Assert.Equal(f.Types.Struct(["key", "inner"], [i64, narrowed], Nullability.NonNullable), leaf.Schema);
        Assert.Equal([10L, 20L, 30L], leaf.Column(0).AsPrimitive<long>().Values.ToArray());
        Assert.Equal([4, 5, 6], leaf.Column(1).AsStruct().GetField(0).AsPrimitive<int>().Values.ToArray());

        // No value is copied: the kept columns are the source's own nodes.
        CanonicalNode root = leaf.Arena.GetNode(leaf.RootIndex);
        Assert.Equal(keys, root.GetFieldIndex(0));
        Assert.Equal(d, leaf.Arena.GetNode(root.GetFieldIndex(1)).GetFieldIndex(0));

        // Everything is the batch itself, and the source is untouched by any of it.
        using RecordBatch all = batch.Project(Projection.All);
        Assert.Equal(schema, all.Schema);
        Assert.Equal(schema, batch.Schema);
        Assert.Equal([10L, 20L, 30L], batch.Column(0).AsPrimitive<long>().Values.ToArray());
    }

    [Fact]
    public void ProjectRefusesAColumnlessBatchAndADisposedOne()
    {
        using ColumnFixture f = new ColumnFixture();
        RecordBatch batch = f.Batch(f.Int32Node([1, 2, 3], Validity.NonNullable));
        DType table = f.Types.Struct(
            ["a"], [f.Types.Primitive(PType.I32, Nullability.NonNullable)], Nullability.NonNullable);

        Assert.Throws<ArgumentException>(() => batch.Project(Projection.Parse(table, ["a"])));

        batch.Dispose();
        Assert.Throws<ObjectDisposedException>(() => batch.Project(Projection.All));
    }

    private static bool[] Bits(VortexColumn column)
    {
        BoolColumn bits = column.AsBool();
        bool[] values = new bool[bits.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = bits[i];
        }

        return values;
    }
}
