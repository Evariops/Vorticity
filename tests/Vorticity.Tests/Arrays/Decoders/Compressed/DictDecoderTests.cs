using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class DictDecoderTests
{
    [Fact]
    public void GathersValuesThroughTheCodes()
    {
        TestNode root = Node(TestMetadata.Dict(3, PType.U8, false));
        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.Bytes(0, 2, 1, 2, 0), TestBuffers.Int32(10, 20, 30));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 5));
        Assert.Equal([10, 30, 20, 30, 10], ForDecoderTests.ReadInt32(node));
    }

    [Fact]
    public void ACodeEqualToValuesLengthIsRejected()
    {
        TestNode root = Node(TestMetadata.Dict(3, PType.U8, false));
        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.Bytes(0, 3), TestBuffers.Int32(10, 20, 30));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 2));
    }

    [Fact]
    public void ANegativeCodeIsRejected()
    {
        TestNode root = Node(TestMetadata.Dict(3, PType.I8, false));
        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.Bytes(0, 0xFF), TestBuffers.Int32(10, 20, 30));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 2));
    }

    [Theory]
    [InlineData(null, Nullability.Nullable, Nullability.Nullable)]
    [InlineData(false, Nullability.Nullable, Nullability.NonNullable)]
    [InlineData(true, Nullability.Nullable, Nullability.Nullable)]
    [InlineData(null, Nullability.NonNullable, Nullability.NonNullable)]
    [InlineData(false, Nullability.NonNullable, Nullability.NonNullable)]
    [InlineData(true, Nullability.NonNullable, Nullability.Nullable)]
    internal void IsNullableCodesDecidesTheCodesChildDType(
        bool? isNullableCodes, Nullability arrayNullability, Nullability expectedCodesNullability)
    {
        // Absent is a back-compat fallback to the ARRAY's nullability and is not
        // the same as `false`. Getting it wrong changes the codes child's dtype, which changes how
        // its own validity child is interpreted, which silently changes values.
        TestNode root = Node(TestMetadata.Dict(2, PType.U8, isNullableCodes));
        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.Bytes(0, 1), TestBuffers.Int32(10, 20));

        DType dtype = harness.Types.Primitive(PType.I32, arrayNullability);
        harness.DecodeRoot(dtype, 2);

        Assert.Equal(expectedCodesNullability, FindCodesNode(harness).DType.Nullability);
    }

    [Fact]
    public void NullableCodesOverANonNullableArrayRejectTheNullRow()
    {
        TestNode root = new TestNode("vortex.dict")
            .WithMetadata(TestMetadata.Dict(2, PType.U8, true))
            .WithChild(new TestNode("vortex.primitive")
                .WithBuffer(0)
                .WithChild(new TestNode("vortex.bool").WithBuffer(2)))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.Bytes(0, 1), TestBuffers.Int32(10, 20),
            TestBuffers.Bitmap(true, false));

        DType nonNullable = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(nonNullable, 2));
    }

    private static CanonicalNode FindCodesNode(DecodeHarness harness)
    {
        for (int i = 0; i < harness.Scan.Canonical.NodeCount; i++)
        {
            CanonicalNode node = harness.Node(i);
            if (node.Kind == CanonicalKind.Primitive && node.PType == PType.U8)
            {
                return node;
            }
        }

        Assert.Fail("no codes node was decoded");
        return default;
    }

    [Fact]
    public void ANullCodeProducesANullRow()
    {
        TestNode root = new TestNode("vortex.dict")
            .WithMetadata(TestMetadata.Dict(2, PType.U8, true))
            .WithChild(new TestNode("vortex.primitive")
                .WithBuffer(0)
                .WithChild(new TestNode("vortex.bool").WithBuffer(2)))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestBuffers.Bytes(0, 1, 0),
            TestBuffers.Int32(10, 20),
            TestBuffers.Bitmap(true, false, true));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 3));

        Assert.Equal(ValidityKind.Bitmap, node.Validity.Kind);
        CanonicalNode bits = harness.Node(node.Validity.CanonicalNodeIndex);
        Assert.Equal(0b101, bits.Bits.Span[0] & 0b111);
        Assert.Equal([10, 0, 10], ForDecoderTests.ReadInt32(node));
    }

    [Fact]
    public void ANullDictionaryValueMakesEveryReferencingRowNull()
    {
        TestNode root = new TestNode("vortex.dict")
            .WithMetadata(TestMetadata.Dict(2, PType.U8, false))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive")
                .WithBuffer(1)
                .WithChild(new TestNode("vortex.bool").WithBuffer(2)));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestBuffers.Bytes(0, 1, 1, 0),
            TestBuffers.Int32(10, 20),
            TestBuffers.Bitmap(true, false));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 4));
        CanonicalNode bits = harness.Node(node.Validity.CanonicalNodeIndex);
        Assert.Equal(0b1001, bits.Bits.Span[0] & 0b1111);
    }

    [Fact]
    public void FloatKeysAreCopiedByBitPatternAndNeverCanonicalized()
    {
        // Corpus manifest caveat 2: distributions/float_specials_f64_* keep -0.0 and +0.0 and
        // several NaN payloads as distinct keys. A decoder that compares by IEEE equality, or that
        // normalizes a NaN, produces a different dictionary and a different answer.
        double[] keys = [0.0, -0.0, double.NaN, BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0001UL)];
        byte[] values = new byte[keys.Length * 8];
        for (int i = 0; i < keys.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(values.AsSpan(i * 8), keys[i]);
        }

        TestNode root = Node(TestMetadata.Dict((uint)keys.Length, PType.U8, false));
        using DecodeHarness harness = DecodeHarness.Load(root, TestBuffers.Bytes(3, 1, 0, 2), values);

        DType f64 = harness.Types.Primitive(PType.F64, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(f64, 4));

        ulong[] expected =
        [
            0x7FF8_0000_0000_0001UL,
            BitConverter.DoubleToUInt64Bits(-0.0),
            BitConverter.DoubleToUInt64Bits(0.0),
            BitConverter.DoubleToUInt64Bits(double.NaN),
        ];

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(
                expected[i],
                BinaryPrimitives.ReadUInt64LittleEndian(node.Values.Span.Slice(i * 8, 8)));
        }
    }

    [Fact]
    public void GathersVarBinViewValues()
    {
        // encodings/dict is exactly this shape: utf8 values under a varbinview child.
        byte[] data = Encoding.UTF8.GetBytes("alphabravocharlie-a-very-long-value-here");
        byte[] views = new byte[3 * 16];
        WriteView(views.AsSpan(0, 16), data, 0, 5);            // "alpha", inline
        WriteView(views.AsSpan(16, 16), data, 5, 5);           // "bravo", inline
        WriteView(views.AsSpan(32, 16), data, 10, data.Length - 10);  // long, referenced

        TestNode root = new TestNode("vortex.dict")
            .WithMetadata(TestMetadata.Dict(3, PType.U8, false))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.varbinview").WithBuffer(1).WithBuffer(2));

        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.Bytes(2, 0, 1, 2), data, views);

        DType utf8 = harness.Types.Utf8(Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(utf8, 4));

        Assert.Equal(CanonicalKind.VarBinView, node.Kind);
        Assert.Equal(1, node.DataBufferCount);
        Assert.Equal("charlie-a-very-long-value-here", ReadValue(node, 0));
        Assert.Equal("alpha", ReadValue(node, 1));
        Assert.Equal("bravo", ReadValue(node, 2));
        Assert.Equal("charlie-a-very-long-value-here", ReadValue(node, 3));
    }

    [Fact]
    public void AWrongChildCountIsRejected()
    {
        TestNode root = new TestNode("vortex.dict")
            .WithMetadata(TestMetadata.Dict(1, PType.U8, false))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, TestBuffers.Bytes(0));
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 1));
    }

    [Fact]
    public void ABufferOnTheNodeIsRejected()
    {
        TestNode root = new TestNode("vortex.dict")
            .WithMetadata(TestMetadata.Dict(1, PType.U8, false))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.Bytes(0), TestBuffers.Int32(1));
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 1));
    }

    [Fact]
    public void AFloatCodesPTypeIsRejected()
    {
        TestNode root = Node(TestMetadata.Dict(1, PType.F32, false));
        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.UInt32(0), TestBuffers.Int32(1));
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    public void DecodesTheBoundaryRowCounts(int rows)
    {
        byte[] codes = new byte[rows];
        for (int i = 0; i < rows; i++)
        {
            codes[i] = (byte)(i % 4);
        }

        TestNode root = Node(TestMetadata.Dict(4, PType.U8, false));
        using DecodeHarness harness = DecodeHarness.Load(
            root, codes, TestBuffers.Int32(100, 200, 300, 400));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, rows));
        int[] decoded = ForDecoderTests.ReadInt32(node);
        for (int i = 0; i < rows; i++)
        {
            Assert.Equal(100 * ((i % 4) + 1), decoded[i]);
        }
    }

    [Fact]
    public void ATakeOfAFewRowsDecodesOnlyTheEntriesTheyName()
    {
        // Sixty-four entries, some past the twelve bytes a view holds inline; three rows taken, two
        // of them naming the same entry.
        (TestNode root, byte[][] buffers, string[] entries, byte[] codes) = TextDictionary(64, rows: 200, nullableCodes: false, nullEntry: -1);
        using DecodeHarness harness = DecodeHarness.Load(root, buffers);

        int[] wanted = [3, 67, 150];
        DType utf8 = harness.Types.Utf8(Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRootSelected(utf8, 200, wanted));

        Assert.Equal(3, node.Length);
        for (int i = 0; i < wanted.Length; i++)
        {
            Assert.Equal(entries[codes[wanted[i]]], ReadValue(node, i));
        }

        Assert.Equal(0, WholeEntries(harness, 64));
    }

    [Fact]
    public void ANarrowedTakeKeepsTheNullsOfTheCodesAndOfTheEntries()
    {
        (TestNode root, byte[][] buffers, string[] entries, byte[] codes) = TextDictionary(64, rows: 200, nullableCodes: true, nullEntry: 5);
        using DecodeHarness harness = DecodeHarness.Load(root, buffers);

        // Row 7 is a null code, row 5 names the null entry, row 11 a valid one.
        int[] wanted = [5, 7, 11];
        DType utf8 = harness.Types.Utf8(Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRootSelected(utf8, 200, wanted));

        Assert.False(harness.IsValid(node, 0));
        Assert.False(harness.IsValid(node, 1));
        Assert.True(harness.IsValid(node, 2));
        Assert.Equal(entries[codes[11]], ReadValue(node, 2));
    }

    [Fact]
    public void ATakeNamingOnlyNullEntriesReadsNullRows()
    {
        // The entries it names are all null, which the whole dictionary is not: the narrowed values
        // carry no bitmap at all.
        (TestNode root, byte[][] buffers, _, _) = TextDictionary(64, rows: 200, nullableCodes: true, nullEntry: 5);
        using DecodeHarness harness = DecodeHarness.Load(root, buffers);

        CanonicalNode node = harness.Node(harness.DecodeRootSelected(harness.Types.Utf8(Nullability.Nullable), 200, [5, 7]));

        Assert.Equal(2, node.Length);
        Assert.False(harness.IsValid(node, 0));
        Assert.False(harness.IsValid(node, 1));
    }

    [Fact]
    public void ATakeOfManyRowsDecodesTheEntriesWhole()
    {
        (TestNode root, byte[][] buffers, string[] entries, byte[] codes) = TextDictionary(64, rows: 200, nullableCodes: false, nullEntry: -1);
        using DecodeHarness harness = DecodeHarness.Load(root, buffers);

        // Sixteen rows name up to sixteen entries, a quarter of them: past the bar.
        int[] wanted = [.. Enumerable.Range(0, 16).Select(i => i * 12)];
        CanonicalNode node = harness.Node(harness.DecodeRootSelected(harness.Types.Utf8(Nullability.NonNullable), 200, wanted));

        for (int i = 0; i < wanted.Length; i++)
        {
            Assert.Equal(entries[codes[wanted[i]]], ReadValue(node, i));
        }

        Assert.Equal(1, WholeEntries(harness, 64));
    }

    [Fact]
    public void ANarrowedTakeRefusesACodePastTheEntries()
    {
        (TestNode root, byte[][] buffers, _, byte[] codes) = TextDictionary(64, rows: 200, nullableCodes: false, nullEntry: -1);
        codes[67] = 64;
        using DecodeHarness harness = DecodeHarness.Load(root, buffers);

        Assert.Throws<VortexFormatException>(
            () => harness.DecodeRootSelected(harness.Types.Utf8(Nullability.NonNullable), 200, [3, 67]));
    }

    /// <summary>
    /// A dictionary of <paramref name="count"/> text entries under <c>vortex.varbin</c>, which
    /// reaches an entry without decoding the others, and <paramref name="rows"/> codes over it.
    /// </summary>
    private static (TestNode Root, byte[][] Buffers, string[] Entries, byte[] Codes) TextDictionary(
        int count, int rows, bool nullableCodes, int nullEntry)
    {
        string[] entries = [.. Enumerable.Range(0, count).Select(i => i % 3 == 0 ? $"entry-{i:D2}-longer-than-a-view" : $"e{i}")];
        byte[] heap = Encoding.UTF8.GetBytes(string.Concat(entries));
        int[] offsets = new int[count + 1];
        for (int i = 0; i < count; i++)
        {
            offsets[i + 1] = offsets[i] + Encoding.UTF8.GetByteCount(entries[i]);
        }

        byte[] codes = new byte[rows];
        for (int row = 0; row < rows; row++)
        {
            codes[row] = (byte)((row * 37) % count);
        }

        // Row 67 names the same entry as row 3.
        codes[67] = codes[3];
        codes[5] = (byte)Math.Max(nullEntry, 0);

        TestNode values = new TestNode("vortex.varbin")
            .WithMetadata(Canonical.TestMetadata.VarBin(PType.I32))
            .WithBuffer(1)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2));
        List<byte[]> buffers = [codes, heap, TestBuffers.Int32(offsets)];
        if (nullEntry >= 0)
        {
            values = values.WithChild(new TestNode("vortex.bool").WithMetadata(Canonical.TestMetadata.Bool(0)).WithBuffer(buffers.Count));
            buffers.Add(TestBuffers.Bitmap([.. Enumerable.Range(0, count).Select(i => i != nullEntry)]));
        }

        TestNode codesNode = new TestNode("vortex.primitive").WithBuffer(0);
        if (nullableCodes)
        {
            codesNode = codesNode.WithChild(new TestNode("vortex.bool").WithBuffer(buffers.Count));
            buffers.Add(TestBuffers.Bitmap([.. Enumerable.Range(0, rows).Select(row => row != 7)]));
        }

        TestNode root = new TestNode("vortex.dict")
            .WithMetadata(TestMetadata.Dict((uint)count, PType.U8, nullableCodes))
            .WithChild(codesNode)
            .WithChild(values);
        return (root, [.. buffers], entries, codes);
    }

    /// <summary>How many times the decode left the dictionary's <paramref name="count"/> entries whole in the arena.</summary>
    private static int WholeEntries(DecodeHarness harness, int count)
    {
        int found = 0;
        for (int i = 0; i < harness.Scan.Canonical.NodeCount; i++)
        {
            CanonicalNode node = harness.Node(i);
            found += node.Kind == CanonicalKind.VarBinView && node.Length == count ? 1 : 0;
        }

        return found;
    }

    internal static void WriteView(Span<byte> view, ReadOnlySpan<byte> data, int offset, int length)
    {
        view.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)length);
        if (length <= 12)
        {
            data.Slice(offset, length).CopyTo(view[4..]);
            return;
        }

        data.Slice(offset, 4).CopyTo(view[4..8]);
        BinaryPrimitives.WriteUInt32LittleEndian(view.Slice(8, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(view.Slice(12, 4), (uint)offset);
    }

    internal static string ReadValue(CanonicalNode node, int row)
    {
        ReadOnlySpan<byte> view = node.Views.Span.Slice(row * 16, 16);
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(view);
        if (length <= 12)
        {
            return Encoding.UTF8.GetString(view.Slice(4, length));
        }

        int buffer = (int)BinaryPrimitives.ReadUInt32LittleEndian(view.Slice(8, 4));
        int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(view.Slice(12, 4));
        return Encoding.UTF8.GetString(node.GetDataBuffer(buffer).Span.Slice(offset, length));
    }

    private static TestNode Node(byte[] metadata) =>
        new TestNode("vortex.dict")
            .WithMetadata(metadata)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));
}
