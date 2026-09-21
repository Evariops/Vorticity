// The projection compiler.
//
// The file these tests keep coming back to is types/struct_field_names, whose eleven i32 fields are
// named "", "zones", "data", "codes", "values", "élan", "名前", "😀", "has space", "a.b", "A" and
// "a". It is in the corpus precisely because it breaks the dotted grammar: "a.b" is a single field
// name AND a two-segment path, and "" is a field name that no path can distinguish from an empty
// segment. The compiler must therefore resolve "a.b" as a.b - descend into "a" and fail - and the
// escape hatch must exist.
using System;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class ProjectionTests
{
    private static DType Schema()
    {
        DTypeArena arena = new DTypeArena();
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType utf8 = arena.Utf8(Nullability.Nullable);
        DType inner = arena.Struct(
            ["size", "name"],
            [i32, utf8],
            Nullability.NonNullable);
        return arena.Struct(
            ["id", "payload", "ts"],
            [i32, inner, i32],
            Nullability.NonNullable);
    }

    [Fact]
    public void DefaultProjectionIsAll()
    {
        Projection projection = default;
        Assert.True(projection.IsAll);
        Assert.True(projection.RootMask.IsAll);
        Assert.Equal(-1, projection.LeafCount);
        Assert.True(Projection.All.IsAll);
    }

    [Fact]
    public void AnEmptyPathListIsAll()
    {
        Assert.True(Projection.Parse(Schema(), ReadOnlySpan<string>.Empty).IsAll);
    }

    [Fact]
    public void OneTopLevelPath()
    {
        Projection projection = Projection.Parse(Schema(), ["id"]);

        Assert.False(projection.IsAll);
        Assert.Equal(1, projection.LeafCount);
        Assert.True(projection.RootMask.Includes(0));
        Assert.False(projection.RootMask.Includes(1));
        Assert.False(projection.RootMask.Includes(2));
        Assert.True(projection.RootMask.Descend(0).IsAll);
        Assert.True(projection.RootMask.Descend(1).IsEmpty);
    }

    [Fact]
    public void TwoNonAdjacentPaths()
    {
        Projection projection = Projection.Parse(Schema(), ["id", "ts"]);

        Assert.Equal(2, projection.LeafCount);
        Assert.True(projection.RootMask.Includes(0));
        Assert.False(projection.RootMask.Includes(1));
        Assert.True(projection.RootMask.Includes(2));
    }

    [Fact]
    public void ANestedPathSelectsOnlyThatLeaf()
    {
        Projection projection = Projection.Parse(Schema(), ["payload.size"]);

        Assert.Equal(1, projection.LeafCount);
        Assert.True(projection.RootMask.Includes(1));
        FieldMask payload = projection.RootMask.Descend(1);
        Assert.False(payload.IsAll);
        Assert.True(payload.Includes(0));
        Assert.False(payload.Includes(1));
    }

    [Fact]
    public void AnAncestorSubsumesItsDescendant()
    {
        // "payload" then "payload.size" must stay "the whole payload", in either order.
        Projection wide = Projection.Parse(Schema(), ["payload", "payload.size"]);
        Assert.True(wide.RootMask.Descend(1).IsAll);
        Assert.Equal(1, wide.LeafCount);

        Projection reversed = Projection.Parse(Schema(), ["payload.size", "payload"]);
        Assert.True(reversed.RootMask.Descend(1).IsAll);
        Assert.Equal(1, reversed.LeafCount);
    }

    [Fact]
    public void ADuplicatePathIsIdempotent()
    {
        Projection projection = Projection.Parse(Schema(), ["id", "id"]);
        Assert.Equal(1, projection.LeafCount);
    }

    [Fact]
    public void AnUnknownPathIsACallerError()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => Projection.Parse(Schema(), ["nope"]));
        Assert.Contains("nope", error.Message, StringComparison.Ordinal);

        ArgumentException nested = Assert.Throws<ArgumentException>(
            () => Projection.Parse(Schema(), ["payload.nope"]));
        Assert.Contains("payload.nope", nested.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APathThroughALeafIsACallerError()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => Projection.Parse(Schema(), ["id.deeper"]));
        Assert.Contains("id.deeper", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullPathIsRejected()
    {
        string?[] paths = [null];
        Assert.Throws<ArgumentNullException>(
            () => Projection.Parse(Schema(), new ReadOnlySpan<string>(paths!)));
    }

    [Fact]
    public void ANonStructRootAcceptsOnlyAll()
    {
        DTypeArena arena = new DTypeArena();
        DType scalar = arena.Primitive(PType.I64, Nullability.NonNullable);

        Assert.True(Projection.Parse(scalar, ReadOnlySpan<string>.Empty).IsAll);
        Assert.Throws<ArgumentException>(() => Projection.Parse(scalar, ["anything"]));
    }

    [Fact]
    public void ProjectedSchemaKeepsOrderAndNullability()
    {
        DType schema = Schema();
        DTypeArena target = new DTypeArena();

        // Selected out of order; the result must still be id, ts - the schema's own order.
        DType projected = Projection.Parse(schema, ["ts", "id"]).ProjectedSchema(schema, target);

        Assert.Equal(DTypeKind.Struct, projected.Kind);
        Assert.Equal(2, projected.FieldCount);
        Assert.Equal("id", projected.GetFieldName(0));
        Assert.Equal("ts", projected.GetFieldName(1));
        Assert.Equal(schema.Nullability, projected.Nullability);
    }

    [Fact]
    public void ProjectedSchemaNarrowsNestedStructs()
    {
        DType schema = Schema();
        DTypeArena target = new DTypeArena();
        DType projected = Projection.Parse(schema, ["payload.name"]).ProjectedSchema(schema, target);

        Assert.Equal(1, projected.FieldCount);
        Assert.Equal("payload", projected.GetFieldName(0));
        DType payload = projected.GetField(0);
        Assert.Equal(1, payload.FieldCount);
        Assert.Equal("name", payload.GetFieldName(0));
        Assert.Equal(DTypeKind.Utf8, payload.GetField(0).Kind);
    }

    [Fact]
    public void ProjectedSchemaOfAllIsTheSchema()
    {
        DType schema = Schema();
        DTypeArena target = new DTypeArena();
        Assert.Equal(schema, Projection.All.ProjectedSchema(schema, target));
    }

    [Fact]
    public void ProjectedSchemaRejectsADefaultSchema()
    {
        Assert.Throws<ArgumentException>(() => Projection.All.ProjectedSchema(default, new DTypeArena()));
        Assert.Throws<ArgumentNullException>(() => Projection.All.ProjectedSchema(Schema(), null!));
    }

    // ---------------------------------------------------------------- against the real schema

    [Fact]
    public async Task ADottedPathCannotAddressAFieldNamedWithADot()
    {
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path("types/struct_field_names"), CancellationToken.None);

        // The file really does have a field called "a.b", and a field called "a" that is an i32.
        Assert.True(file.Schema.IndexOfField("a.b") >= 0);

        // So the dotted grammar resolves "a.b" as a -> b and fails on the leaf. This is the
        // documented limitation, asserted rather than worked around.
        Assert.Throws<ArgumentException>(() => file.Scan().Project("a.b"));
    }

    [Fact]
    public async Task ProjectFieldsIsTheEscapeHatch()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path("types/struct_field_names"), CancellationToken.None);

        int dotted = file.Schema.IndexOfField("a.b");
        Assert.True(dotted >= 0);

        Span<int> fields = stackalloc int[1];
        fields[0] = dotted;

        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().ProjectFields(fields).ExecuteAsync())
        {
            Assert.Equal(1, batch.FieldCount);
            Assert.Equal("a.b", batch.GetFieldName(0));
            rows += batch.RowCount;
        }

        Assert.Equal(file.RowCount, rows);
    }

    [Fact]
    public async Task AnEmptyFieldNameIsReachableByIndexAndByPath()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path("types/struct_field_names"), CancellationToken.None);

        int empty = file.Schema.IndexOfField(string.Empty);
        Assert.True(empty >= 0);

        // The empty path happens to work, because "" splits into one empty segment.
        await foreach (RecordBatch batch in file.Scan().Project(string.Empty).ExecuteAsync())
        {
            Assert.Equal(1, batch.FieldCount);
            Assert.Equal(string.Empty, batch.GetFieldName(0));
        }
    }

    [Fact]
    public async Task ProjectFieldsRejectsAnIndexOutsideTheRoot()
    {
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path("types/struct_field_names"), CancellationToken.None);

        int count = file.Schema.FieldCount;
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            Span<int> fields = stackalloc int[1];
            fields[0] = count;
            file.Scan().ProjectFields(fields);
        });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            Span<int> fields = stackalloc int[1];
            fields[0] = -1;
            file.Scan().ProjectFields(fields);
        });
    }

    [Fact]
    public async Task ProjectingOnANonStructRootFileIsRejected()
    {
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path("containers/chunked_stream_3"), CancellationToken.None);

        Assert.False(file.IsTabular);
        Assert.Throws<ArgumentException>(() => file.Scan().Project("anything"));

        Assert.Throws<ArgumentException>(() =>
        {
            Span<int> fields = stackalloc int[1];
            fields[0] = 0;
            file.Scan().ProjectFields(fields);
        });
    }

    [Fact]
    public async Task ProjectionShapesTheBatchSchema()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path("containers/uncompressed_canonical"), CancellationToken.None);

        System.Collections.Generic.IAsyncEnumerable<RecordBatch> scan =
            file.Scan().Project("strs").ExecuteAsync();
        BatchAsyncEnumerable typed = Assert.IsType<BatchAsyncEnumerable>(scan);

        long rows = 0;
        await foreach (RecordBatch batch in scan)
        {
            Assert.Equal(1, batch.FieldCount);
            Assert.Equal("strs", batch.GetFieldName(0));

            // The promise the compiler made and the schema the layout produced must agree.
            Assert.Equal(typed.Schema, batch.Schema);
            rows += batch.RowCount;
        }

        Assert.Equal(file.RowCount, rows);
    }

    [Fact]
    public async Task ProjectingTwiceUnionsTheProjections()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path("containers/uncompressed_canonical"), CancellationToken.None);

        await foreach (RecordBatch batch in file.Scan().Project("strs").Project("ints").ExecuteAsync())
        {
            Assert.Equal(2, batch.FieldCount);

            // Union, not append: the schema's own order survives.
            Assert.Equal("ints", batch.GetFieldName(0));
            Assert.Equal("strs", batch.GetFieldName(1));
        }
    }
}
