// The search of one object's sparse index, over an index built in memory: what it judges, what it holds, which granules it
// keeps.
//
// THE ORACLE IS THE GRANULES' KEYS: granule i holds keys from its key to the next one's, both included, the last to the
// object's last key; a filter whose conjuncts on the indexed components a box judges exactly keeps a granule exactly when
// that span meets them.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetKeyPruningTests
{
    /// <summary>
    /// A window on the hour under <c>(Service, Hour)</c> bounds the second component alone: the search judges boxes, as many
    /// as it counts, and the index it searched holds every key it was built from.
    /// </summary>
    [Fact]
    public async Task ASearchCountsTheBoxesItJudgesAndTheIndexTheBytesItHolds()
    {
        DatasetSchema schema = await SchemaAsync(new DatasetLayout
        {
            SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.At)).Truncate(CalendarUnit.Hour)],
        });
        List<FilterLiteral[]> keys = [];
        for (int service = 0; service < 6; service++)
        {
            for (int hour = 0; hour < 24; hour += 2)
            {
                keys.Add([FilterLiteral.From($"svc-{service:D2}"), Micros(Start.AddHours(hour))]);
            }
        }

        GranuleKeys index = Index(schema, keys, [FilterLiteral.From("svc-05"), Micros(Start.AddHours(23))]);
        DateTime ten = Start.AddHours(10);
        KeyPruning pruning = KeyPruning.Of(schema, Expr.And(Ge(nameof(Row.At), Micros(ten)), Lt(nameof(Row.At), Micros(ten.AddHours(1)))))!;
        List<(int First, int End)> kept = pruning.Granules(index, default);

        Assert.NotEmpty(kept);
        Assert.True(pruning.Judgments > kept.Count, $"{pruning.Judgments} boxes judged for {kept.Count} ranges kept");
        long keyBytes = keys.Sum(key => (long)Encode(schema, key).Length);
        Assert.True(index.ResidentBytes >= keyBytes, $"{index.ResidentBytes} bytes held for {keyBytes} bytes of keys");
    }

    /// <summary>The schema of a dataset of <see cref="Row"/>s under <paramref name="layout"/>, which holds no row.</summary>
    private static async Task<DatasetSchema> SchemaAsync(DatasetLayout layout)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions { Layout = layout }, Ct);
        return dataset.Snapshot.Schema;
    }

    /// <summary>A key of the layout's indexed components, row-encoded as an object's sparse index holds it.</summary>
    private static byte[] Encode(DatasetSchema schema, FilterLiteral[] values) =>
        SortKey.Of(schema.Layout!, partition: false, values.Length).Encode(values);

    /// <summary>
    /// The sparse index of an object of granules of <paramref name="granuleRows"/> rows whose keys are <paramref name="keys"/>,
    /// ascending, and whose last row's key is <paramref name="last"/>, every page in hand.
    /// </summary>
    private static GranuleKeys Index(DatasetSchema schema, List<FilterLiteral[]> keys, FilterLiteral[] last, int granuleRows = 8, int pageEntries = 16)
    {
        List<byte[]> encoded = [.. keys.Select(key => Encode(schema, key))];
        (byte[] table, byte[] pages) = GranuleKeys.Build(granuleRows, (long)keys.Count * granuleRows, last.Length, encoded, Encode(schema, last), pageEntries);
        GranuleKeys index = GranuleKeys.From(table) ?? throw new InvalidOperationException("The table does not decode.");
        for (int page = 0; page < index.Pages; page++)
        {
            (long offset, int length, ulong _) = index.PageAt(page);
            Assert.True(index.HasPage(page) || index.TakePage(page, pages.AsSpan((int)offset, length)));
        }

        return index;
    }
}
