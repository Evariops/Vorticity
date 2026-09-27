using System;
using System.Linq;
using System.Numerics;

using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The sum kernels against a sum taken one value at a time: every integer width, lengths either
/// side of the 32- and 64-value blocks the wide kernels take and of the pairs they unroll, and the
/// extremes -- all bytes 255 or -128, all words 65 535 or -32 768, all longs at either end -- that an overflowing lane or a
/// bias applied the wrong way shows. Floats, with NaN among them and without, are held to a tolerance, since their order of addition
/// is the kernel's, and their NaN count exactly.
/// </summary>
public sealed class SumKernelsTests
{
    private static readonly int[] Lengths = [0, 1, 31, 32, 33, 63, 64, 65, 127, 128, 129, 1_000, 70_000];

    [Fact]
    public void IntegerSumsAreTheValuesSummedOneByOne()
    {
        Random random = new Random(20260927);
        foreach (int length in Lengths)
        {
            foreach (string shape in new[] { "random", "low", "high" })
            {
                byte[] bytes = new byte[length * 8];
                random.NextBytes(bytes);
                if (shape != "random")
                {
                    bytes.AsSpan().Fill(shape == "high" ? (byte)0xFF : (byte)0x00);
                    for (int i = 0; i < length; i++)
                    {
                        // The top byte of each little-endian lane: 0x80 makes the signed minimum.
                        if (shape == "low")
                        {
                            bytes[i] = 0x80;
                        }
                    }
                }

                Check<byte>(bytes, length);
                Check<sbyte>(bytes, length);
                Check<ushort>(bytes, length);
                Check<short>(bytes, length);
                Check<uint>(bytes, length);
                Check<int>(bytes, length);
                Check<ulong>(bytes, length);
                Check<long>(bytes, length);
            }
        }
    }

    [Fact]
    public void FloatSumsCountTheNumbersAndSumThemClosely()
    {
        Random random = new Random(20260928);
        foreach ((int length, bool nans) in Lengths.SelectMany(length => new[] { (length, false), (length, true) }))
        {
            double[] doubles = new double[length];
            float[] singles = new float[length];
            double expected = 0;
            long numbers = 0;
            for (int i = 0; i < length; i++)
            {
                double value = nans && random.Next(10) == 0 ? double.NaN : (random.NextDouble() - 0.3) * 1000;
                doubles[i] = value;
                singles[i] = (float)value;
                if (!double.IsNaN(value))
                {
                    expected += (float)value;
                    numbers++;
                }
            }

            double fromSingles = SumKernels.Float<float>(singles, out long countedSingles);
            Assert.Equal(numbers, countedSingles);
            Assert.True(Math.Abs(fromSingles - expected) <= 1e-9 * Math.Max(1, Math.Abs(expected)) + 1e-6, $"{length}: {fromSingles} against {expected}");

            double exact = 0;
            foreach (double value in doubles)
            {
                exact += double.IsNaN(value) ? 0 : value;
            }

            double fromDoubles = SumKernels.Float<double>(doubles, out long countedDoubles);
            Assert.Equal(numbers, countedDoubles);
            Assert.True(Math.Abs(fromDoubles - exact) <= 1e-9 * Math.Max(1, Math.Abs(exact)) + 1e-6, $"{length}: {fromDoubles} against {exact}");
        }
    }

    [Fact]
    public void SixtyFourBitSumsAreExactAtTheEndsOfTheRange()
    {
        foreach (int length in Lengths)
        {
            foreach (long value in new[] { long.MinValue, long.MaxValue, -1L, 1L << 32, (1L << 32) - 1 })
            {
                byte[] bytes = new byte[length * 8];
                System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes).Fill(value);
                Check<long>(bytes, length);
                Check<ulong>(bytes, length);
            }
        }
    }

    [Fact]
    public void InfinitiesAreNumbersAndOpposedOnesMakeNaN()
    {
        double[] rising = Enumerable.Repeat(double.PositiveInfinity, 40).ToArray();
        Assert.Equal(double.PositiveInfinity, SumKernels.Float<double>(rising, out long counted));
        Assert.Equal(40, counted);

        float[] opposed = Enumerable.Repeat(1f, 70).ToArray();
        opposed[3] = float.PositiveInfinity;
        opposed[40] = float.NegativeInfinity;
        opposed[50] = float.NaN;
        Assert.True(double.IsNaN(SumKernels.Float<float>(opposed, out counted)));
        Assert.Equal(69, counted);
    }

    private static void Check<T>(byte[] bytes, int length)
        where T : unmanaged, IBinaryInteger<T>
    {
        ReadOnlySpan<T> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, T>(bytes)[..length];
        Int128 expected = 0;
        foreach (T value in values)
        {
            expected += Int128.CreateTruncating(value);
        }

        Int128 actual = T.IsNegative(T.Zero - T.One)
            ? SumKernels.Signed(values)
            : (Int128)SumKernels.Unsigned(values);
        Assert.True(expected == actual, $"{typeof(T).Name} over {length}: {actual} against {expected}");
    }
}
