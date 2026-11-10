// A bucket of floats: each value floored to a multiple of the width, Floor(x / w) * w in doubles as a value at a time
// computes it and narrowed to the column's type, for doubles and floats of every kind (negative, zero of either sign,
// subnormal, huge, infinite, NaN), at row counts that leave a tail.
using System;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Compute;

public sealed class FloatBucketTests
{
    private static readonly double[] Specials =
        [0.0, -0.0, double.Epsilon, -double.Epsilon, 1e-310, double.MaxValue, double.MinValue, double.PositiveInfinity, double.NegativeInfinity, double.NaN, 2.5, -2.5, 7.4999999, -1e300];

    [Theory]
    [InlineData(PType.F64, 2.5)]
    [InlineData(PType.F32, 2.5)]
    [InlineData(PType.F64, 0.1)]
    [InlineData(PType.F32, 1e-3)]
    internal void EachValueIsFlooredAsAValueAtATimeFloorsIt(PType storage, double width)
    {
        Random random = new Random(163);
        foreach (int rows in new[] { 1, 7, 64, 1_001 })
        {
            double[] drawn = new double[rows];
            for (int i = 0; i < rows; i++)
            {
                drawn[i] = i < Specials.Length ? Specials[i] : (random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-5, 12));
            }

            CanonicalArena arena = new CanonicalArena();
            int size = storage.ByteWidth();
            VortexBuffer buffer = arena.Allocate(rows * size, size, out Span<byte> bytes);
            for (int i = 0; i < rows; i++)
            {
                if (storage == PType.F64)
                {
                    MemoryMarshal.Cast<byte, double>(bytes)[i] = drawn[i];
                }
                else
                {
                    MemoryMarshal.Cast<byte, float>(bytes)[i] = (float)drawn[i];
                }
            }

            int column = arena.AddPrimitive(new DTypeArena().Primitive(storage, Nullability.NonNullable), rows, Validity.NonNullable, storage, buffer);
            NumberBucket bucket = new NumberBucket(storage, 0, width, width.ToString(System.Globalization.CultureInfo.InvariantCulture));
            ReadOnlySpan<byte> mapped = arena.GetNode(bucket.Apply(arena, column)).Values.Span;
            for (int i = 0; i < rows; i++)
            {
                if (storage == PType.F64)
                {
                    double value = MemoryMarshal.Cast<byte, double>(bytes)[i];
                    Assert.Equal(Math.Floor(value / width) * width, MemoryMarshal.Cast<byte, double>(mapped)[i]);
                }
                else
                {
                    float value = MemoryMarshal.Cast<byte, float>(bytes)[i];
                    Assert.Equal((float)(Math.Floor(value / width) * width), MemoryMarshal.Cast<byte, float>(mapped)[i]);
                }
            }
        }
    }
}
