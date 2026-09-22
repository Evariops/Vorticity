// Composite keys: a sorted-runs index keyed by the row encoding of a tuple, written through
// `VortexWriteOptions.KeyEncoder`, walked bytewise, and sought with `RowEncoder.EncodeKey`, whose
// encoding of the leading columns is a prefix of the key.
//
// THE ORACLE IS THE ENCODER ITSELF, applied to one tuple at a time: every non-null tuple's
// `EncodeKey` bytes, sorted bytewise with the row as tiebreak. That the one-tuple encoding equals the
// batch encoding the writer used is what the test is about.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Keys;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Keys;

public sealed class CompositeKeyTests
{
    private const int Rows = 5_000;
    private const int Block = 512;

    private static readonly string[] Countries = ["FR", "DE", "ES", "IT", "a-country-with-a-long-name"];

    private static string Country(int row) => Countries[(row * 7) % Countries.Length];

    /// <summary>A nullable city, sometimes long enough to leave the view.</summary>
    private static string? City(int row) =>
        row % 13 == 0 ? null : "city-" + ((row * 31) % 97).ToString(CultureInfo.InvariantCulture) + (row % 4 == 0 ? "-with-a-long-suffix" : string.Empty);

    private static int Number(int row) => ((row * 17) % 301) - 150;

    private static readonly DTypeArena Types = new DTypeArena();
    private static readonly DType Utf8 = Types.Utf8(Nullability.NonNullable);
    private static readonly DType Utf8N = Types.Utf8(Nullability.Nullable);
    private static readonly DType I32 = Types.Primitive(PType.I32, Nullability.NonNullable);
    private static readonly RowSortField Asc = RowSortField.Ascending;

    [Fact]
    public async Task AWalkOfTheTupleIsEveryNonNullTupleInEncodedOrder()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(withEncoder: true);
        List<(byte[] Key, long Row)> oracle = Oracle(row => City(row) is string city
            ? RowEncoder.EncodeKey([FilterLiteral.From(Country(row)), FilterLiteral.From(city)], [Utf8, Utf8N], [Asc, Asc])
            : null);

        await using KeyCursor cursor = await written.File.Keys("country", "city").OpenAsync();
        Assert.Equal(FilterLiteralKind.Bytes, cursor.KeyKind);
        Assert.Equal(oracle.Count, cursor.EntryCount);
        Assert.Equal(new RowKeyEncoder(Asc).Format, cursor.KeyFormat);
        Assert.StartsWith("vortex-row " + RowEncoder.VortexVersion, cursor.KeyFormat, StringComparison.Ordinal);

        int index = 0;
        for (bool ok = await cursor.SeekFirstAsync(); ok; ok = await cursor.NextAsync())
        {
            Assert.True(cursor.KeyBytes.SequenceEqual(oracle[index].Key), $"entry {index}: the key differs");
            Assert.Equal(oracle[index].Row, cursor.Row);
            index++;
        }

