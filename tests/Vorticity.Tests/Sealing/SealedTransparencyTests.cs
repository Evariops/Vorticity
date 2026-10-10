// Transparency: a sealed file and an encrypted dataset answer every kind of query with the same bits as
// the plain ones of the same rows.
//
// WHAT IS HELD: over a sealed file and the plain file its plaintext is, at one, four and fourteen lanes,
// the rows, a filter, a range of rows, a projection, every aggregate, a group by, an ordered top-k, an
// ordered scan and a walk of a key index give the same answers, compared as text with every double in
// its round-trip form; and an encrypted dataset gives the answers of a plain dataset of the same objects.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Tests.Aggregation;
using Xunit;

namespace Vorticity.Tests.Sealing;

public sealed class SealedTransparencyTests : IDisposable
{
    private const int Rows = 300_000;

    private static readonly string[] Cities = ["Paris", "Lyon", "Nice", "Lille", "Brest", "Nantes", "Metz"];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vorticity-sealed-transparency-tests", Guid.NewGuid().ToString("N"));

    public SealedTransparencyTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A test failure must not be masked by a cleanup failure.
        }
    }

    [Fact]
    public async Task ASealedFileAnswersEveryQueryAsItsPlaintextDoes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        await using VortexSession session = VortexSession.Create(o =>
        {
            o.Keyring = keyring;
            o.EncryptFiles = true;
        });
        string sealedPath = Path.Combine(_directory, "sealed.vortex");
        string plainPath = Path.Combine(_directory, "plain.vortex");
        VortexWriteOptions options = new VortexWriteOptions
        {
            Indexes = IndexPolicy.None.SortedRuns("City", true).Bloom("Day", required: true).WithBudgetPerMille(100_000),
        };
        await using (VortexFileWriter writer = session.CreateWriter<Grouped>(sealedPath, options))
        {
            await writer.WriteAsync<Grouped>(RowsOf(Rows), ct);
            await writer.CompleteAsync(ct);
        }

        // The plain file is the sealed one's plaintext, byte for byte: only the envelope differs.
        await using (FileStream plain = System.IO.File.Create(plainPath))
        {
            await session.DecryptAsync(sealedPath, plain, ct);
        }

        await using VortexFile sealedFile = await session.OpenAsync(sealedPath, cancellationToken: ct);
        await using VortexFile plainFile = await VortexFile.OpenAsync(plainPath, ct);
        Assert.NotNull(sealedFile.SealedLayout);
        foreach (int degree in new[] { 1, 4, 14 })
        {
            ScanOptions scan = new ScanOptions { DegreeOfParallelism = degree };
            foreach ((string name, Func<Func<Scan<Grouped>>, CancellationToken, Task<string>> query) in Queries(indexed: true))
            {
                string expected = await query(() => plainFile.Scan<Grouped>().With(scan), ct);
                string actual = await query(() => sealedFile.Scan<Grouped>().With(scan), ct);
                Assert.True(expected is not ("" or "0"), $"{name} answers nothing, which proves nothing.");
                Assert.True(expected == actual, $"{name} at {degree} lanes: the sealed file answers otherwise.");
            }
        }
    }

    [Fact]
    public async Task AnEncryptedDatasetAnswersEveryQueryAsAPlainOneDoes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        await using VortexSession session = VortexSession.Create(o => o.Keyring = keyring);
        await using MemoryObjectStore plainStore = new MemoryObjectStore();
        await using MemoryObjectStore sealedStore = new MemoryObjectStore();
        await using VortexDataset plain = await VortexDataset.CreateAsync(plainStore, Grouped.Schema, new DatasetOptions(), ct);
        await using VortexDataset encrypted = await VortexDataset.CreateAsync(
            sealedStore, Grouped.Schema, new DatasetOptions { Session = session, Encrypted = true }, ct);
        Grouped[] rows = RowsOf(Rows / 3);
        for (int part = 0; part < 3; part++)
        {
            foreach (VortexDataset dataset in new[] { plain, encrypted })
            {
                ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Grouped>(rows[(part * rows.Length / 3)..((part + 1) * rows.Length / 3)], ct);
                await dataset.AppendAsync(draft, ct);
            }
        }

        foreach (int degree in new[] { 1, 4 })
        {
            ScanOptions scan = new ScanOptions { DegreeOfParallelism = degree };
            foreach ((string name, Func<Func<Scan<Grouped>>, CancellationToken, Task<string>> query) in Queries(indexed: false))
            {
                string expected = await query(() => plain.Scan<Grouped>().With(scan), ct);
                string actual = await query(() => encrypted.Scan<Grouped>().With(scan), ct);
                Assert.True(expected is not ("" or "0"), $"{name} answers nothing, which proves nothing.");
                Assert.True(expected == actual, $"{name} at {degree} lanes: the encrypted dataset answers otherwise.");
            }
        }
    }

    /// <summary>The queries, each answering as text, doubles in their round-trip form; those that read an index of sorted runs on the city when <paramref name="indexed"/>.</summary>
    private static IEnumerable<(string Name, Func<Func<Scan<Grouped>>, CancellationToken, Task<string>> Query)> Queries(bool indexed)
    {
        yield return ("every row", (scan, ct) => TextAsync(scan().ToRecordsAsync(ct)));
        yield return ("a filter", async (scan, ct) =>
            Text(await scan().Where(r => r.City == "Lyon" && r.Day >= 50).CountAsync(ct)));
        yield return ("the rows a filter keeps", (scan, ct) =>
            TextAsync(scan().Where(r => r.Bucket > 2_000_000_000L && r.Score < 3.0).ToRecordsAsync(ct)));
        yield return ("a range of rows", (scan, ct) =>
            TextAsync(scan().Rows(new RowRange(12_345, 98_765)).ToRecordsAsync(ct)));
        yield return ("a projection", (scan, ct) =>
            TextAsync(scan().Where(r => r.Day < 40).Select(r => (r.City, r.Score)).As<CityScore>().ToRecordsAsync(ct)));
        yield return ("the aggregates", async (scan, ct) => Text(
            await scan().SumAsync(r => r.Day, ct),
            await scan().AverageAsync(r => r.Score, ct),
            await scan().MinAsync(r => r.Day, ct),
            await scan().MaxAsync(r => r.Score, ct),
            await scan().CountDistinctAsync(r => r.Bucket, ct),
            await scan().CountAsync(ct)));
        yield return ("a group by", async (scan, ct) =>
        {
            List<CityDay> groups = await ListAsync(scan()
                .GroupBy(r => (r.City, r.Day))
                .Select(g => (g.Key.City, g.Key.Day, g.Count(), g.Average(r => r.Score)))
                .As<CityDay>()
                .ToRecordsAsync(ct));
            groups.Sort((a, b) => (a.City, a.Day).CompareTo((b.City, b.Day)));
            return string.Join('\n', groups.Select(g => Text(g.City, g.Day, g.Count, g.Mean)));
        });
        yield return ("an ordered top-k", (scan, ct) => TextAsync(scan()
            .GroupBy(r => r.City)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Take(3)
            .Select(g => (g.Key, g.Count()))
            .As<CityCount>()
            .ToRecordsAsync(ct)));
        if (indexed)
        {
            // An ordered scan and a key walk read the file's index of sorted runs.
            yield return ("an ordered scan", (scan, ct) =>
                TextAsync(scan().Where(r => r.Day < 25).OrderBy(r => r.City).ToRecordsAsync(ct)));
            yield return ("a walk of a key index", async (scan, ct) =>
            {
                StringBuilder walked = new StringBuilder();
                await using KeyCursor<string> cursor = await scan().Keys(r => r.City).OpenAsync(ct);
                for (bool ok = await cursor.SeekFirstAsync(ct); ok; ok = await cursor.NextKeyAsync(ct))
                {
                    walked.Append(cursor.Key).Append('=').Append(await cursor.CountAtKeyAsync(ct)).Append('\n');
                }

                return walked.ToString();
            });
        }
    }

    private static Grouped[] RowsOf(int count)
    {
        Grouped[] rows = new Grouped[count];
        for (int row = 0; row < count; row++)
        {
            rows[row] = new Grouped(
                Cities[(row * 31) % Cities.Length],
                row / 1_000,
                row % 4 == 0,
                row % 9 == 0 ? null : row % 6,
                row % 13 == 0 ? null : row % 5 * 1_000_000_000L,
                (row * 7_919 % 10_007) / 997.0);
        }

        return rows;
    }

    private static async Task<List<T>> ListAsync<T>(IAsyncEnumerable<T> records)
    {
        List<T> list = [];
        await foreach (T record in records)
        {
            list.Add(record);
        }

        return list;
    }

    private static async Task<string> TextAsync<T>(IAsyncEnumerable<T> records)
    {
        StringBuilder text = new StringBuilder();
        await foreach (T record in records)
        {
            text.Append(Convert.ToString(record, CultureInfo.InvariantCulture)).Append('\n');
        }

        return text.ToString();
    }

    private static string Text(params object?[] values) =>
        string.Join(';', values.Select(v => v switch
        {
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            null => "null",
            _ => Convert.ToString(v, CultureInfo.InvariantCulture),
        }));
}

/// <summary>A city and a score.</summary>
[VortexRecord]
public partial record struct CityScore(string City, double Score);
