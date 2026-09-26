// The three byte-pattern predicates: StartsWith, Contains and Like.
//
// TWO ORACLES, BECAUSE THE TWO HALVES FAIL DIFFERENTLY. `Like` is a backtracking matcher and its
// failure mode is a wrong ANSWER on an awkward pattern, so it is compared against a naive recursive
// matcher — exponential, obviously correct, and unusable in production, which is exactly what an
// oracle is for. The pruning is a different claim entirely: `StartsWith(p)` becomes the range
// `x >= p AND x < succ(p)`, and its failure mode is a DROPPED ROW, so it is tested end to end with
// pruning on and off over a real file, the shape `ScanFilterTests` uses for every comparison.
//
// The adversarial patterns: empty, longer than the value, all 0xFF, non-ASCII,
// escapes, and `_` across a multi-byte code point, which matches one character of utf8 text and one
// byte of binary.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Compute;

public sealed class StringMatchTests
{
    /// <summary>{monotone, banded, strs=utf8, nulls, nans}: the file every filter test reads.</summary>
    private const string Mixed = "containers/zoned_many_zones_nulls";

    // ------------------------------------------------------------------------------ the matcher

    public static TheoryData<string, string, bool> LikeCases() => new()
    {
        { "", "", true },
        { "", "%", true },
        { "", "_", false },
        { "abc", "abc", true },
        { "abc", "ab", false },
        { "abc", "abcd", false },
        { "abc", "a%", true },
        { "abc", "%c", true },
        { "abc", "%b%", true },
        { "abc", "a_c", true },
        { "abc", "a_", false },
        { "abc", "%", true },
        { "abc", "%%%", true },
        { "abc", "_%_", true },
        { "aaa", "%a%a%a%", true },
        { "aaa", "%a%a%a%a%", false },
        { "a%b", @"a\%b", true },
        { "axb", @"a\%b", false },
        { "a_b", @"a\_b", true },
        { "axb", @"a\_b", false },
        { @"a\b", @"a\\b", true },
        { "abc", @"abc\", false },
        { "ab", "a%b%", true },
        { "banana", "%an%na", true },
        { "banana", "b%n%n%a", true },
        { "banana", "b%n%n%n%a", false },
    };

    [Theory]
    [MemberData(nameof(LikeCases))]
    public void LikeMatchesTheNaiveMatcher(string value, string pattern, bool expected)
    {
        byte[] v = Encoding.UTF8.GetBytes(value);
        byte[] p = Encoding.UTF8.GetBytes(pattern);

        Assert.Equal(expected, BytePattern.Like(v, p, (byte)'\\'));
        Assert.Equal(expected, Naive(v, 0, p, 0, (byte)'\\'));
    }

    /// <summary>
    /// Generated values and patterns, against the naive matcher, including the shapes a backtracker
    /// gets wrong.
    /// </summary>
    [Fact]
    public void LikeMatchesTheNaiveMatcherOnGeneratedPatterns()
    {
        string[] alphabet = ["a", "b", "%", "_", "\\", "é"];
        List<byte[]> values = [];
        List<byte[]> patterns = [];

        // Every string of up to three symbols, which is small enough to enumerate and wide enough to
        // contain every adjacency a backtracker can trip on.
        Build(alphabet, 3, values);
        Build(alphabet, 3, patterns);

        foreach (byte[] value in values)
        {
            foreach (byte[] pattern in patterns)
            {
                bool naive = Naive(value, 0, pattern, 0, (byte)'\\');
                bool actual = BytePattern.Like(value, pattern, (byte)'\\');
                Assert.True(
                    naive == actual,
                    $"LIKE '{Show(pattern)}' over '{Show(value)}': matcher says {actual}, " +
                    $"the naive reference says {naive}");
            }
        }
    }

    [Fact]
    public void UnderscoreMatchesOneByteOfAMultiByteCodePoint()
    {
        // "é" is two bytes in UTF-8, so over binary `_` matches half of it and `__` matches it whole.
        byte[] value = Encoding.UTF8.GetBytes("é");
        Assert.Equal(2, value.Length);
        Assert.False(BytePattern.Like(value, "_"u8, (byte)'\\'));
        Assert.True(BytePattern.Like(value, "__"u8, (byte)'\\'));
    }

    [Theory]
    [InlineData("é", "_", true)]
    [InlineData("é", "__", false)]
    [InlineData("René", "Ren_", true)]
    [InlineData("Rene", "Ren_", true)]
    [InlineData("Renée", "Ren_", false)]
    [InlineData("Renée", "Ren__", true)]
    [InlineData("€x", "_x", true)]
    [InlineData("a𝄞b", "a_b", true)]
    [InlineData("a𝄞b", "a%_", true)]
    [InlineData("éa", "%_a", true)]
    [InlineData("é", "%__", false)]
    public void UnderscoreMatchesOneCharacterOfText(string value, string pattern, bool expected)
    {
        byte[] v = Encoding.UTF8.GetBytes(value);
        byte[] p = Encoding.UTF8.GetBytes(pattern);
        Assert.Equal(expected, BytePattern.LikeText(v, p, (byte)'\\'));
        Assert.Equal(expected, NaiveText(v, p));
    }

    /// <summary>
    /// Generated utf8 values and patterns, with characters of one to four bytes, against the naive
    /// matcher over characters; and without an unescaped <c>_</c>, the byte matcher agrees too.
    /// </summary>
    [Fact]
    public void LikeTextMatchesTheNaiveMatcherOnGeneratedPatterns()
    {
        string[] alphabet = ["a", "%", "_", "\\", "é", "€", "𝄞"];
        List<byte[]> values = [];
        List<byte[]> patterns = [];
        Build(alphabet, 3, values);
        Build(alphabet, 3, patterns);

        foreach (byte[] value in values)
        {
            foreach (byte[] pattern in patterns)
            {
                bool naive = NaiveText(value, pattern);
                bool actual = BytePattern.LikeText(value, pattern, (byte)'\\');
                Assert.True(
                    naive == actual,
                    $"LIKE '{Show(pattern)}' over '{Show(value)}': text matcher says {actual}, the naive reference says {naive}");
                if (!BytePattern.HasUnescapedOne(pattern, (byte)'\\'))
                {
                    Assert.Equal(naive, BytePattern.Like(value, pattern, (byte)'\\'));
                }
            }
        }
    }

    /// <summary>
    /// A pattern without <c>_</c>, read once into its segments, answers every generated value as the
    /// naive matcher does, and one with a <c>_</c> is left to the backtracking matcher.
    /// </summary>
    [Fact]
    public void APlannedLikeMatchesTheNaiveMatcherOnGeneratedPatterns()
    {
        // Four symbols deep: two segments with a wildcard between, next to an escape, is the
        // smallest shape a plan can split wrongly.
        string[] alphabet = ["a", "b", "%", "_", "\\", "é"];
        List<byte[]> values = [];
        List<byte[]> patterns = [];
        Build(alphabet, 4, values);
        Build(alphabet, 4, patterns);

        foreach (byte[] pattern in patterns)
        {
            byte[] literals = new byte[pattern.Length];
            int[] ends = new int[(pattern.Length / 2) + 1];
            bool planned = BytePattern.TryPlan(pattern, (byte)'\\', literals, ends, out LikePlan plan);
            Assert.Equal(!BytePattern.HasUnescapedOne(pattern, (byte)'\\'), planned);
            if (!planned)
            {
                continue;
            }

            foreach (byte[] value in values)
            {
                bool naive = Naive(value, 0, pattern, 0, (byte)'\\');
                if (plan.Holds(value) != naive)
                {
                    Assert.Fail(
                        $"LIKE '{Show(pattern)}' over '{Show(value)}': the plan says {!naive}, " +
                        $"the naive reference says {naive}");
                }
            }
        }
    }

    /// <summary>
    /// The kernel answers a column as the backtracking matcher answers each row, for patterns read
    /// onto the stack and for one long enough to be read into rented buffers.
    /// </summary>
    [Fact]
    public void TheKernelAnswersAColumnAsTheMatcherAnswersEachRow()
    {
        Random random = new Random(26);
        string[] values = new string[500];
        for (int i = 0; i < values.Length; i++)
        {
            char[] text = new char[random.Next(0, 400)];
            for (int c = 0; c < text.Length; c++)
            {
                text[c] = (char)('a' + random.Next(3));
            }

            values[i] = new string(text);
        }

        string run = new string('a', 150) + "b" + new string('c', 150);
        values[7] = "xx" + run + "yy";
        string[] patterns =
        [
            "%ab%", "a%", "%ba", "%a%b%c%", "abc", "", "%", @"%a\%b%", "%" + run + "%", run + "%",
            "%" + run, "x%" + run + "%y", "a%_%",
        ];

        CanonicalArena arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        int column = Views(arena, types.Utf8(Nullability.NonNullable), values);
        byte[] states = new byte[values.Length];
        foreach (string pattern in patterns)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(pattern);
            ComparisonKernels.StringMatch(
                arena, column, StringMatchOp.Like, FilterLiteral.From(bytes), (byte)'\\', states);
            for (int row = 0; row < values.Length; row++)
            {
                bool expected = BytePattern.LikeText(Encoding.UTF8.GetBytes(values[row]), bytes, (byte)'\\');
                Assert.True(
                    states[row] == (expected ? Trilean.True : Trilean.False),
                    $"LIKE '{pattern}' over row {row}: {states[row]}, the matcher says {expected}");
            }
        }

        arena.Reset();
    }

