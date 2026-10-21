using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SynopsisContentsTests
{
    /// <summary>
    /// The tables of a sealed hour of calls, of which a question reads a few: the contents check every table's chunks and
    /// parse none, a table's the first time one is asked for, as the contents hold them; the regions the contents declare
    /// count every table's, and find a table's once it is parsed, the only regions a read through the cache asks for, as
    /// a question learns them from the chunks.
    /// </summary>
    [Fact]
    public async Task ATableParsesItsChunksTheFirstTimeOneIsAskedFor()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Call.At)],
            Periods = new PeriodOptions { Lateness = System.TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 500 },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(1_000) },
        }, Ct);
        System.Random random = new System.Random(5);
        for (int part = 0; part < 16; part++)
        {
            Call[] rows = [.. Enumerable.Range(0, 400).Select(i => new Call(
                Noon.AddSeconds((part * 400 + i) * 1.25), $"region-{random.Next(3)}", 200 + (random.Next(5) * 100), random.NextInt64(1_000_000)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Call>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        ObjectEntry hour = default!;
        await foreach (TreeEntry leaf in dataset.Levels[PeriodLevels.Periods].EnumerateAsync(dataset.Pages, Ct))
        {
            hour = ObjectEntry.FromBytes(leaf.Value.Span);
            break;
        }

        // Every table's chunks in the bytes, their count known; the regions counted, none parsed.
        SynopsisContents contents = SynopsisContents.From(hour.Synopsis.Span)!;
        Assert.True(contents.Tables.Count > 1);
        Assert.All(contents.Tables, t => Assert.Null(Assert.IsType<SynopsisContents.TableChunks>(t.Chunks).Parsed));
        List<IndexSegment> declared = SynopsisCacheReader.Declared(SynopsisContents.From(hour.Synopsis.Span)!);
        IReadOnlyDictionary<(ulong Offset, uint Length), IndexSegment> regions = contents.DeclaredRegions;
        Assert.Equal(declared.Select(r => (r.Offset, r.Length)).Distinct().Count(), regions.Count);
        Assert.All(contents.Tables, t => Assert.True(t.Chunks.Count > 0));
        Assert.All(contents.Tables, t => Assert.Null(((SynopsisContents.TableChunks)t.Chunks).Parsed));

        // One table asked for: its chunks parsed, no other's, and its regions found.
        IReadOnlyList<TableChunk> asked = contents.Tables[1].Chunks;
        TableChunk first = asked[0];
        Assert.NotNull(((SynopsisContents.TableChunks)asked).Parsed);
        Assert.All(contents.Tables.Where((_, t) => t != 1), t => Assert.Null(((SynopsisContents.TableChunks)t.Chunks).Parsed));
        Assert.True(regions.TryGetValue((first.Region.Offset, first.Region.Length), out IndexSegment found));
        Assert.Equal(first.Region, found);

        // The chunks parsed are those the bytes hold: the contents of their arrays write the same bytes. The bounds of a
        // table of values parse through the same store, which the round trip of the contents reads back.
        SynopsisContents held = contents with { Tables = [.. contents.Tables.Select(t => (t.Table, (IReadOnlyList<TableChunk>)[.. t.Chunks]))] };
        Assert.Equal(hour.Synopsis.ToArray(), held.ToBytes());
    }
}
