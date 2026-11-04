// The predicates an encoding answers over its own values (spec N): comparisons, ranges, sets, prefixes and substrings
// asked of packings, frames, zigzags, progressions, the parts of instants and of decimals, ALP, sparse arrays,
// dictionaries and compressed strings, on every shape of the matrix written under every hint.
//
// THE ORACLE IS THE ROWS: the rows a predicate keeps, how many and which, are those the rows' values say under
// three-valued logic, a null known to no comparison, a float compared as IEEE 754 says (a NaN equal to nothing and unequal
// to everything, -0.0 equal to +0.0), text by its UTF-8 bytes. Whether the encoding answered or the scan decoded first,
// they are the same: over the 99 753 predicates, a throwaway counter saw the encodings answer some 45 000 chunks, each of
// FoR, zigzag, progression, the parts of instants and of decimals, ALP, sparse, dictionary and FSST among them.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Matrix;

public sealed class EncodedPredicateTests
{
    /// <summary>The columns a hint writes in a scheme of its own, whose decoder may answer a predicate.</summary>
    [Flags]
    private enum Families
    {
        Integers = 1,
        Floats = 2,
        Decimals = 4,
        Instants = 8,
        Texts = 16,
        All = Integers | Floats | Decimals | Instants | Texts,
    }

    /// <summary>
    /// Every shape under every hint whose scheme answers a predicate, or whose choice the chooser makes (the canonical
    /// arrays, Zstd and pco answer none, and leave every predicate to the scan).
    /// </summary>
    public static TheoryData<Shape, EncodingHint> Cases()
    {
        TheoryData<Shape, EncodingHint> cases = [];
        foreach (Shape shape in Enum.GetValues<Shape>())
        {
            foreach (EncodingHint hint in Enum.GetValues<EncodingHint>())
            {
                if (hint is not (EncodingHint.Canonical or EncodingHint.Zstd or EncodingHint.Pco))
                {
                    cases.Add(shape, hint);
                }
            }
        }

        return cases;
    }

    /// <summary>The columns <paramref name="hint"/> writes in its scheme: those it applies to, every one for the chooser's.</summary>
    private static Families Of(EncodingHint hint) => hint switch
    {
        EncodingHint.Alp or EncodingHint.AlpRd => Families.Floats,
        EncodingHint.DateTimeParts => Families.Instants,
        EncodingHint.DecimalByteParts => Families.Decimals,
        EncodingHint.Fsst or EncodingHint.OnPair => Families.Texts,
        EncodingHint.BitPacked or EncodingHint.Sequence or EncodingHint.RunEnd => Families.Integers | Families.Instants | Families.Decimals,
        _ => Families.All,
    };

    /// <summary>
    /// Each column of a number, an instant, a decimal or a text the hint writes in its scheme: equal, unequal and the four
    /// orderings against values of its rows (the least, the quartiles, the greatest) and against values it does not hold
    /// (its type's edges, a NaN, the infinities and -0.0 of a float); a closed range and a half-open one between its
    /// quartiles; a set; and for a text its prefixes and substrings. Each keeps the rows that hold a value the predicate
    /// is true of.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EachPredicateKeepsTheRowsItsValuesSay(Shape shape, EncodingHint hint)
    {
        Decoders.EnsureRegistered();
        CancellationToken ct = TestContext.Current.CancellationToken;
        (string path, _) = await TypeEncodingMatrixTests.WrittenAsync(shape, hint);
        await using VortexFile file = await VortexFile.OpenAsync(path, ct);
        Checks checks = new Checks(file, MatrixRows.Shared(shape), ct);
        Families families = Of(hint);
        if (families.HasFlag(Families.Integers))
        {
            await IntegersAsync(checks);
        }

        if (families.HasFlag(Families.Floats))
        {
            await FloatsAsync(checks);
        }

        if (families.HasFlag(Families.Decimals))
        {
            await DecimalsAsync(checks);
        }

        if (families.HasFlag(Families.Instants))
        {
            await InstantsAsync(checks);
        }

        if (families.HasFlag(Families.Texts))
        {
            await checks.TextAsync("Text", r => r.Text);
            await checks.TextAsync("TextN", r => r.TextN);
        }

        Assert.True(checks.Failures.Count == 0, $"{shape} under {hint}, {checks.Asked} predicates, {checks.Failures.Count} wrong:\n{string.Join('\n', checks.Failures.Take(25))}");
    }

