using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Tests.Columns;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>
/// ALP-RD on the write side: the cut and the dictionary held to the reference's own case, and
/// columns ALP refuses written as <c>vortex.alprd</c> and read back bit for bit -- through the
/// decoder that reads the reference's files, so the round trip is a statement about the format.
/// </summary>
public sealed class AlpRdEncoderTests
{
    private const int Rows = 20_000;

    /// <summary>
    /// The case vortex-alp pins in its own tests (rd_encoder_uses_deterministic_codes_for_tied_prefixes):
    /// a thousand coordinates with two decimals, whose best cut leaves 53 right bits and whose
    /// dictionary, ties broken by pattern, is exactly this one.
    /// </summary>
    [Fact]
    public void TheSearchPicksTheReferencesCutAndDictionary()
    {
        double[] values = new double[1_000];
        for (int i = 0; i < values.Length; i++)
        {
            long row = 1_000 + i;
            values[i] = ((row * 53) % 36_000 / 100.0) - 180.0;
        }

        using ColumnFixture fixture = new ColumnFixture();
        int node = fixture.Arena.AddPrimitive(
            fixture.Types.Primitive(PType.F64, Nullability.NonNullable), values.Length,
            Validity.NonNullable, PType.F64, fixture.Bytes(MemoryMarshal.AsBytes<double>(values)));

        (int right, ushort[] dictionary) = AlpRdPlan.SearchForTests(fixture.Arena, node);
        Assert.Equal(53, right);
        Assert.Equal(new ushort[] { 514, 1538, 515, 1539, 513, 1537, 512, 1536 }, dictionary);
    }

    [Fact]
    public async Task FullPrecisionDoublesAreWrittenAsAlpRdAndReadBackBitForBit()
    {
        double[] values = RealDoubles();
        double[] read = await RoundTripAsync(values, valid: null, "vortex.alprd");
        AssertBits(values, read, valid: null);
    }

    [Fact]
    public async Task NullsNaNsSignedZerosAndInfinitiesSurvive()
    {
        double[] values = RealDoubles();
        values[1] = double.NaN;
        values[2] = BitConverter.Int64BitsToDouble(0x7FF8_0000_DEAD_BEEF);
        values[3] = -0.0;
        values[4] = double.PositiveInfinity;
        values[5] = double.NegativeInfinity;
        values[6] = double.Epsilon;
        values[7] = -double.MaxValue;
        bool[] valid = new bool[Rows];
        for (int i = 0; i < Rows; i++)
        {
            valid[i] = i % 17 != 0;
        }

        double[] read = await RoundTripAsync(values, valid, "vortex.alprd");
        AssertBits(values, read, valid);
    }

    /// <summary>
    /// Magnitudes spread over far more binades than a dictionary of eight holds, so that many rows'
    /// high bits are exceptions and the patches carry them.
    /// </summary>
    [Fact]
    public async Task HighBitsTheDictionaryMissesComeBackThroughThePatches()
    {
        double[] values = new double[Rows];
        for (int i = 0; i < Rows; i++)
        {
            // Most rows in two binades, one row in twenty anywhere in forty more.
            double magnitude = i % 20 == 0 ? Math.Pow(2, (i / 20) % 40) : 1_000.0;
            values[i] = magnitude * (1.0 + (Math.Sin(i) * 0.25));
        }

        double[] read = await RoundTripAsync(values, valid: null, "vortex.alprd");
        AssertBits(values, read, valid: null);
    }

