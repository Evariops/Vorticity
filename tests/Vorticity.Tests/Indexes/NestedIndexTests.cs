// Indexes on nested columns: an entry's `column_path` resolves to a leaf column, and a policy
// holds one `IndexPolicy` per column path; `WritePolicy.For` documents "a.b" as a path.
//
// WHAT A NESTED COLUMN ADDS IS ITS PARENTS' NULLS. A struct that is null nulls every field below it,
// whatever the field's own buffer holds; so the rows under a null `person` carry the value "ghost"
// in their buffers, and no index may ever list it. The oracle is the rows themselves, and every
// answer is taken with the indexes on and off.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

namespace Vorticity.Tests.Indexes;

public sealed class NestedIndexTests
{
    private const int Rows = 20_000;
    private const int Block = 1_024;
    // Above every real value, so an extreme that reads it says so.
    private const string Ghost = "~ghost";

    private static bool PersonNull(int row) => row % 13 == 0;

    private static string Name(int row) => PersonNull(row) ? Ghost : "n" + (row / Block).ToString(CultureInfo.InvariantCulture);

    private static int? Age(int row) => row % 7 == 0 ? null : PersonNull(row) ? 999_999 : row / 512;

    private static string City(int row) =>
        PersonNull(row) ? Ghost : "c" + ((row * 31) % 211).ToString("D3", CultureInfo.InvariantCulture);

    private static readonly RowSortField Asc = RowSortField.Ascending;

    private static WritePolicy Policy => WritePolicy.None
        .For("person.name", IndexSpec.Postings)
        .For("person.age", IndexSpec.Bloom(minDistinct: 1))
        .For("person.address.city", IndexSpec.SortedRuns.WithSegmentEntries(500))
        .For("person.nope", IndexSpec.Bloom())
        .For("person.address", IndexSpec.Auto)
        .ForKey(["person.address.city", "id"], IndexSpec.SortedRuns);

    [Fact]
    public async Task EveryOverrideIsBuiltOrSaysWhyNot()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(indexed: true);
        IReadOnlyList<IndexWriteReport> reports = written.Report.Indexes;
        Assert.Equal(IndexOutcome.Built, written.Report.Index("person.name", IndexKinds.PostingsBlocks)?.Outcome);
        Assert.Equal(IndexOutcome.Built, written.Report.Index("person.age", IndexKinds.BloomSbbf)?.Outcome);
        Assert.Equal(IndexOutcome.Built, written.Report.Index("person.address.city", IndexKinds.SortedRuns)?.Outcome);
        Assert.Equal(IndexOutcome.Built, written.Report.Index("(person.address.city, id)", IndexKinds.SortedRuns)?.Outcome);

        IndexWriteReport nope = Assert.Single(reports, r => r.Column == "person.nope");
        Assert.Equal(IndexOutcome.Abandoned, nope.Outcome);
        Assert.Contains("names no column", nope.Reason, StringComparison.Ordinal);
        IndexWriteReport auto = Assert.Single(reports, r => r.Column == "person.address");
        Assert.Equal(IndexOutcome.Abandoned, auto.Outcome);
        Assert.Contains("Auto chooses among the top-level columns", auto.Reason, StringComparison.Ordinal);

