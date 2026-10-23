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
    /// The tables of a sealed hour of calls, listed apart as a day's large tables are, of which a question reads a few: the
    /// entry names each table's list of chunks, a region of the object, and holds none of its chunks; a list loaded is
    /// checked and parsed the first time a chunk is asked for; the regions the contents declare count every table's list
    /// and chunks, and find a table's chunks once they are parsed, the only regions a read through the cache asks for, as
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
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(1_000), InlineListBytes = 0 },
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

        // Every table's list named, its chunks' count known, none in the entry.
        SynopsisContents contents = SynopsisContents.From(hour.Synopsis.Span)!;
        Assert.True(contents.Tables.Count > 1);
        Assert.All(contents.Tables, t => Assert.False(Assert.IsType<SynopsisContents.TableChunks>(t.Chunks).Loaded));
        Assert.All(contents.Tables, t => Assert.True(t.Chunks.Count > 0));
        Assert.Throws<System.InvalidOperationException>(() => contents.Tables[0].Chunks[0]);

        // The lists loaded, a round trip: checked, none parsed; the regions counted, lists and chunks, by a walk of contents
        // of their own, their lists loaded too, which parses every table.
        await dataset.LoadSynopsisListsAsync([(hour, contents, new FieldsRead(_ => []))], Ct);
        Assert.All(contents.Tables, t => Assert.True(((SynopsisContents.TableChunks)t.Chunks).Loaded));
        Assert.All(contents.Tables, t => Assert.Null(((SynopsisContents.TableChunks)t.Chunks).Parsed));
        SynopsisContents walked = SynopsisContents.From(hour.Synopsis.Span)!;
        await dataset.LoadSynopsisListsAsync([(hour, walked, null)], Ct);
        List<IndexSegment> declared = walked.Declared();
        IReadOnlyDictionary<(ulong Offset, uint Length), IndexSegment> regions = contents.DeclaredRegions;
        Assert.Equal(declared.Select(r => (r.Offset, r.Length)).Distinct().Count(), regions.Count);
        Assert.All(contents.Tables, t => Assert.Null(((SynopsisContents.TableChunks)t.Chunks).Parsed));

        // One table asked for: its chunks parsed, no other's, and its regions found.
        IReadOnlyList<TableChunk> asked = contents.Tables[1].Chunks;
        TableChunk first = asked[0];
        Assert.NotNull(((SynopsisContents.TableChunks)asked).Parsed);
        Assert.All(contents.Tables.Where((_, t) => t != 1), t => Assert.Null(((SynopsisContents.TableChunks)t.Chunks).Parsed));
        Assert.True(regions.TryGetValue((first.Region.Offset, first.Region.Length), out IndexSegment found));
        Assert.Equal(first.Region, found);

        // The chunks parsed are those the lists hold: written in an entry of their own, which outweighs the one that names
        // their lists, they read back the same. The bounds of a table of values parse through the same store, which the
        // round trip of the contents reads back.
        SynopsisContents held = new SynopsisContents(
            contents.Schema, contents.Encodings, contents.Slices, contents.ChunkDirectories, contents.PartStart, contents.PartEnd,
            [.. contents.Tables.Select(t => (t.Table, (IReadOnlyList<TableChunk>)[.. t.Chunks]))], contents.PathDirectories)
        {
            DirectoryRegion = contents.DirectoryRegion,
            Locating = contents.Locating,
            Paging = contents.Paging,
        };
        byte[] inline = held.ToBytes();
        Assert.True(hour.Synopsis.Length < inline.Length, $"an entry of {hour.Synopsis.Length} B, {inline.Length} B with its chunks");
        SynopsisContents back = SynopsisContents.From(inline)!;
        Assert.Equal(held.Tables.SelectMany(t => t.Chunks).Select(Bounds), back.Tables.SelectMany(t => t.Chunks).Select(Bounds));
        Assert.Equal(held.Tables.SelectMany(t => t.Chunks).Select(Unbounded), back.Tables.SelectMany(t => t.Chunks).Select(Unbounded));
    }
}