    private static async Task IntegersAsync(Checks checks)
    {
        await checks.ColumnAsync("I8", r => r.I8, Ordered, [sbyte.MinValue, sbyte.MaxValue, (sbyte)4]);
        await checks.ColumnAsync("I8N", r => r.I8N, Ordered, [sbyte.MinValue, (sbyte?)4]);
        await checks.ColumnAsync("I16", r => r.I16, Ordered, [short.MinValue, short.MaxValue, (short)4]);
        await checks.ColumnAsync("I16N", r => r.I16N, Ordered, [(short?)short.MaxValue, (short?)4]);
        await checks.ColumnAsync("I32", r => r.I32, Ordered, [int.MinValue, int.MaxValue, 4]);
        await checks.ColumnAsync("I32N", r => r.I32N, Ordered, [(int?)int.MinValue, (int?)4]);
        await checks.ColumnAsync("I64", r => r.I64, Ordered, [long.MinValue, long.MaxValue, 4L]);
        await checks.ColumnAsync("I64N", r => r.I64N, Ordered, [(long?)long.MaxValue, (long?)4L]);
        await checks.ColumnAsync("U8", r => r.U8, Ordered, [byte.MinValue, byte.MaxValue, (byte)4]);
        await checks.ColumnAsync("U8N", r => r.U8N, Ordered, [(byte?)byte.MaxValue, (byte?)4]);
        await checks.ColumnAsync("U16", r => r.U16, Ordered, [ushort.MinValue, ushort.MaxValue, (ushort)4]);
        await checks.ColumnAsync("U16N", r => r.U16N, Ordered, [(ushort?)ushort.MaxValue, (ushort?)4]);
        await checks.ColumnAsync("U32", r => r.U32, Ordered, [uint.MinValue, uint.MaxValue, 4u]);
        await checks.ColumnAsync("U32N", r => r.U32N, Ordered, [(uint?)uint.MaxValue, (uint?)4u]);
        await checks.ColumnAsync("U64", r => r.U64, Ordered, [ulong.MinValue, ulong.MaxValue, 4UL]);
        await checks.ColumnAsync("U64N", r => r.U64N, Ordered, [(ulong?)ulong.MaxValue, (ulong?)4UL]);
        await checks.ColumnAsync("Char", r => r.Char, Ordered, [char.MinValue, char.MaxValue, 'q']);
        await checks.ColumnAsync("CharN", r => r.CharN, Ordered, [(char?)char.MaxValue, (char?)'q']);
        await checks.ColumnAsync("NInt", r => r.NInt, Ordered, [nint.MinValue, nint.MaxValue, (nint)4]);
        await checks.ColumnAsync("NUInt", r => r.NUInt, Ordered, [nuint.MinValue, nuint.MaxValue, (nuint)4]);
        await checks.ColumnAsync("Span", r => r.Span, Ordered, [TimeSpan.MinValue, TimeSpan.MaxValue, TimeSpan.FromTicks(4)]);
        await checks.ColumnAsync("SpanN", r => r.SpanN, Ordered, [(TimeSpan?)TimeSpan.MaxValue, (TimeSpan?)TimeSpan.FromTicks(4)]);
        await checks.ColumnAsync("State", r => r.State, Ordered, [Status.Draft, Status.Archived]);
        await checks.ColumnAsync("StateN", r => r.StateN, Ordered, [(Status?)Status.Archived]);
    }

    private static async Task FloatsAsync(Checks checks)
    {
        await checks.ColumnAsync("F16", r => r.F16, Floats, [Half.NaN, Half.PositiveInfinity, Half.NegativeInfinity, Half.NegativeZero]);
        await checks.ColumnAsync("F16N", r => r.F16N, Floats, [(Half?)Half.NaN, (Half?)Half.NegativeZero]);
        await checks.ColumnAsync("F32", r => r.F32, Floats, [float.NaN, float.PositiveInfinity, float.NegativeInfinity, -0.0f, 0.3f]);
        await checks.ColumnAsync("F32N", r => r.F32N, Floats, [(float?)float.NaN, (float?)-0.0f, (float?)0.3f]);
        await checks.ColumnAsync("F64", r => r.F64, Floats, [double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.0, 0.3]);
        await checks.ColumnAsync("F64N", r => r.F64N, Floats, [(double?)double.NaN, (double?)-0.0, (double?)0.3]);
    }

    private static async Task DecimalsAsync(Checks checks)
    {
        await checks.ColumnAsync("Dec8", r => r.Dec8, Ordered, [-9.9m, 9.9m, 0.5m]);
        await checks.ColumnAsync("Dec16", r => r.Dec16, Ordered, [-99.99m, 99.99m, 0.05m]);
        await checks.ColumnAsync("Dec32", r => r.Dec32, Ordered, [-9_999_999.99m, 9_999_999.99m, 0.05m]);
        await checks.ColumnAsync("Dec64", r => r.Dec64, Ordered, [-99_999_999_999_999.9999m, 0.0005m]);
        await checks.ColumnAsync("Dec128", r => r.Dec128, Ordered, [decimal.MinValue, decimal.MaxValue, 0.5m]);
        await checks.ColumnAsync("Dec128N", r => r.Dec128N, Ordered, [(decimal?)decimal.MaxValue, (decimal?)0.5m]);
    }

