using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Expressions;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// A text key of short values groups as words (<see cref="ShortTextKeys"/>): the bytes come back exact at
/// every length, a longer value turns a lane's table into bytes with every group keeping its number, many
/// words move to slots of a hash and a group, and lanes of words and lanes of bytes hash, cut, spill and
/// merge alike.
/// </summary>
public sealed partial class ShortTextKeysTests
{
    // Every length from 0 to 16 bytes, two-byte characters among them, beside null.
    private static readonly string?[] Values =
    [
        "", "a", "ab", "abc", "abcd", "abcde", "abcdef", "abcdefg", "abcdefgh", "abcdefghi", "abcdefghij",
        "abcdefghijk", "abcdefghijkl", "abcdefghijklm", "abcdefghijklmn", "abcdefghijklmno", "abcdefghijklmnop",
        "é", "éé", "ééé", "éééééé", "\0", "a\0", "ab\0", null,
    ];

    [Fact]
    public void WordsAndBytesHashAndCutAKeyAlike()
    {
        string?[] shorts = [.. Values.Where(v => v is null || Encoding.UTF8.GetByteCount(v) <= 12)];
        ShortTextKeys words = Keys(shorts);
        BytesKeys bytes = BytesOf(shorts);
        Assert.True(words.Words);

        ulong[] wordHashes = new ulong[words.Count];
        ulong[] byteHashes = new ulong[bytes.Count];
        words.Hashes(wordHashes);
        bytes.Hashes(byteHashes);
        Assert.Equal(byteHashes, wordHashes);

        byte[] wordParts = new byte[words.Count];
        byte[] byteParts = new byte[bytes.Count];
        words.Parts(MergeHash.Seed, 60, wordParts);
        bytes.Parts(MergeHash.Seed, 60, byteParts);
        Assert.Equal(byteParts, wordParts);
        words.Parts(12345, 58, wordParts);
        bytes.Parts(12345, 58, byteParts);
        Assert.Equal(byteParts, wordParts);
    }

    [Fact]
    public void ALongValueTurnsWordsIntoBytesEachGroupKeepingItsNumber()
    {
        string?[] shorts = [.. Values.Where(v => v is null || Encoding.UTF8.GetByteCount(v) <= 12)];
        ShortTextKeys keys = Keys(shorts);
        Assert.True(keys.Words);

        // Read back with a long value at the end: the table turns, and every group keeps its key.
        AddKeys(keys, ["a value longer than twelve bytes", "abc"]);
        Assert.False(keys.Words);
        Func<int, string> key = keys.Reader<string>(0);
        for (int g = 0; g < shorts.Length; g++)
        {
            Assert.Equal(shorts[g], key(g));
        }

        Assert.Equal("a value longer than twelve bytes", key(shorts.Length));
        Assert.Equal(shorts.Length + 1, keys.Count);
    }