    /// <summary>A varbinview column of <paramref name="values"/>, every row valid.</summary>
    private static int Views(CanonicalArena arena, DType dtype, string[] values)
    {
        byte[][] encoded = Array.ConvertAll(values, Encoding.UTF8.GetBytes);
        int total = 0;
        foreach (byte[] value in encoded)
        {
            total += value.Length;
        }

        VortexBuffer heap = arena.Allocate(Math.Max(total, 1), 1, out Span<byte> heapBytes);
        VortexBuffer views = arena.Allocate(values.Length * 16, 16, out Span<byte> viewBytes);
        int offset = 0;
        for (int row = 0; row < encoded.Length; row++)
        {
            byte[] value = encoded[row];
            Span<byte> view = viewBytes.Slice(row * 16, 16);
            view.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(view, value.Length);
            if (value.Length <= 12)
            {
                value.CopyTo(view[4..]);
            }
            else
            {
                value.AsSpan(0, 4).CopyTo(view.Slice(4, 4));
                BinaryPrimitives.WriteInt32LittleEndian(view[12..], offset);
            }

            value.CopyTo(heapBytes[offset..]);
            offset += value.Length;
        }

        return arena.AddVarBinView(dtype, values.Length, Validity.NonNullable, views, [heap]);
    }

    [Fact]
    public async Task UnderscoreTakesACharacterOfTextAndAByteOfBinary()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string[] names = ["René", "Rene", "Renée", "é", "ab"];
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-like-{Guid.NewGuid():N}.vortex");
        try
        {
            VortexSchema schema = [("text", VortexType.Utf8), ("bytes", VortexType.Binary)];
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter(path, schema))
            {
                ColumnsBuilder builder = writer.Builder();
                foreach (string name in names)
                {
                    builder.Column<string>(0).Append(name);
                    builder.Column<ReadOnlyMemory<byte>>(1).Append(Encoding.UTF8.GetBytes(name));
                }

                await writer.WriteAsync(builder, ct);
                await writer.CompleteAsync(ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            Assert.Equal(["René", "Rene"], await MatchingAsync(file, "text", "Ren_", ct));
            Assert.Equal(["é"], await MatchingAsync(file, "text", "_", ct));
            Assert.Equal(["ab"], await MatchingAsync(file, "text", "__", ct));
            Assert.Equal(["Rene"], await MatchingAsync(file, "bytes", "Ren_", ct));
            Assert.Equal(["é", "ab"], await MatchingAsync(file, "bytes", "__", ct));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>The rows of <paramref name="file"/> whose <paramref name="column"/> is <c>like</c> <paramref name="pattern"/>, as text.</summary>
    private static async Task<List<string>> MatchingAsync(VortexFile file, string column, string pattern, CancellationToken ct)
    {
        VortexExpr filter = Expr.Like(Expr.Field(column), FilterLiteral.From(Encoding.UTF8.GetBytes(pattern)));
        List<string> rows = [];
        await foreach (RecordBatch batch in file.ScanBuilder().Project("text").Where(filter).ExecuteAsync().WithCancellation(ct))
        {
            for (int row = 0; row < batch.RowCount; row++)
            {
                rows.Add(batch.Column("text"u8).AsBinary().GetString(row)!);
            }
        }

        return rows;
    }

    [Fact]
    public void StartsWithAndContainsTakeTheEmptyPattern()
    {
        Assert.True(BytePattern.StartsWith("abc"u8, default));
        Assert.True(BytePattern.Contains("abc"u8, default));
        Assert.True(BytePattern.StartsWith(default, default));
        Assert.False(BytePattern.StartsWith("ab"u8, "abc"u8));
        Assert.False(BytePattern.Contains("ab"u8, "abc"u8));
    }

    // --------------------------------------------------------------------------- succ and prefix

    /// <summary>
    /// The successor table, which is what turns <c>StartsWith</c> into a range a zone map prunes.
    /// </summary>
    [Fact]
    public void SuccessorIsTheSmallestStringPastEveryValueWithThePrefix()
    {
        Assert.Equal("ab", Successor("aa"));
        Assert.Equal("b", Successor("a"));

        // BYTES, NOT CODE POINTS, and the difference is worth a case of its own: U+00FF is 0xC3 0xBF
        // in UTF-8 and has an ordinary successor, while the BYTE 0xFF is the one with none. A prefix
        // ending in 0xFF drops it and increments what is left.
        Assert.Equal<byte[]>([0x61, 0xC4], SuccessorOrNull([0x61, 0xC3])!);
        Assert.Equal<byte[]>([0x62], SuccessorOrNull([0x61, 0xFF])!);
        Assert.Equal<byte[]>([0x62], SuccessorOrNull([0x61, 0xFF, 0xFF])!);

        // No successor at all: nothing sorts above every 0xFF string, and nothing sorts above every
        // string whatever. Both give an empty answer and the caller keeps only the lower bound.
        Assert.Null(SuccessorOrNull([0xFF]));
        Assert.Null(SuccessorOrNull([0xFF, 0xFF]));
        Assert.Null(SuccessorOrNull([]));
    }

    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("abc%", "abc")]
    [InlineData("abc_", "abc")]
    [InlineData("%abc", "")]
    [InlineData("_abc", "")]
    [InlineData(@"a\%b%", "a%b")]
    [InlineData("", "")]
    public void TheLeadingLiteralIsWhatAPatternClaimsAboutItsStart(string pattern, string expected)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(pattern);
        Span<byte> buffer = new byte[Math.Max(bytes.Length, 1)];
        int taken = BytePattern.LeadingLiteral(bytes, (byte)'\\', buffer);
        Assert.Equal(expected, Encoding.UTF8.GetString(buffer[..taken]));
    }

