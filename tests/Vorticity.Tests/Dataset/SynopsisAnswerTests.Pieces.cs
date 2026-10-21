// The pieces of a pass on several lanes over an object read as rows share the slices its window reaches, as the object's
// scan reads them, rather than every row of the object.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SynopsisAnswerTests
{
    /// <summary>
    /// Ten minutes of an hour's object, some 1 200 of its 7 200 rows: the lanes' pieces lie in the slices the window
    /// reaches, several of them, where the cut of the object's every row left the window to the one or two pieces that
    /// hold it; and the groups of the window are those one lane reads.
    /// </summary>
    [Fact]
    public async Task ThePiecesOfAWindowReadAsRowsLieInTheSlicesItReaches()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Request.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 100 },
            Write = new VortexWriteOptions { BlockRows = 128, ChunkTargetBytes = 1 << 10 },
            LeastPieceRows = 64,
        }, Ct);
        Random random = new Random(71);
        for (int part = 0; part < 8; part++)
        {
            Request[] rows = [.. Enumerable.Range(0, 1_000).Select(i => Drawn(random, Noon.AddSeconds(((part * 1_000) + i) * 0.5)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Request>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        // The window: ten slices of an hour's object, from its twentieth.
        PositionedObject? hour = null;
        await foreach (PositionedObject held in dataset.Snapshot.WalkAsync(null, 0, long.MaxValue, null, Ct))
        {
            hour ??= held.Entry.Contents is { Slices.Slices.Count: > 40 } ? held : null;
        }

        SynopsisContents contents = hour!.Value.Entry.Contents!;
        IReadOnlyList<TimeSlice> slices = contents.Slices.Slices;
        long firstRow = hour.Value.FirstRow;
        VortexExpr window = Expr.And(
            Expr.Ge(Expr.Field(nameof(Request.At)), Expr.Literal(FilterLiteral.From(slices[20].Start))),
            Expr.Lt(Expr.Field(nameof(Request.At)), Expr.Literal(FilterLiteral.From(slices[29].End))));
        RowRange reached = new RowRange(firstRow + slices[20].FirstRow, firstRow + slices[29].FirstRow + slices[29].Rows);

        DatasetScanSource source = new DatasetScanSource(dataset, dataset.Snapshot);
        RowRange[] pieces = Assert.IsType<RowRange[]>(await source.PiecesAsync(new ScanSpec { Filter = window }, 4, Ct));
        Assert.True(pieces.Length > 2, $"{pieces.Length} pieces over {reached.Length} rows");
        Assert.All(pieces, piece => Assert.True(piece.Start >= reached.Start && piece.End <= reached.End, $"{piece} outside {reached}"));
        Assert.Equal(reached.Start, pieces[0].Start);
        Assert.Equal(reached.End, pieces[^1].End);

        // The same groups at four lanes as at one.
        DateTimeOffset from = TimeSlicer.TimeOf(slices[20].Start, contents.Slices.Unit);
        DateTimeOffset to = TimeSlicer.TimeOf(slices[29].End, contents.Slices.Unit);
        List<string>[] groups = new List<string>[2];
        for (int d = 0; d < 2; d++)
        {
            (groups[d], _) = await GroupsAsync(dataset.Scan<Request>().With(new ScanOptions { UseSynopses = false, DegreeOfParallelism = d == 0 ? 1 : 4 })
                .Where(r => r.At >= from && r.At < to).GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))));
        }

        Assert.NotEmpty(groups[0]);
        Assert.Equal(groups[0], groups[1]);
    }
}
