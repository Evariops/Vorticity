// The token index of log lines (spec G, 6; G5): token postings on a text column name the granules each word lies in, so
// that a search of a word reads them alone; HasToken, and a Contains a word bounded by spaces, answer as a scan of every
// line does.
//
// THE ORACLE IS LINQ: the rows a search keeps, with the index or without, are those a word test over each line keeps; with
// the index, a rare word decodes a fraction of the blocks a plain dataset decodes.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetTokenPostingsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Words = ["GET", "POST", "user", "session", "timeout", "retry", "ok", "error", "cache", "miss", "hit", "db", "query", "slow", "fast", "id"];

    /// <summary>
    /// Six appends of 20 000 lines from a vocabulary of sixteen words, a rare word in one line of 40 000 and a longer word
    /// that holds it in one of 2 500, merged: a token search, a bounded Contains, a frequent word and an absent one keep
    /// LINQ's rows with the index and without; with it, the rare word decodes a quarter of the blocks or fewer.
    /// </summary>
    [Fact]
    public async Task ASearchOfAWordReadsTheGranulesItLiesIn()
    {
        Dictionary<bool, long> rare = [];
        foreach (bool indexed in new[] { false, true })
        {
            Random random = new Random(29);
            await using MemoryObjectStore store = new MemoryObjectStore();
            await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Line.Schema, new DatasetOptions
            {
                Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Line.Service))], ObjectBytes = 1 << 20, MergeFanout = 4, CalmAfter = null },
                ContentsObjectBytes = 0,
                Write = indexed ? new VortexWriteOptions { Indexes = IndexPolicy.None.TokenPostings(nameof(Line.Body)) } : VortexWriteOptions.Default,
            }, Ct);
            List<Line> all = [];
            long sequence = 0;
            for (int a = 0; a < 6; a++)
            {
                Line[] lines = [.. Enumerable.Range(0, 20_000).Select(_ => new Line($"svc-{random.Next(0, 20):D2}", sequence++, Body(random)))];
                all.AddRange(lines);
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Line>(lines, Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
            {
            }

            (string Name, Func<Line, bool> Keep, Func<Scan<Line>, Scan<Line>> Filter)[] cases =
            [
                ("has token deadlock", static r => HasToken(r.Body, "deadlock"), static s => s.Where(r => r.Body.HasToken("deadlock"))),
                ("contains ' deadlock '", static r => r.Body.Contains(" deadlock ", StringComparison.Ordinal), static s => s.Where(r => r.Body.Contains(" deadlock "))),
                ("has token timeout", static r => HasToken(r.Body, "timeout"), static s => s.Where(r => r.Body.HasToken("timeout"))),
                ("has token zebra", static r => HasToken(r.Body, "zebra"), static s => s.Where(r => r.Body.HasToken("zebra"))),
            ];
            foreach ((string name, Func<Line, bool> keep, Func<Scan<Line>, Scan<Line>> filter) in cases)
            {
                Projection<long> rows = filter(dataset.Scan<Line>()).Select(r => r.Sequence);
                Assert.Equal(all.Where(keep).Select(static r => r.Sequence).Order(), (await rows.ToListAsync(Ct)).Order());
                Console.WriteLine($"TOKENS {(indexed ? "indexed" : "plain")} {name}: {rows.Metrics.BlocksDecoded} blocks decoded, {rows.Metrics.BlocksPruned} pruned");
                if (name == "has token deadlock")
                {
                    rare[indexed] = rows.Metrics.BlocksDecoded;
                }
            }
        }

        Assert.True(rare[true] * 4 <= rare[false], $"{rare[true]} blocks decoded with the index, {rare[false]} without");
    }

    /// <summary>A line of four to eleven words of the vocabulary, joined by spaces or commas; the rare word in some.</summary>
    private static string Body(Random random)
    {
        string[] line = new string[random.Next(4, 12)];
        for (int w = 0; w < line.Length; w++)
        {
            line[w] = Words[random.Next(Words.Length)];
        }

        if (random.Next(40_000) == 0)
        {
            line[random.Next(line.Length)] = "deadlock";
        }
        else if (random.Next(2_500) == 0)
        {
            line[random.Next(line.Length)] = "deadlocked";
        }

        return string.Join(random.Next(3) == 0 ? ", " : " ", line);
    }

    /// <summary>Whether <paramref name="text"/> holds <paramref name="token"/> as a whole word, between characters that are not letters or digits.</summary>
    private static bool HasToken(string text, string token)
    {
        for (int at = text.IndexOf(token, StringComparison.Ordinal); at >= 0; at = text.IndexOf(token, at + 1, StringComparison.Ordinal))
        {
            int end = at + token.Length;
            if ((at == 0 || !char.IsAsciiLetterOrDigit(text[at - 1])) && (end == text.Length || !char.IsAsciiLetterOrDigit(text[end])))
            {
                return true;
            }
        }

        return false;
    }

    [VortexRecord]
    public partial record struct Line(string Service, long Sequence, string Body);
}