    [Fact]
    public async Task SinglesAreWrittenAsAlpRdToo()
    {
        float[] values = new float[Rows];
        for (int i = 0; i < Rows; i++)
        {
            values[i] = (float)((Math.Sin(i) * 100.0) + (i / 7.0));
        }

        string path = TempPath();
        try
        {
            DTypeArena types = new DTypeArena();
            DType f32 = types.Primitive(PType.F32, Nullability.NonNullable);
            DType schema = types.Struct(["v"], [f32], Nullability.NonNullable);
            CanonicalArena arena = new CanonicalArena();
            try
            {
                VortexBuffer buffer = arena.Allocate(Rows * sizeof(float), sizeof(float), out Span<byte> bytes);
                MemoryMarshal.AsBytes<float>(values).CopyTo(bytes);
                int column = arena.AddPrimitive(f32, Rows, Validity.NonNullable, PType.F32, buffer);
                int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [column]);
                await using (VortexFileWriter writer = VortexFileWriter.Create(path, schema))
                {
                    using RecordBatch batch = new RecordBatch(arena, root, 0);
                    await writer.WriteAsync(batch, CancellationToken.None);
                    await writer.CompleteAsync(CancellationToken.None);
                }
            }
            finally
            {
                arena.Reset();
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
            Assert.Contains("vortex.alprd", Encodings(file));
            int row = 0;
            await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync())
            {
                ReadOnlySpan<float> read = batch.Column(0).AsPrimitive<float>().Values;
                for (int i = 0; i < read.Length; i++, row++)
                {
                    Assert.Equal(BitConverter.SingleToInt32Bits(values[row]), BitConverter.SingleToInt32Bits(read[i]));
                }
            }

            Assert.Equal(Rows, row);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// Doubles of full precision over three binades: what ALP refuses, and what ALP-RD's eight
    /// high-bit patterns hold with a handful of exceptions.
    /// </summary>
    private static double[] RealDoubles()
    {
        double[] values = new double[Rows];
        for (int i = 0; i < Rows; i++)
        {
            values[i] = 1_000.0 + (Math.Sin(i) * 100.0) + (i / 7.0);
        }

        return values;
    }

    /// <summary>Writes one f64 column and reads it back, requiring <paramref name="encoding"/> among the file's encodings.</summary>
    private static async Task<double[]> RoundTripAsync(double[] values, bool[]? valid, string encoding)
    {
        string path = TempPath();
        try
        {
            DTypeArena types = new DTypeArena();
            DType f64 = types.Primitive(PType.F64, valid is null ? Nullability.NonNullable : Nullability.Nullable);
            DType schema = types.Struct(["v"], [f64], Nullability.NonNullable);
            CanonicalArena arena = new CanonicalArena();
            try
            {
                VortexBuffer buffer = arena.Allocate(Rows * sizeof(double), sizeof(double), out Span<byte> bytes);
                MemoryMarshal.AsBytes<double>(values).CopyTo(bytes);
                Validity validity = Validity.NonNullable;
                if (valid is not null)
                {
                    VortexBuffer bits = arena.Allocate((Rows + 7) / 8, 1, out Span<byte> bitmap);
                    bitmap.Clear();
                    for (int i = 0; i < Rows; i++)
                    {
                        if (valid[i])
                        {
                            bitmap[i >> 3] |= (byte)(1 << (i & 7));
                        }
                    }

                    int mask = arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0);
                    validity = Validity.Bitmap(mask);
                }

                int column = arena.AddPrimitive(f64, Rows, validity, PType.F64, buffer);
                int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [column]);
                await using (VortexFileWriter writer = VortexFileWriter.Create(path, schema))
                {
                    using RecordBatch batch = new RecordBatch(arena, root, 0);
                    await writer.WriteAsync(batch, CancellationToken.None);
                    await writer.CompleteAsync(CancellationToken.None);
                }
            }
            finally
            {
                arena.Reset();
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
            Assert.Contains(encoding, Encodings(file));
            double[] read = new double[Rows];
            bool[] present = new bool[Rows];
            int row = 0;
            await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync())
            {
                VortexColumn column = batch.Column(0);
                ReadOnlySpan<double> typed = column.AsPrimitive<double>().Values;
                for (int i = 0; i < typed.Length; i++, row++)
                {
                    present[row] = column.IsValid(i);
                    read[row] = typed[i];
                }
            }

            Assert.Equal(Rows, row);
            if (valid is not null)
            {
                Assert.Equal(valid, present);
            }

            return read;
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static void AssertBits(double[] expected, double[] actual, bool[]? valid)
    {
        for (int i = 0; i < expected.Length; i++)
        {
            if (valid is not null && !valid[i])
            {
                continue;
            }

            Assert.True(
                BitConverter.DoubleToInt64Bits(expected[i]) == BitConverter.DoubleToInt64Bits(actual[i]),
                $"row {i}: wrote {expected[i]:R}, read {actual[i]:R}");
        }
    }

    private static List<string> Encodings(VortexFile file)
    {
        List<string> ids = [];
        for (int i = 0; i < file.ArrayEncodingCount; i++)
        {
            ids.Add(file.GetArrayEncodingId(i));
        }

        return ids;
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"vorticity-alprd-{Guid.NewGuid():N}.vortex");
}