        Assert.Equal(oracle.Count, index);
    }

    [Theory]
    [InlineData("FR")]
    [InlineData("a-country-with-a-long-name")]
    [InlineData("ZZ")]
    public async Task APrefixOfTheLeadingColumnIsASeekAndAWalk(string country)
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(withEncoder: true);
        List<(byte[] Key, long Row)> oracle = Oracle(row => City(row) is string city && Country(row) == country
            ? RowEncoder.EncodeKey([FilterLiteral.From(country), FilterLiteral.From(city)], [Utf8, Utf8N], [Asc, Asc])
            : null);

        // The spec's overload infers a non-nullable utf8, which is the leading column's dtype.
        byte[] prefix = RowEncoder.EncodeKey([FilterLiteral.From(country)], [Asc]);
        await using KeyCursor cursor = await written.File.Keys("country", "city").OpenAsync();
        List<long> rows = [];
        for (bool ok = await cursor.SeekAsync(FilterLiteral.From(prefix), SeekOp.AtOrAfter);
             ok && cursor.KeyBytes.StartsWith(prefix);
             ok = await cursor.NextAsync())
        {
            rows.Add(cursor.Row);
        }

        Assert.Equal(oracle.ConvertAll(e => e.Row), rows);
    }

    [Fact]
    public async Task AnIntegerLeadsAKeyAtItsOwnWidthAndSignsOrderIt()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(withEncoder: true);
        List<(byte[] Key, long Row)> oracle = Oracle(row =>
            RowEncoder.EncodeKey([FilterLiteral.From((long)Number(row)), FilterLiteral.From(Country(row))], [I32, Utf8], [Asc, Asc]));

        await using KeyCursor cursor = await written.File.Keys("n", "country").OpenAsync();
        long previous = long.MinValue;
        int index = 0;
        for (bool ok = await cursor.SeekFirstAsync(); ok; ok = await cursor.NextAsync())
        {
            Assert.Equal(oracle[index].Row, cursor.Row);
            long n = Number((int)cursor.Row);
            Assert.True(n >= previous, $"row {cursor.Row}: {n} after {previous}");
            previous = n;
            index++;
        }

        Assert.Equal(Rows, index);

        // A seek at -1 lands on the first row whose number is -1.
        byte[] minusOne = RowEncoder.EncodeKey([FilterLiteral.From(-1L)], [I32], [Asc]);
        Assert.True(await cursor.SeekAsync(FilterLiteral.From(minusOne), SeekOp.AtOrAfter));
        Assert.Equal(-1, Number((int)cursor.Row));
        Assert.True(cursor.KeyBytes.StartsWith(minusOne));

        // Distinct walks the tuples once each.
        await using KeyCursor distinct = await written.File.Keys("n", "country").Distinct().OpenAsync();
        HashSet<(int, string)> tuples = [];
        for (int row = 0; row < Rows; row++)
        {
            tuples.Add((Number(row), Country(row)));
        }

        int keys = 0;
        for (bool ok = await distinct.SeekFirstAsync(); ok; ok = await distinct.NextAsync())
        {
            keys++;
        }

        Assert.Equal(tuples.Count, keys);
    }

    [Fact]
    public async Task WithoutAnEncoderTheKeyIsAbandonedAndTheCursorRefusedByName()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(withEncoder: false);
        IndexWriteReport report = Assert.Single(written.Report.Indexes, r => r.Column == "(country, city)");
        Assert.Equal(IndexOutcome.Abandoned, report.Outcome);
        Assert.Contains("KeyEncoder", report.Reason, StringComparison.Ordinal);

        VortexUnsupportedException refused = await Assert.ThrowsAsync<VortexUnsupportedException>(
            async () => await written.File.Keys("country", "city").OpenAsync());
        Assert.Contains("IndexPolicy.ForKey", refused.Message, StringComparison.Ordinal);

        // The policy still records the key, so an append would ask for it again.
        IndexDirectory? directory = await written.File.ReadIndexDirectoryAsync();
        Assert.NotNull(directory);
        Assert.Equal(2, directory.Policy.Keys.Count);
        Assert.Equal(["country", "city"], directory.Policy.Keys[0].Paths);
        Assert.Equal(IndexPolicyKind.SortedRuns, directory.Policy.Keys[0].Policy.Kind);
        Assert.Equal(64, directory.Policy.Keys[0].Policy.SegmentEntries);
    }

    [Fact]
    public async Task AFileWithCompositeKeysScansAndPrunesAsWithout()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(withEncoder: true);
        VortexFile file = written.File;
        int expected = 0;
        for (int row = 0; row < Rows; row++)
        {
            expected += Country(row) == "DE" ? 1 : 0;
        }

        VortexExpr filter = Expr.Eq(Expr.Field("country"), Expr.Literal(FilterLiteral.From("DE")));
        Assert.Equal(expected, await file.ScanBuilder().Where(filter).CountAsync());
        Assert.Equal(expected, await file.ScanBuilder().Where(filter).WithIndexes(false).CountAsync());

        // The composite entry does not pose as a single column's source.
        KeyPlan single = await file.Keys("country").ExplainAsync();
        Assert.Equal(KeySourceKind.None, single.Source);
        Assert.Throws<ArgumentException>(() => WritePolicy.None.ForKey(["country"], IndexSpec.SortedRuns));
        Assert.Throws<ArgumentException>(() => WritePolicy.None.ForKey(["a", "b"], IndexSpec.Postings));
    }

    /// <summary>Every row's key, the null ones left out, sorted by key then row.</summary>
    [Theory]
    [InlineData(false, 1, 0)]
    [InlineData(true, 2, 100)]
    public async Task AScanInTheTuplesOrderDeliversTheWalk(bool descending, int degree, int window)
    {
        // `InKeyOrder(paths)`, the permuted read over the composite run that a composite
        // clustering key's compaction and key-ordered reads need. The oracle is the walk's: every
        // non-null tuple in encoded order, ties in row order, reversed descending; the filter, on
        // a column the tuple does not hold, removes rows and nothing else.
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(withEncoder: true);
        List<(byte[] Key, long Row)> oracle = Oracle(row => City(row) is string city && Number(row) > 0
            ? RowEncoder.EncodeKey([FilterLiteral.From(Country(row)), FilterLiteral.From(city)], [Utf8, Utf8N], [Asc, Asc])
            : null);
        if (descending)
        {
            oracle.Reverse();
        }

        ScanBuilder scan = written.File.ScanBuilder()
            .InKeyOrder(["country", "city"], descending)
            .Where(Expr.Gt(Expr.Field("n"), Expr.Literal(FilterLiteral.From(0))))
            .Project("country", "city", "n")
            .WithDegreeOfParallelism(degree);
        if (window > 0)
        {
            scan.WithMaxBatchRows(window);
        }

        List<string> delivered = [];
        await foreach (RecordBatch batch in scan.ExecuteAsync())
        {
            Assert.True(window == 0 || batch.RowCount <= window);
            BinaryColumn countries = batch.Column(0).AsBinary();
            BinaryColumn cities = batch.Column(1).AsBinary();
            ReadOnlySpan<int> numbers = batch.Column(2).AsPrimitive<int>().Values;
            for (int i = 0; i < batch.RowCount; i++)
            {
                delivered.Add($"{countries.GetString(i)}|{cities.GetString(i)}|{numbers[i]}");
            }
        }

        Assert.Equal(oracle.ConvertAll(e => $"{Country((int)e.Row)}|{City((int)e.Row)}|{Number((int)e.Row)}"), delivered);

        ScanExplanation plan = await written.File.ScanBuilder().InKeyOrder(["country", "city"], descending).ExplainAsync();
        Assert.Equal("(country, city)", plan.Order!.Path);
        Assert.Equal(KeySourceKind.SortedRuns, plan.Order.Source);

        // A tuple the file has no run for is refused, with the policy that would have served.
        VortexUnsupportedException refused = await Assert.ThrowsAsync<VortexUnsupportedException>(async () =>
        {
            await foreach (RecordBatch batch in written.File.ScanBuilder().InKeyOrder(["city", "n"]).ExecuteAsync())
            {
                batch.Dispose();
            }
        });
        Assert.Contains("IndexPolicy.ForKey", refused.Message, StringComparison.Ordinal);
    }

    private static List<(byte[] Key, long Row)> Oracle(Func<int, byte[]?> key)
    {
        List<(byte[] Key, long Row)> entries = [];
        for (int row = 0; row < Rows; row++)
        {
            if (key(row) is { } bytes)
            {
                entries.Add((bytes, row));
            }
        }

        entries.Sort((a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key) is var o && o != 0 ? o : a.Row.CompareTo(b.Row));
        return entries;
    }

    private sealed class Written : IAsyncDisposable
    {
        private readonly string _path;

        private Written(string path, VortexFile file, WriteReport report)
        {
            _path = path;
            File = file;
            Report = report;
        }

        internal VortexFile File { get; }

        internal WriteReport Report { get; }

        internal static async Task<Written> CreateAsync(bool withEncoder)
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"vorticity-composite-{Guid.NewGuid():N}.vortex");
            WriteReport report = await WriteAsync(path, withEncoder);
            return new Written(path, await VortexFile.OpenAsync(path, CancellationToken.None), report);
        }

        public async ValueTask DisposeAsync()
        {
            await File.DisposeAsync();
            System.IO.File.Delete(_path);
        }

        private static async Task<WriteReport> WriteAsync(string path, bool withEncoder)
        {
            DTypeArena types = new DTypeArena();
            DType utf8 = types.Utf8(Nullability.NonNullable);
            DType utf8n = types.Utf8(Nullability.Nullable);
            DType i32 = types.Primitive(PType.I32, Nullability.NonNullable);
            DType schema = types.Struct(["country", "city", "n"], [utf8, utf8n, i32], Nullability.NonNullable);
            IndexSpec runs = IndexSpec.SortedRuns.WithSegmentEntries(64);
            VortexWriteOptions options = new VortexWriteOptions
            {
                RowBlockSize = Block,
                DataBlockTargetBytes = null,
                IndexBudgetPerMille = 1_000_000,
                WritePolicy = WritePolicy.None.ForKey(["country", "city"], runs).ForKey(["n", "country"], runs),
                KeyEncoder = withEncoder ? new RowKeyEncoder(RowSortField.Ascending) : null,
            };

            await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
            for (int start = 0; start < Rows; start += Block)
            {
                CanonicalArena arena = new CanonicalArena();
                int count = Math.Min(Block, Rows - start);
                int[] columns =
                [
                    Strings(arena, types, utf8, start, count, Country),
                    Strings(arena, types, utf8n, start, count, City),
                    Ints(arena, i32, start, count),
                ];
                int root = arena.AddStruct(schema, count, Validity.NonNullable, columns);
                using RecordBatch record = new RecordBatch(arena, root, start);
                await writer.WriteAsync(record, CancellationToken.None);
            }

            return await writer.CompleteAsync(CancellationToken.None);
        }

        private static int Ints(CanonicalArena arena, DType dtype, int start, int count)
        {
            VortexBuffer buffer = arena.Allocate(count * sizeof(int), sizeof(int), out Span<byte> bytes);
            Span<int> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(bytes);
            for (int i = 0; i < count; i++)
            {
                values[i] = Number(start + i);
            }

            return arena.AddPrimitive(dtype, count, Validity.NonNullable, PType.I32, buffer);
        }

        private static int Strings(
            CanonicalArena arena, DTypeArena types, DType dtype, int start, int count, Func<int, string?> value)
        {
            List<byte[]> values = [];
            int heap = 0;
            bool nulls = false;
            for (int i = 0; i < count; i++)
            {
                string? text = value(start + i);
                nulls |= text is null;
                byte[] utf8 = text is null ? [] : Encoding.UTF8.GetBytes(text);
                values.Add(utf8);
                heap += utf8.Length > 12 ? utf8.Length : 0;
            }

            Validity validity = dtype.IsNullable ? Validity.AllValid : Validity.NonNullable;
            if (nulls)
            {
                VortexBuffer bits = arena.Allocate(Math.Max((count + 7) / 8, 1), 8, out Span<byte> raw);
                raw.Clear();
                for (int i = 0; i < count; i++)
                {
                    if (value(start + i) is not null)
                    {
                        raw[i >> 3] |= (byte)(1 << (i & 7));
                    }
                }

                validity = Validity.Bitmap(
                    arena.AddBool(types.Bool(Nullability.NonNullable), count, Validity.NonNullable, bits, 0));
            }

            VortexBuffer data = arena.Allocate(Math.Max(heap, 1), 1, out Span<byte> dataBytes);
            VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> view);
            view.Clear();
            int offset = 0;
            for (int i = 0; i < count; i++)
            {
                byte[] utf8 = values[i];
                Span<byte> one = view.Slice(i * 16, 16);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(one, (uint)utf8.Length);
                if (utf8.Length <= 12)
                {
                    utf8.CopyTo(one[4..]);
                    continue;
                }

                utf8.AsSpan(0, 4).CopyTo(one[4..]);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(one[12..], (uint)offset);
                utf8.CopyTo(dataBytes[offset..]);
                offset += utf8.Length;
            }

            return arena.AddVarBinView(dtype, count, validity, views, [data]);
        }
    }
}
