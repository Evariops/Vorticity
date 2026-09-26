// FieldMask, the tree the scan builds and the layouts consume.
using System;

using Vorticity.Layouts;

using Xunit;

namespace Vorticity.Tests.Layouts;

public sealed class FieldMaskTests
{
    [Fact]
    public void DefaultIsAll()
    {
        FieldMask mask = default;
        Assert.True(mask.IsAll);
        Assert.False(mask.IsEmpty);
        Assert.True(mask.Includes(0));
        Assert.True(mask.Includes(4096));
        Assert.True(mask.Descend(7).IsAll);
    }

    [Fact]
    public void EmptyIncludesNothing()
    {
        FieldMask mask = FieldMask.Empty;
        Assert.False(mask.IsAll);
        Assert.True(mask.IsEmpty);
        Assert.False(mask.Includes(0));
        Assert.True(mask.Descend(0).IsEmpty);
    }

    [Fact]
    public void AnEmptyBuilderProducesTheEmptyMask()
    {
        FieldMask mask = new FieldMaskBuilder().Build();
        Assert.True(mask.IsEmpty);
        Assert.False(mask.IsAll);
    }

    [Fact]
    public void AnEmptyPathSelectsEverything()
    {
        FieldMask mask = new FieldMaskBuilder().Include([]).Build();
        Assert.True(mask.IsAll);
    }

    [Fact]
    public void NamedFieldsAreSortedAndDistinct()
    {
        FieldMask mask = new FieldMaskBuilder()
            .IncludeField(5)
            .IncludeField(1)
            .IncludeField(5)
            .IncludeField(3)
            .Build();

        Assert.Equal(3, mask.NamedFieldCount);
        Assert.Equal(1, mask.GetNamedField(0));
        Assert.Equal(3, mask.GetNamedField(1));
        Assert.Equal(5, mask.GetNamedField(2));

        Assert.True(mask.Includes(1));
        Assert.True(mask.Includes(3));
        Assert.True(mask.Includes(5));
        Assert.False(mask.Includes(0));
        Assert.False(mask.Includes(2));
        Assert.False(mask.Includes(4));
        Assert.False(mask.Includes(6));
    }

    [Fact]
    public void NestedPathsSelectTheirAncestorsWithoutSelectingTheirSiblings()
    {
        // Project "1.2.0": field 1 is wanted, but only its field 2, and only that field's field 0.
        FieldMask mask = new FieldMaskBuilder().Include([1, 2, 0]).Build();

        Assert.True(mask.Includes(1));
        Assert.False(mask.Includes(0));
        Assert.False(mask.Includes(2));

        FieldMask level1 = mask.Descend(1);
        Assert.False(level1.IsAll);
        Assert.True(level1.Includes(2));
        Assert.False(level1.Includes(1));

        FieldMask level2 = level1.Descend(2);
        Assert.True(level2.Includes(0));
        Assert.True(level2.Descend(0).IsAll);
        Assert.True(level2.Descend(1).IsEmpty);
    }

    [Fact]
    public void AWholeFieldSwallowsANarrowerPathBelowIt()
    {
        FieldMask wideFirst = new FieldMaskBuilder().IncludeField(1).Include([1, 2]).Build();
        Assert.True(wideFirst.Descend(1).IsAll);

        FieldMask narrowFirst = new FieldMaskBuilder().Include([1, 2]).IncludeField(1).Build();
        Assert.True(narrowFirst.Descend(1).IsAll);
    }

