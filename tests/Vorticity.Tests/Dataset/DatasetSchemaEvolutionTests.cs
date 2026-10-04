using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Tests.Scan;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

/// <summary>A meter's first schema.</summary>
[VortexRecord]
public partial record struct MeterV1(long Key, int Reading, string Site);

/// <summary>The second: the reading widened, the site renamed and nullable, a temperature added.</summary>
[VortexRecord]
public partial record struct MeterV2(long Key, long Reading, string? Place, double? Temperature);

/// <summary>The third: the place dropped.</summary>
[VortexRecord]
public partial record struct MeterV3(long Key, long Reading, double? Temperature);

/// <summary>A file holding a column the dataset never had.</summary>
[VortexRecord]
public partial record struct MeterExtra(long Key, int Reading, string Site, int Extra);

/// <summary>Numbers of every kind that widens, nullable and not.</summary>
[VortexRecord]
public partial record struct NarrowNumbers(long Key, sbyte? Small, ushort Count, float? Ratio, short Level);

/// <summary>The same numbers, widened within their kinds.</summary>
[VortexRecord]
public partial record struct WideNumbers(long Key, long? Small, ulong Count, double? Ratio, int? Level);

/// <summary>A nested record, which a later schema adds as a column.</summary>
[VortexRecord]
public partial record struct MeterDetail(int Floor, string? Room);

/// <summary>The first schema with a nested record added.</summary>
[VortexRecord]
public partial record struct MeterNested(long Key, int Reading, string Site, MeterDetail? Detail);