    // ---------------------------------------------------------------------------- end to end

    /// <summary>
    /// Every predicate returns the rows the same predicate in plain C# selects, pruning on and off.
    /// </summary>
    /// <remarks>
    /// THE TWO RUNS ARE THE POINT. Pruning rewrites `StartsWith` into a range over the zone map's
    /// string bounds, and the one failure that rewrite can have is dropping a row a full scan would
    /// return, and pruning must never change a scan's answer. Comparing the pruned run against the
    /// unpruned one is the only test that sees it.
    /// </remarks>
    [Theory]
    [InlineData(StringMatchOp.StartsWith, "a")]
    [InlineData(StringMatchOp.StartsWith, "")]
    [InlineData(StringMatchOp.StartsWith, "zzzzz")]
    [InlineData(StringMatchOp.Contains, "a")]
    [InlineData(StringMatchOp.Contains, "")]
    [InlineData(StringMatchOp.Like, "a%")]
    [InlineData(StringMatchOp.Like, "%a%")]
    [InlineData(StringMatchOp.Like, "_a%")]
    [InlineData(StringMatchOp.Like, "%")]
    internal async Task EveryPredicateAgreesWithTheSameOneInCSharp(StringMatchOp op, string pattern)
    {
        List<string?> all = await ReadStrings(Mixed, "strs", filter: null, prune: true);

        byte[] bytes = Encoding.UTF8.GetBytes(pattern);
        VortexExpr filter = op switch
        {
            StringMatchOp.StartsWith => Expr.StartsWith(Expr.Field("strs"), FilterLiteral.From(bytes)),
            StringMatchOp.Contains => Expr.Contains(Expr.Field("strs"), FilterLiteral.From(bytes)),
            _ => Expr.Like(Expr.Field("strs"), FilterLiteral.From(bytes)),
        };

        List<string?> expected = [];
        foreach (string? value in all)
        {
            if (value is null)
            {
                continue;
            }

            byte[] v = Encoding.UTF8.GetBytes(value);
            bool holds = op switch
            {
                StringMatchOp.StartsWith => BytePattern.StartsWith(v, bytes),
                StringMatchOp.Contains => BytePattern.Contains(v, bytes),
                _ => BytePattern.LikeText(v, bytes, (byte)'\\'),
            };

            if (holds)
            {
                expected.Add(value);
            }
        }

        Assert.Equal(expected, await ReadStrings(Mixed, "strs", filter, prune: true));
        Assert.Equal(expected, await ReadStrings(Mixed, "strs", filter, prune: false));
    }