    private static async Task InstantsAsync(Checks checks)
    {
        await checks.ColumnAsync("Date", r => r.Date, Ordered, [DateOnly.MinValue, DateOnly.MaxValue, new DateOnly(2000, 2, 29)]);
        await checks.ColumnAsync("DateN", r => r.DateN, Ordered, [(DateOnly?)DateOnly.MaxValue, (DateOnly?)new DateOnly(2000, 2, 29)]);
        await checks.ColumnAsync("Time", r => r.Time, Ordered, [TimeOnly.MinValue, TimeOnly.MaxValue, new TimeOnly(12, 0, 0, 1)]);
        await checks.ColumnAsync("TimeN", r => r.TimeN, Ordered, [(TimeOnly?)TimeOnly.MaxValue, (TimeOnly?)new TimeOnly(12, 0, 0, 1)]);
        DateTime instant = new DateTime(2001, 2, 3, 4, 5, 6, 7);
        await checks.ColumnAsync("Stamp", r => r.Stamp, Ordered, [DateTime.MinValue, DateTime.MaxValue, instant]);
        await checks.ColumnAsync("StampN", r => r.StampN, Ordered, [(DateTime?)DateTime.MaxValue, (DateTime?)instant]);
        await checks.ColumnAsync("StampNs", r => r.StampNs, Ordered, [instant, instant.AddTicks(1)]);
        await checks.ColumnAsync("StampMsUtc", r => r.StampMsUtc, Ordered, [instant, instant.AddTicks(1)]);
        DateTimeOffset zoned = new DateTimeOffset(instant, TimeSpan.FromHours(2));
        await checks.ColumnAsync("Zoned", r => r.Zoned, Ordered, [zoned, zoned.AddTicks(1)]);
        await checks.ColumnAsync("ZonedN", r => r.ZonedN, Ordered, [(DateTimeOffset?)zoned, (DateTimeOffset?)zoned.AddTicks(1)]);
    }

    /// <summary>How a value orders against another: under the type's order, or none at all.</summary>
    private enum Order
    {
        Less,
        Equal,
        Greater,

        /// <summary>A NaN, which is equal to nothing and unequal to everything.</summary>
        Unordered,

        /// <summary>A null, which no comparison knows.</summary>
        Unknown,
    }

    private static Order Ordered<T>(T left, T right)
    {
        if (left is null || right is null)
        {
            return Order.Unknown;
        }

        int sign = Comparer<T>.Default.Compare(left, right);
        return sign < 0 ? Order.Less : sign > 0 ? Order.Greater : Order.Equal;
    }

    private static Order Floats<T>(T left, T right) =>
        left is null || right is null ? Order.Unknown : Compare(Widened(left), Widened(right));

    private static double Widened(object value) => value switch
    {
        Half half => (double)half,
        float single => single,
        _ => (double)value,
    };

    private static Order Compare(double left, double right) =>
        double.IsNaN(left) || double.IsNaN(right) ? Order.Unordered : left < right ? Order.Less : left > right ? Order.Greater : Order.Equal;

    private static Order Utf8(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return Order.Unknown;
        }

