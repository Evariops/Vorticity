using System;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;

using Xunit;

namespace Vorticity.Tests.Layouts;

/// <summary>
/// What a flat layout tells the read of how much of its segment the decode touches: all of it for
/// a scan, the fields a projection keeps of a struct stored whole, and a page or two a row for each
/// field a take wants, the rows counted over the node at the share the split wants. Under a
/// quarter, the segment touches little.
/// </summary>
public sealed class TouchedBytesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AScanOfEveryFieldTouchesTheWholeSegment()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path("encodings/table_wide"), Ct);

        Assert.False(TouchesLittle(file, FieldMask.All, new RowRange(0, file.RowCount), take: null));
    }

    [Fact]
    public async Task AProjectionOfOneFieldOfFiftyTouchesLittle()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path("encodings/table_wide"), Ct);

        Assert.True(TouchesLittle(file, FieldMask.Single(7), new RowRange(0, file.RowCount), take: null));
    }

    [Fact]
    public async Task ATakeCountsItsRowsOverTheNodeTheSegmentIsReadForOnce()
    {
        // Six fields of 4,096 rows in 354 KB: a row the take wants of the whole node is 48 KiB
        // touched, under a quarter; a row of a quarter of it stands for four, 192 KiB, which is not.
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path("encodings/table_mixed"), Ct);
        Assert.Equal(4_096, file.RowCount);
        RowSelection take = RowSelection.Create([5], file.RowCount);

        Assert.True(TouchesLittle(file, FieldMask.All, new RowRange(0, 4_096), take));
        Assert.False(TouchesLittle(file, FieldMask.All, new RowRange(0, 1_024), take));
    }

    /// <summary>Registers a split of the file's root and reads back what its one segment touches.</summary>
    private static bool TouchesLittle(VortexFile file, FieldMask mask, RowRange split, RowSelection? take)
    {
        using ScanContext context = new ScanContext(file);
        SplitExecution.Register(context, file.LayoutTree, in mask, split, take);
        SegmentRequestSet segments = context.Segments;

        Assert.Equal(Half.One, segments.WantedShare);
        Assert.Equal(1, segments.Count);
        return segments.TouchesLittle(0);
    }
}
