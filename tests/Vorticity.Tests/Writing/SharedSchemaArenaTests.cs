using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>
/// A schema's dtypes are shared: a dataset's writers start from its one schema side by side, and a file's lanes read its
/// one schema. An arena is not safe to grow from two threads, so neither a writer's stores nor a read's filling of a
/// column with nulls derives a dtype in the schema's arena: what they compose lies in an arena of their own.
/// </summary>
public sealed class SharedSchemaArenaTests
{
    private static readonly VortexSchema Schema =
    [
        ("attributes", VortexType.Variant.Nullable),
        ("labels", VortexType.Map(VortexType.Utf8, VortexType.Int64)),
    ];

    [Fact]
    public void AWritersStoresDeriveNothingInTheSchemasArena()
    {
        DTypeArena arena = new();
        DType schema = VortexTypes.ToDType(Schema, arena);
        int before = arena.NodeCount;
        StructStore root = ColumnStores.Root(schema, TestAccount.Shared, null);
        Assert.Equal(before, arena.NodeCount);
        Assert.Equal(2, root.Children.Length);
    }

    [Fact]
    public void ANullColumnOfVariantsDerivesNothingInItsDtypesArena()
    {
        DTypeArena arena = new();
        DType schema = VortexTypes.ToDType(Schema, arena);
        int before = arena.NodeCount;
        CanonicalArena canonical = new(16, TestAccount.Shared);
        CanonicalFill.BuildZeroed(canonical, schema.GetField(0), 4, Validity.AllInvalid);
        Assert.Equal(before, arena.NodeCount);
    }
}
