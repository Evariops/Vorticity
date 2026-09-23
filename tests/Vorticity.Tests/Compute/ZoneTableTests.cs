using System;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Xunit;

namespace Vorticity.Tests.Compute;

public sealed class ZoneTableTests
{
    private const long ZoneLength = 100;

    [Theory]
    [InlineData(FilterLiteralKind.Signed, 1)]
    [InlineData(FilterLiteralKind.Signed, 2)]
    [InlineData(FilterLiteralKind.Unsigned, 3)]
    [InlineData(FilterLiteralKind.Unsigned, 4)]
    [InlineData(FilterLiteralKind.Float, 5)]
    [InlineData(FilterLiteralKind.Float, 6)]
    [InlineData(FilterLiteralKind.Float, 7)]
    internal void TheMaskAnswersAsTheZonesDoOneByOne(FilterLiteralKind kind, int seed)
    {
        // Zones a writer of this library never makes and a foreign one may: bounds missing on one
        // side, NaN bounds, zones whose nulls fill them, NaN counts recorded or not, a short last
        // zone. Every filter on the column, over masks with dead blocks already, must leave live
        // exactly the blocks the range question does not refuse, with the table answering what it
        // can and the zones asked one by one for the rest.
        Random random = new Random(seed);
        FieldExpr field = Expr.Field("x");
        for (int column = 0; column < 20; column++)
        {
            int zones = random.Next(1, 300);
            long rows = (zones * ZoneLength) - random.Next(0, (int)ZoneLength);
            ZoneColumn zoned = new ZoneColumn(field, ZoneLength, rows, RandomZones(random, kind, zones, rows));
            bool bounded = false;
            for (int zone = 0; zone < zones; zone++)
            {
                bounded |= zoned.Bounds(zone).HasMin || zoned.Bounds(zone).HasMax;
            }

            Assert.Equal(bounded ? kind : FilterLiteralKind.Null, zoned.Table.Kind);
            for (int trial = 0; trial < 60; trial++)
            {
                VortexExpr filter = RandomFilter(random, field, depth: 2);
                ZonePruner pruner = new ZonePruner(filter, [zoned]);
                BlockMask mask = new BlockMask(rows, ZoneLength);
                bool[] wasLive = new bool[mask.BlockCount];
                for (int block = 0; block < mask.BlockCount; block++)
                {
                    if (random.Next(5) == 0)
                    {
                        mask.Kill(block);
                    }

                    wasLive[block] = mask.IsLive(block);
                }

                pruner.Refine(mask);
                for (int block = 0; block < mask.BlockCount; block++)
                {
                    bool expected = wasLive[block] && pruner.MayMatch(mask.BlockRange(block));
                    Assert.True(expected == mask.IsLive(block), $"block {block} of {filter}: the question says {expected}");
                }
            }
        }
    }

    [Fact]
    public void AColumnOfMixedKindsHasNoTable()
    {
        ZoneBounds[] zones =
        [
            ZoneBounds.Create(FilterLiteral.From(1L), true, FilterLiteral.From(5L), true, true, 0, true),
            ZoneBounds.Create(FilterLiteral.From(1.5), true, FilterLiteral.From(5.5), true, true, 0, true),
        ];

        ZoneColumn column = new ZoneColumn(Expr.Field("x"), ZoneLength, 2 * ZoneLength, zones);

        Assert.Same(ZoneTable.None, column.Table);
    }

    private static ZoneBounds[] RandomZones(Random random, FilterLiteralKind kind, int zones, long rows)
    {
        ZoneBounds[] bounds = new ZoneBounds[zones];
        for (int zone = 0; zone < zones; zone++)
        {
            long inZone = Math.Min(ZoneLength, rows - (zone * ZoneLength));
            FilterLiteral low = RandomValue(random, kind);
            FilterLiteral high = RandomValue(random, kind);
            if (kind != FilterLiteralKind.Float && Order(low) > Order(high))
            {
                (low, high) = (high, low);
            }

            long nulls = random.Next(4) switch
            {
                0 => 0,
                1 => inZone,
                2 => inZone + 5,
                _ => random.NextInt64(0, inZone + 1),
            };

            bounds[zone] = ZoneBounds.Create(
                low,
                random.Next(6) != 0,
                high,
                random.Next(6) != 0,
                random.Next(2) == 0,
                nulls,
                random.Next(5) != 0,
                random.Next(3),
                random.Next(2) == 0);
        }

        return bounds;
    }

    private static double Order(FilterLiteral value) =>
        value.Kind == FilterLiteralKind.Signed ? value.SignedValue : value.UnsignedValue;

    private static FilterLiteral RandomValue(Random random, FilterLiteralKind kind) => kind switch
    {
        FilterLiteralKind.Signed => FilterLiteral.From(random.Next(12) == 0 ? long.MinValue : random.NextInt64(-40, 41)),
        FilterLiteralKind.Unsigned => FilterLiteral.From(random.Next(12) == 0 ? ulong.MaxValue : (ulong)random.Next(0, 81)),
        _ => random.Next(10) switch
        {
            0 => FilterLiteral.From(double.NaN),
            1 => FilterLiteral.From(double.PositiveInfinity),
            2 => FilterLiteral.From(double.NegativeInfinity),
            3 => FilterLiteral.From(-0.0),
            _ => FilterLiteral.From(random.Next(-80, 81) / 2.0),
        },
    };

    private static VortexExpr RandomFilter(Random random, FieldExpr field, int depth) =>
        (depth == 0 ? 0 : random.Next(5)) switch
        {
            1 => Expr.Not(RandomFilter(random, field, depth - 1)),
            2 => Expr.And(RandomFilter(random, field, depth - 1), RandomFilter(random, field, depth - 1)),
            3 => Expr.Or(RandomFilter(random, field, depth - 1), RandomFilter(random, field, depth - 1)),
            _ => Compare(random, field),
        };

    private static ComparisonExpr Compare(Random random, FieldExpr field)
    {
        FilterLiteral value = random.Next(9) switch
        {
            0 => FilterLiteral.From(random.NextInt64(-45, 46)),
            1 => FilterLiteral.From((ulong)random.Next(0, 90)),
            2 => FilterLiteral.From(random.Next(-90, 91) / 2.0),
            3 => FilterLiteral.From(double.NaN),
            4 => FilterLiteral.From(random.Next(2) == 0 ? double.PositiveInfinity : double.NegativeInfinity),
            5 => FilterLiteral.Null,
            6 => FilterLiteral.From("x"),
            7 => FilterLiteral.From(random.Next(2) == 0 ? long.MinValue : long.MaxValue),
            _ => FilterLiteral.From(random.Next(2) == 0 ? ulong.MaxValue : 0UL),
        };

        LiteralExpr literal = Expr.Literal(value);
        return random.Next(6) switch
        {
            0 => Expr.Eq(field, literal),
            1 => Expr.Ne(field, literal),
            2 => Expr.Lt(field, literal),
            3 => Expr.Le(field, literal),
            4 => Expr.Gt(field, literal),
            _ => Expr.Ge(field, literal),
        };
    }
}