    /// <summary>A pattern no row begins with returns nothing, and pruning does not change that.</summary>
    [Fact]
    public async Task APrefixNoRowHasSelectsNothingWithAndWithoutPruning()
    {
        VortexExpr filter = Expr.StartsWith(
            Expr.Field("strs"), FilterLiteral.From("�no-row-starts-with-this"u8.ToArray()));

        Assert.Empty(await ReadStrings(Mixed, "strs", filter, prune: true));
        Assert.Empty(await ReadStrings(Mixed, "strs", filter, prune: false));
    }

    /// <summary>A pattern that is not bytes is refused where it is written, not where it runs.</summary>
    [Fact]
    public void ANonBytesPatternIsRefusedAtConstruction()
    {
        Assert.Throws<ArgumentException>(
            () => Expr.StartsWith(Expr.Field("strs"), FilterLiteral.From(3L)));
        Assert.Throws<ArgumentException>(
            () => Expr.Like(Expr.Field("strs"), FilterLiteral.From(1.5)));
    }

    // --------------------------------------------------------------------------------- the oracle

    /// <summary>
    /// SQL <c>LIKE</c>, written the obvious way: try every split at a <c>%</c>. Exponential, and
    /// therefore useless for anything but saying what the answer is.
    /// </summary>
    private static bool Naive(
        ReadOnlySpan<byte> value, int v, ReadOnlySpan<byte> pattern, int p, byte escape)
    {
        if (p == pattern.Length)
        {
            return v == value.Length;
        }

        byte token = pattern[p];
        if (token == (byte)'%')
        {
            for (int take = v; take <= value.Length; take++)
            {
                if (Naive(value, take, pattern, p + 1, escape))
                {
                    return true;
                }
            }

            return false;
        }

        if (v == value.Length)
        {
            return false;
        }

        if (token == escape && p + 1 < pattern.Length)
        {
            return pattern[p + 1] == value[v] && Naive(value, v + 1, pattern, p + 2, escape);
        }

        if (token == (byte)'_')
        {
            return Naive(value, v + 1, pattern, p + 1, escape);
        }

        return token == value[v] && Naive(value, v + 1, pattern, p + 1, escape);
    }

