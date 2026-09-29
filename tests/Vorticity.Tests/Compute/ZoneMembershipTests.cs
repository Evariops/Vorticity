// A zone map answering an IN, at a size where the cost per zone is visible.
//
// The pruner answers membership from ordered candidates once there are enough of them, and from a
// pass per candidate below that. Both have to give the same answers, so every case here is run at
// a count on each side of the threshold and the two are compared against a reference written from
// the definition: a zone may hold a candidate exactly when one falls within its bounds.
using System;
using System.Collections.Generic;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Xunit;

namespace Vorticity.Tests.Compute;

public class ZoneMembershipTests
{
    private const string Field = "v";
    private const int ZoneLength = 1_024;
    private const int Zones = 1_000;
    private const long Rows = (long)Zones * ZoneLength;

    /// <summary>Counts on each side of the threshold that turns the walk into a search.</summary>
    public static TheoryData<int> Counts => [4, 31, 32, 4_096];

    [Theory]
    [MemberData(nameof(Counts))]
    public void ProvesAThousandZonesCannotHoldCandidatesThatSortBelowThemAll(int candidates)
    {
        FilterLiteral[] literals = new FilterLiteral[candidates];
        for (int i = 0; i < candidates; i++)
        {
            literals[i] = FilterLiteral.From(-(i + 1L));
        }

        Assert.False(Pruner(literals).MayMatch(new RowRange(0, Rows)));
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void FindsACandidateTheLastZoneHolds(int candidates)
    {
        FilterLiteral[] literals = new FilterLiteral[candidates];
        for (int i = 0; i < candidates - 1; i++)
        {
            literals[i] = FilterLiteral.From(-(i + 1L));
        }

        literals[candidates - 1] = FilterLiteral.From(Rows - 1);
        Assert.True(Pruner(literals).MayMatch(new RowRange(0, Rows)));
    }

    /// <summary>
    /// Every zone asked on its own, against the answer the definition gives: the two paths may not
    /// disagree about a single one, and an off-by-one in the search would show as one zone out of a
    /// thousand.
    /// </summary>
    [Theory]
    [MemberData(nameof(Counts))]
    public void AnswersEachZoneAsTheDefinitionDoes(int candidates)
    {
        FilterLiteral[] literals = Spread(candidates);
        ZonePruner pruner = Pruner(literals);
        List<int> disagreed = [];
        for (int zone = 0; zone < Zones; zone++)
        {
            long low = (long)zone * ZoneLength;
            bool expected = false;
            for (int i = 0; i < literals.Length; i++)
            {
                long value = literals[i].SignedValue;
                expected |= value >= low && value <= low + ZoneLength - 1;
            }

            if (pruner.MayMatch(new RowRange(low, low + ZoneLength)) != expected)
            {
                disagreed.Add(zone);
            }
        }

        Assert.Empty(disagreed);
    }

    /// <summary>
    /// The counting dual over the same map: a zone holding no candidate selects none of its rows,
    /// and the count the pruner proves is the one the definition gives over the whole range.
    /// </summary>
    [Theory]
    [MemberData(nameof(Counts))]
    public void CountsTheRowsOfZonesThatHoldNoCandidate(int candidates)
    {
        FilterLiteral[] literals = new FilterLiteral[candidates];
        for (int i = 0; i < candidates; i++)
        {
            literals[i] = FilterLiteral.From(-(i + 1L));
        }

        Assert.True(Pruner(literals).TryCount(new RowRange(0, Rows), out long count));
        Assert.Equal(0, count);
    }

    /// <summary>
    /// A candidate that is a zone's every value proves the whole zone, which is the one claim the
    /// search has to make besides impossibility.
    /// </summary>
    [Theory]
    [MemberData(nameof(Counts))]
    public void ProvesAZoneWhoseSingleExactValueIsACandidate(int candidates)
    {
        const long constant = 7;
        FilterLiteral[] literals = new FilterLiteral[candidates];
        for (int i = 0; i < candidates - 1; i++)
        {
            literals[i] = FilterLiteral.From(-(i + 1L));
        }

        literals[candidates - 1] = FilterLiteral.From(constant);

        ZoneBounds[] zones =
        [
            ZoneBounds.Create(
                FilterLiteral.From(constant), true, FilterLiteral.From(constant), true,
                exact: true, nullCount: 0, hasNullCount: true),
        ];

        ZonePruner pruner = new ZonePruner(
            Expr.In(Expr.Field(Field), literals),
            [new ZoneColumn(Expr.Field(Field), ZoneLength, ZoneLength, zones)]);

        Assert.True(pruner.TryCount(new RowRange(0, ZoneLength), out long count));
        Assert.Equal(ZoneLength, count);
    }

    /// <summary>
    /// Signed and unsigned candidates in one <c>IN</c>, which order on a single line only if every
    /// negative sits below every unsigned value.
    /// </summary>
    [Fact]
    public void OrdersSignedAndUnsignedCandidatesTogether()
    {
        FilterLiteral[] literals = new FilterLiteral[64];
        for (int i = 0; i < 32; i++)
        {
            literals[i] = FilterLiteral.From(-(i + 1L));
            literals[32 + i] = FilterLiteral.From((ulong)(Rows + i));
        }

        // Nothing inside the column: the negatives sort below every zone and the unsigned ones
        // above the last, and a comparison that read a negative as a large unsigned would place it
        // inside one.
        Assert.False(Pruner(literals).MayMatch(new RowRange(0, Rows)));

        literals[0] = FilterLiteral.From(ZoneLength + 5L);
        Assert.True(Pruner(literals).MayMatch(new RowRange(0, Rows)));
    }

    /// <summary>A null candidate leaves every row unknown, whatever the count of the others.</summary>
    [Theory]
    [MemberData(nameof(Counts))]
    public void KeepsAZoneWhoseCandidatesIncludeNull(int candidates)
    {
        FilterLiteral[] literals = new FilterLiteral[candidates];
        for (int i = 0; i < candidates - 1; i++)
        {
            literals[i] = FilterLiteral.From(-(i + 1L));
        }

        literals[candidates - 1] = FilterLiteral.Null;
        Assert.True(Pruner(literals).MayMatch(new RowRange(0, Rows)));
    }

    /// <summary>
    /// Float candidates against float bounds, zone by zone, against the definition as IEEE orders
    /// floats: the two zeros are one value, and a NaN bound claims nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(Counts))]
    public void AnswersEachFloatZoneAsTheDefinitionDoes(int candidates)
    {
        // Zone z holds [z - 500, z - 499.5], half of them below zero, but for two exact zones at
        // -0.0 and +0.0 and one whose maximum is a NaN. The candidates fall in every other zone,
        // below zero as above it, and the last is +0.0.
        const double Offset = Zones / 2;
        ZoneBounds[] zones = new ZoneBounds[Zones];
        for (int zone = 0; zone < Zones; zone++)
        {
            (double min, double max) = zone switch
            {
                10 => (-0.0, -0.0),
                11 => (0.0, 0.0),
                12 => (zone - Offset, double.NaN),
                _ => (zone - Offset, zone - Offset + 0.5),
            };

            zones[zone] = ZoneBounds.Create(
                FilterLiteral.From(min), true, FilterLiteral.From(max), true,
                exact: true, nullCount: 0, hasNullCount: true);
        }

        FilterLiteral[] literals = new FilterLiteral[candidates];
        for (int i = 0; i < candidates - 1; i++)
        {
            literals[i] = FilterLiteral.From((2.0 * (i % (Zones / 2))) + 0.25 - Offset);
        }

        literals[candidates - 1] = FilterLiteral.From(0.0);
        ZonePruner pruner = new ZonePruner(
            Expr.In(Expr.Field(Field), literals),
            [new ZoneColumn(Expr.Field(Field), ZoneLength, Rows, zones)]);

        List<int> disagreed = [];
        for (int zone = 0; zone < Zones; zone++)
        {
            // A NaN bound says nothing of its side, and the other side still rules a value out. A
            // zone every value of which is a candidate counts whole when its NaNs are known, which
            // these bounds do not say.
            ZoneBounds bounds = zones[zone];
            double min = bounds.Min.FloatValue;
            double max = bounds.Max.FloatValue;
            bool expected = false;
            bool whole = false;
            for (int i = 0; i < literals.Length; i++)
            {
                double value = literals[i].FloatValue;
                expected |= (double.IsNaN(min) || value >= min) && (double.IsNaN(max) || value <= max);
                whole |= value == min && value == max;
            }

            RowRange range = new RowRange((long)zone * ZoneLength, (long)(zone + 1) * ZoneLength);
            bool counted = pruner.TryCount(range, out long count);
            if (pruner.MayMatch(range) != expected
                || (!expected && !(counted && count == 0))
                || (whole && counted && count != ZoneLength))
            {
                disagreed.Add(zone);
            }
        }

        Assert.Empty(disagreed);
    }

    /// <summary>
    /// A NaN candidate equals no value, and the proof of an equality leaves it open on every zone:
    /// an <c>IN</c> holding one keeps every zone, whatever the count of the others.
    /// </summary>
    [Theory]
    [MemberData(nameof(Counts))]
    public void KeepsAZoneWhoseFloatCandidatesIncludeANaN(int candidates)
    {
        FilterLiteral[] literals = new FilterLiteral[candidates];
        for (int i = 0; i < candidates - 1; i++)
        {
            literals[i] = FilterLiteral.From(-(i + 1.5));
        }

        literals[candidates - 1] = FilterLiteral.From(double.NaN);
        ZoneBounds[] zones =
        [
            ZoneBounds.Create(
                FilterLiteral.From(1.0), true, FilterLiteral.From(2.0), true,
                exact: true, nullCount: 0, hasNullCount: true),
        ];

        ZonePruner pruner = new ZonePruner(
            Expr.In(Expr.Field(Field), literals),
            [new ZoneColumn(Expr.Field(Field), ZoneLength, ZoneLength, zones)]);
        Assert.True(pruner.MayMatch(new RowRange(0, ZoneLength)));
    }

    /// <summary>Candidates one per zone, so half the zones hold one and half do not.</summary>
    private static FilterLiteral[] Spread(int candidates)
    {
        FilterLiteral[] literals = new FilterLiteral[candidates];
        for (int i = 0; i < candidates; i++)
        {
            literals[i] = FilterLiteral.From(((long)i * ZoneLength * 2) + 3);
        }

        return literals;
    }

    private static ZonePruner Pruner(FilterLiteral[] literals)
    {
        ZoneBounds[] zones = new ZoneBounds[Zones];
        for (int zone = 0; zone < Zones; zone++)
        {
            long low = (long)zone * ZoneLength;
            zones[zone] = ZoneBounds.Create(
                FilterLiteral.From(low), true,
                FilterLiteral.From(Math.Min(low + ZoneLength, Rows) - 1), true,
                exact: true, nullCount: 0, hasNullCount: true);
        }

        return new ZonePruner(
            Expr.In(Expr.Field(Field), literals),
            [new ZoneColumn(Expr.Field(Field), ZoneLength, Rows, zones)]);
    }
}