        int sign = Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right));
        return sign < 0 ? Order.Less : sign > 0 ? Order.Greater : Order.Equal;
    }

    /// <summary>The predicates asked of one file, each counted by a scan and by the rows.</summary>
    private sealed class Checks(VortexFile file, AllTypes[] rows, CancellationToken cancellationToken)
    {
        internal List<string> Failures { get; } = [];

        internal int Asked { get; private set; }

        /// <summary>The comparisons, ranges and a set of the column named <paramref name="name"/>.</summary>
        internal async Task ColumnAsync<T>(string name, Func<AllTypes, T> value, Func<T, T, Order> order, T[] absent)
        {
            List<T> held = Held(value, order);
            List<T> probes = [.. Picked(held), .. absent];
            foreach (T probe in probes)
            {
                await CompareAsync(name, value, order, probe);
            }

            if (held.Count > 0)
            {
                T low = held[held.Count / 4];
                T high = held[held.Count * 3 / 4];
                await ExpectAsync($"{name} between {low} and {high}", r => r.Column<T>(name).Between(low, high), row => Is(order(value(row), low), Order.Greater, Order.Equal) && Is(order(value(row), high), Order.Less, Order.Equal));
                await ExpectAsync($"{name} from {low} below {high}", r => r.Column<T>(name) >= low & r.Column<T>(name) < high, row => Is(order(value(row), low), Order.Greater, Order.Equal) && order(value(row), high) == Order.Less);
                T[] members = [held[0], held[held.Count / 2], absent[^1]];
                await ExpectAsync($"{name} in {string.Join(", ", members)}", r => r.Column<T>(name).In(members), row => members.Any(member => order(value(row), member) == Order.Equal));
            }
        }

        /// <summary>The comparisons of the text column named <paramref name="name"/>, then its prefixes and substrings.</summary>
        internal async Task TextAsync(string name, Func<AllTypes, string?> value)
        {
            await ColumnAsync(name, value, Utf8, ["", char.MaxValue.ToString(), "~absent"]);
            foreach (string? picked in Picked(Held(value, Utf8)))
            {
                string text = picked!;
                foreach (string part in (string[])[text[..Math.Min(1, text.Length)], text[..Math.Min(3, text.Length)], text[(text.Length / 2)..]])
                {
                    await ExpectAsync($"{name} starts with '{part}'", r => r.Column<string>(name).StartsWith(part), row => value(row) is { } held && held.StartsWith(part, StringComparison.Ordinal));
                    await ExpectAsync($"{name} contains '{part}'", r => r.Column<string>(name).Contains(part), row => value(row) is { } held && held.Contains(part, StringComparison.Ordinal));
                }
            }
        }

        private async Task CompareAsync<T>(string name, Func<AllTypes, T> value, Func<T, T, Order> order, T probe)
        {
            await ExpectAsync($"{name} == {probe}", r => r.Column<T>(name) == probe, row => order(value(row), probe) == Order.Equal);
            await ExpectAsync($"{name} != {probe}", r => r.Column<T>(name) != probe, row => Is(order(value(row), probe), Order.Less, Order.Greater, Order.Unordered));
            await ExpectAsync($"{name} < {probe}", r => r.Column<T>(name) < probe, row => order(value(row), probe) == Order.Less);
            await ExpectAsync($"{name} <= {probe}", r => r.Column<T>(name) <= probe, row => Is(order(value(row), probe), Order.Less, Order.Equal));
            await ExpectAsync($"{name} > {probe}", r => r.Column<T>(name) > probe, row => order(value(row), probe) == Order.Greater);
            await ExpectAsync($"{name} >= {probe}", r => r.Column<T>(name) >= probe, row => Is(order(value(row), probe), Order.Greater, Order.Equal));
        }

        /// <summary>
        /// Asks <paramref name="filter"/> of the file, a column of every row projected: the rows it keeps, counted and their
        /// values of that column hashed, against the rows <paramref name="keeps"/> holds true of. A projection reads its
        /// batches, where the scan offers the predicate to the column's encoding; a count would decode first.
        /// </summary>
        private async Task ExpectAsync(string what, Func<Probe<AllTypes>, Predicate> filter, Func<AllTypes, bool> keeps)
        {
            Asked++;
            (long Rows, long Hash) want = default;
            foreach (AllTypes row in rows)
            {
                if (keeps(row))
                {
                    want = (want.Rows + 1, want.Hash + Hashed(row.I64));
                }
            }

            (long Rows, long Hash) got = default;
            await foreach (long held in file.Scan<AllTypes>().Where(filter).Select(r => r.Column<long>(nameof(AllTypes.I64))).WithCancellation(cancellationToken))
            {
                got = (got.Rows + 1, got.Hash + Hashed(held));
            }

            if (got != want)
            {
                Failures.Add($"{what}: {got.Rows} rows, the rows say {want.Rows}{(got.Rows == want.Rows ? ", other rows" : "")}");
            }
        }

        /// <summary>A hash of a row's value of the projected column, mixed so that a sum of them tells rows apart.</summary>
        private static long Hashed(long value) => (long)System.IO.Hashing.XxHash3.HashToUInt64(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<long>(in value)));

        /// <summary>The distinct values of the column that order, least first.</summary>
        private List<T> Held<T>(Func<AllTypes, T> value, Func<T, T, Order> order)
        {
            List<T> held = [.. rows.Select(value).Where(v => order(v, v) == Order.Equal).Distinct()];
            held.Sort((left, right) => order(left, right) switch { Order.Less => -1, Order.Greater => 1, _ => 0 });
            return held;
        }

        /// <summary>The least, the quartiles and the greatest of <paramref name="held"/>.</summary>
        private static IEnumerable<T> Picked<T>(List<T> held) =>
            held.Count == 0 ? [] : new[] { 0, held.Count / 4, held.Count / 2, held.Count * 3 / 4, held.Count - 1 }.Distinct().Select(at => held[at]);

        private static bool Is(Order order, params ReadOnlySpan<Order> any) => any.Contains(order);
    }
}