    /// <summary>
    /// SQL <c>LIKE</c> over the characters of UTF-8 text, written the obvious way on decoded runes,
    /// with <c>\</c> as the escape.
    /// </summary>
    private static bool NaiveText(ReadOnlySpan<byte> value, ReadOnlySpan<byte> pattern) =>
        NaiveText(Runes(value), 0, Runes(pattern), 0);

    private static bool NaiveText(List<Rune> value, int v, List<Rune> pattern, int p)
    {
        if (p == pattern.Count)
        {
            return v == value.Count;
        }

        Rune token = pattern[p];
        if (token.Value == '%')
        {
            for (int take = v; take <= value.Count; take++)
            {
                if (NaiveText(value, take, pattern, p + 1))
                {
                    return true;
                }
            }

            return false;
        }

        if (v == value.Count)
        {
            return false;
        }

        if (token.Value == '\\' && p + 1 < pattern.Count)
        {
            return pattern[p + 1] == value[v] && NaiveText(value, v + 1, pattern, p + 2);
        }

        if (token.Value == '_')
        {
            return NaiveText(value, v + 1, pattern, p + 1);
        }

        return token == value[v] && NaiveText(value, v + 1, pattern, p + 1);
    }

    private static List<Rune> Runes(ReadOnlySpan<byte> utf8)
    {
        List<Rune> runes = [];
        while (!utf8.IsEmpty)
        {
            Rune.DecodeFromUtf8(utf8, out Rune rune, out int consumed);
            runes.Add(rune);
            utf8 = utf8[consumed..];
        }

        return runes;
    }

