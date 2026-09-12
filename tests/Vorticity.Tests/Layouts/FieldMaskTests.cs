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
}
