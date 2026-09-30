// A timestamp chunk split into days, seconds and subseconds, recomposed exactly: every unit, instants
// before the epoch as after it, null rows whose slots hold anything, and the two refusals -- a day
// past 32 bits, and parts that do not come in a tenth under the plan they compete with.
using System;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class DateTimePartsPlanTests
{
    private const long SecondsPerDay = 86_400;

    [Theory]
    [InlineData(1L, false)]
    [InlineData(1_000L, false)]
    [InlineData(1_000_000L, false)]
    [InlineData(1_000_000_000L, false)]
    [InlineData(1L, true)]
    [InlineData(1_000_000L, true)]
    [InlineData(1_000_000_000L, true)]
    public void EveryInstantRecomposesFromItsParts(long unitsPerSecond, bool nullable)
    {
        const int Rows = 3_000;
        Random random = new Random((int)(unitsPerSecond % 1_000_003) + (nullable ? 7 : 0));
        long[] values = new long[Rows];
        bool[] valid = new bool[Rows];
        long span = long.MaxValue / 2;
        for (int i = 0; i < Rows; i++)
        {
            valid[i] = !nullable || random.Next(5) != 0;

            // Around the epoch on both sides, the extremes of the range among them, and a null
            // row's slot left at whatever a caller put there.
            values[i] = (i % 4) switch
            {
                0 => random.NextInt64(-span, span),
                1 => random.NextInt64(-SecondsPerDay * unitsPerSecond * 3, SecondsPerDay * unitsPerSecond * 3),
                2 => i % 8 == 2 ? long.MinValue / 2 : long.MaxValue / 2,
                _ => -random.NextInt64(1, unitsPerSecond * 2),
            };
            if (!valid[i])
            {
                values[i] = random.NextInt64();
            }
        }

        // A day of 32 bits reaches five million years each way, which in seconds and milliseconds
        // a 64-bit instant goes past: keep inside it where it does.
        if ((double)int.MaxValue * SecondsPerDay * unitsPerSecond < long.MaxValue)
        {
            long bound = int.MaxValue * SecondsPerDay * unitsPerSecond;
            for (int i = 0; i < Rows; i++)
            {
                values[i] = Math.Clamp(values[i], -bound, bound);
            }
        }

        CanonicalArena arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        int node = Instants(arena, types, values, valid, nullable);

        DateTimePartsPlan? plan = DateTimePartsPlan.TryBuild(arena, arena.GetNode(node), unitsPerSecond, long.MaxValue);
        Assert.NotNull(plan);
        try
        {
            Assert.Equal(Rows, plan.Rows);
            for (int i = 0; i < Rows; i++)
            {
                long day = plan.Days[i];
                long second = plan.Seconds[i];
                long sub = plan.Subseconds[i];
                Assert.InRange(second, -SecondsPerDay + 1, SecondsPerDay - 1);
                Assert.InRange(sub, -unitsPerSecond + 1, unitsPerSecond - 1);
                Assert.True(Fits(second, plan.SecondsPType) && Fits(sub, plan.SubsecondsPType) && Fits(day, plan.DaysPType));
                if (valid[i])
                {
                    Assert.Equal(values[i], (day * SecondsPerDay * unitsPerSecond) + (second * unitsPerSecond) + sub);
                }
                else
                {
                    // A null row splits as the instant zero.
                    Assert.Equal((0L, 0L, 0L), (day, second, sub));
                }
            }
        }
        finally
        {
            plan.Release();
        }
    }

    [Fact]
    public void InstantsToTheSecondSplitNarrowAndFullResolutionOnesDoNot()
    {
        const int Rows = 4_096;
        const long Micros = 1_000_000;
        long year = 1_700_000_000L * Micros;
        long[] seconds = new long[Rows];
        long[] micros = new long[Rows];
        Random random = new Random(3);
        for (int i = 0; i < Rows; i++)
        {
            seconds[i] = year + (random.NextInt64(0, 365 * SecondsPerDay) * Micros);
            micros[i] = year + random.NextInt64(0, 365 * SecondsPerDay * Micros);
        }

        CanonicalArena arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        bool[] valid = new bool[Rows];
        Array.Fill(valid, true);

        // Against a frame of reference over a year of microseconds: 45 bits a row.
        long packed = (Rows / 1024) * 128 * 45;
        DateTimePartsPlan? coarse = DateTimePartsPlan.TryBuild(arena, arena.GetNode(Instants(arena, types, seconds, valid, false)), Micros, packed);
        Assert.NotNull(coarse);
        Assert.Equal(PType.I8, coarse.SubsecondsPType);
        Assert.True(coarse.EncodedSize < packed * 6 / 10, $"{coarse.EncodedSize} bytes against {packed}");
        coarse.Release();

        Assert.Null(DateTimePartsPlan.TryBuild(arena, arena.GetNode(Instants(arena, types, micros, valid, false)), Micros, packed));
    }

    [Fact]
    public void ADayPastThirtyTwoBitsHasNoPart()
    {
        CanonicalArena arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        long[] values = [0, long.MaxValue];
        int node = Instants(arena, types, values, [true, true], false);
        Assert.Null(DateTimePartsPlan.TryBuild(arena, arena.GetNode(node), 1, long.MaxValue));
        Assert.NotNull(DateTimePartsPlan.TryBuild(arena, arena.GetNode(node), 1_000_000_000, long.MaxValue));
    }

    private static bool Fits(long value, PType ptype) => ptype switch
    {
        PType.I8 => value is >= sbyte.MinValue and <= sbyte.MaxValue,
        PType.I16 => value is >= short.MinValue and <= short.MaxValue,
        _ => value is >= int.MinValue and <= int.MaxValue,
    };

    private static int Instants(CanonicalArena arena, DTypeArena types, long[] values, bool[] valid, bool nullable)
    {
        VortexBuffer buffer = arena.Allocate(values.Length * 8, 8, out Span<byte> bytes);
        MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(bytes);
        Validity validity = Validity.NonNullable;
        if (nullable)
        {
            VortexBuffer bits = arena.Allocate((values.Length + 7) / 8, 1, out Span<byte> set);
            for (int i = 0; i < valid.Length; i++)
            {
                if (valid[i])
                {
                    set[i >> 3] |= (byte)(1 << (i & 7));
                }
            }

            validity = Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), values.Length, Validity.NonNullable, bits, 0));
        }

        DType type = types.Primitive(PType.I64, nullable ? Nullability.Nullable : Nullability.NonNullable);
        return arena.AddPrimitive(type, values.Length, validity, PType.I64, buffer);
    }
}
