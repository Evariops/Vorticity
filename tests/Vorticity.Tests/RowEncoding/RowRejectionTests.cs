// What the encoder refuses, and why refusing is the right answer.
//
// The unsupported set is not a to-do list. For List, Map, Variant, Union and Decimal256 the
// format defines no ordering at all, and inventing one would produce keys no other implementation
// can compare against - a silent divergence, which is worse than an exception. Extension is the
// one that surprises people, because it takes timestamps and dates with it: upstream flags
// normalizing them to their storage arrays as a possible future addition, and until it lands the
// caller must normalize explicitly or our bytes would stop matching Rust's the day it does.
using System;
using Vorticity.Arrays;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Vorticity.RowEncoding;
using Xunit;

namespace Vorticity.Tests.RowEncoding;

public sealed class RowRejectionTests
{
    [Fact]
    public void ListIsRefused()
    {
        using RowFixture fixture = new RowFixture();
        DType list = fixture.Types.List(fixture.Types.Primitive(PType.I32, Nullability.NonNullable), Nullability.NonNullable);
        int column = fixture.Arena.AddBare(CanonicalKind.ListView, list, 1, Validity.NonNullable);

        VortexUnsupportedException error = Assert.Throws<VortexUnsupportedException>(
            () => RowEncoder.Encode(fixture.Arena, [column], [RowSortField.Ascending]));
        Assert.Equal(VortexComponentKind.DType, error.Kind);
        Assert.Contains("list", error.ComponentId, StringComparison.Ordinal);
    }

    [Fact]
    public void MapIsRefused()
    {
        using RowFixture fixture = new RowFixture();
        DType key = fixture.Types.Primitive(PType.I32, Nullability.NonNullable);
        DType map = fixture.Types.Map(key, key, keysSorted: false, Nullability.NonNullable);
        int column = fixture.Arena.AddBare(CanonicalKind.ListView, map, 1, Validity.NonNullable);

        Assert.Throws<VortexUnsupportedException>(
            () => RowEncoder.Encode(fixture.Arena, [column], [RowSortField.Ascending]));
    }

    [Fact]
    public void VariantIsRefused()
    {
        using RowFixture fixture = new RowFixture();
        DType variant = fixture.Types.Variant(Nullability.NonNullable);
        int column = fixture.Arena.AddBare(CanonicalKind.VarBinView, variant, 1, Validity.NonNullable);

        Assert.Throws<VortexUnsupportedException>(
            () => RowEncoder.Encode(fixture.Arena, [column], [RowSortField.Ascending]));
    }

    /// <summary>
    /// Extension is refused, and the message says what to do about it, because "row-encode a
    /// timestamp" is the request this rejection actually answers.
    /// </summary>
    [Fact]
    public void ExtensionIsRefusedWithAnActionableMessage()
    {
        using RowFixture fixture = new RowFixture();
        int storage = fixture.Primitive<long>(PType.I64, [1, 2]);
        DType extension = fixture.Types.Extension(
            "vortex.timestamp", fixture.Arena.GetNode(storage).DType, default);
        int column = fixture.Arena.AddExtension(extension, 2, storage);

        VortexUnsupportedException error = Assert.Throws<VortexUnsupportedException>(
            () => RowEncoder.Encode(fixture.Arena, [column], [RowSortField.Ascending]));
        Assert.Equal("vortex.timestamp", error.ComponentId);
        Assert.Contains("storage type", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nested extensions are refused too, and refused BEFORE a byte is written: the whole dtype is
    /// walked up front, so an unsupported schema costs nothing and cannot half-encode.
    /// </summary>
    [Fact]
    public void ExtensionNestedInAStructIsRefused()
    {
        using RowFixture fixture = new RowFixture();
        int storage = fixture.Primitive<long>(PType.I64, [1]);
        DType extension = fixture.Types.Extension(
            "vortex.date", fixture.Arena.GetNode(storage).DType, default);
        int inner = fixture.Arena.AddExtension(extension, 1, storage);
        int column = fixture.Struct(["when"], [inner], rows: 1);

        VortexUnsupportedException error = Assert.Throws<VortexUnsupportedException>(
            () => RowEncoder.Encode(fixture.Arena, [column], [RowSortField.Ascending]));
        Assert.Equal("vortex.date", error.ComponentId);
    }

    [Fact]
    public void Decimal256IsRefused()
    {
        using RowFixture fixture = new RowFixture();
        // Precision 40 implies i256 storage, which has no defined row encoding.
        DType dtype = fixture.Types.Decimal(precision: 40, scale: 0, Nullability.NonNullable);
        int column = fixture.Arena.AddBare(CanonicalKind.Decimal, dtype, 1, Validity.NonNullable);

        VortexUnsupportedException error = Assert.Throws<VortexUnsupportedException>(
            () => RowEncoder.Encode(fixture.Arena, [column], [RowSortField.Ascending]));
        Assert.Equal("decimal256", error.ComponentId);
    }

    /// <summary>
    /// A valid decimal too wide for the key its precision implies fails loudly. Silently
    /// truncating would produce a key that compares wrong, which is the one outcome this whole
    /// library exists to prevent.
    /// </summary>
    [Fact]
    public void ADecimalValueTooWideForItsKeyIsRefused()
    {
        using RowFixture fixture = new RowFixture();
        int column = fixture.Decimal(
            [10_000_000_000_000], precision: 7, scale: 5, DecimalStorageType.I64);

        VortexFormatException error = Assert.Throws<VortexFormatException>(
            () => RowEncoder.Encode(fixture.Arena, [column], [RowSortField.Ascending]));
        Assert.Contains("does not fit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoColumnsIsRefused()
    {
        using RowFixture fixture = new RowFixture();
        Assert.Throws<ArgumentException>(
            () => RowEncoder.Encode(fixture.Arena, [], []));
    }

    [Fact]
    public void AFieldCountThatDisagreesWithTheColumnCountIsRefused()
    {
        using RowFixture fixture = new RowFixture();
        int column = fixture.Primitive<int>(PType.I32, [1]);
        Assert.Throws<ArgumentException>(
            () => RowEncoder.Encode(
                fixture.Arena, [column], [RowSortField.Ascending, RowSortField.Ascending]));
    }

    [Fact]
    public void ColumnsOfDifferentLengthsAreRefused()
    {
        using RowFixture fixture = new RowFixture();
        int first = fixture.Primitive<int>(PType.I32, [1, 2]);
        int second = fixture.Primitive<int>(PType.I32, [1]);

        Assert.Throws<ArgumentException>(
            () => RowEncoder.Encode(
                fixture.Arena, [first, second], [RowSortField.Ascending, RowSortField.Ascending]));
    }

    /// <summary>
    /// A struct field shorter than its parent would desynchronize the cursors and corrupt a later
    /// column, so it is refused rather than discovered as a wrong byte somewhere downstream.
    /// </summary>
    [Fact]
    public void AStructFieldShorterThanItsParentIsRefused()
    {
        using RowFixture fixture = new RowFixture();
        int child = fixture.Primitive<int>(PType.I32, [1]);
        int column = fixture.Struct(["a"], [child], rows: 2);

        Assert.Throws<VortexFormatException>(
            () => RowEncoder.Encode(fixture.Arena, [column], [RowSortField.Ascending]));
    }
}
