// DTypeImport copies a dtype from one DTypeArena into another. Its walk has to be bounded, because
// a DTypeArena deduplicates: Struct(["a","b"], [d, d]) stores one child index twice, so a dtype
// nested that way 64 deep is a 65-node DAG with 2^64 root-to-leaf paths and the depth cap bounds
// only the stack (docs/03-architecture.md §6).
//
// But a FLAT visit budget bounds the wrong dimension. The same dedup means a struct of N
// identically-typed fields is one shared child node - four distinct nodes for any N - while costing
// N+1 visits. Rejecting that rejects an ordinary wide feature block as if the file were malformed,
// and nothing in the format, in DTypeArena or in VortexLimits caps a struct's field count. So the
// budget is a fast path and the memo is the bound: linear in DISTINCT source nodes, never a refusal.
using System;
using System.Diagnostics;

using Vorticity.Arrays;
using Vorticity.Scan;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class DTypeImportTests
{
    // Comfortably past the 4096-visit fast path, and the shape a 5000-column feature block takes.
    private const int WideFieldCount = 5000;

    [Fact]
    public void AWideStructOfIdenticallyTypedFieldsImportsWhole()
    {
        DTypeArena source = new DTypeArena();
        DType schema = WideSchema(source);

        // The source arena holds a handful of nodes for the whole thing: i64, i32, the inner
        // struct, the root. The cost of copying it is per VISIT, not per node.
        Assert.True(source.NodeCount < 16, $"the source arena holds {source.NodeCount} nodes");

        DTypeArena target = new DTypeArena();
        DType copied = DTypeImport.Into(target, schema.GetField(1));

        Assert.Equal(WideFieldCount, copied.FieldCount);
        Assert.True(copied.Equals(schema.GetField(1)));
        Assert.Same(target, copied.Arena);

        // Re-importing finds every node already interned.
        DType again = DTypeImport.Into(target, schema.GetField(1));
        Assert.Equal(copied.NodeIndex, again.NodeIndex);
    }

    [Fact]
    public void ProjectingAWideNestedStructSucceeds()
    {
        // The reachable path: BatchAsyncEnumerable's constructor calls ProjectedSchema for any
        // non-All projection, and a sub-mask of All sends the whole subtree through one import.
        DTypeArena source = new DTypeArena();
        DType schema = WideSchema(source);

        Projection projection = Projection.Parse(schema, ["payload"]);
        DType projected = projection.ProjectedSchema(schema, new DTypeArena());

        Assert.Equal(1, projected.FieldCount);
        Assert.Equal(WideFieldCount, projected.GetField(0).FieldCount);
    }

    [Fact]
    public void ADeeplySharedChildDagStillTerminatesQuickly()
    {
        // The hazard the budget was written for: 64 levels of Struct(["a","b"], [d, d]) is a
        // 65-node DAG with 2^64 distinct root-to-leaf paths. The memo has to make it linear.
        DTypeArena source = new DTypeArena();
        DType node = source.Primitive(PType.I32, Nullability.NonNullable);
        Span<int> names = stackalloc int[2];
        names[0] = source.InternName("a"u8);
        names[1] = source.InternName("b"u8);
        for (int i = 0; i < 62; i++)
        {
            Span<DType> children = [node, node];
            node = source.Struct(names, children, Nullability.NonNullable);
        }

        DTypeArena target = new DTypeArena();
        Stopwatch clock = Stopwatch.StartNew();
        DType copied = DTypeImport.Into(target, node);
        clock.Stop();

        Assert.True(copied.Equals(node));
        Assert.True(
            clock.ElapsedMilliseconds < 5_000,
            $"copying a 63-level shared-child DAG took {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>struct { id: i64, payload: struct { f0: i32, ..., f4999: i32 } }</summary>
    private static DType WideSchema(DTypeArena arena)
    {
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        int[] innerNames = new int[WideFieldCount];
        DType[] innerFields = new DType[WideFieldCount];
        for (int i = 0; i < WideFieldCount; i++)
        {
            innerNames[i] = arena.InternName(
                System.Text.Encoding.UTF8.GetBytes("f" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            innerFields[i] = i32;
        }

        DType payload = arena.Struct(innerNames, innerFields, Nullability.NonNullable);

        Span<int> rootNames = stackalloc int[2];
        rootNames[0] = arena.InternName("id"u8);
        rootNames[1] = arena.InternName("payload"u8);
        Span<DType> rootFields = [arena.Primitive(PType.I64, Nullability.NonNullable), payload];
        return arena.Struct(rootNames, rootFields, Nullability.NonNullable);
    }
}
