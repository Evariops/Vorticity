// The layout as the library holds it: what a header records of it, the values it is declared with, what a schema refuses
// of it, and how its partition maps a column's values, tells two tree keys of one partition apart and proves the one an
// object holds from its summaries.
//
// THE ORACLE IS THE FUNCTION A QUESTION APPLIES: two tree keys hold one partition exactly when the partition's own
// function gives their columns one image; the keys are encoded as the tree encodes them, never by hand.
using System;
using Vorticity.Compute;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Scanning;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class LayoutValueTests
{
    private static readonly DateTimeOffset Midnight = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EveryLayoutIsWrittenInTheHeaderAndReadBackTheSame()
    {
        DatasetLayout[] declared =
        [
            new DatasetLayout { SortKey = [LayoutValue.Column("At")] },
            new DatasetLayout
            {
                Partition = LayoutValue.Column("At").Truncate(CalendarUnit.Hour),
                SortKey = [LayoutValue.Column("Service"), LayoutValue.Column("At")],
                ObjectBytes = 1L << 30,
            },
            new DatasetLayout { Partition = LayoutValue.Column("At").Bucket(TimeSpan.FromMinutes(5)) },
            new DatasetLayout { Partition = LayoutValue.Column("Id").Bucket(1_000), SortKey = [LayoutValue.Column("Id")], Organization = LayoutOrganization.Leveled },
            new DatasetLayout { Partition = LayoutValue.Column("Level").Bucket(0.25), CalmAfter = null, MergeFanout = 4, LateShare = 2 },
            new DatasetLayout
            {
                SortKey = [LayoutValue.Path("Attributes", "http.route", VariantClass.String), LayoutValue.Column("Request.Host"), LayoutValue.Column("At")],
                IndexedPrefix = 2,
            },
            new DatasetLayout { ObjectBytes = 64L << 20 },
        ];

        foreach (DatasetLayout layout in declared)
        {
            Assert.Equal(layout, Read(new CommitHeader { Version = 3, Layout = layout }).Layout);
        }

        Assert.Null(Read(new CommitHeader { Version = 3 }).Layout);
    }

    [Fact]
    public void AValueIsDeclaredOnlyWithArgumentsItCanTake()
    {
        LayoutValue at = LayoutValue.Column("At");
        Assert.Throws<ArgumentException>(() => LayoutValue.Column(string.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => at.Truncate((CalendarUnit)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => at.Bucket(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => at.Bucket(0L));
        Assert.Throws<ArgumentOutOfRangeException>(() => at.Bucket(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => at.Bucket(-1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutValue.Path("Attributes", "k", (VariantClass)99));
        Assert.Throws<InvalidOperationException>(() => at.Truncate(CalendarUnit.Day).Bucket(TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatasetLayout { ObjectBytes = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatasetLayout { MergeFanout = 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatasetLayout { IndexedPrefix = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatasetLayout { CalmAfter = TimeSpan.FromSeconds(-1) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatasetLayout { LateShare = 0 });

        Assert.Equal("At.Truncate(Day)", at.Truncate(CalendarUnit.Day).ToString());
        Assert.Equal("Id.Bucket(1000)", LayoutValue.Column("Id").Bucket(1_000).ToString());
        Assert.Equal(at.Bucket(TimeSpan.FromHours(1)), at.Bucket(TimeSpan.FromHours(1)));
        Assert.NotEqual(LayoutValue.Column("Id").Bucket(1_000), LayoutValue.Column("Id").Bucket(1_000.0));
    }

    [Fact]
    public void ASchemaRefusesAValueItCannotMake()
    {
        // A text has no calendar and no bucket; a number no calendar and no span of time.
        Refused(Event.Schema, new DatasetLayout { Partition = LayoutValue.Column(nameof(Event.Name)).Truncate(CalendarUnit.Day) });
        Refused(Event.Schema, new DatasetLayout { Partition = LayoutValue.Column(nameof(Event.Name)).Bucket(10L) });
        Refused(Numbered.Schema, new DatasetLayout { Partition = LayoutValue.Column(nameof(Numbered.Id)).Bucket(TimeSpan.FromHours(1)) });
        Refused(Numbered.Schema, new DatasetLayout { Partition = LayoutValue.Column(nameof(Numbered.Id)).Truncate(CalendarUnit.Day) });

        // A column it lacks, a key of a column that is no variant, a component twice, a prefix past the key.
        Refused(Event.Schema, new DatasetLayout { SortKey = [LayoutValue.Column("Nowhere")] });
        Refused(Event.Schema, new DatasetLayout { SortKey = [LayoutValue.Path(nameof(Event.Name), "k", VariantClass.Integer)] });
        Refused(Event.Schema, new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Event.At)), LayoutValue.Column(nameof(Event.At))] });
        Refused(Event.Schema, new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Event.At))], IndexedPrefix = 2 });

        LayoutPlan plan = Plan(Event.Schema, new DatasetLayout
        {
            Partition = LayoutValue.Column(nameof(Event.At)).Truncate(CalendarUnit.Day),
            SortKey = [LayoutValue.Column(nameof(Event.Name))],
        });
        Assert.Equal(nameof(Event.At), plan.Partition!.Value.SourcePath);
        Assert.Single(plan.Components);
    }

    [Fact]
    public void AnInstantTruncatedToTheDayTellsTwoKeysOfOneDayApartFromTwoDays()
    {
        LayoutPlan plan = Plan(Event.Schema, new DatasetLayout
        {
            Partition = LayoutValue.Column(nameof(Event.At)).Truncate(CalendarUnit.Day),
            SortKey = [LayoutValue.Column(nameof(Event.At)), LayoutValue.Column(nameof(Event.Name))],
        });
        SortKey tree = SortKey.Of(plan, partition: true, plan.Components.Count);
        ValueCut cut = ValueCut.Of(plan, tree)!;

        byte[] morning = Key(plan, tree, Micros(Midnight.AddHours(1)), FilterLiteral.From("b"));
        byte[] evening = Key(plan, tree, Micros(Midnight.AddHours(23).AddMinutes(59)), FilterLiteral.From("a"));
        byte[] next = Key(plan, tree, Micros(Midnight.AddDays(1)), FilterLiteral.From("a"));
        byte[] before = Key(plan, tree, Micros(Midnight.AddTicks(-10)), FilterLiteral.From("z"));
        Assert.True(cut.Same(morning, evening));
        Assert.False(cut.Same(evening, next));
        Assert.False(cut.Same(before, morning));

        // Every instant maps to its day's midnight.
        foreach (DateTimeOffset at in (DateTimeOffset[])[Midnight, Midnight.AddHours(1), Midnight.AddDays(1).AddTicks(-10), Midnight.AddDays(1), Midnight.AddTicks(-10)])
        {
            Assert.True(plan.Partition!.Function!.TryMap(Micros(at), out FilterLiteral day));
            Assert.Equal(Micros(new DateTimeOffset(at.UtcDateTime.Date, TimeSpan.Zero)), day);
        }
    }

    [Fact]
    public void ATextPartitionCutsAtEveryValueWhateverItsLength()
    {
        LayoutPlan plan = Plan(Tenanted.Schema, new DatasetLayout
        {
            Partition = LayoutValue.Column(nameof(Tenanted.Tenant)),
            SortKey = [LayoutValue.Column(nameof(Tenanted.Tenant)), LayoutValue.Column(nameof(Tenanted.Id))],
        });
        SortKey tree = SortKey.Of(plan, partition: true, plan.Components.Count);
        ValueCut cut = ValueCut.Of(plan, tree)!;

        // Values of a block and more, a prefix of another, and the empty one: the bytes of the value end where it ends.
        string longer = new string('t', 70);
        Assert.True(cut.Same(Key(plan, tree, FilterLiteral.From(longer), FilterLiteral.From(1L)), Key(plan, tree, FilterLiteral.From(longer), FilterLiteral.From(9L))));
        Assert.False(cut.Same(Key(plan, tree, FilterLiteral.From(longer), FilterLiteral.From(1L)), Key(plan, tree, FilterLiteral.From(longer + "u"), FilterLiteral.From(1L))));
        Assert.False(cut.Same(Key(plan, tree, FilterLiteral.From("ab"), FilterLiteral.From(1L)), Key(plan, tree, FilterLiteral.From("abc"), FilterLiteral.From(1L))));
        Assert.False(cut.Same(Key(plan, tree, FilterLiteral.From(string.Empty), FilterLiteral.From(1L)), Key(plan, tree, FilterLiteral.From("a"), FilterLiteral.From(1L))));
        Assert.True(cut.Same(Key(plan, tree, FilterLiteral.From(string.Empty), FilterLiteral.From(1L)), Key(plan, tree, FilterLiteral.From(string.Empty), FilterLiteral.From(2L))));
    }

    /// <summary>
    /// A key's values found in one walk, as a granule's prefix runs find them a run at a time: each value's end is where
    /// its prefix ends, and the last the key's own end, for texts of no block, of one, of a block exactly and of several.
    /// </summary>
    [Fact]
    public void TheEndsOfAKeysValuesAreWhereItsPrefixesEnd()
    {
        LayoutPlan plan = Plan(Tenanted.Schema, new DatasetLayout
        {
            Partition = LayoutValue.Column(nameof(Tenanted.Tenant)),
            SortKey = [LayoutValue.Column(nameof(Tenanted.Tenant)), LayoutValue.Column(nameof(Tenanted.Id))],
        });
        SortKey tree = SortKey.Of(plan, partition: true, plan.Components.Count);
        Span<int> ends = stackalloc int[tree.Count];
        foreach (int length in (int[])[0, 1, 31, 32, 33, 64, 70])
        {
            byte[] key = Key(plan, tree, FilterLiteral.From(new string('t', length)), FilterLiteral.From(-(long)length));
            Assert.True(tree.Ends(key, ends));
            for (int i = 0; i < tree.Count; i++)
            {
                Assert.Equal(tree.PrefixLength(key, i + 1), ends[i]);
            }

            Assert.Equal(key.Length, ends[^1]);
            Assert.True(ends[0] < ends[1] && ends[1] - ends[0] == ends[0]);
        }
    }

    [Fact]
    public void ANumberBucketCutsWhereTheScansBucketChanges()
    {
        LayoutPlan plan = Plan(Numbered.Schema, new DatasetLayout
        {
            Partition = LayoutValue.Column(nameof(Numbered.Id)).Bucket(100),
            SortKey = [LayoutValue.Column(nameof(Numbered.Id))],
        });
        SortKey tree = SortKey.Of(plan, partition: true, plan.Components.Count);
        ValueCut cut = ValueCut.Of(plan, tree)!;
        long[] ids = [-201, -200, -101, -100, -1, 0, 1, 99, 100, 199, 200];
        foreach (long left in ids)
        {
            foreach (long right in ids)
            {
                bool same = Math.Floor(left / 100.0) == Math.Floor(right / 100.0);
                Assert.Equal(same, cut.Same(Key(plan, tree, FilterLiteral.From(left)), Key(plan, tree, FilterLiteral.From(right))));
            }
        }

        // A float bucket, on a float column.
        LayoutColumn halves = Plan(Measured.Schema, new DatasetLayout { Partition = LayoutValue.Column(nameof(Measured.Level)).Bucket(0.5) }).Partition!;
        Assert.Equal(Image(halves, FilterLiteral.From(-0.75)), Image(halves, FilterLiteral.From(-0.5000001)));
        Assert.NotEqual(Image(halves, FilterLiteral.From(-0.5)), Image(halves, FilterLiteral.From(-0.5000001)));
        Assert.Equal(Image(halves, FilterLiteral.From(1.0)), Image(halves, FilterLiteral.From(1.49)));
    }

    [Fact]
    public void AnObjectHoldsOnePartitionWhenTheImagesOfItsBoundsAreOne()
    {
        LayoutPlan plan = Plan(Event.Schema, new DatasetLayout
        {
            Partition = LayoutValue.Column(nameof(Event.At)).Truncate(CalendarUnit.Day),
            SortKey = [LayoutValue.Column(nameof(Event.At))],
        });
        ValueCut cut = ValueCut.Of(plan, SortKey.Of(plan, partition: true, plan.Components.Count))!;

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

    private static LayoutPlan Plan(VortexSchema schema, DatasetLayout layout) => LayoutPlan.Of(layout, VortexTypes.ToDType(schema, new DTypeArena()))!;

    private static void Refused(VortexSchema schema, DatasetLayout layout) =>
        Assert.Throws<ArgumentException>(() => LayoutPlan.Of(layout, VortexTypes.ToDType(schema, new DTypeArena())));

    /// <summary>A tree key of the components' values, led by the partition's image of its column, as the tree encodes it.</summary>
    private static byte[] Key(LayoutPlan plan, SortKey tree, params FilterLiteral[] components)
    {
        FilterLiteral[] values = new FilterLiteral[components.Length + 1];
        values[0] = Image(plan.Partition!, components[0]);
        components.CopyTo(values, 1);
        return tree.Encode(values);
    }

    /// <summary>The partition's image of its column's value: the value itself for a column's own.</summary>
    private static FilterLiteral Image(LayoutColumn partition, FilterLiteral value)
    {
        if (partition.Function is not { } function)
        {
            return value;
        }

        Assert.True(function.TryMap(value, out FilterLiteral image));
        return image;
    }

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
