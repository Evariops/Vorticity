using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Parquet.Encodings;
using Vorticity.Parquet.Metadata;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// Fixed-width values past PLAIN: decimals and half floats under the byte array delta encodings, as
/// another writer lays them out, and the writer's own choice of DELTA_BYTE_ARRAY and
/// BYTE_STREAM_SPLIT where they pay.
/// </summary>
public sealed class FixedWidthEncodingTests : IDisposable
{
    private readonly List<string> _paths = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (string path in _paths)
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>A path of its own for each file, since a file the session has mapped stays mapped while it is cached.</summary>
    private string NewPath()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vx-fixed-{Guid.NewGuid():N}.parquet");
        _paths.Add(path);
        return path;
    }

    /// <summary>
    /// parquet-mr's v2 writer gives a FIXED_LEN_BYTE_ARRAY DELTA_BYTE_ARRAY, a decimal's among them,
    /// and the standard allows either delta encoding on a BYTE_ARRAY decimal: each reads as the same
    /// values written PLAIN.
    /// </summary>
    [Fact]
    public async Task ReadsDecimalsAndHalfFloatsUnderTheDeltaEncodings()
    {
        long[] unscaled = [12_345, -1, 0, 99_999_999_999_999_999, -42, 255, -256, 1L << 40];
        Half[] halves = [(Half)1.5, (Half)(-2), Half.Zero, (Half)65_504, (Half)0.25, Half.NegativeZero, (Half)3, (Half)(-0.5)];
        byte[][] fixedBytes = [.. unscaled.Select(value => BigEndian(value, 9))];
        byte[][] shortest = [.. unscaled.Select(Shortest)];
        byte[][] halfBytes = [.. halves.Select(Little)];
        byte[] definition = [1, 0, 1, 1, 1, 0, 1, 1];
        byte[][] present = [.. shortest.Where((_, i) => definition[i] == 1)];

        string[][] plain = await RowsAsync(Build(ParquetEncoding.Plain));
        string[][] delta = await RowsAsync(Build(ParquetEncoding.DeltaByteArray));
        Assert.Equal(plain, delta);
        Assert.Equal("null", plain[1][1]);
        Assert.Equal(plain[0][0], plain[0][2]);
        Assert.Equal(plain[3][0], plain[3][2]);

        HandBuiltFile Build(ParquetEncoding encoding)
        {
            bool delta = encoding != ParquetEncoding.Plain;
            HandBuiltFile built = new HandBuiltFile(4) { Rows = unscaled.Length }
                .Leaf("fixed", FieldRepetition.Required, PhysicalType.FixedLenByteArray, ConvertedType.Decimal, length: 9, scale: 2, precision: 20)
                .Leaf("lengths", FieldRepetition.Optional, PhysicalType.ByteArray, ConvertedType.Decimal, scale: 2, precision: 20)
                .Leaf("prefixed", FieldRepetition.Required, PhysicalType.ByteArray, ConvertedType.Decimal, scale: 2, precision: 20)
                .Leaf("half", FieldRepetition.Required, PhysicalType.FixedLenByteArray, length: 2, logical: LogicalTypeKind.Float16);
            byte[] none = new byte[unscaled.Length];
            built.Chunk(PhysicalType.FixedLenByteArray, "fixed")
                .V2(null, 0, none, 0, unscaled.Length, delta ? Prefixed(fixedBytes) : [.. fixedBytes.SelectMany(b => b)], encoding);
            built.Chunk(PhysicalType.ByteArray, "lengths")
                .V2(null, 0, definition, 1, unscaled.Length, delta ? Lengths(present) : Prefixes(present), delta ? ParquetEncoding.DeltaLengthByteArray : encoding);
            built.Chunk(PhysicalType.ByteArray, "prefixed")
                .V2(null, 0, none, 0, unscaled.Length, delta ? Prefixed(shortest) : Prefixes(shortest), encoding);
            built.Chunk(PhysicalType.FixedLenByteArray, "half")
                .V2(null, 0, none, 0, unscaled.Length, delta ? Prefixed(halfBytes) : [.. halfBytes.SelectMany(b => b)], encoding);
            return built;
        }
    }

    [Fact]
    public async Task RefusesADeltaEncodedDecimalOfAnotherLength()
    {
        // A FIXED_LEN_BYTE_ARRAY(9) page whose second value rebuilds to 8 bytes.
        HandBuiltFile built = new HandBuiltFile(1) { Rows = 2 }
            .Leaf("fixed", FieldRepetition.Required, PhysicalType.FixedLenByteArray, ConvertedType.Decimal, length: 9, scale: 2, precision: 20);
        built.Chunk(PhysicalType.FixedLenByteArray, "fixed").V2(null, 0, [0, 0], 0, 2, Prefixed([BigEndian(1, 9), BigEndian(2, 8)]), ParquetEncoding.DeltaByteArray);
        await Assert.ThrowsAsync<ParquetFormatException>(() => RowsAsync(built));
    }

    /// <summary>
    /// Wide decimals of small values share their sign bytes, which DELTA_BYTE_ARRAY drops; a smooth
    /// half float, and integers too far apart for deltas whose high bytes repeat, take
    /// BYTE_STREAM_SPLIT; identifiers of no shape stay PLAIN. Each reads back as written.
    /// </summary>
    [Fact]
    public async Task WritesFixedWidthValuesInTheEncodingThatPays()
    {
        const int Rows = 20_000;
        VortexSchema schema =
        [
            ("money", VortexType.Decimal(28, 2).Nullable),
            ("half", VortexType.Float16),
            ("far", VortexType.Int64),
            ("serial", VortexType.Uuid),
            ("random", VortexType.Uuid),
        ];
        Random random = new(5);
        decimal?[] money = new decimal?[Rows];
        Half[] halves = new Half[Rows];
        long[] far = new long[Rows];
        Guid[] serials = new Guid[Rows];
        Guid[] ids = new Guid[Rows];
        for (int i = 0; i < Rows; i++)
        {
            // Unique, so that no dictionary takes the column first.
            money[i] = i % 17 == 0 ? null : (i * 1.01m) - 5_000m;
            halves[i] = (Half)(20.0 + (5.0 * Math.Sin(i / 300.0)));
            far[i] = ((long)random.Next(2) << 62) | (long)random.Next(1 << 16);
            serials[i] = new Guid(0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)(i >> 8), (byte)i);
            ids[i] = new Guid([.. Enumerable.Range(0, 16).Select(_ => (byte)random.Next(256))]);
        }

        string encodedPath = NewPath();
        string plainPath = NewPath();
        await WriteAsync(encodedPath, CompressionProfile.Auto);
        await WriteAsync(plainPath, CompressionProfile.None);
        await using ParquetFile file = await ParquetFile.OpenAsync(encodedPath, Ct);
        IReadOnlyList<string> Encodings(int column) => file.Metadata.RowGroups[0].Chunks[column].Encodings;
        Assert.Contains("DELTA_BYTE_ARRAY", Encodings(0));
        Assert.Contains("BYTE_STREAM_SPLIT", Encodings(1));
        Assert.Contains("BYTE_STREAM_SPLIT", Encodings(2));
        Assert.Contains("DELTA_BYTE_ARRAY", Encodings(3));
        Assert.DoesNotContain("DELTA_BYTE_ARRAY", Encodings(4));
        Assert.DoesNotContain("BYTE_STREAM_SPLIT", Encodings(4));

        long row = 0;
        await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                VortexColumn amounts = batch.Column(0);
                for (int r = 0; r < batch.RowCount; r++, row++)
                {
                    Assert.Equal(money[row].HasValue, amounts.IsValid(r));
                    if (money[row] is { } amount)
                    {
                        Assert.Equal(amount, amounts.AsDecimal()[r].ToDecimal());
                    }

                    Assert.Equal(halves[row], batch.Column(1).AsPrimitive<Half>()[r]);
                    Assert.Equal(far[row], batch.Column(2).AsPrimitive<long>()[r]);
                }
            }
        }

        Assert.Equal(Rows, row);
        Assert.Empty(await file.VerifyAsync(Ct));

        // Every column, the identifiers among them, reads as the same rows written PLAIN.
        Assert.Equal(await RowsAsync(plainPath), await RowsAsync(encodedPath));

        async Task WriteAsync(string path, CompressionProfile profile)
        {
            await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(path, schema, new ParquetWriteOptions { RowGroupRows = 16_384, Profile = profile });
            ColumnsBuilder builder = writer.Builder();
            for (int i = 0; i < Rows; i++)
            {
                if (money[i] is { } amount)
                {
                    builder.Column<decimal?>(0).Append(amount);
                }
                else
                {
                    builder.Column<decimal?>(0).AppendNull();
                }

                builder.Column<Half>(1).Append(halves[i]);
                builder.Column<long>(2).Append(far[i]);
                builder.Column<Guid>(3).Append(serials[i]);
                builder.Column<Guid>(4).Append(ids[i]);
            }

            await writer.WriteAsync(builder, Ct);
            await writer.CompleteAsync(Ct);
        }
    }

    private async Task<string[][]> RowsAsync(HandBuiltFile built)
    {
        string path = NewPath();
        await System.IO.File.WriteAllBytesAsync(path, built.ToBytes(), Ct);
        return await RowsAsync(path);
    }

    private static async Task<string[][]> RowsAsync(string path)
    {
        await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);
        List<string[]> rows = [];
        await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                for (int r = 0; r < batch.RowCount; r++)
                {
                    rows.Add([.. Enumerable.Range(0, batch.Schema.Count).Select(c => Render.Row(batch, c, r))]);
                }
            }
        }

        return [.. rows];
    }

    /// <summary>A two's-complement integer as <paramref name="width"/> big-endian bytes.</summary>
    private static byte[] BigEndian(long value, int width)
    {
        byte[] bytes = new byte[width];
        for (int b = 0; b < width; b++)
        {
            bytes[width - 1 - b] = b < sizeof(long) ? (byte)(value >> (8 * b)) : (byte)(value < 0 ? 0xFF : 0);
        }

        return bytes;
    }

    /// <summary>The fewest big-endian bytes that hold <paramref name="value"/>, none for zero.</summary>
    private static byte[] Shortest(long value) =>
        value == 0 ? [] : new BigInteger(value).ToByteArray(isUnsigned: false, isBigEndian: true);

    private static byte[] Little(Half value)
    {
        byte[] bytes = new byte[2];
        BinaryPrimitives.WriteHalfLittleEndian(bytes, value);
        return bytes;
    }

    /// <summary>Values PLAIN as a BYTE_ARRAY stores them: each behind its length.</summary>
    private static byte[] Prefixes(byte[][] values)
    {
        List<byte> bytes = [];
        foreach (byte[] value in values)
        {
            byte[] length = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
            bytes.AddRange(length);
            bytes.AddRange(value);
        }

        return [.. bytes];
    }

    private static byte[] Lengths(byte[][] values)
    {
        int[] lengths = [.. values.Select(v => v.Length)];
        byte[] data = [.. values.SelectMany(v => v)];
        byte[] encoded = new byte[DeltaByteArrays.SizeLengths(lengths, data.Length)];
        Assert.Equal(encoded.Length, DeltaByteArrays.EncodeLengths(lengths, data, encoded));
        return encoded;
    }

    private static byte[] Prefixed(byte[][] values)
    {
        int[] lengths = [.. values.Select(v => v.Length)];
        byte[] data = [.. values.SelectMany(v => v)];
        int[] prefixes = new int[values.Length];
        int[] suffixes = new int[values.Length];
        int rest = DeltaByteArrays.Prefixes(data, lengths, prefixes, suffixes);
        byte[] encoded = new byte[DeltaByteArrays.SizePrefixes(prefixes, suffixes, rest)];
        Assert.Equal(encoded.Length, DeltaByteArrays.EncodePrefixes(data, lengths, prefixes, suffixes, encoded));
        return encoded;
    }
}
