using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vorticity.Tests.Matrix;

/// <summary>What the values of every column look like, each shape being the one some encoding wants.</summary>
public enum Shape
{
    /// <summary>Runs of eight rows over sixteen values, the edges of each type among them, nulls a run of their own.</summary>
    Runs,

    /// <summary>One value in 97 rows of 100, the rest spread over the other fifteen, nulls among them.</summary>
    Dominant,

    /// <summary>Arithmetic progressions where the type has integers, distinct values elsewhere, no null.</summary>
    Progression,

    /// <summary>One value in every row.</summary>
    Constant,

    /// <summary>A null in every nullable member and one value in every other: the columns a schema declares and a batch leaves empty.</summary>
    Absent,

    /// <summary>A null in nineteen rows of twenty of every nullable member, the pools' values scattered in between.</summary>
    Scarce,

    /// <summary>High cardinality within realistic ranges: prices, emails, instants to the second; one row in twenty null.</summary>
    Spread,
}

/// <summary>The rows of the matrix, one table per <see cref="Shape"/>, the same for every run.</summary>
internal static class MatrixRows
{
    /// <summary>Two whole blocks of 8 192 rows and a ragged tail.</summary>
    internal const int Count = 20_000;

    private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    internal static AllTypes[] Build(Shape shape, int count = Count)
    {
        AllTypes[] rows = new AllTypes[count];
        for (int i = 0; i < count; i++)
        {
            rows[i] = Row(shape, i);
        }

        return rows;
    }

