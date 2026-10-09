using System;
using System.Buffers;
using Vorticity.Parquet;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Thrift;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// The compact protocol as Parquet uses it: the bytes the standards' own examples show, every type
/// written and read back, unknown fields skipped whatever they hold, and malformed input refused
/// with <see cref="ParquetFormatException"/> and nothing else.
/// </summary>
public sealed class ThriftCompactTests
{
    private delegate void Writing(ref ThriftCompactWriter writer);

    private delegate void Reading(ref ThriftCompactReader reader);

    private static byte[] Write(Writing write)
    {
        ArrayBufferWriter<byte> output = new();
        ThriftCompactWriter writer = new(output);
        write(ref writer);
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    [Fact]
    public void TheVarintExampleOfTheProtocolIsTheBytesItShows()
    {
        // 50399 is the protocol's own example, `DF 89 03`; a list of fifteen elements or more
        // carries its size as that varint after a header of `1111` and the element type.
        byte[] bytes = Write((ref ThriftCompactWriter w) => w.WriteListHeader(ThriftType.Byte, 50399));

        Assert.Equal(new byte[] { 0xF3, 0xDF, 0x89, 0x03 }, bytes);
    }

    [Fact]
    public void AFieldIdTakesTheShortFormWithinFifteenOfThePreviousOne()
    {
        byte[] bytes = Write((ref ThriftCompactWriter w) =>
        {
            short saved = w.BeginStruct();
            w.WriteI32Field(1, 0);
            w.WriteI32Field(16, 0);
            w.WriteI32Field(17, 0);
            w.EndStruct(saved);
        });

        // Field 1: delta 1 in the high nibble. Field 16: delta 15, still short. Field 17 after it:
        // delta 1 again.
        Assert.Equal(new byte[] { 0x15, 0x00, 0xF5, 0x00, 0x15, 0x00, 0x00 }, bytes);
    }

    [Fact]
    public void TheExtensionFieldIsWrittenWithTheProtocolsZigzagFieldId()
    {
        byte[] bytes = Write((ref ThriftCompactWriter w) =>
        {
            short saved = w.BeginStruct();
            w.WriteI32Field(8, 0);
            w.WriteBinaryField(PageHeader.ExtensionFieldId, [0xAB]);
            w.EndStruct(saved);
        });

        // 32767 zigzags to 65534, the varint FE FF 03, after a long-form header of type binary.
        Assert.Equal(new byte[] { 0x85, 0x00, 0x08, 0xFE, 0xFF, 0x03, 0x01, 0xAB, 0x00 }, bytes);
    }

    [Fact]
    public void TheStandardsExampleExtensionBytesAreSkippedAsAnUnknownField()
    {
        // The extension page's own bytes, `08 FF FF 01`, read under the protocol as field -16384:
        // either way an unknown binary field, which a reader skips.
        byte[] bytes = [0x15, 0x0E, 0x08, 0xFF, 0xFF, 0x01, 0x03, 0x01, 0x02, 0x03, 0x00];
        ThriftCompactReader reader = new(bytes);
        short saved = reader.EnterStruct();

        Assert.True(reader.ReadFieldHeader(out ThriftType first, out short firstId));
        Assert.Equal((1, ThriftType.I32), (firstId, first));
        Assert.Equal(7, reader.ReadI32());
        Assert.True(reader.ReadFieldHeader(out ThriftType second, out short secondId));
        Assert.Equal((-16384, ThriftType.Binary), (secondId, second));
        reader.Skip(second);
        Assert.False(reader.ReadFieldHeader(out _, out _));
        reader.ExitStruct(saved);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void EveryTypeIsReadBackAsItWasWritten()
    {
        byte[] payload = [1, 2, 3, 250];
        byte[] bytes = Write((ref ThriftCompactWriter w) =>
        {
            short saved = w.BeginStruct();
            w.WriteBooleanField(1, true);
            w.WriteBooleanField(2, false);
            w.WriteByteField(3, sbyte.MinValue);
            w.WriteI16Field(4, short.MinValue);
            w.WriteI32Field(5, int.MinValue);
            w.WriteI64Field(6, long.MinValue);
            w.WriteDoubleField(7, -0.0);
            w.WriteBinaryField(8, payload);
            w.WriteStringField(9, "é€𝄞");
            w.WriteListField(10, ThriftType.I64, 3);
            w.WriteI64Element(long.MaxValue);
            w.WriteI64Element(-1);
            w.WriteI64Element(0);
            w.WriteListField(11, ThriftType.BooleanTrue, 2);
            w.WriteBooleanElement(true);
            w.WriteBooleanElement(false);
            short inner = w.BeginStructField(300);
            w.WriteI32Field(1, int.MaxValue);
            w.EndStruct(inner);
            w.WriteI32Field(301, -2);
            w.EndStruct(saved);
        });

        ThriftCompactReader reader = new(bytes);
        short outer = reader.EnterStruct();
        Assert.True(reader.ReadFieldHeader(out ThriftType type, out short id));
        Assert.Equal(1, id);
        Assert.True(ThriftCompactReader.BooleanField(type));
        Assert.True(reader.ReadFieldHeader(out type, out id));
        Assert.Equal(2, id);
        Assert.False(ThriftCompactReader.BooleanField(type));
        Assert.True(reader.ReadFieldHeader(out _, out _));
        Assert.Equal(sbyte.MinValue, reader.ReadByte());
        Assert.True(reader.ReadFieldHeader(out _, out _));
        Assert.Equal(short.MinValue, reader.ReadI16());
        Assert.True(reader.ReadFieldHeader(out _, out _));
        Assert.Equal(int.MinValue, reader.ReadI32());
        Assert.True(reader.ReadFieldHeader(out _, out _));
        Assert.Equal(long.MinValue, reader.ReadI64());
        Assert.True(reader.ReadFieldHeader(out _, out _));
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(reader.ReadDouble()));
        Assert.True(reader.ReadFieldHeader(out _, out _));
        Assert.Equal(payload, reader.ReadBinary().ToArray());
        Assert.True(reader.ReadFieldHeader(out _, out _));
        Assert.Equal("é€𝄞", System.Text.Encoding.UTF8.GetString(reader.ReadBinary()));
        Assert.True(reader.ReadFieldHeader(out _, out id));
        Assert.Equal(10, id);
        Assert.Equal(3, reader.ReadListHeader(out ThriftType element));
        Assert.Equal(ThriftType.I64, element);
        Assert.Equal(long.MaxValue, reader.ReadI64());
        Assert.Equal(-1, reader.ReadI64());
        Assert.Equal(0, reader.ReadI64());
        Assert.True(reader.ReadFieldHeader(out _, out _));
        Assert.Equal(2, reader.ReadListHeader(out element));
        ThriftCompactReader.ExpectBooleanElements(element);
        Assert.True(reader.ReadBooleanElement());
        Assert.False(reader.ReadBooleanElement());
        Assert.True(reader.ReadFieldHeader(out type, out id));
        Assert.Equal((300, ThriftType.Struct), (id, type));
        short nested = reader.EnterStruct();
        Assert.True(reader.ReadFieldHeader(out _, out id));
        Assert.Equal(1, id);
        Assert.Equal(int.MaxValue, reader.ReadI32());
        Assert.False(reader.ReadFieldHeader(out _, out _));
        reader.ExitStruct(nested);
        Assert.True(reader.ReadFieldHeader(out _, out id));
        Assert.Equal(301, id);
        Assert.Equal(-2, reader.ReadI32());
        Assert.False(reader.ReadFieldHeader(out _, out _));
        reader.ExitStruct(outer);
        Assert.Equal(0, reader.Remaining);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(1000)]
    public void AListOfAnySizeReadsBack(int count)
    {
        byte[] bytes = Write((ref ThriftCompactWriter w) =>
        {
            w.WriteListHeader(ThriftType.I32, count);
            for (int i = 0; i < count; i++)
            {
                w.WriteI32Element(i - 7);
            }
        });

        Assert.Equal(count < 15 ? (byte)((count << 4) | 5) : (byte)0xF5, bytes[0]);
        ThriftCompactReader reader = new(bytes);
        Assert.Equal(count, reader.ReadListHeader(out ThriftType element));
        Assert.Equal(ThriftType.I32, element);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(i - 7, reader.ReadI32());
        }

        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void UnknownFieldsOfEveryKindAreSkippedToTheNextKnownOne()
    {
        byte[] bytes = Write((ref ThriftCompactWriter w) =>
        {
            short saved = w.BeginStruct();
            w.WriteBooleanField(2, true);
            w.WriteByteField(3, 5);
            w.WriteI16Field(4, -300);
            w.WriteI64Field(6, long.MaxValue);
            w.WriteDoubleField(7, 1.5);
            w.WriteBinaryField(8, new byte[40]);
            w.WriteListField(9, ThriftType.Struct, 2);
            for (int i = 0; i < 2; i++)
            {
                short element = w.BeginStruct();
                w.WriteListField(1, ThriftType.BooleanFalse, 3);
                w.WriteBooleanElement(true);
                w.WriteBooleanElement(false);
                w.WriteBooleanElement(true);
                short deep = w.BeginStructField(40);
                w.WriteStringField(2, "deep");
                w.EndStruct(deep);
                w.EndStruct(element);
            }

            // Parquet's structures hold no map and no set, but a skip must walk both.
            w.WriteFieldHeader(ThriftType.Map, 10);
            w.WriteMapHeader(ThriftType.Binary, ThriftType.Struct, 0);
            w.WriteFieldHeader(ThriftType.Map, 11);
            w.WriteMapHeader(ThriftType.Binary, ThriftType.Struct, 2);
            w.WriteBinaryElement("a"u8);
            short first = w.BeginStruct();
            w.WriteI32Field(1, 1);
            w.EndStruct(first);
            w.WriteBinaryElement("b"u8);
            short second = w.BeginStruct();
            w.EndStruct(second);
            w.WriteFieldHeader(ThriftType.Set, 12);
            w.WriteListHeader(ThriftType.BooleanTrue, 2);
            w.WriteBooleanElement(false);
            w.WriteBooleanElement(true);
            w.WriteI32Field(100, 42);
            w.EndStruct(saved);
        });

        ThriftCompactReader reader = new(bytes);
        short saved = reader.EnterStruct();
        int known = 0;
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            if (id == 100)
            {
                known = reader.ReadI32();
            }
            else
            {
                reader.Skip(type);
            }
        }

        reader.ExitStruct(saved);
        Assert.Equal(42, known);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void AUuidFieldIsSkippedAsItsSixteenBytes()
    {
        // Field 1 a uuid, which no writer here emits, then field 2 an i32 of 42.
        byte[] bytes = [0x1D, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 0x15, 0x54, 0x00];
        ThriftCompactReader reader = new(bytes);
        short saved = reader.EnterStruct();
        Assert.True(reader.ReadFieldHeader(out ThriftType type, out short id));
        Assert.Equal((1, ThriftType.Uuid), (id, type));
        reader.Skip(type);
        Assert.True(reader.ReadFieldHeader(out _, out id));
        Assert.Equal(2, id);
        Assert.Equal(42, reader.ReadI32());
        Assert.False(reader.ReadFieldHeader(out _, out _));
        reader.ExitStruct(saved);
    }

    [Theory]
    [InlineData(new byte[] { 1 }, true)]
    [InlineData(new byte[] { 2 }, false)]
    [InlineData(new byte[] { 0 }, false)]
    public void ABooleanElementIsOneForTrueAndTwoOrZeroForFalse(byte[] bytes, bool expected)
    {
        ThriftCompactReader reader = new(bytes);
        Assert.Equal(expected, reader.ReadBooleanElement());
    }

    [Fact]
    public void ABooleanElementOfThreeIsMalformed() =>
        Assert.Throws<ParquetFormatException>(() => new ThriftCompactReader([3]).ReadBooleanElement());

    [Theory]
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x10 })]
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 })]
    public void AnI32VarintPastThirtyTwoBitsIsRefused(byte[] bytes) =>
        Assert.Throws<ParquetFormatException>(() => new ThriftCompactReader(bytes).ReadI32());

    [Fact]
    public void AnI64VarintPastSixtyFourBitsIsRefused()
    {
        byte[] bytes = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02];
        Assert.Throws<ParquetFormatException>(() => new ThriftCompactReader(bytes).ReadI64());
    }

    [Fact]
    public void AnI16OutOfItsRangeIsRefused()
    {
        // 65536 zigzagged, in a varint.
        byte[] bytes = [0x80, 0x80, 0x08];
        Assert.Throws<ParquetFormatException>(() => new ThriftCompactReader(bytes).ReadI16());
    }

    [Fact]
    public void AListDeclaringMoreElementsThanBytesIsRefusedBeforeAnyAllocation()
    {
        byte[] bytes = Write((ref ThriftCompactWriter w) => w.WriteListHeader(ThriftType.I32, 1_000_000));
        Assert.Throws<ParquetFormatException>(() => new ThriftCompactReader(bytes).ReadListHeader(out _));
    }

    [Fact]
    public void NestingPastSixtyFourLevelsIsRefused()
    {
        byte[] bytes = Write((ref ThriftCompactWriter w) =>
        {
            short[] saved = new short[70];
            saved[0] = w.BeginStruct();
            for (int i = 1; i < saved.Length; i++)
            {
                saved[i] = w.BeginStructField(1);
            }

            for (int i = saved.Length - 1; i >= 0; i--)
            {
                w.EndStruct(saved[i]);
            }
        });

        ThriftCompactReader reader = new(bytes);
        short outer = reader.EnterStruct();
        Assert.True(reader.ReadFieldHeader(out ThriftType type, out _));
        try
        {
            reader.Skip(type);
            Assert.Fail("A structure 70 levels deep was skipped.");
        }
        catch (ParquetFormatException)
        {
        }

        _ = outer;
    }

    [Fact]
    public void EveryTruncationOfAStructFailsWithAFormatException()
    {
        byte[] bytes = Write((ref ThriftCompactWriter w) =>
        {
            short saved = w.BeginStruct();
            w.WriteI64Field(1, long.MinValue);
            w.WriteStringField(2, "a string of some length");
            w.WriteListField(3, ThriftType.Struct, 2);
            for (int i = 0; i < 2; i++)
            {
                short element = w.BeginStruct();
                w.WriteDoubleField(1, i);
                w.EndStruct(element);
            }

            w.EndStruct(saved);
        });

        for (int length = 0; length < bytes.Length; length++)
        {
            byte[] prefix = bytes.AsSpan(0, length).ToArray();
            try
            {
                ThriftCompactReader reader = new(prefix);
                reader.Skip(ThriftType.Struct);
                Assert.Fail($"A struct cut at {length} of {bytes.Length} bytes was skipped.");
            }
            catch (ParquetFormatException)
            {
            }
        }
    }

    [Fact]
    public void RandomBytesFailOnlyWithTheTwoParquetExceptions()
    {
        Random random = new(20261009);
        byte[] buffer = new byte[96];
        for (int round = 0; round < 20_000; round++)
        {
            int length = random.Next(buffer.Length);
            random.NextBytes(buffer.AsSpan(0, length));
            byte[] input = buffer.AsSpan(0, length).ToArray();
            try
            {
                if ((round & 1) == 0)
                {
                    PageHeader.Read(input);
                }
                else
                {
                    ParquetFooter.Read(input);
                }
            }
            catch (ParquetFormatException)
            {
            }
            catch (ParquetUnsupportedException)
            {
            }
        }
    }

    [Theory]
    [InlineData((int)PageType.DataPageV2, 0)]
    [InlineData((int)PageType.DataPageV2, 37)]
    [InlineData((int)PageType.DictionaryPage, 0)]
    [InlineData((int)PageType.DataPage, 0)]
    public void APageHeaderReadsBackAsItWasWritten(int pageType, int extensionLength)
    {
        PageType kind = (PageType)pageType;
        PageHeader header = new()
        {
            Type = kind,
            UncompressedPageSize = 70_000,
            CompressedPageSize = 31_000,
            HasCrc = true,
            Crc = -123456,
            ValueCount = 8192,
            Encoding = kind == PageType.DictionaryPage ? ParquetEncoding.Plain : ParquetEncoding.RleDictionary,
            DefinitionLevelEncoding = ParquetEncoding.Rle,
            RepetitionLevelEncoding = ParquetEncoding.Rle,
            NullCount = kind == PageType.DataPageV2 ? 12 : 0,
            RowCount = kind == PageType.DataPageV2 ? 8192 : 0,
            DefinitionLevelsLength = kind == PageType.DataPageV2 ? 1030 : 0,
            IsCompressed = kind != PageType.DataPageV2 || extensionLength == 0,
            IsSorted = kind == PageType.DictionaryPage,
        };
        byte[] extension = new byte[extensionLength];
        byte[] bytes = Write((ref ThriftCompactWriter w) => header.Write(ref w, extension));
        byte[] withData = [.. bytes, 0xEE, 0xEE];

        PageHeader read = PageHeader.Read(withData);

        Assert.Equal(bytes.Length, read.HeaderLength);
        Assert.Equal(header.Type, read.Type);
        Assert.Equal(header.UncompressedPageSize, read.UncompressedPageSize);
        Assert.Equal(header.CompressedPageSize, read.CompressedPageSize);
        Assert.Equal((true, header.Crc), (read.HasCrc, read.Crc));
        Assert.Equal(header.ValueCount, read.ValueCount);
        Assert.Equal(header.Encoding, read.Encoding);
        Assert.Equal(header.NullCount, read.NullCount);
        Assert.Equal(header.RowCount, read.RowCount);
        Assert.Equal(header.DefinitionLevelsLength, read.DefinitionLevelsLength);
        Assert.Equal(header.IsCompressed, read.IsCompressed);
        Assert.Equal(header.IsSorted, read.IsSorted);
    }
}