    [Fact]
    public void WordsWrittenToARunComeBackExactAsWordsOrAsBytes()
    {
        string?[] shorts = [.. Values.Where(v => v is null || Encoding.UTF8.GetByteCount(v) <= 12)];
        ShortTextKeys words = Keys(shorts);
        int[] groups = [.. Enumerable.Range(0, words.Count).Reverse()];
        SpillBuffer run = new SpillBuffer(memory: null);
        words.WriteKeys(groups, run);

        ShortTextKeys back = new ShortTextKeys(Shape(), sorted: false);
        int[] read = new int[groups.Length];
        SpillReader reader = new SpillReader(run.Written.Span);
        back.ReadKeys(ref reader, read);
        Assert.True(back.Words);
        Func<int, string> original = words.Reader<string>(0);
        Func<int, string> again = back.Reader<string>(0);
        for (int i = 0; i < groups.Length; i++)
        {
            Assert.Equal(original(groups[i]), again(read[i]));
        }

        // The same run read by a table of bytes, as a lane of bytes reads a lane of words' run.
        BytesKeys bytes = new BytesKeys(Shape(), sorted: false);
        SpillReader asBytes = new SpillReader(run.Written.Span);
        bytes.ReadKeys(ref asBytes, read);
        Func<int, string> asText = bytes.Reader<string>(0);
        for (int i = 0; i < groups.Length; i++)
        {
            Assert.Equal(original(groups[i]), asText(read[i]));
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void LanesOfWordsAndOfBytesMergeIntoOneAnother(bool fromWords, bool intoWords)
    {
        string?[] shorts = [.. Values.Where(v => v is null || Encoding.UTF8.GetByteCount(v) <= 12)];
        ShortTextKeys from = Keys(shorts);
        ShortTextKeys into = Keys(["abc", "zz", null]);
        if (!fromWords)
        {
            AddKeys(from, ["a value longer than twelve bytes"]);
        }

        if (!intoWords)
        {
            AddKeys(into, ["another value longer than twelve"]);
        }

        Assert.Equal(fromWords, from.Words);
        Assert.Equal(intoWords, into.Words);
        int[] map = new int[from.Count];
        from.MergeInto(into, map);
        Func<int, string> source = from.Reader<string>(0);
        Func<int, string> target = into.Reader<string>(0);
        for (int g = 0; g < from.Count; g++)
        {
            Assert.Equal(source(g), target(map[g]));
        }

        Assert.Equal(map.Distinct().Count(), from.Count);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(1, false)]
    [InlineData(4, false)]
    public async Task EveryLengthFromNoneToSixteenBytesComesBackExact(int degree, bool canonical)
    {
        // Short values in the first rows alone, long ones from the middle: lanes of words and lanes turned
        // into bytes merge; ordered by the key, the groups come in the order of the bytes, null last.
        Row[] rows = new Row[60_000];
        for (int row = 0; row < rows.Length; row++)
        {
            string?[] pool = row < rows.Length / 2 ? [.. Values.Where(v => v is null || Encoding.UTF8.GetByteCount(v) <= 12)] : Values;
            rows[row] = new Row(pool[(row * 7) % pool.Length], row % 10);
        }

        await ExactAsync(rows, degree, canonical);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ManyShortKeysMoveToCompactSlotsInTheMiddleOfABlock(int degree)
    {
        Row[] rows = new Row[200_000];
        for (int row = 0; row < rows.Length; row++)
        {
            rows[row] = new Row($"id{(row * 7919) % 20_000:D8}", row % 10);
        }

        await ExactAsync(rows, degree, canonical: true);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    public async Task NullsInEveryChunkAndALongValuePastTheFirstChunksGroupExact(int degree, bool longValue)
    {
        // Words are made and found 4 096 rows at a time: a null in every chunk, read at its row's place in
        // the block; and a value too long for a word past two chunks, which turns the block, its first
        // chunks numbered already, into bytes.
        Row[] rows = new Row[200_000];
        for (int row = 0; row < rows.Length; row++)
        {
            string? text = row % 997 == 0 ? null : longValue && row == 10_000 ? "a value longer than twelve bytes" : $"id{(row * 7919) % 20_000:D8}";
            rows[row] = new Row(text, row % 10);
        }

        await ExactAsync(rows, degree, canonical: true);
    }

    [Fact]
    public async Task FewShortKeysInDictionariesAndRunsGroupAsWords()
    {
        Row[] rows = new Row[100_000];
        for (int row = 0; row < rows.Length; row++)
        {
            rows[row] = new Row(Values[(row / 1000) % Values.Length], row % 10);
        }

        await ExactAsync(rows, degree: 2, canonical: false);
    }

    /// <summary>The count and the sum by the text, and the keys in their order, against LINQ's.</summary>
    private static async Task ExactAsync(Row[] rows, int degree, bool canonical)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "short-text-keys", $"rows-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        VortexWriteOptions write = canonical ? new VortexWriteOptions { Compression = CompressionProfile.None } : new VortexWriteOptions();
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path, write))
        {
            await writer.WriteAsync<Row>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            List<TextTotal> groups = await file.Scan<Row>().GroupBy(r => r.Text).Select(g => (g.Key, g.Count(), g.Sum(r => r.Value))).As<TextTotal>().ToRecordsAsync(Ct).ToListAsync(Ct);
            Dictionary<string, (long Count, long Total)> expected = rows
                .GroupBy(r => r.Text ?? "\u0001null")
                .ToDictionary(g => g.Key, g => ((long)g.Count(), g.Sum(r => r.Value)));
            Assert.Equal(expected.Count, groups.Count);
            foreach (TextTotal group in groups)
            {
                Assert.Equal(expected[group.Text ?? "\u0001null"], (group.Count, group.Total));
            }

            List<string?> ordered = await file.Scan<Row>().GroupBy(r => r.Text).OrderBy(g => g.Key).Select(g => g.Key).ToListAsync(Ct);
            List<string?> sorted = [.. rows.Select(r => r.Text).Where(t => t is not null).Distinct().Order(StringComparer.Ordinal)];
            if (rows.Any(r => r.Text is null))
            {
                sorted.Add(null);
            }

            Assert.Equal(sorted, ordered);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>Keys of words holding <paramref name="values"/>, group i value i, read in as a run is.</summary>
    private static ShortTextKeys Keys(string?[] values)
    {
        ShortTextKeys keys = new ShortTextKeys(Shape(), sorted: false);
        AddKeys(keys, values);
        return keys;
    }

    private static BytesKeys BytesOf(string?[] values)
    {
        BytesKeys keys = new BytesKeys(Shape(), sorted: false);
        SpillBuffer run = Run(values);
        SpillReader reader = new SpillReader(run.Written.Span);
        keys.ReadKeys(ref reader, new int[values.Length]);
        return keys;
    }

    private static void AddKeys(GroupKeys keys, string?[] values)
    {
        SpillBuffer run = Run(values);
        SpillReader reader = new SpillReader(run.Written.Span);
        keys.ReadKeys(ref reader, new int[values.Length]);
    }

    /// <summary>A run of <paramref name="values"/>, as a table of bytes writes one: the null's place, then each key's hash and bytes.</summary>
    private static SpillBuffer Run(string?[] values)
    {
        SpillBuffer run = new SpillBuffer(memory: null);
        run.Write(Array.IndexOf(values, null));
        foreach (string? value in values)
        {
            if (value is not null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                run.Write(MergeHash.Of(bytes, MergeHash.Seed));
                run.WriteBytes(bytes);
            }
        }

        return run;
    }

    private static ColumnShape Shape() => new ColumnShape(new ColumnSym(Expr.Field("k"), VortexType.Utf8, null, null, -1, []));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Row(string? Text, long Value);

    [VortexRecord]
    public partial record struct TextTotal(string? Text, long Count, long Total);
}