    private static void Build(string[] alphabet, int depth, List<byte[]> into)
    {
        List<string> current = [string.Empty];
        into.Add([]);
        for (int i = 0; i < depth; i++)
        {
            List<string> next = [];
            foreach (string prefix in current)
            {
                foreach (string symbol in alphabet)
                {
                    string grown = prefix + symbol;
                    next.Add(grown);
                    into.Add(Encoding.UTF8.GetBytes(grown));
                }
            }

            current = next;
        }
    }

    private static string Show(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    private static string Successor(string prefix)
    {
        byte[]? result = SuccessorOrNull(Encoding.UTF8.GetBytes(prefix));
        Assert.NotNull(result);
        return Encoding.UTF8.GetString(result);
    }

    private static byte[]? SuccessorOrNull(byte[] prefix)
    {
        Span<byte> buffer = new byte[Math.Max(prefix.Length, 1)];
        int length = BytePattern.Successor(prefix, buffer);
        return length == 0 ? null : buffer[..length].ToArray();
    }

    private static async Task<List<string?>> ReadStrings(
        string id, string column, VortexExpr? filter, bool prune)
    {
        Decoders.EnsureRegistered();
        List<string?> values = [];
        byte[] name = Encoding.UTF8.GetBytes(column);

        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path(id), CancellationToken.None);
        ScanBuilder builder = file.ScanBuilder().Project(column).WithPruning(prune);
        if (filter is not null)
        {
            builder = builder.Where(filter);
        }

        await foreach (RecordBatch batch in builder.ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            for (int row = 0; row < batch.RowCount; row++)
            {
                values.Add(batch.Column(name).AsBinary().GetString(row));
            }
        }

        return values;
    }
}
