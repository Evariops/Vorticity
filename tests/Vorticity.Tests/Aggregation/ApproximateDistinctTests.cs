// Approximate distinct counts: exact while few, within a few standard errors past them, the same
// registers and estimate whatever the order of the values and merges and whatever the form a sketch
// passed through, and the same counts from a group by at every degree.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed partial class ApproximateDistinctTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Four standard errors of a sketch of 2^14 registers: 1.04 / 128 each.</summary>
    private const double Tolerance = 4 * 1.04 / 128;

    [Fact]
    public void ACountIsExactWhileFewAndCloseAfter()
    {
        CardinalitySketch few = new CardinalitySketch();
        for (int i = 0; i < 200; i++)
        {
            few.Add(Hash(i % 50));
        }

        Assert.True(few.IsExact);
        Assert.Equal(50, few.Estimate());

        foreach (int distinct in (int[])[100, 1_000, 10_000, 100_000, 1_000_000])
        {
            CardinalitySketch sketch = new CardinalitySketch();
            for (int i = 0; i < distinct; i++)
            {
                sketch.Add(Hash(i));
                sketch.Add(Hash(i));
            }

            double error = Math.Abs(sketch.Estimate() - distinct) / (double)distinct;
            Assert.True(error <= Tolerance, $"{distinct} distinct values counted {sketch.Estimate()}");
        }
    }

    /// <summary>
    /// Values cut into parts, merged in shuffled orders, each written and read back: the same words and
    /// the same estimate as one sketch fed every value, whether the parts stayed exact, sparse or dense.
    /// </summary>
    [Fact]
    public void MergesKeepTheSameRegistersWhateverTheirOrder()
    {
        Random random = new Random(5);
        foreach (int distinct in (int[])[40, 900, 50_000])
        {
            CardinalitySketch whole = new CardinalitySketch();
            List<int> values = [.. Enumerable.Range(0, distinct * 2).Select(i => i % distinct)];
            foreach (int value in values)
            {
                whole.Add(Hash(value));
            }

            for (int trial = 0; trial < 3; trial++)
            {
                List<CardinalitySketch> parts = [.. Enumerable.Range(0, 9).Select(_ => new CardinalitySketch())];
                foreach (int value in values)
                {
                    parts[random.Next(parts.Count)].Add(Hash(value));
                }

                CardinalitySketch merged = new CardinalitySketch();
                foreach (CardinalitySketch part in parts.OrderBy(_ => random.Next()))
                {
                    List<long> words = [];
                    part.Write(words.Add);
                    int at = 0;
                    merged.Merge(CardinalitySketch.Read(() => words[at++]));
                }

                Assert.Equal(Words(whole), Words(merged));
                Assert.Equal(whole.Estimate(), merged.Estimate());
            }
        }
    }

    /// <summary>A group by's approximate counts of a number and a text, close to the exact ones and the same at every degree.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(14)]
    public async Task AGroupsApproximateCountsAreCloseAndTheSameAtEveryDegree(int degree)
    {
        (Visit[] rows, string path) = await Fixture.Async;
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
        await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);

        List<KeyCounts> found = [];
        await foreach (KeyCounts row in file.Scan<Visit>().GroupBy(r => r.Site)
            .Select(g => (g.Key, g.ApproximateCountDistinct(r => r.User), g.ApproximateCountDistinct(r => r.Page), g.CountDistinct(r => r.User)))
            .As<KeyCounts>().ToRecordsAsync(Ct))
        {
            found.Add(row);
        }

        Dictionary<int, (HashSet<long> Users, HashSet<string> Pages)> exact = [];
        foreach (Visit row in rows)
        {
            if (!exact.TryGetValue(row.Site, out var sets))
            {
                sets = ([], []);
                exact[row.Site] = sets;
            }

            sets.Users.Add(row.User);
            sets.Pages.Add(row.Page);
        }

        Assert.Equal(exact.Count, found.Count);
        foreach (KeyCounts row in found)
        {
            (HashSet<long> users, HashSet<string> pages) = exact[row.Site];
            Assert.Equal(users.Count, row.Users);
            Assert.InRange(row.ApproximateUsers, users.Count * (1 - Tolerance), users.Count * (1 + Tolerance));
            Assert.InRange(row.ApproximatePages, pages.Count * (1 - Tolerance), pages.Count * (1 + Tolerance));
        }

        // Every degree gives the counts degree 1 gives.
        await using VortexSession one = VortexSession.Create(options => options.MaxDegreeOfParallelism = 1);
        await using VortexFile alone = await one.OpenAsync(path, cancellationToken: Ct);
        long all = await alone.Scan<Visit>().AggregateAsync(a => a.ApproximateCountDistinct(r => r.User), Ct);
        Assert.Equal(all, await file.Scan<Visit>().AggregateAsync(a => a.ApproximateCountDistinct(r => r.User), Ct));
        Assert.InRange(all, exact.Values.SelectMany(s => s.Users).Distinct().Count() * (1 - Tolerance), exact.Values.SelectMany(s => s.Users).Distinct().Count() * (1 + Tolerance));
    }

    private static ulong Hash(int value) => CardinalitySlot.Hash(BitConverter.GetBytes((long)value));

    private static List<long> Words(CardinalitySketch sketch)
    {
        List<long> words = [];
        sketch.Write(words.Add);
        return words;
    }

    private static class Fixture
    {
        internal static readonly Task<(Visit[] Rows, string Path)> Async = WriteAsync();

        private static async Task<(Visit[], string)> WriteAsync()
        {
            Random random = new Random(13);
            Visit[] rows = [.. Enumerable.Range(0, 300_000).Select(i => new Visit(random.Next(20), random.Next(i % 3 == 0 ? 50 : 40_000), $"/page/{random.Next(5_000)}"))];
            string path = Path.Combine(Path.GetTempPath(), $"vorticity-approximate-{Guid.NewGuid():N}.vortex");
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Visit>(path))
            {
                await writer.WriteAsync<Visit>(rows, default);
                await writer.CompleteAsync(default);
            }

            return (rows, path);
        }
    }

    [VortexRecord]
    public partial record struct Visit(int Site, long User, string Page);

    [VortexRecord]
    public partial record struct KeyCounts(int Site, long ApproximateUsers, long ApproximatePages, long Users);
}
