using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Xunit;

namespace Vorticity.Tests.Columns;

/// <summary>
/// The copies of bits into <see cref="bool"/> and of values into nullables against the same
/// copies a row at a time: every width a nullable is written at, lengths either side of the 64
/// rows a word holds and of the 8 a byte does, validity absent, partial and empty. A null row must
/// hold the default value beneath its flag, and a <see cref="bool"/> no byte but 0 or 1.
/// </summary>
public sealed class ColumnKernelsTests
{
    private static readonly int[] Lengths = [0, 1, 7, 8, 9, 63, 64, 65, 127, 128, 129, 200, 1_000];

    [Fact]
    public void BitsBecomeBooleans()
    {
        Random random = new Random(20260927);
        foreach (int length in Lengths)
        {
            ulong[] bits = Words(random, length, 32);
            bool[] actual = new bool[length];
            ColumnKernels.Bools(bits, actual);
            for (int i = 0; i < length; i++)
            {
                Assert.Equal(Bit(bits, i), actual[i]);
            }

            Assert.All(MemoryMarshal.AsBytes(actual.AsSpan()).ToArray(), b => Assert.True(b <= 1));
        }
    }

    [Fact]
    public void BitsBecomeNullableBooleans()
    {
        Random random = new Random(20260928);
        foreach (int length in Lengths)
        {
            foreach (int kept in new[] { -1, 0, 40, 64 })
            {
                ulong[] bits = Words(random, length, 32);
                ulong[] valid = kept < 0 ? [] : Words(random, length, kept);
                bool?[] actual = new bool?[length];
                ColumnKernels.NullableBools(valid, bits, actual);
                for (int i = 0; i < length; i++)
                {
                    bool? expected = valid.Length == 0 || Bit(valid, i) ? Bit(bits, i) : null;
                    Assert.Equal(expected, actual[i]);
                    Assert.Equal(expected.GetValueOrDefault(), actual[i].GetValueOrDefault());
                }

                Assert.All(Bytes(actual), b => Assert.True(b <= 1));
            }
        }
    }

    [Fact]
    public void ValuesBecomeNullables()
    {
        Check<byte>();
        Check<sbyte>();
        Check<short>();
        Check<ushort>();
        Check<Half>();
        Check<int>();
        Check<uint>();
        Check<float>();
        Check<long>();
        Check<ulong>();
        Check<double>();
    }

    private static void Check<T>()
        where T : unmanaged
    {
        Random random = new Random(20260929);
        foreach (int length in Lengths)
        {
            foreach (int kept in new[] { -1, 0, 40, 64 })
            {
                T[] values = new T[length];
                random.NextBytes(MemoryMarshal.AsBytes(values.AsSpan()));
                ulong[] valid = kept < 0 ? [] : Words(random, length, kept);
                T?[] actual = new T?[length];
                ColumnKernels.Nullables<T>(values, valid, actual);
                for (int i = 0; i < length; i++)
                {
                    T? expected = valid.Length == 0 || Bit(valid, i) ? values[i] : null;
                    Assert.True(expected.Equals(actual[i]), $"{typeof(T).Name}, {length} rows, {kept} of 64 kept: row {i} is {actual[i]}, not {expected}");
                    Assert.True(MemoryMarshal.AsBytes([expected.GetValueOrDefault()]).SequenceEqual(MemoryMarshal.AsBytes([actual[i].GetValueOrDefault()])));
                }
            }
        }
    }

    private static byte[] Bytes(bool?[] rows) =>
        rows.Length == 0 ? [] : MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<bool?, byte>(ref rows[0]), rows.Length * Unsafe.SizeOf<bool?>()).ToArray();

    private static bool Bit(ulong[] words, int i) => ((words[i >> 6] >> (i & 63)) & 1) != 0;

    /// <summary>Words for <paramref name="length"/> rows, <paramref name="kept"/> of each 64 set on average; bits past the end set too.</summary>
    private static ulong[] Words(Random random, int length, int kept)
    {
        ulong[] words = new ulong[(length + 63) / 64];
        for (int w = 0; w < words.Length; w++)
        {
            for (int bit = 0; bit < 64; bit++)
            {
                if (kept == 64 || random.Next(64) < kept || (w << 6) + bit >= length)
                {
                    words[w] |= 1UL << bit;
                }
            }
        }

        return words;
    }
}