    private static AllTypes Row(Shape shape, int i)
    {
        int k = Pick(shape, i);
        bool none = shape switch
        {
            Shape.Runs or Shape.Dominant => k == 15,
            Shape.Spread => Mix(i, 7) % 20 == 0,
            Shape.Absent => true,
            Shape.Scarce => Mix(i, 7) % 20 != 0,
            _ => false,
        };

        return new AllTypes(
            Bool: Bools(shape, i, k),
            BoolN: none ? null : !Bools(shape, i, k),
            I8: Signed8(shape, i, k),
            I8N: none ? null : Signed8(shape, i, 14 - k),
            I16: Signed16(shape, i, k),
            I16N: none ? null : Signed16(shape, i, 14 - k),
            I32: Signed32(shape, i, k),
            I32N: none ? null : Signed32(shape, i, 14 - k),
            I64: Signed64(shape, i, k),
            I64N: none ? null : Signed64(shape, i, 14 - k),
            U8: Unsigned8(shape, i, k),
            U8N: none ? null : Unsigned8(shape, i, 14 - k),
            U16: Unsigned16(shape, i, k),
            U16N: none ? null : Unsigned16(shape, i, 14 - k),
            U32: Unsigned32(shape, i, k),
            U32N: none ? null : Unsigned32(shape, i, 14 - k),
            U64: Unsigned64(shape, i, k),
            U64N: none ? null : Unsigned64(shape, i, 14 - k),
            F16: Halves(shape, i, k),
            F16N: none ? null : Halves(shape, i, 14 - k),
            F32: Singles(shape, i, k),
            F32N: none ? null : Singles(shape, i, 14 - k),
            F64: Doubles(shape, i, k),
            F64N: none ? null : Doubles(shape, i, 14 - k),
            Dec8: Decimals(shape, i, k, precision: 2, scale: 1),
            Dec16: Decimals(shape, i, k, precision: 4, scale: 2),
            Dec32: Decimals(shape, i, k, precision: 9, scale: 2),
            Dec64: Decimals(shape, i, k, precision: 18, scale: 4),
            Dec128: Decimals(shape, i, k, precision: 28, scale: 10),
            Dec128N: none ? null : Decimals(shape, i, 14 - k, precision: 28, scale: 10),
            Wide128: Wide(shape, i, k, precision: 38, scale: 6),
            Wide256: Wide(shape, i, k, precision: 76, scale: 10),
            Wide256N: none ? null : Wide(shape, i, 14 - k, precision: 76, scale: 10),
            Text: Texts(shape, i, k),
            TextN: none ? null : Texts(shape, i, 14 - k),
            Bytes: Blobs(shape, i, k),
            BytesN: none ? (ReadOnlyMemory<byte>?)null : Blobs(shape, i, 14 - k),
            Date: Dates(shape, i, k),
            DateN: none ? null : Dates(shape, i, 14 - k),
            Time: Times(shape, i, k),
            TimeN: none ? null : Times(shape, i, 14 - k),
            Stamp: Stamps(shape, i, k, TimeSpan.TicksPerMicrosecond, wide: true),
            StampN: none ? null : Stamps(shape, i, 14 - k, TimeSpan.TicksPerMicrosecond, wide: true),
            StampNs: Stamps(shape, i, k, 1, wide: false),
            StampMsUtc: DateTime.SpecifyKind(Stamps(shape, i, k, TimeSpan.TicksPerMillisecond, wide: true), DateTimeKind.Utc),
            Zoned: new DateTimeOffset(Stamps(shape, i, k, TimeSpan.TicksPerMicrosecond, wide: false), TimeSpan.Zero),
            ZonedN: none ? null : new DateTimeOffset(Stamps(shape, i, 14 - k, TimeSpan.TicksPerSecond, wide: false), TimeSpan.Zero),
            Uuid: Uuids(shape, i, k),
            UuidN: none ? null : Uuids(shape, i, 14 - k),
            State: shape switch { Shape.Spread => (Status)(int)(Mix(i, 3) % 4), Shape.Progression => Status.Open, _ => (Status)(k % 4) },
            StateN: none ? null : shape == Shape.Progression ? Status.Closed : (Status)((k + 1) % 4),
            Ints: IntLists(shape, i, k),
            Texts: TextLists(shape, i, k),
            // Typed, or the null converts to an empty memory through the array conversion and the
            // member is never null at all.
            LongsN: none ? (ReadOnlyMemory<long?>?)null : LongLists(shape, i, k),
            Nested: new Inner(Signed32(shape, i, k), k % 3 == 0 ? null : Texts(shape, i, k)),
            NestedN: none ? null : new Inner(Signed32(shape, i, 14 - k), Texts(shape, i, 14 - k)),
            Char: Chars(shape, i, k),
            CharN: none ? null : Chars(shape, i, 14 - k),
            NInt: (nint)Signed64(shape, i, k),
            NUInt: (nuint)Unsigned64(shape, i, k),
            Span: Spans(shape, i, k),
            SpanN: none ? null : Spans(shape, i, 14 - k),
            I128: Integers128(shape, i, k, wide: false),
            I128N: none ? null : Integers128(shape, i, 14 - k, wide: false),
            U128: Unsigned128(shape, i, k, wide: false),
            U128N: none ? null : Unsigned128(shape, i, 14 - k, wide: false),
            I128Wide: Integers128(shape, i, k, wide: true),
            U128Wide: Unsigned128(shape, i, k, wide: true),
            Big: Bigs(shape, i, k),
            BigN: none ? null : Bigs(shape, i, 14 - k),
            IntArray: IntLists(shape, i, k).ToArray(),
            IntArrayN: none ? null : IntLists(shape, i, 14 - k).ToArray(),
            TextList: [.. TextLists(shape, i, k).Span],
            TextListN: none ? null : [.. TextLists(shape, i, 14 - k).Span, null],
            Blob: Blobs(shape, i, k).ToArray(),
            BlobN: none ? null : Blobs(shape, i, 14 - k).ToArray(),
            BlobMemory: Blobs(shape, i, 13 - (k % 13)).ToArray(),
            LongArray: [.. LongLists(shape, i, k).Span.ToArray().Select(v => v ?? -1)],
            Doubles: [Doubles(shape, i, k), null, Doubles(shape, i + 1, (k + 1) % 15)],
            Jagged: [[Doubles(shape, i, k)], [], [Doubles(shape, i, 14 - (k % 15)), Doubles(shape, i + 2, (k + 2) % 15)]],
            ListOfArrays: [IntLists(shape, i, k).ToArray(), []],
            Shorts: new short[] { Signed16(shape, i, k), Signed16(shape, i, 14 - (k % 15)) },
            Guids: [Uuids(shape, i, k)],
            Items: Records(shape, i, k),
            ItemsN: none ? null : [.. Records(shape, i, 14 - (k % 15))],
            ItemSequence: Records(shape, i + 1, (k + 1) % 15),
            Scores: Scores(shape, i, k),
            Weights: none ? null : new Dictionary<int, double?> { [Signed32(shape, i, k)] = Doubles(shape, i, k), [-7] = null },
            Statuses: new Dictionary<Guid, Status> { [Uuids(shape, i, k)] = (Status)(k % 4) });
    }