/// <summary>
/// A dataset's schema changes without an object being rewritten: the objects written before read as
/// the new schema through every read the dataset has -- rows, counts, extremes, key order, key
/// cursors, deletes and updates -- until compaction writes them in it. The oracle is the rows as they
/// were written, converted in plain C#, and filtered there under three-valued logic.
/// </summary>
public sealed class DatasetSchemaEvolutionTests
{
    private static readonly string[] Sites = ["Paris", "Lyon", "Nice", "Lille"];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ObjectsWrittenBeforeAChangeReadAsTheNewSchema(bool clustered)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        (VortexDataset dataset, List<MeterV2> expected) = await EvolvedAsync(store, clustered, ct);
        await using (dataset)
        {
            Assert.Equal(["Key", "Reading", "Place", "Temperature"], dataset.Schema.Select(field => field.Name));
            Assert.Equal(Sorted(expected), Sorted(await RowsAsync<MeterV2>(dataset, ct)));

            // Counts, over renamed, widened and added columns, and over a mix of old and new objects.
            await AssertCountAsync(dataset, expected, r => r.Temperature > 5.0, row => row.Temperature > 5.0, ct);
            await AssertCountAsync(dataset, expected, r => r.Temperature.IsNull, row => row.Temperature is null, ct);
            await AssertCountAsync(dataset, expected, r => r.Place == "Lyon", row => row.Place == "Lyon", ct);
            await AssertCountAsync(dataset, expected, r => r.Reading >= 500L, row => row.Reading >= 500, ct);
            await AssertCountAsync(dataset, expected, r => r.Reading > 3_000_000_000L, row => row.Reading > 3_000_000_000L, ct);
            await AssertCountAsync(dataset, expected, r => r.Temperature.IsNull | r.Reading > 100L, row => row.Temperature is null || row.Reading > 100, ct);

            // A negation over a column an old object lacks is unknown there, as a null is: those
            // rows are not selected, and neither side of a disjunction under it is lost.
            await AssertCountAsync(dataset, expected, r => !(r.Temperature > 5.0), row => Not(Gt(row.Temperature, 5.0)) == true, ct);
            await AssertCountAsync(
                dataset,
                expected,
                r => !(r.Temperature > 5.0 | r.Reading > 100L),
                row => Not(Or(Gt(row.Temperature, 5.0), row.Reading > 100)) == true,
                ct);
            await AssertCountAsync(
                dataset,
                expected,
                r => !(r.Temperature > 5.0) | r.Place == "Nice",
                row => Or(Not(Gt(row.Temperature, 5.0)), row.Place == "Nice") == true,
                ct);

            // The rows themselves, under a filter the old objects answer from their own columns and
            // under one they answer on the reshaped batches.
            Assert.Equal(
                Sorted(expected.Where(row => row.Place == "Nice" && row.Reading < 700)),
                Sorted(await dataset.Scan<MeterV2>().Where(r => r.Place == "Nice" & r.Reading < 700L).ToRecordsAsync(ct).ToListAsync(ct)));
            Assert.Equal(
                Sorted(expected.Where(row => Not(Or(Gt(row.Temperature, 5.0), row.Reading > 100)) == true)),
                Sorted(await dataset.Scan<MeterV2>().Where(r => !(r.Temperature > 5.0 | r.Reading > 100L)).ToRecordsAsync(ct).ToListAsync(ct)));

            // Extremes, of a widened, a renamed and an added column, filtered or not.
            Assert.Equal(expected.Max(row => row.Reading), await dataset.Scan<MeterV2>().MaxAsync(r => r.Reading, ct));
            Assert.Equal(expected.Min(row => row.Temperature), await dataset.Scan<MeterV2>().MinAsync(r => r.Temperature, ct));
            Assert.Equal(
                expected.Where(row => row.Reading < 300 && row.Place is not null).Select(row => row.Place).Min(StringComparer.Ordinal),
                await dataset.Scan<MeterV2>().Where(r => r.Reading < 300L).MinAsync(r => r.Place, ct));
            Assert.Equal(
                expected.Where(row => Not(Gt(row.Temperature, 5.0)) == true).Max(row => row.Reading),
                await dataset.Scan<MeterV2>().Where(r => !(r.Temperature > 5.0)).MaxAsync(r => r.Reading, ct));

            if (clustered)
            {
                // Key order, up and down, and the key cursor both ways, across both schemas.
                List<long> keys = [.. expected.Select(row => row.Key).Order()];
                Assert.Equal(keys, (await dataset.Scan<MeterV2>().OrderBy(r => r.Key).ToRecordsAsync(ct).ToListAsync(ct)).Select(row => row.Key));
                Assert.Equal(
                    [.. keys.AsEnumerable().Reverse()],
                    (await dataset.Scan<MeterV2>().OrderByDescending(r => r.Key).ToRecordsAsync(ct).ToListAsync(ct)).Select(row => row.Key));
                Assert.Equal(
                    expected.Where(row => row.Temperature is null).Select(row => row.Key).Order(),
                    (await dataset.Scan<MeterV2>().Where(r => r.Temperature.IsNull).OrderBy(r => r.Key).ToRecordsAsync(ct).ToListAsync(ct)).Select(row => row.Key));
                await using DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(dataset, ct);
                List<long> up = [];
                for (bool ok = await cursor.SeekFirstAsync(ct); ok; ok = await cursor.NextAsync(ct))
                {
                    up.Add(cursor.Key.SignedValue);
                }

                List<long> down = [];
                for (bool ok = await cursor.SeekLastAsync(ct); ok; ok = await cursor.PrevAsync(ct))
                {
                    down.Add(cursor.Key.SignedValue);
                }

                down.Reverse();
                Assert.Equal(keys, up);
                Assert.Equal(keys, down);
            }

            Assert.True((await dataset.VerifyAsync(cancellationToken: ct)).Holds);
        }
    }

    [Fact]
    public async Task CompactionWritesTheObjectsItTakesInTheCurrentSchema()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        (VortexDataset dataset, List<MeterV2> expected) = await EvolvedAsync(store, clustered: true, ct);
        await using (dataset)
        {
            Assert.True(await EarlierObjectsAsync(dataset, ct) > 0);
            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1, TargetBytesAtLevelOne = 4 << 10 }, ct) is not null)
            {
            }

            Assert.Equal(0, await EarlierObjectsAsync(dataset, ct));
            Assert.Equal(Sorted(expected), Sorted(await RowsAsync<MeterV2>(dataset, ct)));
            Assert.True((await dataset.VerifyAsync(cancellationToken: ct)).Holds);
        }
    }

    [Fact]
    public async Task DeletesAndUpdatesTakeRowsFromObjectsOfEitherSchema()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        (VortexDataset dataset, List<MeterV2> expected) = await EvolvedAsync(store, clustered: true, ct);
        await using (dataset)
        {
            // By the renamed column, which the old objects hold under its old name.
            RowChangeResult deleted = await dataset.DeleteAsync<MeterV2>(r => r.Place == "Nice", ct);
            Assert.Equal(expected.Count(row => row.Place == "Nice"), deleted.Rows);
            expected.RemoveAll(row => row.Place == "Nice");

            // By the added column, which the old objects lack: every row of theirs is null there.
            RowChangeResult updated = await dataset.UpdateAsync<MeterV2>(
                r => r.Temperature.IsNull & r.Reading < 400L, row => row with { Temperature = -1.0 }, ct);
            Assert.Equal(expected.Count(row => row.Temperature is null && row.Reading < 400), updated.Rows);
            expected = expected.ConvertAll(row => row.Temperature is null && row.Reading < 400 ? row with { Temperature = -1.0 } : row);

            Assert.Equal(Sorted(expected), Sorted(await RowsAsync<MeterV2>(dataset, ct)));
            Assert.True((await dataset.VerifyAsync(cancellationToken: ct)).Holds);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheMarkedRowsOfObjectsOfEitherSchemaAreInNoExtremeAndNoCount(bool clustered)
    {
        // An object's own extreme answers only while a live row holds it, whatever schema it was
        // written under: of a widened column, of a renamed one, under no filter, under one its own
        // scan takes and under one evaluated on its reshaped batches.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        (VortexDataset dataset, List<MeterV2> expected) = await EvolvedAsync(store, clustered, ct, marking: true);
        await using (dataset)
        {
            // The largest readings and the smallest keys of every object go, the earlier objects' and the later ones'.
            RowChangeResult deleted = await dataset.DeleteAsync<MeterV2>(r => r.Reading > 2_000L | r.Key < 30L, ct);
            Assert.Equal(5, deleted.ObjectsMarked);
            expected.RemoveAll(row => row.Reading > 2_000 || row.Key < 30);

            Assert.Equal(expected.Max(row => row.Reading), await dataset.Scan<MeterV2>().MaxAsync(r => r.Reading, ct));
            Assert.Equal(expected.Min(row => row.Key), await dataset.Scan<MeterV2>().MinAsync(r => r.Key, ct));
            Assert.Equal(
                expected.Where(row => row.Place is not null).Select(row => row.Place).Max(StringComparer.Ordinal),
                await dataset.Scan<MeterV2>().MaxAsync(r => r.Place, ct));
            Assert.Equal(
                expected.Where(row => row.Place == "Nice").Max(row => row.Reading),
                await dataset.Scan<MeterV2>().Where(r => r.Place == "Nice").MaxAsync(r => r.Reading, ct));
            Assert.Equal(
                expected.Where(row => Not(Gt(row.Temperature, 5.0)) == true).Min(row => row.Key),
                await dataset.Scan<MeterV2>().Where(r => !(r.Temperature > 5.0)).MinAsync(r => r.Key, ct));

            await AssertCountAsync(dataset, expected, r => r.Reading < 900L, row => row.Reading < 900, ct);
            await AssertCountAsync(dataset, expected, r => r.Place == "Lyon", row => row.Place == "Lyon", ct);
            await AssertCountAsync(dataset, expected, r => !(r.Temperature > 5.0), row => Not(Gt(row.Temperature, 5.0)) == true, ct);
        }
    }

    [Fact]
    public async Task ADroppedColumnIsReadByNothingAndItsNameIsNeverUsedAgain()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        (VortexDataset dataset, List<MeterV2> expected) = await EvolvedAsync(store, clustered: true, ct);
        await using (dataset)
        {
            await dataset.EvolveSchemaAsync(MeterV3.Schema, cancellationToken: ct);
            Assert.Equal(
                Sorted3(expected.Select(row => new MeterV3(row.Key, row.Reading, row.Temperature))),
                Sorted3(await RowsAsync<MeterV3>(dataset, ct)));

            // Neither the dropped name nor the one it had before a rename comes back, whatever the type.
            ArgumentException place = await Assert.ThrowsAsync<ArgumentException>(
                async () => await dataset.EvolveSchemaAsync(MeterV2.Schema, cancellationToken: ct));
            Assert.Contains("'Place' was a column", place.Message, StringComparison.Ordinal);
            ArgumentException site = await Assert.ThrowsAsync<ArgumentException>(
                async () => await dataset.EvolveSchemaAsync(
                    VortexSchema.Create([.. MeterV3.Schema.FieldArray, new VortexField("Site", VortexType.Utf8.Nullable)]),
                    cancellationToken: ct));
            Assert.Contains("'Site' was a column", site.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AChangeSomeValueWouldNotSurviveIsRefusedBeforeAnythingIsCommitted()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, MeterV1.Schema, Options(clustered: true), ct);
        ulong version = dataset.Version;
        VortexField key = MeterV1.Schema[0];
        VortexField reading = MeterV1.Schema[1];
        VortexField site = MeterV1.Schema[2];

        await AssertRefusedAsync(dataset, "must be nullable", [key, reading, site, new VortexField("Floor", VortexType.Int32)]);
        await AssertRefusedAsync(dataset, "cannot become", [key, reading with { Type = VortexType.Int16 }, site]);
        await AssertRefusedAsync(dataset, "cannot become", [key, reading with { Type = VortexType.UInt64 }, site]);
        await AssertRefusedAsync(dataset, "cannot become", [key, reading, site with { Type = VortexType.Int32 }]);
        await AssertRefusedAsync(dataset, "cannot become", [key, reading with { Type = VortexType.Int64.Nullable }, site with { Type = VortexType.Binary }]);
        await AssertRefusedAsync(dataset, "clustering key", [key with { Type = VortexType.Int64.Nullable }, reading, site]);
        await AssertRefusedAsync(dataset, "clustering key", [reading, site]);
        await AssertRefusedAsync(dataset, "clustering key", [key with { Name = "Id" }, reading, site], new Dictionary<string, string> { ["Id"] = "Key" });
        await AssertRefusedAsync(dataset, "is a column already", [key, reading, site with { Name = "Reading" }], new Dictionary<string, string> { ["Reading"] = "Site" });
        await AssertRefusedAsync(dataset, "no column of that name", [key, reading, site with { Name = "City" }], new Dictionary<string, string> { ["City"] = "Town" });

        Assert.Equal(version, dataset.Version);
        Assert.Equal(version, await dataset.EvolveSchemaAsync(MeterV1.Schema, cancellationToken: ct));

        async Task AssertRefusedAsync(VortexDataset target, string reason, VortexField[] fields, IReadOnlyDictionary<string, string>? renamed = null)
        {
            ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(
                async () => await target.EvolveSchemaAsync(VortexSchema.Create(fields), renamed, ct));
            Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TwoChangesOfTheSchemaDoNotBothLand()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset first = await VortexDataset.CreateAsync(store, MeterV1.Schema, Options(clustered: true), ct);
        await using VortexDataset second = await VortexDataset.OpenAsync(store, Options(clustered: true), ct);

        await first.EvolveSchemaAsync(MeterV2.Schema, new Dictionary<string, string> { ["Place"] = "Site" }, ct);

        // The second handle checked its change against the schema the first one replaced.
        VortexSchema other = VortexSchema.Create([.. MeterV1.Schema.FieldArray, new VortexField("Note", VortexType.Utf8.Nullable)]);
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await second.EvolveSchemaAsync(other, cancellationToken: ct));
        Assert.Contains("Refresh", refused.Message, StringComparison.Ordinal);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(first.Schema.Select(field => field.Name), second.Schema.Select(field => field.Name));

        // The same change again is already there.
        Assert.Equal(first.Version, await second.EvolveSchemaAsync(MeterV2.Schema, new Dictionary<string, string> { ["Place"] = "Site" }, ct));
    }

    [Fact]
    public async Task AHandleReadsTheSchemaOfItsVersionUntilItRefreshes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset writer = await VortexDataset.CreateAsync(store, MeterV1.Schema, Options(clustered: false), ct);
        List<MeterV1> rows = Meters(0, 3);
        await AppendAsync(writer, rows, ct);
        await using VortexDataset reader = await VortexDataset.OpenAsync(store, Options(clustered: false), ct);
        Scan<MeterV1> pinned = writer.Scan<MeterV1>();

        await writer.EvolveSchemaAsync(MeterV2.Schema, new Dictionary<string, string> { ["Place"] = "Site" }, ct);

        Assert.Equal(rows.Count, await pinned.CountAsync(ct));
        Assert.Equal(rows, await reader.Scan<MeterV1>().ToRecordsAsync(ct).ToListAsync(ct));
        Assert.Equal("Site", reader.Schema[2].Name);
        await reader.RefreshAsync(ct);
        Assert.Equal("Place", reader.Schema[2].Name);
        Assert.Equal(rows.ConvertAll(ToV2), await reader.Scan<MeterV2>().ToRecordsAsync(ct).ToListAsync(ct));
    }

    [Fact]
    public async Task AnImportedFileOfAnEarlierSchemaReadsAsTheCurrentOne()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, MeterV1.Schema, Options(clustered: true), ct);
        await dataset.EvolveSchemaAsync(MeterV2.Schema, new Dictionary<string, string> { ["Place"] = "Site" }, ct);

        List<MeterV1> earlier = Meters(0, 2);
        await PutAsync(store, "imports/earlier.vortex", earlier, ct);
        await dataset.ImportAsync("imports/earlier.vortex", ct);
        Assert.Equal(Sorted(earlier.ConvertAll(ToV2)), Sorted(await RowsAsync<MeterV2>(dataset, ct)));

        // A column no schema of the dataset ever had would be data nothing reads.
        MeterExtra[] extra = [new MeterExtra(1, 2, "Paris", 3)];
        await PutAsync(store, "imports/extra.vortex", extra, ct);
        ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(
            async () => await dataset.ImportAsync("imports/extra.vortex", ct));
        Assert.Contains("'Extra'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NumbersWidenedWithinTheirKindReadBackExactly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, NarrowNumbers.Schema, Options(clustered: true), ct);
        NarrowNumbers[] narrow = new NarrowNumbers[500];
        for (int i = 0; i < narrow.Length; i++)
        {
            narrow[i] = new NarrowNumbers(
                i,
                i % 5 == 0 ? null : (sbyte)(i % 256 - 128),
                (ushort)(i * 131),
                i % 7 == 0 ? null : i / 3.0f,
                (short)(-i * 60));
        }

        await AppendAsync(dataset, narrow, ct);
        await dataset.EvolveSchemaAsync(WideNumbers.Schema, cancellationToken: ct);

        List<WideNumbers> expected = [.. narrow.Select(row => new WideNumbers(row.Key, row.Small, row.Count, row.Ratio, row.Level))];
        Assert.Equal(expected, await dataset.Scan<WideNumbers>().ToRecordsAsync(ct).ToListAsync(ct));

        // A filter on a widened column goes to the narrow one, a literal beyond its range included.
        Assert.Equal(expected.Count(row => row.Small >= 100), await dataset.Scan<WideNumbers>().Where(r => r.Small >= 100L).CountAsync(ct));
        Assert.Equal(expected.Count(row => row.Small > 1_000), await dataset.Scan<WideNumbers>().Where(r => r.Small > 1_000L).CountAsync(ct));
        Assert.Equal(expected.Count(row => row.Count > 40_000), await dataset.Scan<WideNumbers>().Where(r => r.Count > 40_000UL).CountAsync(ct));
        Assert.Equal(expected.Count(row => row.Level < -20_000), await dataset.Scan<WideNumbers>().Where(r => r.Level < -20_000).CountAsync(ct));
        Assert.Equal(expected.Count(row => row.Small == null), await dataset.Scan<WideNumbers>().Where(r => r.Small.IsNull).CountAsync(ct));
        Assert.Equal(expected.Max(row => row.Ratio), await dataset.Scan<WideNumbers>().MaxAsync(r => r.Ratio, ct));
        Assert.Equal(expected.Min(row => row.Level), await dataset.Scan<WideNumbers>().MinAsync(r => r.Level, ct));
    }

    [Fact]
    public async Task AnAddedNestedRecordIsNullInTheObjectsWrittenBeforeIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, MeterV1.Schema, Options(clustered: false), ct);
        List<MeterV1> earlier = Meters(0, 2);
        await AppendAsync(dataset, earlier, ct);
        await dataset.EvolveSchemaAsync(MeterNested.Schema, cancellationToken: ct);
        MeterNested[] later = [new MeterNested(-1, 7, "Paris", new MeterDetail(3, "B12")), new MeterNested(-2, 8, "Lyon", null)];
        await AppendAsync(dataset, later, ct);

        List<MeterNested> expected = [.. earlier.Select(row => new MeterNested(row.Key, row.Reading, row.Site, null)), .. later];
        Assert.Equal(expected, await dataset.Scan<MeterNested>().ToRecordsAsync(ct).ToListAsync(ct));
        Assert.Equal(1, await dataset.Scan<MeterNested>().Where(r => r.Detail.IsNotNull).CountAsync(ct));
    }

    [Fact]
    public async Task AFilterEvaluatedOnReshapedBatchesLeavesThemAsTheProjectionAsked()
    {
        // Under a negation, a predicate over the struct the earlier objects lack is unknown there
        // and the filter is evaluated on the reshaped batches, which hold the struct whole for it;
        // the batches that go out hold only the part of it that was asked for.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, MeterV1.Schema, Options(clustered: false), ct);
        List<MeterV1> earlier = Meters(0, 2);
        await AppendAsync(dataset, earlier, ct);
        await dataset.EvolveSchemaAsync(MeterNested.Schema, cancellationToken: ct);
        MeterNested[] later =
        [
            new MeterNested(-1, 7, "Paris", new MeterDetail(3, "B12")),
            new MeterNested(-2, 8, "Lyon", new MeterDetail(1, null)),
            new MeterNested(-3, 900, "Nice", new MeterDetail(1, "C3")),
            new MeterNested(-4, 9, "Lille", null),
        ];
        await AppendAsync(dataset, later, ct);

        long rows = 0;
        VortexSchema? delivered = null;
        await foreach (BatchView batch in dataset.Scan("Key", "Detail.Floor").Where(VortexExpr.Parse("not (Detail.Floor > 2 and Reading > 100)")).WithCancellation(ct))
        {
            rows += batch.RowCount;
            delivered ??= batch.Schema;

            // The struct's node holds the one field its type declares, not the two the filter read.
            int detail = batch.Arena.GetNode(batch.Node).GetFieldIndex(1);
            Assert.Equal(1, batch.Arena.GetNode(detail).FieldCount);
        }

        // An earlier row's floor is unknown, so the conjunction is false where the reading is small
        // and unknown where it is not: the seventeen earlier rows with a reading of at most 100,
        // then every later row, none of which has both a floor above 2 and a reading above 100.
        Assert.Equal(earlier.Count(row => row.Reading <= 100) + later.Length, rows);
        Assert.NotNull(delivered);
        Assert.Equal(["Key", "Detail"], delivered!.Select(field => field.Name));
        Assert.Equal(["Floor"], delivered[1].Type.NonNullable.Fields.ToArray().Select(field => field.Name));
    }

    [Fact]
    public async Task ATakeOfMarkedRowsInAnEarlierObjectSelectsWhatTheReshapedFilterKept()
    {
        // A membership test does not go to a widened column: it is evaluated on the reshaped
        // batches, whose gather leaves fewer rows than the take's selection described.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = Options(clustered: false) with { MarkedObjectBytes = 0, MarkedShare = 1 };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, MeterV1.Schema, options, ct);
        await AppendAsync(dataset, Meters(0, 1), ct);
        await dataset.EvolveSchemaAsync(MeterV2.Schema, new Dictionary<string, string> { ["Place"] = "Site" }, ct);
        RowChangeResult deleted = await dataset.DeleteAsync<MeterV2>(r => r.Key < 20L, ct);
        Assert.Equal(1, deleted.ObjectsMarked);

        // The live rows are keys 20 to 199, at places 0 to 179, and each reading is three times its key.
        List<MeterV2> taken = await dataset.Scan<MeterV2>()
            .Where(r => r.Reading.In(75L, 90L, 93L, 300L, 450L))
            .Rows(5, 10, 11, 80, 130, 170)
            .ToRecordsAsync(ct).ToListAsync(ct);
        Assert.Equal([25L, 30, 31, 100, 150], taken.Select(row => row.Key));
    }

    [Fact]
    public void TheRetiredNamesTravelInTheHeader()
    {
        CommitHeader header = new CommitHeader
        {
            Version = 3,
            Retired = [new RetiredColumn("Site", "Place"), new RetiredColumn("Old", string.Empty)],
        };

        Vorticity.Serialization.Protobuf.ProtoWriter writer = new Vorticity.Serialization.Protobuf.ProtoWriter();
        try
        {
            header.Write(ref writer);
            CommitHeader back = CommitHeader.Read(writer.WrittenSpan);
            Assert.Equal(header.Retired, back.Retired);
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Fact]
    public void ARetiredNameTravelsEvenEmptyAndAnEntryNamingNoneIsRefused()
    {
        // A column's name may be empty, and a retired one is never used again: the entry is written
        // whatever its name, and one that names no column is a torn header, not an empty name.
        CommitHeader header = new CommitHeader
        {
            Version = 3,
            Retired = [new RetiredColumn(string.Empty, string.Empty), new RetiredColumn("Old", "New")],
        };

        Vorticity.Serialization.Protobuf.ProtoWriter writer = new Vorticity.Serialization.Protobuf.ProtoWriter();
        Vorticity.Serialization.Protobuf.ProtoWriter nameless = new Vorticity.Serialization.Protobuf.ProtoWriter();
        Vorticity.Serialization.Protobuf.ProtoWriter torn = new Vorticity.Serialization.Protobuf.ProtoWriter();
        try
        {
            header.Write(ref writer);
            Assert.Equal(header.Retired, CommitHeader.Read(writer.WrittenSpan).Retired);

            nameless.WriteString(CommitHeader.RetiredField.Current, "New");
            torn.WriteUInt64(CommitHeader.Field.Version, 3);
            torn.WriteBytes(CommitHeader.Field.Retired, nameless.WrittenSpan);
            byte[] bytes = torn.WrittenSpan.ToArray();
            Assert.Throws<CommitFormatException>(() => CommitHeader.Read(bytes));
        }
        finally
        {
            writer.Dispose();
            nameless.Dispose();
            torn.Dispose();
        }
    }

    /// <summary>
    /// A dataset created with <see cref="MeterV1"/>, three objects appended, changed to
    /// <see cref="MeterV2"/> with the site renamed to place, and two objects appended in it; and its
    /// rows as the second schema reads them.
    /// </summary>
    private static async Task<(VortexDataset Dataset, List<MeterV2> Expected)> EvolvedAsync(
        IObjectStore store, bool clustered, CancellationToken ct, bool marking = false)
    {
        Decoders.EnsureRegistered();
        DatasetOptions options = marking ? Options(clustered) with { MarkedObjectBytes = 0, MarkedShare = 1 } : Options(clustered);
        VortexDataset dataset = await VortexDataset.CreateAsync(store, MeterV1.Schema, options, ct);
        List<MeterV2> expected = [];
        for (int part = 0; part < 3; part++)
        {
            List<MeterV1> rows = Meters(part, 5);
            await AppendAsync(dataset, rows, ct);
            expected.AddRange(rows.Select(ToV2));
        }

        ulong before = dataset.Version;
        ulong evolved = await dataset.EvolveSchemaAsync(MeterV2.Schema, new Dictionary<string, string> { ["Place"] = "Site" }, ct);
        Assert.Equal(before + 1, evolved);

        for (int part = 3; part < 5; part++)
        {
            MeterV2[] rows = new MeterV2[200];
            for (int i = 0; i < rows.Length; i++)
            {
                long key = ((long)i * 5) + part;
                rows[i] = new MeterV2(key, key * 3L, i % 9 == 0 ? null : Sites[i % Sites.Length], i % 4 == 0 ? null : (i % 23) - 5.5);
            }

            await AppendAsync(dataset, rows, ct);
            expected.AddRange(rows);
        }

        return (dataset, expected);
    }

    /// <summary>Two hundred meters whose keys are congruent to <paramref name="part"/> modulo <paramref name="parts"/>.</summary>
    private static List<MeterV1> Meters(int part, int parts)
    {
        List<MeterV1> rows = [];
        for (int i = 0; i < 200; i++)
        {
            long key = ((long)i * parts) + part;
            rows.Add(new MeterV1(key, (int)(key * 3), Sites[(int)(key % Sites.Length)]));
        }

        return rows;
    }

    private static MeterV2 ToV2(MeterV1 row) => new MeterV2(row.Key, row.Reading, row.Site, null);

    private static async Task AppendAsync<TRecord>(VortexDataset dataset, IReadOnlyList<TRecord> rows, CancellationToken ct)
        where TRecord : IVortexRecord<TRecord>
    {
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<TRecord>([.. rows], ct);
        await dataset.AppendAsync(draft, ct);
    }

    /// <summary>Writes rows into a file of their own record's schema and puts it in the store.</summary>
    private static async Task PutAsync<TRecord>(IObjectStore store, string key, IReadOnlyList<TRecord> rows, CancellationToken ct)
        where TRecord : IVortexRecord<TRecord>
    {
        System.IO.MemoryStream stream = new System.IO.MemoryStream();
        Vorticity.Types.DType schema = Vorticity.VortexTypes.ToDType(TRecord.Schema, new Vorticity.Types.DTypeArena());
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), schema, VortexWriteOptions.Default))
        {
            await writer.WriteAsync<TRecord>([.. rows], ct);
            await writer.CompleteAsync(ct);
        }

        await store.PutIfAbsentAsync(key, stream.ToArray(), ct);
    }

    private static DatasetOptions Options(bool clustered) => new DatasetOptions
    {
        Seed = 0x5C4E_3A,
        Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 2 << 10 },
        ClusteringKey = clustered ? ["Key"] : null,
    };

    private static async Task<List<TRecord>> RowsAsync<TRecord>(VortexDataset dataset, CancellationToken ct)
        where TRecord : IVortexRecord<TRecord> =>
        await dataset.Scan<TRecord>().ToRecordsAsync(ct).ToListAsync(ct);

    private static async Task AssertCountAsync(
        VortexDataset dataset, List<MeterV2> expected, Func<Probe<MeterV2>, Predicate> filter, Func<MeterV2, bool> oracle, CancellationToken ct)
    {
        long count = expected.Count(oracle);
        Assert.Equal(count, await dataset.Scan<MeterV2>().Where(filter).CountAsync(ct));
        Assert.Equal(count > 0, await dataset.Scan<MeterV2>().Where(filter).AnyAsync(ct));
        Assert.Equal(count, await dataset.Scan<MeterV2>().Where(filter).ToRecordsAsync(ct).CountAsync(ct));
    }

    /// <summary>How many objects of the version were written under another schema than its own.</summary>
    private static async Task<int> EarlierObjectsAsync(VortexDataset dataset, CancellationToken ct)
    {
        int earlier = 0;
        await foreach (PositionedObject held in dataset.WalkAsync(null, 0, long.MaxValue, null, ct))
        {
            await using ObjectLease lease = await dataset.RentAsync(held.Entry, ct);
            earlier += lease.File.DType == dataset.DType ? 0 : 1;
        }

        return earlier;
    }

    private static List<MeterV2> Sorted(IEnumerable<MeterV2> rows) => [.. rows.OrderBy(row => row.Key)];

    private static List<MeterV3> Sorted3(IEnumerable<MeterV3> rows) => [.. rows.OrderBy(row => row.Key)];

    // Three-valued logic, a null standing for unknown.
    private static bool? Gt(double? value, double than) => value is { } v ? v > than : null;

    private static bool? Not(bool? value) => value is { } v ? !v : null;

    private static bool? Or(bool? left, bool? right) =>
        left == true || right == true ? true : left == false && right == false ? false : null;
}
