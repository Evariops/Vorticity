// The arena is the only place a decoder gets writable memory and the only thing standing between
// a decoder's arithmetic and a buffer that is one element short.
using System;
using Vorticity;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class CanonicalArenaTests
{
    private readonly DTypeArena _types = new DTypeArena();

    [Fact]
    public void AFreshArenaIsEmptyAndBoundsChecked()
    {
        CanonicalArena arena = new CanonicalArena();
        Assert.Equal(0, arena.NodeCount);
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(0).Index; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(-1).Index; });
    }

    [Fact]
    public void ANullNodeIsAllInvalid()
    {
        CanonicalArena arena = new CanonicalArena();
        int index = arena.AddNull(_types.Null(Nullability.Nullable), 1025);

        CanonicalNode node = arena.GetNode(index);
        Assert.Equal(CanonicalKind.Null, node.Kind);
        Assert.Equal(1025, node.Length);
        Assert.Equal(ValidityKind.AllInvalid, node.Validity.Kind);
        Assert.False(node.Validity.IsAllValid);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 7)]
    [InlineData(1, 0)]
    [InlineData(1023, 3)]
    [InlineData(1024, 0)]
    [InlineData(1025, 7)]
    [InlineData(8191, 5)]
    [InlineData(8192, 0)]
    [InlineData(8193, 1)]
    public void ABoolNodeNeedsEnoughBytesForItsBitsAtItsOffset(int length, int bitOffset)
    {
        CanonicalArena arena = new CanonicalArena();
        DType dtype = _types.Bool(Nullability.NonNullable);
        int needed = (bitOffset + length + 7) / 8;

        VortexBuffer exact = arena.Allocate(needed, 8);
        int index = arena.AddBool(dtype, length, Validity.NonNullable, exact, bitOffset);
        Assert.Equal(bitOffset, arena.GetNode(index).BitOffset);
        Assert.Equal(length, arena.GetNode(index).Length);

        if (needed > 0)
        {
            VortexBuffer tooShort = arena.Allocate(needed - 1, 8);
            Assert.Throws<VortexFormatException>(
                () => arena.AddBool(dtype, length, Validity.NonNullable, tooShort, bitOffset));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    [InlineData(255)]
    public void ABoolBitOffsetOutsideZeroToSevenIsMalformed(int bitOffset)
    {
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer bits = arena.Allocate(1024, 8);
        Assert.Throws<VortexFormatException>(
            () => arena.AddBool(_types.Bool(Nullability.NonNullable), 8, Validity.NonNullable, bits, bitOffset));
    }

    [Theory]
    [InlineData(PType.U8)]
    [InlineData(PType.I16)]
    [InlineData(PType.I32)]
    [InlineData(PType.I64)]
    [InlineData(PType.F16)]
    [InlineData(PType.F32)]
    [InlineData(PType.F64)]
    public void APrimitiveNodeNeedsExactlyLengthTimesWidthBytes(PType ptype)
    {
        CanonicalArena arena = new CanonicalArena();
        DType dtype = _types.Primitive(ptype, Nullability.NonNullable);
        int width = ptype.ByteWidth();

        VortexBuffer exact = arena.Allocate(1024 * width, 64);
        int index = arena.AddPrimitive(dtype, 1024, Validity.AllValid, ptype, exact);
        Assert.Equal(ptype, arena.GetNode(index).PType);
        Assert.Equal(1024 * width, arena.GetNode(index).Values.Length);

        VortexBuffer tooShort = arena.Allocate((1024 * width) - 1, 64);
        Assert.Throws<VortexFormatException>(
            () => arena.AddPrimitive(dtype, 1024, Validity.AllValid, ptype, tooShort));

        VortexBuffer tooLong = arena.Allocate((1024 * width) + 1, 64);
        Assert.Throws<VortexFormatException>(
            () => arena.AddPrimitive(dtype, 1024, Validity.AllValid, ptype, tooLong));
    }

    [Fact]
    public void AnUndefinedPTypeIsMalformed()
    {
        CanonicalArena arena = new CanonicalArena();
        Assert.Throws<VortexFormatException>(
            () => arena.AddPrimitive(
                _types.Bool(Nullability.NonNullable), 0, Validity.NonNullable, (PType)200, VortexBuffer.Empty));
    }

    [Theory]
    [InlineData(DecimalStorageType.I8, 1)]
    [InlineData(DecimalStorageType.I16, 2)]
    [InlineData(DecimalStorageType.I32, 4)]
    [InlineData(DecimalStorageType.I64, 8)]
    [InlineData(DecimalStorageType.I128, 16)]
    [InlineData(DecimalStorageType.I256, 32)]
    public void ADecimalNodeNeedsExactlyLengthTimesItsStorageWidth(DecimalStorageType storage, int width)
    {
        CanonicalArena arena = new CanonicalArena();
        DType dtype = _types.Decimal(40, 10, Nullability.NonNullable);

        VortexBuffer exact = arena.Allocate(64 * width, 64);
        int index = arena.AddDecimal(dtype, 64, Validity.AllValid, storage, 40, 10, exact);
        Assert.Equal(storage, arena.GetNode(index).Storage);
        Assert.Equal((byte)40, arena.GetNode(index).Precision);
        Assert.Equal((sbyte)10, arena.GetNode(index).Scale);

        VortexBuffer wrong = arena.Allocate((64 * width) + width, 64);
        Assert.Throws<VortexFormatException>(
            () => arena.AddDecimal(dtype, 64, Validity.AllValid, storage, 40, 10, wrong));
    }

    [Fact]
    public void AnUndefinedDecimalStorageIsMalformed()
    {
        CanonicalArena arena = new CanonicalArena();
        Assert.Throws<VortexFormatException>(
            () => arena.AddDecimal(
                _types.Decimal(4, 2, Nullability.NonNullable),
                0,
                Validity.NonNullable,
                (DecimalStorageType)6,
                4,
                2,
                VortexBuffer.Empty));
    }

    [Fact]
    public void AVarBinViewNeedsSixteenBytesPerRowAndKeepsItsDataBuffers()
    {
        CanonicalArena arena = new CanonicalArena();
        DType dtype = _types.Utf8(Nullability.Nullable);

        VortexBuffer views = arena.Allocate(3 * 16, 64);
        VortexBuffer a = arena.Allocate(8, 8);
        VortexBuffer b = arena.Allocate(16, 8);

        int index = arena.AddVarBinView(dtype, 3, Validity.AllValid, views, [a, b]);
        CanonicalNode node = arena.GetNode(index);

        Assert.Equal(2, node.DataBufferCount);
        Assert.Equal(8, node.GetDataBuffer(0).Length);
        Assert.Equal(16, node.GetDataBuffer(1).Length);
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(index).GetDataBuffer(2).Length; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(index).GetDataBuffer(-1).Length; });

        VortexBuffer ragged = arena.Allocate((3 * 16) - 1, 64);
        Assert.Throws<VortexFormatException>(
            () => arena.AddVarBinView(dtype, 3, Validity.AllValid, ragged, []));
    }

    [Fact]
    public void AListViewCarriesItsOwnOffsetAndSizePTypes()
    {
        CanonicalArena arena = new CanonicalArena();
        DType element = _types.Primitive(PType.I32, Nullability.NonNullable);
        DType dtype = _types.List(element, Nullability.NonNullable);

        VortexBuffer values = arena.Allocate(10 * 4, 64);
        int elements = arena.AddPrimitive(element, 10, Validity.NonNullable, PType.I32, values);

        VortexBuffer offsets = arena.Allocate(4 * 8, 64);
        VortexBuffer sizes = arena.Allocate(4 * 4, 64);
        int index = arena.AddListView(
            dtype, 4, Validity.NonNullable, elements, offsets, PType.U64, sizes, PType.U32);

        CanonicalNode node = arena.GetNode(index);
        Assert.Equal(elements, node.ElementsIndex);
        Assert.Equal(PType.U64, node.OffsetPType);
        Assert.Equal(PType.U32, node.SizePType);
        Assert.Equal(32, node.Offsets.Length);
        Assert.Equal(16, node.Sizes.Length);
    }

    [Fact]
    public void AListViewWithAMisSizedOffsetBufferIsMalformed()
    {
        CanonicalArena arena = new CanonicalArena();
        DType element = _types.Primitive(PType.I32, Nullability.NonNullable);
        DType dtype = _types.List(element, Nullability.NonNullable);
        int elements = arena.AddPrimitive(
            element, 0, Validity.NonNullable, PType.I32, VortexBuffer.Empty);

        VortexBuffer offsets = arena.Allocate(3 * 8, 64);
        VortexBuffer sizes = arena.Allocate(4 * 4, 64);
        Assert.Throws<VortexFormatException>(
            () => arena.AddListView(
                dtype, 4, Validity.NonNullable, elements, offsets, PType.U64, sizes, PType.U32));
    }

    [Fact]
    public void AFixedSizeListOfSizeZeroIsLegal()
    {
        // Upstream special-cases it because elements.len() / 0 is undefined.
        CanonicalArena arena = new CanonicalArena();
        DType element = _types.Primitive(PType.I32, Nullability.NonNullable);
        int elements = arena.AddPrimitive(
            element, 0, Validity.NonNullable, PType.I32, VortexBuffer.Empty);

        int index = arena.AddFixedSizeList(
            _types.FixedSizeList(element, 0, Nullability.NonNullable), 8, Validity.NonNullable, elements, 0);

        CanonicalNode node = arena.GetNode(index);
        Assert.Equal(0u, node.FixedSize);
        Assert.Equal(8, node.Length);
        Assert.Equal(elements, node.ElementsIndex);
    }

    [Fact]
    public void AStructExposesItsFieldsInOrder()
    {
        CanonicalArena arena = new CanonicalArena();
        DType i32 = _types.Primitive(PType.I32, Nullability.NonNullable);
        DType utf8 = _types.Utf8(Nullability.NonNullable);

        int a = arena.AddPrimitive(i32, 0, Validity.NonNullable, PType.I32, VortexBuffer.Empty);
        int b = arena.AddVarBinView(utf8, 0, Validity.NonNullable, VortexBuffer.Empty, []);

        DType dtype = _types.Struct(["a", "b"], [i32, utf8], Nullability.NonNullable);
        int index = arena.AddStruct(dtype, 0, Validity.NonNullable, [a, b]);

        CanonicalNode node = arena.GetNode(index);
        Assert.Equal(2, node.FieldCount);
        Assert.Equal(a, node.GetFieldIndex(0));
        Assert.Equal(b, node.GetFieldIndex(1));
        Assert.Throws<VortexFormatException>(() => arena.GetNode(index).GetFieldIndex(2));
        Assert.Throws<VortexFormatException>(() => arena.GetNode(index).GetFieldIndex(-1));
    }

    [Fact]
    public void AChildIndexThatDoesNotExistYetIsMalformed()
    {
        CanonicalArena arena = new CanonicalArena();
        DType i32 = _types.Primitive(PType.I32, Nullability.NonNullable);
        DType dtype = _types.Struct(["a"], [i32], Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(
            () => arena.AddStruct(dtype, 0, Validity.NonNullable, [7]));
    }

    [Fact]
    public void AnExtensionInheritsItsStoragesValidity()
    {
        CanonicalArena arena = new CanonicalArena();
        DType storageType = _types.Primitive(PType.I64, Nullability.Nullable);
        int storage = arena.AddPrimitive(
            storageType, 0, Validity.AllInvalid, PType.I64, VortexBuffer.Empty);

        DType dtype = _types.Extension("vortex.timestamp", storageType, [2, 0, 0]);
        int index = arena.AddExtension(dtype, 0, storage);

        CanonicalNode node = arena.GetNode(index);
        Assert.Equal(CanonicalKind.Extension, node.Kind);
        Assert.Equal(storage, node.StorageIndex);
        Assert.Equal(ValidityKind.AllInvalid, node.Validity.Kind);
    }

    [Fact]
    public void ReadingAFieldOfTheWrongKindIsAFormatErrorAndNotACast()
    {
        CanonicalArena arena = new CanonicalArena();
        int index = arena.AddNull(_types.Null(Nullability.Nullable), 4);

        // A CanonicalNode is a ref struct and cannot be captured, so each case re-fetches it.
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(index).Bits.Length; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(index).BitOffset; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(index).PType; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(index).Values.Length; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(index).Storage; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(index).Precision; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(index).Scale; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(index).Views.Length; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(index).DataBufferCount; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(index).ElementsIndex; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(index).Offsets.Length; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(index).Sizes.Length; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(index).OffsetPType; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(index).SizePType; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(index).FixedSize; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(index).FieldCount; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(index).StorageIndex; });
    }

    [Fact]
    public void ANegativeLengthIsMalformed()
    {
        CanonicalArena arena = new CanonicalArena();
        Assert.Throws<VortexFormatException>(
            () => arena.AddNull(_types.Null(Nullability.Nullable), -1));
    }

    /// <summary>The bound moves with the enum: 9 is `Constant`, 10 is nothing.</summary>
    [Fact]
    public void AnUndefinedCanonicalKindIsMalformed()
    {
        CanonicalArena arena = new CanonicalArena();
        Assert.Throws<VortexFormatException>(
            () => arena.AddBare((CanonicalKind)10, _types.Bool(Nullability.NonNullable), 0, Validity.NonNullable));

        // And the last defined kind is accepted, so the test says where the bound IS and not only
        // where it is not.
        int bare = arena.AddBare(
            CanonicalKind.Constant, _types.Bool(Nullability.NonNullable), 0, Validity.NonNullable);
        Assert.Equal(CanonicalKind.Constant, arena.GetNode(bare).Kind);
    }

    [Fact]
    public void AllocateReturnsZeroedMemoryEvenAfterTheBlockHasBeenRecycled()
    {
        // The pool hands back recycled NATIVE memory. A decoder that writes only part of a buffer
        // would otherwise publish whatever the previous batch left there.
        AlignedBufferPool pool = new AlignedBufferPool();
        CanonicalArena arena = new CanonicalArena(8, pool);

        VortexBuffer first = arena.Allocate(256, 64, out Span<byte> firstSpan);
        Assert.Equal(256, first.Length);
        firstSpan.Fill(0xCD);
        arena.Reset();

        VortexBuffer second = arena.Allocate(256, 64, out Span<byte> secondSpan);
        Assert.Equal(256, second.Length);
        foreach (byte b in secondSpan)
        {
            Assert.Equal(0, b);
        }

        arena.Reset();
    }

    [Fact]
    public void AllocateHonoursTheRequestedAlignment()
    {
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer buffer = arena.Allocate(64, 64);
        Assert.True(buffer.IsAligned);
        arena.Reset();
    }

    [Fact]
    public void AllocateRejectsANegativeLengthAndABadAlignment()
    {
        CanonicalArena arena = new CanonicalArena();
        Assert.Throws<VortexFormatException>(() => arena.Allocate(-1, 8));
        Assert.Throws<VortexFormatException>(() => arena.Allocate(8, 3));
        Assert.Throws<VortexFormatException>(() => arena.Allocate(8, 128));
    }

    [Fact]
    public void ResetClearsTheNodesAndReleasesTheRentedBlocks()
    {
        AlignedBufferPool pool = new AlignedBufferPool();
        CanonicalArena arena = new CanonicalArena(8, pool);
        arena.Allocate(128, 8);
        arena.AddNull(_types.Null(Nullability.Nullable), 1);
        Assert.Equal(1, arena.NodeCount);

        arena.Reset();
        Assert.Equal(0, arena.NodeCount);

        // A second Reset must be a no-op, not a double return to the pool.
        arena.Reset();
        Assert.Equal(0, arena.NodeCount);
    }

    [Fact]
    public void ReusingOneArenaAcrossBatchesAllocatesNothingAfterWarmUp()
    {
        // A dedicated pool with room for every block this test rents. AlignedBufferPool's default
        // MaxPerBucket is 8, so a batch that materializes more than eight buffers of one size class
        // makes the pool free the surplus and allocate a fresh NativeSegmentOwner next time round -
        // a real, if small, per-batch allocation that belongs to the pool's policy and not to the
        // arena. This asserts the arena's own contribution, which is zero.
        AlignedBufferPool pool = new AlignedBufferPool(maxPooledLength: 1 << 20, maxPerBucket: 32);
        CanonicalArena arena = new CanonicalArena(64, pool);
        DType i32 = _types.Primitive(PType.I32, Nullability.NonNullable);

        for (int warm = 0; warm < 8; warm++)
        {
            Fill(arena, i32);
            arena.Reset();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int batch = 0; batch < 32; batch++)
        {
            Fill(arena, i32);
            arena.Reset();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        static void Fill(CanonicalArena arena, DType i32)
        {
            for (int i = 0; i < 16; i++)
            {
                VortexBuffer values = arena.Allocate(4 * 8, 64);
                arena.AddPrimitive(i32, 8, Validity.AllValid, PType.I32, values);
            }
        }
    }

    [Fact]
    public void ConstructionRejectsBadArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanonicalArena(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanonicalArena(-1));
        Assert.Throws<ArgumentNullException>(() => new CanonicalArena(4, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArrayNodeArena(0));
    }

    /// <summary>
    /// A constant node stores its element once and reports the row count it stands for.
    /// </summary>
    /// <remarks>
    /// THE WHOLE CLAIM OF THE FORM IS THE ASYMMETRY between the two numbers below: eight bytes held
    /// against a million rows.
    /// </remarks>
    [Fact]
    public void AConstantNodeHoldsOneElementForAnyNumberOfRows()
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        ReadOnlySpan<byte> element = [1, 2, 3, 4, 5, 6, 7, 8];

        int node = arena.AddConstant(
            types.Primitive(PType.I64, Nullability.NonNullable), 1_000_000, Validity.NonNullable, element);

        CanonicalNode read = arena.GetNode(node);
        Assert.Equal(CanonicalKind.Constant, read.Kind);
        Assert.Equal(1_000_000, read.Length);
        Assert.True(element.SequenceEqual(read.ConstantElement));
        Assert.Equal(8, read.ConstantElement.Length);
    }

    /// <summary>An element is required: a constant with nothing to repeat is malformed.</summary>
    [Fact]
    public void AConstantNodeWithoutAnElementIsRejected()
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        Assert.Throws<VortexFormatException>(() => arena.AddConstant(
            types.Primitive(PType.I64, Nullability.NonNullable), 4, Validity.NonNullable, default));
    }

}