    /// <summary>The index into each type's pool of sixteen values; 15 stands for the null of a nullable member.</summary>
    private static int Pick(Shape shape, int row) => shape switch
    {
        Shape.Runs => (row / 8) % 16,
        Shape.Dominant => Mix(row, 1) % 100 < 97 ? 0 : 1 + (int)(Mix(row, 2) % 15),
        Shape.Constant or Shape.Absent => 3,
        _ => (int)(Mix(row, 5) % 15),
    };

    /// <summary>A hash of the row, the same on every run.</summary>
    internal static ulong Mix(int row, int salt)
    {
        ulong x = ((ulong)(uint)row * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)salt * 0xBF58476D1CE4E5B9UL);
        x ^= x >> 31;
        x *= 0x94D049BB133111EBUL;
        return x ^ (x >> 29);
    }

    private static bool Bools(Shape shape, int i, int k) => shape switch
    {
        Shape.Spread => (Mix(i, 11) & 1) != 0,
        Shape.Progression => i % 3 == 0,
        _ => k % 2 == 0,
    };

    private static readonly sbyte[] Sbytes = [sbyte.MinValue, -100, -1, 0, 1, 2, 3, 5, 8, 13, 21, 34, 55, 89, sbyte.MaxValue, 7];
    private static readonly short[] Shorts = [short.MinValue, -1000, -1, 0, 1, 2, 3, 500, 800, 1300, 2100, 3400, 5500, 8900, short.MaxValue, 7];
    private static readonly int[] Ints = [int.MinValue, -1_000_000, -1, 0, 1, 2, 3, 50_000, 80_000, 130_000, 210_000, 340_000, 550_000, 890_000, int.MaxValue, 7];
    private static readonly long[] Longs = [long.MinValue, -1L << 40, -1, 0, 1, 2, 3, 1L << 33, 1L << 34, 1L << 35, 1L << 36, 1L << 37, 1L << 38, 1L << 39, long.MaxValue, 7];

    private static sbyte Signed8(Shape shape, int i, int k) => shape switch
    {
        Shape.Progression => 42,
        Shape.Spread => (sbyte)((long)(Mix(i, 13) % 200) - 100),
        _ => Sbytes[k],
    };

    private static short Signed16(Shape shape, int i, int k) => shape switch
    {
        Shape.Progression => (short)(i - 10_000),
        Shape.Spread => (short)((long)(Mix(i, 17) % 2_000) - 1_000),
        _ => Shorts[k],
    };

    private static int Signed32(Shape shape, int i, int k) => shape switch
    {
        Shape.Progression => (i * 3) - 1_000_000,
        Shape.Spread => (int)(Mix(i, 19) % 2_000_000) - 1_000_000,
        _ => Ints[k],
    };

    private static long Signed64(Shape shape, int i, int k) => shape switch
    {
        Shape.Progression => (1L << 40) + (i * 1_000L),
        Shape.Spread => (1L << 40) + (long)(Mix(i, 23) % 10_000_000_000UL),
        _ => Longs[k],
    };

    private static byte Unsigned8(Shape shape, int i, int k) => shape switch
    {
        Shape.Progression => 7,
        Shape.Spread => (byte)Mix(i, 29),
        _ => (byte)(k == 0 ? 0 : k == 14 ? byte.MaxValue : k * 13),
    };

    private static ushort Unsigned16(Shape shape, int i, int k) => shape switch
    {
        Shape.Progression => (ushort)i,
        Shape.Spread => (ushort)(Mix(i, 31) % 50_000),
        _ => (ushort)(k == 14 ? ushort.MaxValue : k * 1_111),
    };

    private static uint Unsigned32(Shape shape, int i, int k) => shape switch
    {
        Shape.Progression => (uint)i * 5,
        Shape.Spread => (uint)(Mix(i, 37) % 3_000_000),
        _ => k == 14 ? uint.MaxValue : (uint)k * 111_111,
    };

    private static ulong Unsigned64(Shape shape, int i, int k) => shape switch
    {
        Shape.Progression => (1UL << 50) + ((ulong)i * 11),
        Shape.Spread => Mix(i, 41) % 1_000_000_000_000UL,
        _ => k == 14 ? ulong.MaxValue : (ulong)k * 1_111_111_111_111UL,
    };

    private static readonly char[] CharPool =
    [
        '\0', 'a', 'Z', '0', ' ', '\u00E9', '\u20AC', '\u65E5', '\uD800', '\uDFFF', '\uFFFF', '\t', '\n', '~', '\u007F', 'q',
    ];

    private static char Chars(Shape shape, int i, int k) => shape switch
    {
        Shape.Progression => (char)i,
        Shape.Spread => (char)Mix(i, 109),
        _ => CharPool[k],
    };

    private static TimeSpan Spans(Shape shape, int i, int k) => shape switch
    {
        Shape.Progression => TimeSpan.FromSeconds(i),
        Shape.Spread => TimeSpan.FromTicks((long)(Mix(i, 113) % 864_000_000_000UL) - 432_000_000_000L),
        _ => k switch
        {
            0 => TimeSpan.MinValue,
            14 => TimeSpan.MaxValue,
            _ => TimeSpan.FromTicks(((k - 7) * 1_234_567_890_123L) + k),
        },
    };

    /// <summary>A 128-bit integer: within 38 digits for a decimal(38, 0), the type's own bounds for a decimal(39, 0).</summary>
    private static Int128 Integers128(Shape shape, int i, int k, bool wide)
    {
        Int128 limit = wide ? Int128.MaxValue : Pow10(38) - 1;
        return shape switch
        {
            Shape.Progression => ((Int128)i * 1_000_000_007) - 5,
            Shape.Spread => ((Int128)Mix(i, 127) << 32) - ((Int128)Mix(i, 131) >> 3),
            _ => k switch
            {
                0 => wide ? Int128.MinValue : -limit,
                14 => limit,
                _ => ((k - 7) * Pow10(35)) + k,
            },
        };
    }

    private static UInt128 Unsigned128(Shape shape, int i, int k, bool wide)
    {
        UInt128 limit = wide ? UInt128.MaxValue : (UInt128)(Pow10(38) - 1);
        return shape switch
        {
            Shape.Progression => ((UInt128)(uint)i * 3) + 11,
            Shape.Spread => ((UInt128)Mix(i, 137) << 40) | Mix(i, 139),
            _ => k switch
            {
                0 => 0,
                14 => limit,
                _ => ((UInt128)(uint)k * (UInt128)Pow10(36)) + (uint)k,
            },
        };
    }

    private static System.Numerics.BigInteger Bigs(Shape shape, int i, int k)
    {
        System.Numerics.BigInteger limit = System.Numerics.BigInteger.Pow(10, 76) - 1;
        return shape switch
        {
            Shape.Progression => ((System.Numerics.BigInteger)i * 17) - 3,
            Shape.Spread => ((System.Numerics.BigInteger)Mix(i, 149) << 100) - Mix(i, 151),
            _ => k switch
            {
                0 => -limit,
                14 => limit,
                _ => ((System.Numerics.BigInteger)(k - 7) * System.Numerics.BigInteger.Pow(10, 73)) + k,
            },
        };
    }

    private static readonly double[] DoublePool =
    [
        double.NaN, -0.0, 0.0, 1.5, -2.25, 12.34, 1e-7, double.MaxValue, double.MinValue, double.Epsilon,
        double.PositiveInfinity, double.NegativeInfinity, 100.01, 3.14159, 0.1, 42.0,
    ];

    private static Half Halves(Shape shape, int i, int k) => shape switch
    {
        Shape.Progression => (Half)(i % 2_048 * 0.5),
        Shape.Spread => (Half)(Mix(i, 43) % 2_000 / 4.0),
        _ => k switch
        {
            7 => Half.MaxValue,
            8 => Half.MinValue,
            9 => Half.Epsilon,
            _ => (Half)DoublePool[k],
        },
    };

    private static float Singles(Shape shape, int i, int k) => shape switch
    {
        Shape.Progression => i * 0.25f,
        Shape.Spread => Mix(i, 47) % 1_000_000 / 100f,
        _ => k switch
        {
            7 => float.MaxValue,
            8 => float.MinValue,
            9 => float.Epsilon,
            _ => (float)DoublePool[k],
        },
    };

    private static double Doubles(Shape shape, int i, int k) => shape switch
    {
        Shape.Progression => i * 0.125,
        Shape.Spread => Mix(i, 53) % 10_000_000 / 100.0,
        _ => DoublePool[k],
    };

    /// <summary>A decimal of <paramref name="precision"/> digits at <paramref name="scale"/>, the largest and smallest among them.</summary>
    private static decimal Decimals(Shape shape, int i, int k, int precision, int scale)
    {
        Int128 limit = Pow10(precision) - 1;
        Int128 unscaled = shape switch
        {
            Shape.Progression => (Int128)(i - 10_000) * 7 % (limit + 1),
            Shape.Spread => SpreadWithin(limit, Mix(i, 59)),
            _ => k switch
            {
                0 => -limit,
                14 => limit,
                _ => ((k - 7) * Pow10(Math.Max(0, precision - 3))) + k,
            },
        };

        return ToDecimal(unscaled, scale);
    }

    private static Int128 Pow10(int exponent)
    {
        Int128 power = 1;
        for (int i = 0; i < exponent; i++)
        {
            power *= 10;
        }

        return power;
    }

    /// <summary>A value spread over at most a trillion values centred on zero, within <paramref name="limit"/>.</summary>
    private static Int128 SpreadWithin(Int128 limit, ulong hash)
    {
        Int128 range = Int128.Min(limit, 1_000_000_000_000);
        return (Int128)(hash % (ulong)range) - (range / 2);
    }

    private static decimal ToDecimal(Int128 unscaled, int scale)
    {
        bool negative = unscaled < 0;
        UInt128 magnitude = (UInt128)(negative ? -unscaled : unscaled);
        return new decimal((int)(uint)magnitude, (int)(uint)(magnitude >> 32), (int)(uint)(magnitude >> 64), negative, (byte)scale);
    }

    /// <summary>A wide decimal of <paramref name="precision"/> digits, reaching past what 128 bits hold when the precision allows.</summary>
    private static VortexDecimal Wide(Shape shape, int i, int k, int precision, int scale)
    {
        System.Numerics.BigInteger limit = System.Numerics.BigInteger.Pow(10, precision) - 1;
        System.Numerics.BigInteger unscaled = shape switch
        {
            Shape.Progression => (System.Numerics.BigInteger)(i - 10_000) * 13,
            Shape.Spread => (System.Numerics.BigInteger)(Mix(i, 61) % 1_000_000_000_000UL) - 500_000_000_000L,
            _ => k switch
            {
                0 => -limit,
                14 => limit,
                _ => ((System.Numerics.BigInteger)(k - 7) * System.Numerics.BigInteger.Pow(10, precision - 3)) + k,
            },
        };

        return FromBig(unscaled, (byte)precision, (sbyte)scale);
    }

    internal static VortexDecimal FromBig(System.Numerics.BigInteger unscaled, byte precision, sbyte scale) =>
        VortexDecimal.FromBigInteger(unscaled, precision, scale);

    private static readonly string[] TextPool =
    [
        "", "a", "Paris", "Lyon", "Marseille", "Toulouse", "Nice", "Nantes", "Strasbourg", "Montpellier",
        "Bordeaux", "Lille", "une chaîne bien plus longue que les douze octets d'une vue", "日本語のテキスト", "emoji 🙂", "Rennes",
    ];

    private static string Texts(Shape shape, int i, int k) => shape switch
    {
        Shape.Progression => "row-" + i.ToString(CultureInfo.InvariantCulture),
        Shape.Spread => "user" + (Mix(i, 67) % 50_000).ToString(CultureInfo.InvariantCulture)
            + "@" + TextPool[2 + (int)(Mix(i, 71) % 10)].ToLowerInvariant() + ".example.com",
        _ => TextPool[k],
    };

    private static ReadOnlyMemory<byte> Blobs(Shape shape, int i, int k) => shape switch
    {
        Shape.Progression => BitConverter.GetBytes(i),
        Shape.Spread => BitConverter.GetBytes(Mix(i, 73) % 100_000),
        _ => k == 0 ? ReadOnlyMemory<byte>.Empty : System.Text.Encoding.UTF8.GetBytes(TextPool[k]),
    };

    private static DateOnly Dates(Shape shape, int i, int k) => shape switch
    {
        Shape.Progression => DateOnly.FromDayNumber(730_000 + i),
        Shape.Spread => DateOnly.FromDayNumber(737_000 + (int)(Mix(i, 79) % 3_650)),
        _ => k switch
        {
            0 => DateOnly.MinValue,
            14 => DateOnly.MaxValue,
            _ => DateOnly.FromDayNumber(719_162 + ((k - 7) * 5_000)),
        },
    };

    private static TimeOnly Times(Shape shape, int i, int k) => shape switch
    {
        Shape.Progression => new TimeOnly(i * TimeSpan.TicksPerSecond),
        Shape.Spread => new TimeOnly((long)(Mix(i, 83) % 86_400) * TimeSpan.TicksPerSecond),
        _ => k switch
        {
            0 => TimeOnly.MinValue,
            14 => new TimeOnly(TimeSpan.TicksPerDay - TimeSpan.TicksPerMicrosecond),
            _ => new TimeOnly(k * 3_333 * TimeSpan.TicksPerSecond + (k * TimeSpan.TicksPerMicrosecond)),
        },
    };

    /// <summary>An instant whose ticks are a multiple of <paramref name="granularity"/>, the column's unit; within 1677..2262 unless <paramref name="wide"/>.</summary>
    private static DateTime Stamps(Shape shape, int i, int k, long granularity, bool wide)
    {
        long ticks = shape switch
        {
            Shape.Progression => new DateTime(2024, 1, 1).Ticks + (i * TimeSpan.TicksPerSecond),
            Shape.Spread => new DateTime(2024, 1, 1).Ticks + ((long)(Mix(i, 89) % 31_536_000) * TimeSpan.TicksPerSecond),
            _ => k switch
            {
                0 => wide ? DateTime.MinValue.Ticks : new DateTime(1700, 1, 1).Ticks,
                // The reference's instants stop at 9999-12-30T22:00Z, which leaves room for any offset.
                14 => wide ? new DateTime(9999, 12, 30, 21, 59, 59).Ticks : new DateTime(2250, 1, 1).Ticks,
                _ => Epoch.Ticks + ((k - 7) * 1_000_003 * TimeSpan.TicksPerSecond) + (k * 123_457),
            },
        };

        return new DateTime(ticks - (((ticks % granularity) + granularity) % granularity), DateTimeKind.Unspecified);
    }

    private static Guid Uuids(Shape shape, int i, int k) => shape switch
    {
        Shape.Progression or Shape.Spread => new Guid((int)Mix(i, 97), (short)i, (short)k, 1, 2, 3, 4, 5, 6, 7, 8),
        _ => k switch
        {
            0 => Guid.Empty,
            14 => Guid.AllBitsSet,
            _ => new Guid(k, (short)k, (short)(k * 2), 9, 8, 7, 6, 5, 4, 3, (byte)k),
        },
    };

    private static Dictionary<string, int> Scores(Shape shape, int i, int k)
    {
        int length = shape == Shape.Spread ? (int)(Mix(i, 163) % 4) : k % 3;
        Dictionary<string, int> map = new Dictionary<string, int>(length);
        for (int j = 0; j < length; j++)
        {
            map["key" + j.ToString(CultureInfo.InvariantCulture) + "-" + Texts(shape, i, k)] = Signed32(shape, i + j, (k + j) % 15);
        }

        return map;
    }

    private static List<Inner> Records(Shape shape, int i, int k)
    {
        int length = shape == Shape.Spread ? (int)(Mix(i, 157) % 4) : k % 3;
        List<Inner> list = new List<Inner>(length);
        for (int j = 0; j < length; j++)
        {
            list.Add(new Inner(Signed32(shape, i + j, (k + j) % 15), j == 1 ? null : Texts(shape, i + j, (k + j) % 15)));
        }

        return list;
    }

    private static ReadOnlyMemory<int> IntLists(Shape shape, int i, int k)
    {
        int length = shape == Shape.Spread ? (int)(Mix(i, 101) % 6) : k % 5;
        int[] list = new int[length];
        for (int j = 0; j < length; j++)
        {
            list[j] = Signed32(shape, i + j, (k + j) % 15);
        }

        return list;
    }

    private static ReadOnlyMemory<string> TextLists(Shape shape, int i, int k)
    {
        int length = shape == Shape.Spread ? (int)(Mix(i, 103) % 4) : k % 3;
        string[] list = new string[length];
        for (int j = 0; j < length; j++)
        {
            list[j] = Texts(shape, i + j, (k + j) % 15);
        }

        return list;
    }

    private static ReadOnlyMemory<long?> LongLists(Shape shape, int i, int k)
    {
        int length = shape == Shape.Spread ? (int)(Mix(i, 107) % 5) : (k % 4) + 1;
        long?[] list = new long?[length];
        for (int j = 0; j < length; j++)
        {
            list[j] = j == 1 ? null : Signed64(shape, i + j, (k + j) % 15);
        }

        return list;
    }
}
