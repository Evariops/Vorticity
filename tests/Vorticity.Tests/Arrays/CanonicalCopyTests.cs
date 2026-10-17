// The deep copy that three separate items were waiting on.
//
// `ArenaLifetimeTests` shows the problem: an index captured in batch 1 reads batch 2's values,
// because `BatchAsyncEnumerable` resets and refills ONE arena per batch. Anything that wants
// canonical data to outlive the batch that produced it -- a dictionary shared across chunks,
// `CanonicalConcat` across arenas, a chunk decoded once instead of once per batch -- needs the
// bytes materialized, not the record copied.
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
using Vorticity.Types.Numerics;
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

    /// <summary>
    /// A copy puts every buffer on a multiple of its values' width, whatever the destination arena
    /// carved before it.
    /// </summary>
    /// <remarks>
    /// The small blocks of an arena come out of one slab, end to end, so a copy that asked for no
    /// alignment started each buffer where the one before it ended: 32-bit values over an odd number
    /// of rows left the next 64-bit column four bytes off its boundary, and a reader that takes the
    /// values at their width refused them. Every kind is copied after a carve of each length from one
    /// to sixteen bytes, which leaves the slab at every residue a buffer can start from, the four
    /// bytes a writer rebases a list window's offsets into just before it copies the rows among them.
    /// </remarks>
    [Fact]
    public void ACopyPutsEveryBufferOnItsValuesWidthWhateverWasCarvedBefore()
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena source = new CanonicalArena();
        List<(string Name, int Node)> kinds = EveryKind(types, source);

        CanonicalArena keep = new CanonicalArena();
        for (int carved = 1; carved <= 16; carved++)
        {
            foreach ((string name, int node) in kinds)
            {
                keep.Allocate(carved, 1);
                AssertOnWidth(keep, keep.CopyFrom(source, node), $"{name} after {carved} carved bytes");
            }

            keep.Reset();
        }
    }

    /// <summary>
    /// Three rows of every kind a copy meets, an odd count so that narrow values end off the wide
    /// ones' boundary: each physical type bare and nullable, each decimal storage, text with a value
    /// out of its view, lists of each offset width, and the kinds that hold others.
    /// </summary>
    private static List<(string Name, int Node)> EveryKind(DTypeArena types, CanonicalArena arena)
    {
        const int Rows = 3;
        List<(string Name, int Node)> kinds = [];
        foreach (PType ptype in (PType[])[PType.U8, PType.U16, PType.U32, PType.U64, PType.I8, PType.I16, PType.I32, PType.I64, PType.F16, PType.F32, PType.F64])
        {
            kinds.Add((ptype.ToString(), Values(types, arena, ptype, Rows, nullable: false)));
            kinds.Add(($"nullable {ptype}", Values(types, arena, ptype, Rows, nullable: true)));
        }

        foreach ((DecimalStorageType storage, byte precision) in ((DecimalStorageType, byte)[])[
            (DecimalStorageType.I8, 2), (DecimalStorageType.I16, 4), (DecimalStorageType.I32, 9),
            (DecimalStorageType.I64, 18), (DecimalStorageType.I128, 38), (DecimalStorageType.I256, 76)])
        {
            VortexBuffer values = arena.Allocate(Rows * DecimalStorage.ByteWidth(storage), 16);
            kinds.Add(($"decimal {storage}", arena.AddDecimal(
                types.Decimal(precision, 0, Nullability.NonNullable), Rows, Validity.NonNullable, storage, precision, 0, values)));
        }

        VortexBuffer bits = arena.Allocate(1, 8, out Span<byte> bitBytes);
        bitBytes[0] = 0b101;
        kinds.Add(("bool", arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0)));
        int text = Text(types, arena);
        kinds.Add(("text", text));

        int elements = Values(types, arena, PType.U64, Rows, nullable: false);
        foreach ((PType offsets, PType sizes) in ((PType, PType)[])[(PType.U32, PType.U32), (PType.I64, PType.I64), (PType.U16, PType.U8)])
        {
            kinds.Add(($"list of {offsets} offsets and {sizes} sizes", List(types, arena, elements, offsets, sizes)));
        }

        DType u64 = types.Primitive(PType.U64, Nullability.NonNullable);
        int six = Values(types, arena, PType.U64, 2 * Rows, nullable: false);
        kinds.Add(("fixed-size list", arena.AddFixedSizeList(types.FixedSizeList(u64, 2, Nullability.NonNullable), Rows, Validity.NonNullable, six, 2)));

        int entries = Values(types, arena, PType.U64, 2, nullable: false);
        VortexBuffer codes = arena.Allocate(Rows * sizeof(uint), sizeof(uint), out Span<byte> codeBytes);
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(codeBytes)[..Rows].Fill(1);
        kinds.Add(("dictionary", arena.AddDictionary(u64, Rows, Validity.NonNullable, codes, entries)));
        VortexBuffer ends = arena.Allocate(2 * sizeof(uint), sizeof(uint), out Span<byte> endBytes);
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(endBytes)[0] = 1;
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(endBytes)[1] = Rows;
        kinds.Add(("run-end", arena.AddRunEnd(u64, Rows, Validity.NonNullable, ends, Values(types, arena, PType.U64, 2, nullable: false))));

        int narrow = Values(types, arena, PType.U32, Rows, nullable: false);
        int wide = Values(types, arena, PType.U64, Rows, nullable: false);
        int list = List(types, arena, elements, PType.U32, PType.U32);
        DType[] fields = [arena.GetNode(narrow).DType, u64, arena.GetNode(text).DType, arena.GetNode(list).DType];
        kinds.Add(("struct", arena.AddStruct(
            types.Struct(["narrow", "wide", "text", "list"], fields, Nullability.NonNullable), Rows, Validity.NonNullable, [narrow, wide, text, list])));
        return kinds;
    }

    /// <summary>Rows of <paramref name="ptype"/>, the middle one null when <paramref name="nullable"/>.</summary>
    private static int Values(DTypeArena types, CanonicalArena arena, PType ptype, int rows, bool nullable)
    {
        VortexBuffer values = arena.Allocate(rows * ptype.ByteWidth(), 16, out Span<byte> bytes);
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i + 1);
        }

        Validity validity = Validity.NonNullable;
        if (nullable)
        {
            VortexBuffer bits = arena.Allocate((rows + 7) / 8, 8, out Span<byte> bitBytes);
            for (int row = 0; row < rows; row++)
            {
                if (row != 1)
                {
                    bitBytes[row >> 3] |= (byte)(1 << (row & 7));
                }
            }

            validity = Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), rows, Validity.NonNullable, bits, 0));
        }

        return arena.AddPrimitive(
            types.Primitive(ptype, nullable ? Nullability.Nullable : Nullability.NonNullable), rows, validity, ptype, values);
    }

    /// <summary>Three strings, the first too long for its view, so that the copy gathers a data buffer.</summary>
    private static int Text(DTypeArena types, CanonicalArena arena)
    {
        byte[] spilled = System.Text.Encoding.UTF8.GetBytes("a value too long for its view");
        VortexBuffer data = arena.Allocate(spilled.Length, 1, out Span<byte> dataBytes);
        spilled.CopyTo(dataBytes);
        VortexBuffer views = arena.Allocate(3 * 16, 16, out Span<byte> viewBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(viewBytes, (uint)spilled.Length);
        spilled.AsSpan(0, 4).CopyTo(viewBytes[4..]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(viewBytes[16..], 1);
        viewBytes[20] = (byte)'x';
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(viewBytes[32..], 2);
        viewBytes[36] = (byte)'y';
        viewBytes[37] = (byte)'z';
        return arena.AddVarBinView(types.Utf8(Nullability.NonNullable), 3, Validity.NonNullable, views, [data]);
    }

    /// <summary>Three lists over <paramref name="elements"/>: one value, none, then two.</summary>
    private static int List(DTypeArena types, CanonicalArena arena, int elements, PType offsetPType, PType sizePType)
    {
        VortexBuffer offsets = arena.Allocate(3 * offsetPType.ByteWidth(), 16, out Span<byte> offsetBytes);
        VortexBuffer sizes = arena.Allocate(3 * sizePType.ByteWidth(), 16, out Span<byte> sizeBytes);
        ReadOnlySpan<byte> starts = [0, 1, 1];
        ReadOnlySpan<byte> lengths = [1, 0, 2];
        for (int row = 0; row < 3; row++)
        {
            offsetBytes[row * offsetPType.ByteWidth()] = starts[row];
            sizeBytes[row * sizePType.ByteWidth()] = lengths[row];
        }

        DType elementType = arena.GetNode(elements).DType;
        return arena.AddListView(
            types.List(elementType, Nullability.NonNullable), 3, Validity.NonNullable, elements, offsets, offsetPType, sizes, sizePType);
    }

    /// <summary>
    /// Walks the copy and requires every buffer read at a width to start on a multiple of it, up to
    /// the strictest alignment any reader asks for. A bitmap, a text heap and a constant's element
    /// are read a byte at a time.
    /// </summary>
    private static void AssertOnWidth(CanonicalArena arena, int index, string path)
    {
        CanonicalNode node = arena.GetNode(index);
        if (node.Validity.Kind == ValidityKind.Bitmap)
        {
            AssertOnWidth(arena, node.Validity.CanonicalNodeIndex, path + ", its validity");
        }

        switch (node.Kind)
        {
            case CanonicalKind.Primitive:
                AssertOn(node.Values, node.PType.ByteWidth(), path);
                break;
            case CanonicalKind.Decimal:
                AssertOn(node.Values, DecimalStorage.ByteWidth(node.Storage), path);
                break;
            case CanonicalKind.VarBinView:
                AssertOn(node.Views, 16, path + ", its views");
                break;
            case CanonicalKind.ListView:
                AssertOn(node.Offsets, node.OffsetPType.ByteWidth(), path + ", its offsets");
                AssertOn(node.Sizes, node.SizePType.ByteWidth(), path + ", its sizes");
                AssertOnWidth(arena, node.ElementsIndex, path + ", its elements");
                break;
            case CanonicalKind.FixedSizeList:
                AssertOnWidth(arena, node.ElementsIndex, path + ", its elements");
                break;
            case CanonicalKind.Struct:
                for (int field = 0; field < node.FieldCount; field++)
                {
                    AssertOnWidth(arena, node.GetFieldIndex(field), $"{path}, field {field}");
                }

                break;
            case CanonicalKind.Dictionary:
                AssertOn(node.Codes, sizeof(uint), path + ", its codes");
                AssertOnWidth(arena, node.EncodedValuesIndex, path + ", its values");
                break;
            case CanonicalKind.RunEnd:
                AssertOn(node.RunEnds, sizeof(uint), path + ", its ends");
                AssertOnWidth(arena, node.EncodedValuesIndex, path + ", its values");
                break;
        }
    }

    private static unsafe void AssertOn(VortexBuffer buffer, int width, string path)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        nuint alignment = (nuint)Math.Min(width, Vorticity.Arrays.Decoders.Canonical.CanonicalSupport.MaxRequiredAlignment);
        nuint address = (nuint)System.Runtime.CompilerServices.Unsafe.AsPointer(
            ref System.Runtime.InteropServices.MemoryMarshal.GetReference(buffer.Span));
        Assert.True(address % alignment == 0, $"{path}: {width}-byte values start {address % alignment} bytes past a multiple of {alignment}");
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