    [Fact]
    public void ANegativePathElementIsACallerError()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FieldMaskBuilder().IncludeField(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FieldMaskBuilder().Include([0, -2]));
    }

    [Fact]
    public void OnlyASubsetMaskNamesFields()
    {
        Assert.Equal(-1, FieldMask.All.NamedFieldCount);
        Assert.Equal(0, FieldMask.Empty.NamedFieldCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => FieldMask.All.GetNamedField(0));

        FieldMask mask = new FieldMaskBuilder().IncludeField(0).Build();
        Assert.Throws<ArgumentOutOfRangeException>(() => mask.GetNamedField(1));
    }

    [Fact]
    public void OneWholeFieldReadsAsASubsetOfOne()
    {
        FieldMask built = new FieldMaskBuilder().IncludeField(4).Build();

        long before = GC.GetAllocatedBytesForCurrentThread();
        FieldMask single = FieldMask.Single(4);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        foreach (FieldMask mask in new[] { built, single })
        {
            Assert.False(mask.IsAll);
            Assert.False(mask.IsEmpty);
            Assert.True(mask.Includes(4));
            Assert.False(mask.Includes(3));
            Assert.False(mask.Includes(5));
            Assert.True(mask.Descend(4).IsAll);
            Assert.True(mask.Descend(0).IsEmpty);
            Assert.Equal(1, mask.NamedFieldCount);
            Assert.Equal(4, mask.GetNamedField(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => mask.GetNamedField(1));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => FieldMask.Single(-1));
    }

    [Fact]
    public void FieldsWantedWholeReadWholeAndTheOthersKeepTheirPaths()
    {
        FieldMask wholes = new FieldMaskBuilder().IncludeField(1).IncludeField(3).Build();
        Assert.True(wholes.Descend(1).IsAll);
        Assert.True(wholes.Descend(3).IsAll);
        Assert.True(wholes.Descend(2).IsEmpty);

        FieldMask mixed = new FieldMaskBuilder().IncludeField(1).Include([3, 2]).Build();
        Assert.True(mixed.Descend(1).IsAll);
        Assert.False(mixed.Descend(3).IsAll);
        Assert.True(mixed.Descend(3).Includes(2));
        Assert.False(mixed.Descend(3).Includes(0));
    }

    /// <summary>
    /// The fields a mask selects, read by position, are the ones a walk over every field of the
    /// struct keeps, with the same masks below them, whether the mask names fields past the
    /// struct's width or not.
    /// </summary>
    [Fact]
    public void TheSelectedFieldsByPositionAreThoseAWalkOverEveryFieldKeeps()
    {
        Random random = new Random(9);
        for (int trial = 0; trial < 500; trial++)
        {
            FieldMask mask = RandomMask(random);
            int fieldCount = random.Next(0, 48);
            int selected = mask.SelectedCount(fieldCount);
            int position = 0;
            for (int field = 0; field < fieldCount; field++)
            {
                if (!mask.Includes(field))
                {
                    continue;
                }

                Assert.True(position < selected, $"{Describe(mask)} over {fieldCount} fields selects {selected}.");
                Assert.Equal(field, mask.SelectedField(position));
                Assert.Equal(Describe(mask.Descend(field)), Describe(mask.SelectedMask(position)));
                position++;
            }

            Assert.Equal(position, selected);
        }
    }

    private static FieldMask RandomMask(Random random)
    {
        switch (random.Next(8))
        {
            case 0:
                return FieldMask.All;
            case 1:
                return FieldMask.Empty;
            case 2:
                return FieldMask.Single(random.Next(51));
        }

        FieldMaskBuilder builder = new FieldMaskBuilder();
        int paths = random.Next(1, 11);
        for (int i = 0; i < paths; i++)
        {
            int[] path = new int[random.Next(1, 4)];
            for (int level = 0; level < path.Length; level++)
            {
                path[level] = random.Next(51);
            }

            builder.Include(path);
        }

        return builder.Build();
    }

    /// <summary>A mask's shape, as far down as it narrows.</summary>
    private static string Describe(FieldMask mask)
    {
        if (mask.IsAll)
        {
            return "*";
        }

        int count = mask.NamedFieldCount;
        string[] fields = new string[count];
        for (int i = 0; i < count; i++)
        {
            int field = mask.GetNamedField(i);
            fields[i] = $"{field}:{Describe(mask.Descend(field))}";
        }

        return "(" + string.Join(",", fields) + ")";
    }
}
