// The deep copy that three separate items were waiting on.
//
// `ArenaLifetimeTests` shows the problem: an index captured in batch 1 reads batch 2's values,
// because `BatchAsyncEnumerable` resets and refills ONE arena per batch. Anything that wants
// canonical data to outlive the batch that produced it -- a dictionary shared across chunks (§3a),
// `CanonicalConcat` across arenas (§3c), a chunk decoded once instead of once per batch (the scan
// quadratic) -- needs the bytes materialized, not the record copied.
//
// SO THE TEST IS THE LIFETIME, NOT THE EQUALITY. Copying a node and comparing it to its source
// passes just as well when the copy is a view onto the source's storage, which is the bug. Every
// test here RESETS THE SOURCE ARENA before reading the copy, and one of them refills it with
// different data first -- because a reset alone returns blocks to the pool without necessarily
// disturbing them, and a copy that survives `Reset` but not the next tenant is still broken.
using System;
using System.Collections.Generic;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class CanonicalCopyTests
{
    /// <summary>A copied primitive column survives its source arena being reset and reused.</summary>
    [Fact]
    public void APrimitiveCopySurvivesTheSourceBeingRefilled()
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);

        CanonicalArena source = new CanonicalArena();
        CanonicalArena keep = new CanonicalArena();

        const int Rows = 512;
        int original = Primitive(source, i64, Rows, i => i * 7L);
        int copy = keep.CopyFrom(source, original);

        // Reset AND refill: the pool hands the same blocks back, so the copy is now reading storage
        // whose live tenant holds different values. A shallow copy reads 11s here.
        source.Reset();
        Primitive(source, i64, Rows, _ => 11L);

        CanonicalNode node = keep.GetNode(copy);
        Assert.Equal(Rows, node.Length);
        Assert.Equal(PType.I64, node.PType);
        for (int i = 0; i < Rows; i++)
        {
            Assert.Equal(i * 7L, ReadI64(node, i));
        }
    }

    /// <summary>A struct copies with its children, and the children survive too.</summary>
    [Fact]
    public void ChildrenAreCopiedNotReferenced()
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(["a", "b"], [i64, i64], Nullability.NonNullable);

        CanonicalArena source = new CanonicalArena();
        CanonicalArena keep = new CanonicalArena();

        const int Rows = 256;
        int a = Primitive(source, i64, Rows, i => i);
        int b = Primitive(source, i64, Rows, i => -i);
        int root = source.AddStruct(schema, Rows, Validity.NonNullable, [a, b]);
        int copy = keep.CopyFrom(source, root);

        source.Reset();
        Primitive(source, i64, Rows, _ => 99L);
        Primitive(source, i64, Rows, _ => 99L);

        CanonicalNode node = keep.GetNode(copy);
        Assert.Equal(CanonicalKind.Struct, node.Kind);
        Assert.Equal(2, node.FieldCount);

        CanonicalNode fieldA = keep.GetNode(node.GetFieldIndex(0));
        CanonicalNode fieldB = keep.GetNode(node.GetFieldIndex(1));
        for (int i = 0; i < Rows; i++)
        {
            Assert.Equal(i, ReadI64(fieldA, i));
            Assert.Equal(-i, ReadI64(fieldB, i));
        }
    }

    /// <summary>
    /// A nullable column's validity bitmap is a node, so it is copied like one.
    /// </summary>
    /// <remarks>
    /// The case most likely to be missed: validity does not live in the record's buffers, it names
    /// another canonical node. A copy that took the <see cref="Validity"/> struct verbatim would
    /// point at an index in the SOURCE arena -- valid-looking, and wrong as soon as that arena holds
    /// something else.
    /// </remarks>
    [Fact]
    public void TheValidityBitmapIsCopiedAsANode()
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.Nullable);
        DType boolType = types.Bool(Nullability.NonNullable);

        CanonicalArena source = new CanonicalArena();
        CanonicalArena keep = new CanonicalArena();

        const int Rows = 128;
        VortexBuffer bits = source.Allocate((Rows + 7) / 8, 1, out Span<byte> bitBytes);
        for (int i = 0; i < Rows; i++)
        {
            if (i % 3 != 0)
            {
                bitBytes[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        int mask = source.AddBool(boolType, Rows, Validity.NonNullable, bits, 0);
        int values = Primitive(source, i64, Rows, i => i, Validity.Bitmap(mask));
        int copy = keep.CopyFrom(source, values);

        source.Reset();
        Primitive(source, i64, Rows, _ => 0L);

        CanonicalNode node = keep.GetNode(copy);
        Assert.Equal(ValidityKind.Bitmap, node.Validity.Kind);

        // The copy's bitmap index must address the DESTINATION arena, and hold the same bits.
        CanonicalNode copiedMask = keep.GetNode(node.Validity.CanonicalNodeIndex);
        ReadOnlySpan<byte> copiedBits = copiedMask.Bits.Span;
        int valid = 0;
        for (int i = 0; i < Rows; i++)
        {
            bool isValid = (copiedBits[i >> 3] & (1 << (i & 7))) != 0;
            Assert.Equal(i % 3 != 0, isValid);
            valid += isValid ? 1 : 0;
        }

        Assert.Equal(Rows - ((Rows + 2) / 3), valid);
    }

    /// <summary>Copying within one arena is allowed, and does not alias the original.</summary>
    /// <remarks>
    /// Self-copy is the case that would crash on a `ref` into `_records` held across the recursive
    /// calls, because appending can resize that array. Worth a test rather than a comment.
    /// </remarks>
    [Fact]
    public void CopyingWithinOneArenaProducesAnIndependentNode()
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(["a"], [i64], Nullability.NonNullable);

        CanonicalArena arena = new CanonicalArena();
        const int Rows = 64;
        int a = Primitive(arena, i64, Rows, i => i);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [a]);

        int copy = arena.CopyFrom(arena, root);
        Assert.NotEqual(root, copy);

        CanonicalNode copied = arena.GetNode(copy);
        Assert.NotEqual(
            arena.GetNode(root).GetFieldIndex(0),
            copied.GetFieldIndex(0));

        CanonicalNode field = arena.GetNode(copied.GetFieldIndex(0));
        for (int i = 0; i < Rows; i++)
        {
            Assert.Equal(i, ReadI64(field, i));
        }
    }

    private static int Primitive(
        CanonicalArena arena, DType dtype, int rows, Func<int, long> value, Validity? validity = null)
    {
        VortexBuffer buffer = arena.Allocate(rows * sizeof(long), sizeof(long), out Span<byte> destination);
        Span<long> longs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(destination);
        for (int i = 0; i < rows; i++)
        {
            longs[i] = value(i);
        }

        return arena.AddPrimitive(dtype, rows, validity ?? Validity.NonNullable, PType.I64, buffer);
    }

    private static long ReadI64(CanonicalNode node, int index) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(
            node.Values.Span.Slice(index * sizeof(long), sizeof(long)));
}
