// OnPair's plan, checked as a reader would check it: every row spelt back exactly by its codes, and
// the dictionary the reference's reader validates before it reads a token -- the 256 single bytes,
// offsets strictly increasing from zero, no token past sixteen bytes, sixteen readable bytes past the
// last token's start -- on text that repeats, text that never does, and the edges between.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class OnPairPlanTests
{
    public static TheoryData<string> Corpora() => ["urls", "paths", "short", "diverse", "edges"];

    [Theory]
    [MemberData(nameof(Corpora))]
    public void EveryRowIsSpeltBackByItsCodes(string corpus)
    {
        string?[] values = Build(corpus);
        CanonicalArena arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        int node = Strings(arena, types, values);

        OnPairPlan? plan = OnPairPlan.TryBuild(arena, node, long.MaxValue);
        Assert.NotNull(plan);
        try
        {
            CheckDictionary(plan);
            Assert.Equal(values.Length, plan.Rows);
            ReadOnlySpan<int> offsets = plan.DictionaryOffsets;
            for (int row = 0; row < values.Length; row++)
            {
                byte[] expected = values[row] is { } text ? Encoding.UTF8.GetBytes(text) : [];
                Assert.Equal(expected.Length, plan.Lengths[row]);
                List<byte> spelt = [];
                for (int c = plan.CodeOffsets[row]; c < plan.CodeOffsets[row + 1]; c++)
                {
                    int code = plan.Codes[c];
                    Assert.InRange(code, 0, plan.Tokens - 1);
                    spelt.AddRange(plan.Dictionary[offsets[code]..offsets[code + 1]].ToArray());
                }

                Assert.Equal(expected, spelt.ToArray());
            }

            Assert.Equal(plan.CodeCount, plan.CodeOffsets[values.Length]);
        }
        finally
        {
            plan.Release();
        }
    }

    [Fact]
    public void RepeatedSubstringsCostFewerCodesThanBytes()
    {
        string?[] values = Build("urls");
        CanonicalArena arena = new CanonicalArena();
        OnPairPlan? plan = OnPairPlan.TryBuild(arena, Strings(arena, new DTypeArena(), values), long.MaxValue);
        Assert.NotNull(plan);
        int bytes = values.Sum(v => v!.Length);
        Assert.True(plan.CodeCount * 4 < bytes, $"{plan.CodeCount} codes for {bytes} bytes");
        Assert.True(plan.Tokens > 256);
        plan.Release();
    }

    [Fact]
    public void ACeilingItCannotMeetIsRefused()
    {
        string?[] values = Build("diverse");
        CanonicalArena arena = new CanonicalArena();
        int node = Strings(arena, new DTypeArena(), values);
        Assert.Null(OnPairPlan.TryBuild(arena, node, values.Length * 3L));
    }

    private static void CheckDictionary(OnPairPlan plan)
    {
        ReadOnlySpan<int> offsets = plan.DictionaryOffsets;
        ReadOnlySpan<byte> bytes = plan.Dictionary;
        Assert.InRange(plan.Tokens, 256, 4096);
        Assert.Equal(0, offsets[0]);
        bool[] single = new bool[256];
        for (int t = 0; t < plan.Tokens; t++)
        {
            int length = offsets[t + 1] - offsets[t];
            Assert.InRange(length, 1, OnPairPlan.MaxTokenSize);
            if (length == 1)
            {
                single[bytes[offsets[t]]] = true;
            }

            // Sorted by bytes, as the reference numbers them.
            if (t > 0)
            {
                Assert.True(bytes[offsets[t - 1]..offsets[t]].SequenceCompareTo(bytes[offsets[t]..offsets[t + 1]]) <= 0);
            }
        }

        Assert.All(single, Assert.True);
        Assert.True(bytes.Length >= offsets[plan.Tokens - 1] + OnPairPlan.MaxTokenSize);
    }

    private static string?[] Build(string corpus)
    {
        const int Rows = 20_000;
        string[] services = ["billing", "auth", "search", "gateway", "payroll", "timesheet"];
        Random random = new Random(corpus.Length);
        return corpus switch
        {
            "urls" => [.. Enumerable.Range(0, Rows).Select(i => (string?)$"https://example.com/catalog/item/{(i * 7919L) % 100_003}/detail?ref={i}&lang=fr")],
            "paths" => [.. Enumerable.Range(0, Rows).Select(i => (string?)$"/var/log/app/{services[i % 6]}/2024/{1 + (i % 12):D2}/part-{random.Next(100_000):D5}.log.gz")],
            "short" => [.. Enumerable.Range(0, Rows).Select(i => i % 7 == 0 ? null : (string?)$"FR-{i % 95:D2}")],
            "diverse" => [.. Enumerable.Range(0, Rows).Select(_ => (string?)Convert.ToBase64String(RandomBytes(random, 1 + random.Next(40))))],
            _ => [.. Edges()],
        };
    }

    private static byte[] RandomBytes(Random random, int length)
    {
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    /// <summary>Empty values, one byte, exactly sixteen and far past, nulls, and every byte a UTF-8 string can hold.</summary>
    private static IEnumerable<string?> Edges()
    {
        for (int i = 0; i < 2_000; i++)
        {
            yield return (i % 9) switch
            {
                0 => string.Empty,
                1 => null,
                2 => "a",
                3 => "0123456789abcdef",
                4 => new string('z', 100 + (i % 50)),
                5 => "été € " + i,
                6 => "日本語テキスト" + (i % 10),
                7 => string.Concat(Enumerable.Range(1, 127).Select(c => (char)c)),
                _ => "row " + i,
            };
        }
    }

    /// <summary>A varbinview node of the values, the long ones in one data buffer.</summary>
    private static int Strings(CanonicalArena arena, DTypeArena types, string?[] values)
    {
        byte[][] encoded = [.. values.Select(v => v is null ? [] : Encoding.UTF8.GetBytes(v))];
        int heapBytes = encoded.Where(e => e.Length > 12).Sum(e => e.Length);
        VortexBuffer heap = arena.Allocate(Math.Max(heapBytes, 1), 1, out Span<byte> data);
        VortexBuffer views = arena.Allocate(values.Length * 16, 16, out Span<byte> bytes);
        int at = 0;
        bool nullable = values.Any(v => v is null);
        VortexBuffer bits = arena.Allocate((values.Length + 7) / 8, 1, out Span<byte> valid);
        for (int row = 0; row < values.Length; row++)
        {
            byte[] value = encoded[row];
            Span<byte> view = bytes.Slice(row * 16, 16);
            BitConverter.TryWriteBytes(view, value.Length);
            if (value.Length <= 12)
            {
                value.CopyTo(view[4..]);
            }
            else
            {
                value.AsSpan(0, 4).CopyTo(view[4..]);
                BitConverter.TryWriteBytes(view[8..], 0);
                BitConverter.TryWriteBytes(view[12..], at);
                value.CopyTo(data[at..]);
                at += value.Length;
            }

            if (values[row] is not null)
            {
                valid[row >> 3] |= (byte)(1 << (row & 7));
            }
        }

        Validity validity = nullable
            ? Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), values.Length, Validity.NonNullable, bits, 0))
            : Validity.NonNullable;
        DType type = types.Utf8(nullable ? Nullability.Nullable : Nullability.NonNullable);
        return arena.AddVarBinView(type, values.Length, validity, views, [heap]);
    }
}