        IndexDirectory? directory = await written.File.ReadIndexDirectoryAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(directory);
        uint[] name = [1, 0];
        uint[] age = [1, 1];
        uint[] city = [1, 2, 0];
        uint[] id = [0];
        Assert.Equal(name, Assert.Single(directory.Entries, e => e.Kind == IndexKinds.PostingsBlocks).ColumnPath);
        Assert.Contains(directory.Entries, e => e.Kind == IndexKinds.BloomSbbf && e.ColumnPath.SequenceEqual(age));
        Assert.Single(directory.Entries, e => e.Kind == IndexKinds.SortedRuns && e.ColumnPath.SequenceEqual(city));
        IndexEntry composite = Assert.Single(directory.Entries, e => e.Kind == IndexKinds.SortedRuns && e.ColumnPath.Count == 0);
        Assert.True(KeyRunOptions.TryParseEntry(composite.Options, out _, out _, out List<uint[]> columns, out _));
        Assert.Equal(city, columns[0]);
        Assert.Equal(id, columns[1]);
    }

    [Theory]
    [InlineData("person.name", "n5")]
    [InlineData("person.name", Ghost)]
    [InlineData("person.age", 10)]
    [InlineData("person.age", 999_999)]
    [InlineData("person.address.city", "c042")]
    [InlineData("person.address.city", Ghost)]
    public async Task AnEqualityOnANestedColumnPrunesAndAnswersAsWithout(string path, object value)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(indexed: true);
        FilterLiteral literal = value is string text ? FilterLiteral.From(text) : FilterLiteral.From(Convert.ToInt64(value, CultureInfo.InvariantCulture));
        VortexExpr filter = Expr.Eq(Expr.Field(path), Expr.Literal(literal));
        int expected = 0;
        HashSet<int> holding = [];
        for (int row = 0; row < Rows; row++)
        {
            if (Matches(path, value, row))
            {
                expected++;
                holding.Add(row / Block);
            }
        }

        Assert.Equal(expected, await written.File.ScanBuilder().Where(filter).CountAsync(ct));
        Assert.Equal(expected, await written.File.ScanBuilder().Where(filter).WithIndexes(false).CountAsync(ct));

        // The index kills every block that does not hold the value: the Bloom filter may keep a
        // false positive, the locating indexes may not; and a ghost is in no index.
        ScanExplanation plan = await written.File.ScanBuilder().Where(filter).ExplainAsync(ct);
        if (path == "person.age")
        {
            Assert.InRange(plan.LiveBlocks, holding.Count, holding.Count + 1);
            Assert.Contains(plan.Pruning, step => step.Structure == "bloom filter" && step.BlocksPruned > 0);
        }
        else
        {
            Assert.Equal(holding.Count, plan.LiveBlocks);
        }
    }

    [Fact]
    public async Task AFieldOfANullStructIsNullToAFilterAndToAnExtreme()
    {
        // The reference's `get_item` masks a field with its struct's validity; the ghost values in
        // the buffers under a null `person` are no values at all.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(indexed: false);
        int nullNames = 0;
        int nullAges = 0;
        int maxAge = int.MinValue;
        for (int row = 0; row < Rows; row++)
        {
            nullNames += PersonNull(row) ? 1 : 0;
            if (PersonNull(row) || Age(row) is null)
            {
                nullAges++;
            }
            else
            {
                maxAge = Math.Max(maxAge, Age(row)!.Value);
            }
        }

        Assert.Equal(nullNames, await written.File.ScanBuilder().Where(Expr.IsNull(Expr.Field("person.name"))).CountAsync(ct));
        Assert.Equal(nullAges, await written.File.ScanBuilder().Where(Expr.IsNull(Expr.Field("person.age"))).CountAsync(ct));
        Assert.Equal(nullNames, await written.File.ScanBuilder().Where(Expr.IsNull(Expr.Field("person.address.city"))).CountAsync(ct));
        Assert.Equal(maxAge, (await written.File.ScanBuilder().MaxAsync("person.age", ct)).SignedValue);
        Assert.Equal("c210", Encoding.UTF8.GetString((await written.File.ScanBuilder().MaxAsync("person.address.city", ct)).BytesValue));
        Assert.Equal("n9", Encoding.UTF8.GetString((await written.File.ScanBuilder().MaxAsync("person.name", ct)).BytesValue));
    }

    [Fact]
    public async Task ASortedRunsWalkOfANestedColumnListsOnlyTheRowsItsParentsKeep()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(indexed: true);
        await AssertWalksAsync(written.File);
    }

    [Fact]
    public async Task AnIndexAddedAfterTheFactOnANestedColumnIsTheSame()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-nested-{Guid.NewGuid():N}.vortex");
        try
        {
            await Written.WriteAsync(path, indexed: false);
            IReadOnlyList<IndexWriteReport> reports = await VortexFileIndexer.AppendIndexesAsync(
                path, Policy, new VortexWriteOptions { IndexBudgetPerMille = 1_000_000, KeyEncoder = new RowKeyEncoder(Asc) }, ct);
            Assert.Contains(reports, r => r.Column == "person.address.city" && r.Outcome == IndexOutcome.Built);

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            await AssertWalksAsync(file);
            VortexExpr filter = Expr.Eq(Expr.Field("person.name"), Expr.Literal(FilterLiteral.From("n7")));
            ScanExplanation plan = await file.ScanBuilder().Where(filter).ExplainAsync(ct);
            Assert.Equal(1, plan.LiveBlocks);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static async Task AssertWalksAsync(VortexFile file)
    {
        // The single column: every row whose person is there, in (city, row) order.
        List<(string City, int Row)> oracle = [];
        for (int row = 0; row < Rows; row++)
        {
            if (!PersonNull(row))
            {
                oracle.Add((City(row), row));
            }
        }

        oracle.Sort((a, b) => string.CompareOrdinal(a.City, b.City) is var o && o != 0 ? o : a.Row.CompareTo(b.Row));
        await using (KeyCursor cursor = await file.Keys("person.address.city").OpenAsync())
        {
            Assert.Equal(oracle.Count, cursor.EntryCount);
            int index = 0;
            for (bool ok = await cursor.SeekFirstAsync(); ok; ok = await cursor.NextAsync())
            {
                Assert.True(
                    cursor.KeyBytes.SequenceEqual(Encoding.UTF8.GetBytes(oracle[index].City)),
                    $"entry {index}: {Encoding.UTF8.GetString(cursor.KeyBytes)}, expected {oracle[index].City}");
                Assert.Equal(oracle[index].Row, cursor.Row);
                index++;
            }

            Assert.Equal(oracle.Count, index);
        }

        // The composite key over the nested column and a top-level one: the same rows, keyed by the
        // row encoding of the tuple.
        DTypeArena types = new DTypeArena();
        DType utf8 = types.Utf8(Nullability.NonNullable);
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        List<(byte[] Key, long Row)> tuples = [];
        for (int row = 0; row < Rows; row++)
        {
            if (!PersonNull(row))
            {
                tuples.Add((RowEncoder.EncodeKey([FilterLiteral.From(City(row)), FilterLiteral.From((long)row)], [utf8, i64], [Asc, Asc]), row));
            }
        }

        tuples.Sort((a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key) is var o && o != 0 ? o : a.Row.CompareTo(b.Row));
        await using (KeyCursor cursor = await file.Keys("person.address.city", "id").OpenAsync())
        {
            Assert.Equal(tuples.Count, cursor.EntryCount);
            int index = 0;
            for (bool ok = await cursor.SeekFirstAsync(); ok; ok = await cursor.NextAsync())
            {
                Assert.True(cursor.KeyBytes.SequenceEqual(tuples[index].Key), $"entry {index}: the key differs");
                Assert.Equal(tuples[index].Row, cursor.Row);
                index++;
            }

            Assert.Equal(tuples.Count, index);
        }
    }

    private static bool Matches(string path, object value, int row)
    {
        if (PersonNull(row))
        {
            return false;
        }

        return path switch
        {
            "person.name" => Name(row) == (string)value,
            "person.age" => Age(row) == Convert.ToInt32(value, CultureInfo.InvariantCulture),
            _ => City(row) == (string)value,
        };
    }

    private sealed class Written : IAsyncDisposable
    {
        private Written(string path, VortexFile file, WriteReport report)
        {
            Path = path;
            File = file;
            Report = report;
        }

        internal string Path { get; }

        internal VortexFile File { get; }

        internal WriteReport Report { get; }

        internal static async Task<Written> CreateAsync(bool indexed)
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"vorticity-nested-{Guid.NewGuid():N}.vortex");
            WriteReport report = await WriteAsync(path, indexed);
            return new Written(path, await VortexFile.OpenAsync(path, CancellationToken.None), report);
        }

        public async ValueTask DisposeAsync()
        {
            await File.DisposeAsync();
            System.IO.File.Delete(Path);
        }

        internal static async Task<WriteReport> WriteAsync(string path, bool indexed)
        {
            DTypeArena types = new DTypeArena();
            DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
            DType utf8 = types.Utf8(Nullability.NonNullable);
            DType i32n = types.Primitive(PType.I32, Nullability.Nullable);
            DType address = types.Struct(["city"], [utf8], Nullability.NonNullable);
            DType person = types.Struct(["name", "age", "address"], [utf8, i32n, address], Nullability.Nullable);
            DType schema = types.Struct(["id", "person"], [i64, person], Nullability.NonNullable);
            VortexWriteOptions options = new VortexWriteOptions
            {
                RowBlockSize = Block,
                DataBlockTargetBytes = 1L << 16,
                IndexBudgetPerMille = 1_000_000,
                WritePolicy = indexed ? Policy : WritePolicy.None,
                KeyEncoder = new RowKeyEncoder(Asc),
            };

            await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
            const int Batch = 3_000;
            for (int start = 0; start < Rows; start += Batch)
            {
                CanonicalArena arena = new CanonicalArena();
                int count = Math.Min(Batch, Rows - start);
                int city = Strings(arena, utf8, start, count, City);
                int addressNode = arena.AddStruct(address, count, Validity.NonNullable, [city]);
                int[] fields =
                [
                    Strings(arena, utf8, start, count, Name),
                    Ages(arena, types, i32n, start, count),
                    addressNode,
                ];
                int personNode = arena.AddStruct(
                    person, count, Mask(arena, types, start, count, row => !PersonNull(row)), fields);
                int root = arena.AddStruct(schema, count, Validity.NonNullable, [Ids(arena, i64, start, count), personNode]);
                using RecordBatch record = new RecordBatch(arena, root, start);
                await writer.WriteAsync(record, CancellationToken.None);
            }

            return await writer.CompleteAsync(CancellationToken.None);
        }

        private static Validity Mask(CanonicalArena arena, DTypeArena types, int start, int count, Func<int, bool> valid)
        {
            VortexBuffer bits = arena.Allocate(Math.Max((count + 7) / 8, 1), 8, out Span<byte> raw);
            raw.Clear();
            for (int i = 0; i < count; i++)
            {
                if (valid(start + i))
                {
                    raw[i >> 3] |= (byte)(1 << (i & 7));
                }
            }

            return Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), count, Validity.NonNullable, bits, 0));
        }

        private static int Ids(CanonicalArena arena, DType dtype, int start, int count)
        {
            VortexBuffer buffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> bytes);
            Span<long> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes);
            for (int i = 0; i < count; i++)
            {
                values[i] = start + i;
            }

            return arena.AddPrimitive(dtype, count, Validity.NonNullable, PType.I64, buffer);
        }

        private static int Ages(CanonicalArena arena, DTypeArena types, DType dtype, int start, int count)
        {
            VortexBuffer buffer = arena.Allocate(count * sizeof(int), sizeof(int), out Span<byte> bytes);
            Span<int> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(bytes);
            for (int i = 0; i < count; i++)
            {
                values[i] = Age(start + i) ?? 0;
            }

            return arena.AddPrimitive(
                dtype, count, Mask(arena, types, start, count, row => Age(row) is not null), PType.I32, buffer);
        }

        private static int Strings(CanonicalArena arena, DType dtype, int start, int count, Func<int, string> value)
        {
            byte[][] values = new byte[count][];
            int heap = 0;
            for (int i = 0; i < count; i++)
            {
                values[i] = Encoding.UTF8.GetBytes(value(start + i));
                heap += values[i].Length > 12 ? values[i].Length : 0;
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
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(one[12..], offset);
                utf8.CopyTo(dataBytes[offset..]);
                offset += utf8.Length;
            }

            return arena.AddVarBinView(dtype, count, Validity.NonNullable, views, [data]);
        }
    }
}
