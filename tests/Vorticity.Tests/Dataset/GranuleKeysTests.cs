// The sparse index of a sorted object (spec A, 3; A3), `vorticity.key.index.v1`: the first key of each granule on the
// indexed prefix, read by pages; a filter on the key narrows an object to the granules it reaches.
//
// THE ORACLE IS A MODEL AND THE ROWS: the index gives back the keys it was built of, finds a bound where a search of them
// does, and refuses a page that does not hash as its directory says; an object's index holds the prefix of the key of
// each granule's first row, worked out from its rows, and a lookup returns the rows LINQ keeps.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.RowEncoding;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class GranuleKeysTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 600 indexes of random keys over granules of 1 to 64 rows, paged by 1 to 128 entries: each reads back whole, its pages
    /// taken give every granule's key, the first granule at or past a random bound (a prefix of a key, a key, a key and more,
    /// another) is the model's, and a page altered by a bit is refused.
    /// </summary>
    [Fact]
    public void AnIndexReadBackGivesEachGranulesKeyAndFindsABoundAsAModelDoes()
    {
        Random random = new Random(3);
        List<string> failures = [];
        int paged = 0;
        for (int trial = 0; trial < 600; trial++)
        {
            int granuleRows = random.Next(1, 65);
            long rows = random.Next(1, 6000);
            int granules = (int)((rows + granuleRows - 1) / granuleRows);
            int pageEntries = new[] { 1, 2, 3, 4, 7, 16, 128 }[random.Next(7)];
            List<byte[]> keys = [];
            for (int i = 0; i < granules; i++)
            {
                keys.Add(RandomKey(random));
            }

            keys.Sort(Compare);
            byte[] last = RandomKey(random);
            if (Compare(last, keys[^1]) < 0)
            {
                last = [.. keys[^1], .. RandomKey(random)];
            }

            (byte[] table, byte[] pages) = GranuleKeys.Build(granuleRows, rows, 2, keys, last, pageEntries);
            GranuleKeys? index = GranuleKeys.From(table);
            if (index is null || index.Granules != granules || index.GranuleRows != granuleRows || index.Rows != rows
                || !index.LastKey.AsSpan().SequenceEqual(last) || index.Components != 2)
            {
                failures.Add($"trial {trial}: the table does not read back");
                continue;
            }

            paged += index.Pages > 1 ? 1 : 0;
            for (int page = 0; page < index.Pages; page++)
            {
                (long offset, int length, ulong _) = index.PageAt(page);
                if (!index.HasPage(page) && !index.TakePage(page, pages.AsSpan((int)offset, length)))
                {
                    failures.Add($"trial {trial}: page {page} refused");
                }
            }

            // In order, a record each from the key before; backward, each from its run's whole key.
            GranuleKeys.KeyReader reader = default;
            for (int granule = 0; granule < granules; granule++)
            {
                if (!reader.Key(index, granule).SequenceEqual(keys[granule]))
                {
                    failures.Add($"trial {trial}: the key of granule {granule}");
                }
            }

            for (int granule = granules - 1; granule >= 0; granule--)
            {
                if (!reader.Key(index, granule).SequenceEqual(keys[granule]))
                {
                    failures.Add($"trial {trial}: the key of granule {granule}, read backward");
                }
            }

            reader.Dispose();

            for (int probe = 0; probe < 40; probe++)
            {
                byte[] bound = Bound(random, keys);
                int first = index.FirstAtOrPast(bound);
                int want = keys.FindIndex(key => Compare(key, bound) >= 0);
                if (first != (want < 0 ? granules : want))
                {
                    failures.Add($"trial {trial}: the first granule at or past a bound is {first}, the model's {want}");
                }
            }

            if (index.Pages > 1)
            {
                GranuleKeys bad = GranuleKeys.From(table)!;
                (long offset, int length, ulong _) = bad.PageAt(1);
                byte[] copy = pages.AsSpan((int)offset, length).ToArray();
                copy[copy.Length / 2] ^= 1;
                if (bad.TakePage(1, copy))
                {
                    failures.Add($"trial {trial}: a page altered by a bit was taken");
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} wrong:\n{string.Join('\n', failures.Take(10))}");
        Assert.True(paged > 100, $"{paged} indexes of several pages");
    }

    /// <summary>
    /// Four appends of 30 000 rows keyed by a service, an hour and a sequence, drawn with a skew so that the runs of a
    /// prefix range from a row to thousands, merged, the index on the first one, two or three components: each object's
    /// index holds, for each granule, the prefix of its first row's key, its rows, its last key; lookups on the first
    /// column, an absent service among them, return the rows LINQ keeps.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AnObjectsIndexHoldsTheFirstKeyOfEachGranule(int prefix)
    {
        Random random = new Random(17 + prefix);
        string[] services = [.. Enumerable.Range(0, 40).Select(i => $"svc-{i * 7919 % 1000:D3}")];
        List<Row> all = [];
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.Hour)), LayoutValue.Column(nameof(Row.Seq))],
                IndexedPrefix = prefix,
            },
            ContentsObjectBytes = 0,
            Write = new VortexWriteOptions { BlockRows = 512 },
        }, Ct);
        for (int d = 0; d < 4; d++)
        {
            Row[] rows = [.. Enumerable.Range(0, 30_000).Select(_ => new Row(
                services[(int)(Math.Pow(random.NextDouble(), 2) * services.Length)],
                (int)(Math.Pow(random.NextDouble(), 3) * 200),
                random.NextInt64(0, 1L << 40),
                random.Next(0, 600),
                random.NextDouble()))];
            all.AddRange(rows);
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Row>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
        {
        }

        List<string> failures = [];
        int indexed = 0;
        long granules = 0;
        SortKey sortKey = SortKey.OfColumns(dataset.Snapshot.Schema.DType, [nameof(Row.Service), nameof(Row.Hour), nameof(Row.Seq)], descending: false);
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            if (held.Level == 0)
            {
                continue;
            }

            ObjectEntry entry = held.Entry;
            if (entry.Contents is not { KeyIndex: not null } contents || await dataset.KeyIndexAsync(entry, contents, Ct) is not { } keys)
            {
                failures.Add($"{entry.Key}: no key index read");
                continue;
            }

            indexed++;
            List<byte[]> rowKeys = [];
            await using (ObjectLease lease = await dataset.RentAsync(entry, Ct))
            {
                await foreach (RecordBatch batch in lease.File.Scan().ToBatchesAsync(Ct))
                {
                    using (batch)
                    {
                        using RowKeys encoded = sortKey.Encode(batch);
                        for (int i = 0; i < batch.RowCount; i++)
                        {
                            rowKeys.Add(encoded.Row(i).ToArray());
                        }
                    }
                }
            }

            GranuleKeys.KeyReader reader = default;
            for (int row = 0; row < rowKeys.Count; row += keys.GranuleRows)
            {
                granules++;
                if (!reader.Key(keys, row / keys.GranuleRows).SequenceEqual(rowKeys[row].AsSpan(0, sortKey.PrefixLength(rowKeys[row], prefix))))
                {
                    failures.Add($"{entry.Key}: the key of granule {row / keys.GranuleRows}");
                }
            }

            reader.Dispose();
            byte[] lastKey = rowKeys[^1][..sortKey.PrefixLength(rowKeys[^1], prefix)];
            if (rowKeys.Count != keys.Rows || keys.Components != prefix || !keys.LastKey.AsSpan().SequenceEqual(lastKey))
            {
                failures.Add($"{entry.Key}: {keys.Rows} rows of {rowKeys.Count}, {keys.Components} components of {prefix}, the last key {(keys.LastKey.AsSpan().SequenceEqual(lastKey) ? "equal" : "other")}");
            }
        }

        for (int probe = 0; probe < 12; probe++)
        {
            string service = probe == 0 ? "svc-zzz" : services[random.Next(services.Length)];
            int hour = random.Next(0, 40);
            long got = await dataset.Scan<Row>().Where(r => r.Service == service && r.Hour >= hour).ToRecordsAsync(Ct).CountAsync(Ct);
            long want = all.Count(r => r.Service == service && r.Hour >= hour);
            if (got != want)
            {
                failures.Add($"Service == {service} && Hour >= {hour}: {got} rows, LINQ {want}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} wrong:\n{string.Join('\n', failures.Take(10))}");
        Assert.True(indexed > 0 && granules > 0, $"{indexed} objects indexed, {granules} granules");
    }

    private static byte[] RandomKey(Random random)
    {
        byte[] alphabet = [0x01, 0x02, 0x03, 0x7F, 0x80, 0xFF];
        byte[] key = new byte[random.Next(1, 7)];
        for (int i = 0; i < key.Length; i++)
        {
            key[i] = alphabet[random.Next(alphabet.Length)];
        }

        return key;
    }

    private static byte[] Bound(Random random, List<byte[]> keys)
    {
        byte[] near = keys[random.Next(keys.Count)];
        return random.Next(4) switch
        {
            0 => RandomKey(random),
            1 => near[..random.Next(1, near.Length + 1)],
            2 => near,
            _ => [.. near, .. RandomKey(random)],
        };
    }

    private static int Compare(byte[] left, byte[] right) => left.AsSpan().SequenceCompareTo(right);

    [VortexRecord]
    public partial record struct Row(string Service, int Hour, long Seq, int Status, double Latency);
}
