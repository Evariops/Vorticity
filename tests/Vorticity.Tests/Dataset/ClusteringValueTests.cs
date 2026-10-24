// The clustering value as the library holds it: what a header records of it, and how it tells two keys of one value
// apart as a job writes its rows, and the one value an object holds from its summaries.
//
// THE ORACLE IS THE FUNCTION A QUESTION APPLIES: two keys hold one value exactly when the scan's own function, applied
// to their first column, gives one image; the keys are encoded as the merge encodes them, never by hand.
using System;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Scanning;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class ClusteringValueTests
{
    private static readonly DateTimeOffset Midnight = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EveryValueIsWrittenInTheHeaderAndReadBackTheSame()
    {
        ClusteringOptions[] declared =
        [
            new ClusteringOptions { Value = ClusteringValue.Column },
            new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour), ObjectBytes = 1L << 30 },
            new ClusteringOptions { Value = ClusteringValue.Bucket(TimeSpan.FromMinutes(5)) },
            new ClusteringOptions { Value = ClusteringValue.Bucket(1_000) },
            new ClusteringOptions { Value = ClusteringValue.Bucket(0.25) },
            new ClusteringOptions { ObjectBytes = 64L << 20 },
            new ClusteringOptions(),
        ];

        foreach (ClusteringOptions clustering in declared)
        {
            CommitHeader header = new CommitHeader { Version = 3, Clustering = clustering, Features = DatasetFeatures.Clustering };
            ProtoWriter writer = new ProtoWriter();
            try
            {
                header.Write(ref writer);
                Assert.Equal(clustering, CommitHeader.Read(writer.WrittenSpan).Clustering);
            }
            finally
            {
                writer.Dispose();
            }
        }

        // The flag and the field go together.
        Assert.Throws<CommitFormatException>(() => Read(new CommitHeader { Version = 3, Clustering = new ClusteringOptions() }));
        Assert.Throws<CommitFormatException>(() => Read(new CommitHeader { Version = 3, Features = DatasetFeatures.Clustering }));
        Assert.Null(Read(new CommitHeader { Version = 3 }).Clustering);
    }

    [Fact]
    public void AValueIsDeclaredOnlyWithArgumentsItCanCut()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClusteringValue.Truncate((CalendarUnit)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClusteringValue.Bucket(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClusteringValue.Bucket(0L));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClusteringValue.Bucket(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClusteringValue.Bucket(-1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClusteringOptions { ObjectBytes = -1 });

        Assert.Equal("Truncate(Day)", ClusteringValue.Truncate(CalendarUnit.Day).ToString());
        Assert.Equal("Bucket(1000)", ClusteringValue.Bucket(1_000).ToString());
        Assert.Equal(ClusteringValue.Bucket(TimeSpan.FromHours(1)), ClusteringValue.Bucket(TimeSpan.FromHours(1)));
        Assert.NotEqual(ClusteringValue.Bucket(1_000), ClusteringValue.Bucket(1_000.0));
    }

    [Fact]
    public void AnInstantTruncatedToTheDayTellsTwoKeysOfOneDayApartFromTwoDays()
    {
        ClusteringKey key = KeyOf(Event.Schema, nameof(Event.At), nameof(Event.Name));
        ValueCut cut = ValueCut.Of(new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Day) }, key)!;

        byte[] morning = key.Encode([Micros(Midnight.AddHours(1)), FilterLiteral.From("b")]);
        byte[] evening = key.Encode([Micros(Midnight.AddHours(23).AddMinutes(59)), FilterLiteral.From("a")]);
        byte[] next = key.Encode([Micros(Midnight.AddDays(1)), FilterLiteral.From("a")]);
        byte[] before = key.Encode([Micros(Midnight.AddTicks(-10)), FilterLiteral.From("z")]);
        Assert.True(cut.Same(morning, evening));
        Assert.False(cut.Same(evening, next));
        Assert.False(cut.Same(before, morning));

        // The column alone cuts at every instant.
        ValueCut column = ValueCut.Of(new ClusteringOptions { Value = ClusteringValue.Column }, key)!;
        Assert.False(column.Same(morning, evening));
        Assert.True(column.Same(next, key.Encode([Micros(Midnight.AddDays(1)), FilterLiteral.From("zz")])));
    }

    [Fact]
    public void ATextKeyCutsAtEveryValueWhateverItsLength()
    {
        ClusteringKey key = KeyOf(Tenanted.Schema, nameof(Tenanted.Tenant), nameof(Tenanted.Id));
        ValueCut cut = ValueCut.Of(new ClusteringOptions { Value = ClusteringValue.Column }, key)!;

        // Values of a block and more, a prefix of another, and the empty one: the bytes of the value end where it ends.
        string longer = new string('t', 70);
        Assert.True(cut.Same(key.Encode([FilterLiteral.From(longer), FilterLiteral.From(1L)]), key.Encode([FilterLiteral.From(longer), FilterLiteral.From(9L)])));
        Assert.False(cut.Same(key.Encode([FilterLiteral.From(longer), FilterLiteral.From(1L)]), key.Encode([FilterLiteral.From(longer + "u"), FilterLiteral.From(1L)])));
        Assert.False(cut.Same(key.Encode([FilterLiteral.From("ab"), FilterLiteral.From(1L)]), key.Encode([FilterLiteral.From("abc"), FilterLiteral.From(1L)])));
        Assert.False(cut.Same(key.Encode([FilterLiteral.From(string.Empty), FilterLiteral.From(1L)]), key.Encode([FilterLiteral.From("a"), FilterLiteral.From(1L)])));
        Assert.True(cut.Same(key.Encode([FilterLiteral.From(string.Empty), FilterLiteral.From(1L)]), key.Encode([FilterLiteral.From(string.Empty), FilterLiteral.From(2L)])));

        // A text has no calendar and no bucket.
        Assert.Throws<ArgumentException>(() => ValueCut.Of(new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Day) }, key));
        Assert.Throws<ArgumentException>(() => ValueCut.Of(new ClusteringOptions { Value = ClusteringValue.Bucket(10) }, key));
    }

    [Fact]
    public void ANumberBucketCutsWhereTheScansBucketChanges()
    {
        ClusteringKey key = KeyOf(Numbered.Schema, nameof(Numbered.Id));
        ValueCut cut = ValueCut.Of(new ClusteringOptions { Value = ClusteringValue.Bucket(100) }, key)!;
        long[] ids = [-201, -200, -101, -100, -1, 0, 1, 99, 100, 199, 200, long.MinValue, long.MaxValue];
        foreach (long left in ids)
        {
            foreach (long right in ids)
            {
                bool same = Math.Floor(left / 100.0) == Math.Floor(right / 100.0);
                if (left is long.MinValue or long.MaxValue || right is long.MinValue or long.MaxValue)
                {
                    same = left == right;
                }

                Assert.Equal(same, cut.Same(key.Encode([FilterLiteral.From(left)]), key.Encode([FilterLiteral.From(right)])));
            }
        }

        // A float bucket, on a float key.
        ClusteringKey measured = KeyOf(Measured.Schema, nameof(Measured.Level));
        ValueCut halves = ValueCut.Of(new ClusteringOptions { Value = ClusteringValue.Bucket(0.5) }, measured)!;
        Assert.True(halves.Same(measured.Encode([FilterLiteral.From(-0.75)]), measured.Encode([FilterLiteral.From(-0.5000001)])));
        Assert.False(halves.Same(measured.Encode([FilterLiteral.From(-0.5)]), measured.Encode([FilterLiteral.From(-0.5000001)])));
        Assert.True(halves.Same(measured.Encode([FilterLiteral.From(1.0)]), measured.Encode([FilterLiteral.From(1.49)])));

        // An instant's span on a number, a day on a number: refused.
        Assert.Throws<ArgumentException>(() => ValueCut.Of(new ClusteringOptions { Value = ClusteringValue.Bucket(TimeSpan.FromHours(1)) }, key));
        Assert.Throws<ArgumentException>(() => ValueCut.Of(new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Day) }, key));
    }

    [Fact]
    public void AnObjectHoldsOneValueWhenTheImagesOfItsBoundsAreOne()
    {
        ClusteringKey key = KeyOf(Event.Schema, nameof(Event.At));
        ValueCut cut = ValueCut.Of(new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Day) }, key)!;

        ObjectSummaries oneDay = Summaries(Micros(Midnight.AddHours(1)), Micros(Midnight.AddHours(23)));
        ObjectSummaries twoDays = Summaries(Micros(Midnight.AddHours(1)), Micros(Midnight.AddHours(25)));
        Assert.Equal(Micros(Midnight), cut.Single(oneDay));
        Assert.True(cut.HoldsOnly(oneDay, Micros(Midnight)));
        Assert.False(cut.HoldsOnly(oneDay, Micros(Midnight.AddDays(1))));
        Assert.Null(cut.Single(twoDays));
        Assert.Null(cut.Single(ObjectSummaries.Empty));
    }

    private static CommitHeader Read(CommitHeader header)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            header.Write(ref writer);
            return CommitHeader.Read(writer.WrittenSpan);
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static ClusteringKey KeyOf(VortexSchema schema, params string[] paths) =>
        ClusteringKey.For(paths, VortexTypes.ToDType(schema, new DTypeArena()))!;

    private static FilterLiteral Micros(DateTimeOffset at) => FilterLiteral.From((at - DateTimeOffset.UnixEpoch).Ticks / 10);

    private static ObjectSummaries Summaries(FilterLiteral min, FilterLiteral max) =>
        ObjectSummaries.From([new ColumnSummary(nameof(Event.At), min, true, max, true, true, 0, true)]);

    [VortexRecord]
    public partial record struct Event(DateTimeOffset At, string Name);

    [VortexRecord]
    public partial record struct Tenanted(string Tenant, long Id);

    [VortexRecord]
    public partial record struct Numbered(long Id);

    [VortexRecord]
    public partial record struct Measured(double Level);
}
