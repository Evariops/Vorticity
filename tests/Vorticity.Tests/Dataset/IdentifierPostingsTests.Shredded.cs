// A key of a variant column declared shredded (DatasetOptions.ShreddedPaths): every object a job writes keeps it in a
// typed column of its own whatever its rows, the dataset's commits keep the declaration, and a declaration that names
// no key read in one class is refused.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class IdentifierPostingsTests
{
    /// <summary>
    /// A key of the attributes one row in five hundred holds: a dataset that declares it shredded as an integer keeps it in
    /// a typed column of every sealed hour, where the same rows without the declaration leave it in the residual; the
    /// declaration survives the dataset's reopening, and a question on the key counts what the rows hold.
    /// </summary>
    [Fact]
    public async Task ADeclaredPathIsShreddedWhateverItsRows()
    {
        foreach (bool declared in new[] { true, false })
        {
            await using MemoryObjectStore store = new MemoryObjectStore();
            List<Visit> rows = [];
            await using (VortexDataset dataset = await VortexDataset.CreateAsync(store, Visit.Schema, new DatasetOptions
            {
                ClusteringKey = [nameof(Visit.At)],
                ShreddedPaths = declared ? ["Attributes['rare'].AsInt64()"] : null,
                Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour) }, ContentsObjectBytes = 0, SliceTargetRows = 500,
            }, Ct))
            {
                for (int part = 0; part < 16; part++)
                {
                    Visit[] visits = [.. Enumerable.Range(0, 400).Select(i => new Visit(
                        Noon.AddSeconds((part * 400 + i) * 1.25),
                        (part * 400 + i) % 500 == 0
                            ? Variant.Object(("route", Variant.From($"/r/{i % 7}")), ("rare", Variant.From((long)i)))
                            : Variant.Object(("route", Variant.From($"/r/{i % 7}")))))];
                    await using ObjectDraft draft = dataset.StartObject();
                    await draft.Writer.WriteAsync<Visit>(visits, Ct);
                    await dataset.AppendAsync(draft, Ct);
                    rows.AddRange(visits);
                }

                while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
                {
                }

                int field = Visit.Schema.IndexOf(nameof(Visit.Attributes));
                int hours = 0;
                await foreach (TreeEntry leaf in dataset.AboveLevelZeroAsync(Ct))
                {
                    VariantDirectory directory = (await dataset.ReadPathDirectoryAsync(ObjectEntry.FromBytes(leaf.Value.Span), field, Ct))!;
                    PathEntry rare = Assert.Single(directory.Of(["rare"]).ToArray(), entry => entry.Class == PathClass.Integer);
                    Assert.Equal(declared ? PathPlace.Typed : PathPlace.Residual, rare.Place);
                    hours++;
                }

                Assert.Equal(3, hours);
                Assert.Equal(rows.Count(r => r.Attributes["rare"] is not null), await dataset.Scan<Visit>().Where(r => r.Attributes["rare"].AsInt64() >= 0).CountAsync(Ct));
            }

            await using VortexDataset reopened = await VortexDataset.OpenAsync(store, new DatasetOptions(), Ct);
            Assert.Equal(declared ? ["Attributes['rare'].AsInt64()"] : [], reopened.Snapshot.Schema.ShreddedPaths);
        }
    }

    /// <summary>
    /// Appends that shred the declared path, as a job does: a question on the key over the objects of level 0 reads each
    /// by the key as it lies, with no copy shredded in memory, and so does the next one, where appends that shred nothing
    /// have each object copied once; both read what the rows hold.
    /// </summary>
    [Fact]
    public async Task AnAppendThatShredsTheDeclaredPathsIsReadByThemAsItLies()
    {
        foreach (bool onAppend in new[] { true, false })
        {
            await using MemoryObjectStore store = new MemoryObjectStore();
            await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Visit.Schema, new DatasetOptions
            {
                ClusteringKey = [nameof(Visit.At)],
                ShreddedPaths = ["Attributes['status'].AsInt64()"],
                ShredOnAppend = onAppend,
            }, Ct);
            List<Visit> rows = [];
            for (int part = 0; part < 3; part++)
            {
                Visit[] visits = [.. Enumerable.Range(0, 1_000).Select(i => new Visit(
                    Noon.AddSeconds((part * 1_000) + i),
                    Variant.Object(("route", Variant.From($"/r/{i % 7}")), ("status", Variant.From((long)(200 + (100 * (i % 4))))))))];
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Visit>(visits, Ct);
                await dataset.AppendAsync(draft, Ct);
                rows.AddRange(visits);
            }

            for (int asked = 1; asked <= 2; asked++)
            {
                Assert.Equal(
                    rows.Where(r => r.Attributes["status"]?.AsInt64() >= 400).Select(r => r.Attributes["status"]?.AsInt64()).Order(),
                    (await dataset.Scan<Visit>().Where(r => r.Attributes["status"].AsInt64() >= 400).Select(r => r.Attributes["status"].AsInt64()).ToListAsync(Ct)).Order());
                Assert.Equal(onAppend ? 3 * asked : 0, dataset.LevelZero.AsAppended);
                Assert.Equal(onAppend ? 0 : 3, dataset.LevelZero.Shredded);
            }

            // A count reads the key alone too, through the same leases.
            Assert.Equal(
                rows.Count(r => r.Attributes["status"]?.AsInt64() >= 400),
                await dataset.Scan<Visit>().Where(r => r.Attributes["status"].AsInt64() >= 400).CountAsync(Ct));
            Assert.Equal(onAppend ? 9 : 0, dataset.LevelZero.AsAppended);
            Assert.Equal(onAppend ? 0 : 3, dataset.LevelZero.Shredded);
        }
    }

    /// <summary>
    /// The key one row in five hundred holds, declared shredded once the hour of noon is compacted and taken back an hour
    /// later: the objects written while it is declared keep it in a typed column, every other in the residual, each job
    /// shredding the paths of the version it starts from on a handle opened before any change; a question on the key
    /// counts what the rows hold over them all. A change worked out against paths another writer changed first is
    /// refused, one that asks for the paths the dataset has is done, and a path that names no such key is refused.
    /// </summary>
    [Fact]
    public async Task ShreddedPathsChangedLaterAreShreddedByTheJobsThatFollow()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = new DatasetOptions
        {
            ClusteringKey = [nameof(Visit.At)],
            Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour) }, ContentsObjectBytes = 0, SliceTargetRows = 500,
        };
        await using VortexDataset writer = await VortexDataset.CreateAsync(store, Visit.Schema, options, Ct);
        await using VortexDataset compactor = await VortexDataset.OpenAsync(store, options, Ct);
        await using VortexDataset stale = await VortexDataset.OpenAsync(store, options, Ct);
        const string Rare = "Attributes['rare'].AsInt64()";
        List<Visit> rows = [];

        // Nine parts of 400 visits a second and a quarter apart take the rows to a quarter of an hour past the hour that
        // follows; the rows after that come as objects of their own, beside those of their hour.
        async Task AppendAsync(int from, int to)
        {
            for (int part = from; part < to; part++)
            {
                Visit[] visits = [.. Enumerable.Range(0, 400).Select(i => new Visit(
                    Noon.AddSeconds((part * 400 + i) * 1.25),
                    (part * 400 + i) % 500 == 0
                        ? Variant.Object(("route", Variant.From($"/r/{i % 7}")), ("rare", Variant.From((long)i)))
                        : Variant.Object(("route", Variant.From($"/r/{i % 7}")))))];
                await using ObjectDraft draft = writer.StartObject();
                await draft.Writer.WriteAsync<Visit>(visits, Ct);
                await writer.AppendAsync(draft, Ct);
                rows.AddRange(visits);
            }

            await compactor.RefreshAsync(Ct);
            while (await compactor.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
            {
            }
        }

        await AppendAsync(0, 9);
        ulong declared = await writer.SetShreddedPathsAsync([Rare], Ct);
        Assert.Equal([Rare], writer.Snapshot.Header.ShreddedPaths);
        Assert.Equal(DatasetFeatures.ShreddedPaths, writer.Snapshot.Header.Features & DatasetFeatures.ShreddedPaths);
        Assert.Equal(declared, await writer.SetShreddedPathsAsync([Rare], Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await stale.SetShreddedPathsAsync(["Attributes['route'].AsString()"], Ct));
        Assert.Equal(declared, await stale.SetShreddedPathsAsync([Rare], Ct));
        ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(async () => await writer.SetShreddedPathsAsync([nameof(Visit.At)], Ct));
        Assert.Equal("paths", refused.ParamName);

        HashSet<string> undeclared = await KeysAsync();
        await AppendAsync(9, 18);
        HashSet<string> declaredSince = await KeysAsync();
        declaredSince.ExceptWith(undeclared);
        await writer.SetShreddedPathsAsync([], Ct);
        Assert.Empty(writer.Snapshot.Header.ShreddedPaths);
        await AppendAsync(18, 27);

        // Each object keeps the key where the paths of the version its job started from put it.
        int field = Visit.Schema.IndexOf(nameof(Visit.Attributes));
        List<(PathPlace Place, PathPlace Expected)> places = [];
        await foreach (TreeEntry leaf in compactor.AboveLevelZeroAsync(Ct))
        {
            ObjectEntry entry = ObjectEntry.FromBytes(leaf.Value.Span);
            VariantDirectory directory = (await compactor.ReadPathDirectoryAsync(entry, field, Ct))!;
            if (directory.Of(["rare"]).ToArray() is { Length: > 0 } rare)
            {
                places.Add((Assert.Single(rare, held => held.Class == PathClass.Integer).Place, declaredSince.Contains(entry.Key) ? PathPlace.Typed : PathPlace.Residual));
            }
        }

        Assert.Contains(places, place => place.Expected == PathPlace.Typed);
        Assert.Contains(places, place => place.Expected == PathPlace.Residual);
        Assert.All(places, place => Assert.Equal(place.Expected, place.Place));
        await writer.RefreshAsync(Ct);
        Assert.Equal(rows.Count(r => r.Attributes["rare"] is not null), await writer.Scan<Visit>().Where(r => r.Attributes["rare"].AsInt64() >= 0).CountAsync(Ct));

        // The keys of the objects above level 0 the compactor's handle holds.
        async Task<HashSet<string>> KeysAsync()
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            await foreach (TreeEntry leaf in compactor.AboveLevelZeroAsync(Ct))
            {
                keys.Add(ObjectEntry.FromBytes(leaf.Value.Span).Key);
            }

            return keys;
        }
    }

    [Theory]
    [InlineData("Attributes['rare']")]
    [InlineData("Attributes['rare']['deep'].AsInt64()")]
    [InlineData("Attributes['rare'].AsBinary()")]
    [InlineData("Nope['rare'].AsInt64()")]
    [InlineData("At")]
    public async Task AShreddedPathNamesAKeyReadInOneClass(string path)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(async () => await VortexDataset.CreateAsync(store, Visit.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Visit.At)],
            ShreddedPaths = [path],
        }, Ct));
        Assert.Contains("Attributes['status'].AsInt64()", refused.Message, StringComparison.Ordinal);
    }
}
