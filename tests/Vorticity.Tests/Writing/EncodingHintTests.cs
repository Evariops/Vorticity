// The two things a caller who knows their data gets:
// `PreferredBatchRows`, which says how to batch so nothing waits in transit, and `EncodingHint`,
// which pins a column's scheme.
//
// A HINT IS PLAN MEMORY WITH THE TOLERANCE SET TO INFINITY, so what it changes is what is
// PRICED, never what is legal: the hinted scheme is re-priced on every chunk's own statistics and
// written when it still applies, under every profile; a chunk it cannot describe is priced in full
// and the next chunk is offered the hint again. A progression still comes first, because no scheme
// beats a column that costs nothing per row; a run count does not, since runs cost to decode.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class EncodingHintTests
{
    private const int Rows = 16_384;
    private static readonly Guid Pinned = new Guid("29292929-2929-4929-8929-292929292929");

    // ------------------------------------------------------------------------- the batch size

    [Theory]
    [InlineData(8_192, 8_192)]
    [InlineData(512, 512)]
    [InlineData(null, 1)]
    public async Task PreferredBatchRowsIsTheBlockLength(int? rowBlock, int expected)
    {
        using MemoryStream stream = new MemoryStream();
        Fixture fixture = new Fixture(16);
        await using VortexFileWriter writer = VortexFileWriter.Create(
            new StreamSegmentSink(stream), fixture.Schema, new VortexWriteOptions { RowBlockSize = rowBlock });
        Assert.Equal(expected, writer.PreferredBatchRows);
    }

    [Theory]
    [InlineData(4_096, false)]
    [InlineData(4_095, true)]
    public async Task ABatchOfWholeBlocksWaitsForNothing(int batchRows, bool buffered)
    {
        // No byte target, so the block count is the whole condition: a batch of whole blocks is
        // written where it lies, one row short of them is carried.
        Decoders.EnsureRegistered();
        Fixture fixture = new Fixture(Rows);
        string path = Temp();
        try
        {
            VortexWriteOptions options = new VortexWriteOptions
            {
                RowBlockSize = 2_048,
                DataBlockTargetBytes = null,
                Identity = Pinned,
                WritePolicy = WritePolicy.None,
            };

            List<string> read;
            await using (VortexFileWriter writer = VortexFileWriter.Create(path, fixture.Schema, options))
            {
                Assert.Equal(2_048, writer.PreferredBatchRows);
                await fixture.WriteAsync(writer, batchRows);
                await writer.CompleteAsync(TestContext.Current.CancellationToken);
                Assert.Equal(buffered, writer.Buffered);
            }

            read = await ReadAsync(path);
            Assert.Equal(fixture.Rendered, read);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // ------------------------------------------------------------------------------ the hint

    [Fact]
    public async Task AHintPinsTheSchemeOfEveryChunkAndTheRowsStillReadBack()
    {
        Decoders.EnsureRegistered();
        Fixture fixture = new Fixture(Rows);
        (WriteReport plain, string plainPath) = await WriteAsync(fixture, hints: null);
        (WriteReport hinted, string hintedPath) = await WriteAsync(
            fixture, new Dictionary<string, EncodingHint> { ["dense"] = EncodingHint.Zstd });
        try
        {
            // Without a hint the dense column bit-packs, chunk after chunk.
            Assert.All(plain.Columns[0].Encodings, scheme => Assert.Equal("BitPacked", scheme));
            Assert.All(hinted.Columns[0].Encodings, scheme => Assert.Equal("Zstd", scheme));
            Assert.True(hinted.Columns[0].Encodings.Length > 1, "the fixture must write several chunks");

            // The memory held on every chunk, the first included: a pinned column starts with one,
            // where an unpinned column's first chunk has nothing to consult.
            Assert.Equal(hinted.Columns[0].PlansPriced, hinted.Columns[0].PlansHeld);
            Assert.Equal(hinted.Columns[0].Encodings.Length, hinted.Columns[0].PlansPriced);
            Assert.Equal(plain.Columns[0].Encodings.Length - 1, plain.Columns[0].PlansPriced);

            // The other column is untouched, and both files hold the same rows.
            Assert.Equal(plain.Columns[1].Encodings, hinted.Columns[1].Encodings);
            Assert.Equal(fixture.Rendered, await ReadAsync(hintedPath));
            Assert.NotEqual(new FileInfo(plainPath).Length, new FileInfo(hintedPath).Length);
        }
        finally
        {
            System.IO.File.Delete(plainPath);
            System.IO.File.Delete(hintedPath);
        }
    }

    [Fact]
    public async Task AChunkTheHintCannotDescribeIsPricedInFullAndTheNextIsOfferedItAgain()
    {
        // `text` is distinct strings on the first chunks and a handful of repeated ones on the
        // last: a dictionary loses, then wins.
        Decoders.EnsureRegistered();
        Fixture fixture = new Fixture(Rows);
        (WriteReport hinted, string path) = await WriteAsync(
            fixture, new Dictionary<string, EncodingHint> { ["text"] = EncodingHint.Dictionary });
        try
        {
            IReadOnlyList<string> schemes = hinted.Columns[1].Encodings;
            Assert.NotEqual(nameof(EncodingHint.Dictionary), schemes[0]);
            Assert.Equal(nameof(EncodingHint.Dictionary), schemes[^1]);
            Assert.Equal(fixture.Rendered, await ReadAsync(path));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AProgressionIsWrittenAsOneWhateverTheHintSays()
    {
        // The statistics answer a progression for nothing, and nothing beats it.
        Decoders.EnsureRegistered();
        Fixture fixture = new Fixture(Rows);
        (WriteReport hinted, string path) = await WriteAsync(
            fixture, new Dictionary<string, EncodingHint> { ["step"] = EncodingHint.Zstd });
        try
        {
            Assert.All(hinted.Columns[2].Encodings, scheme => Assert.Equal("Sequence", scheme));
            Assert.Equal(fixture.Rendered, await ReadAsync(path));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AHintComesBeforeTheRunCount()
    {
        // One row in a hundred true: the run count prices runs for nothing, which a bitmap reads
        // many times faster, and the caller who pins the bitmap gets it.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType flag = types.Bool(Nullability.NonNullable);
        DType schema = types.Struct(["flag"], [flag], Nullability.NonNullable);
        VortexBuffer buffer = arena.Allocate((Rows + 7) / 8, 64, out Span<byte> bits);
        bits.Clear();
        for (int row = 0; row < Rows; row += 100)
        {
            bits[row >> 3] |= (byte)(1 << (row & 7));
        }

        int column = arena.AddBool(flag, Rows, Validity.NonNullable, buffer, 0);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [column]);
        string runs = Temp();
        string pinned = Temp();
        try
        {
            WriteReport byRuns = await WriteOneAsync(runs, schema, arena, root, Options(null), ct);
            WriteReport byHint = await WriteOneAsync(
                pinned, schema, arena, root, Options(new Dictionary<string, EncodingHint> { ["flag"] = EncodingHint.Canonical }), ct);

            Assert.All(byRuns.Columns[0].Encodings, scheme => Assert.Equal("RunEnd", scheme));
            Assert.All(byHint.Columns[0].Encodings, scheme => Assert.Equal("Canonical", scheme));
            Assert.Equal(await ReadAsync(runs), await ReadAsync(pinned));
        }
        finally
        {
            System.IO.File.Delete(runs);
            System.IO.File.Delete(pinned);
        }
    }

    [Fact]
    public async Task AHintHoldsUnderTheSmallestProfile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        Fixture fixture = new Fixture(Rows);
        string smallest = Temp();
        string pinned = Temp();
        try
        {
            VortexWriteOptions sizeFirst = Options(null) with { Compression = CompressionProfile.Smallest };
            WriteReport bySize;
            await using (VortexFileWriter writer = VortexFileWriter.Create(smallest, fixture.Schema, sizeFirst))
            {
                await fixture.WriteAsync(writer, 4_096);
                bySize = await writer.CompleteAsync(ct);
            }

            WriteReport byHint;
            await using (VortexFileWriter writer = VortexFileWriter.Create(
                pinned, fixture.Schema, sizeFirst with { EncodingHints = new Dictionary<string, EncodingHint> { ["dense"] = EncodingHint.Canonical } }))
            {
                await fixture.WriteAsync(writer, 4_096);
                byHint = await writer.CompleteAsync(ct);
            }

            Assert.All(bySize.Columns[0].Encodings, scheme => Assert.NotEqual("Canonical", scheme));
            Assert.All(byHint.Columns[0].Encodings, scheme => Assert.Equal("Canonical", scheme));
            Assert.Equal(fixture.Rendered, await ReadAsync(pinned));
        }
        finally
        {
            System.IO.File.Delete(smallest);
            System.IO.File.Delete(pinned);
        }
    }

    [Fact]
    public async Task NoHintIsByteForByteTheWriteWithoutOne()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        Fixture fixture = new Fixture(Rows);
        (_, string plain) = await WriteAsync(fixture, hints: null);
        (_, string empty) = await WriteAsync(fixture, new Dictionary<string, EncodingHint>());
        (_, string auto) = await WriteAsync(
            fixture, new Dictionary<string, EncodingHint> { ["dense"] = EncodingHint.Auto });
        try
        {
            byte[] expected = await System.IO.File.ReadAllBytesAsync(plain, ct);
            Assert.Equal(expected, await System.IO.File.ReadAllBytesAsync(empty, ct));
            Assert.Equal(expected, await System.IO.File.ReadAllBytesAsync(auto, ct));
        }
        finally
        {
            System.IO.File.Delete(plain);
            System.IO.File.Delete(empty);
            System.IO.File.Delete(auto);
        }
    }

    [Fact]
    public void AHintNamingNothingIsRefusedWhereTheCallerCanSeeIt()
    {
        Fixture fixture = new Fixture(16);
        using MemoryStream stream = new MemoryStream();
        ArgumentException refused = Assert.Throws<ArgumentException>(() => VortexFileWriter.Create(
            new StreamSegmentSink(stream),
            fixture.Schema,
            new VortexWriteOptions
            {
                EncodingHints = new Dictionary<string, EncodingHint> { ["absent"] = EncodingHint.Zstd },
            }));
        Assert.Contains("'absent'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHintReachesANestedFieldAndTheOneColumnOfANonStructRoot()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType inner = types.Struct(["deep"], [i64], Nullability.NonNullable);
        DType schema = types.Struct(["outer"], [inner], Nullability.NonNullable);
        int values = Fixture.Longs(arena, i64, Rows, row => 1_000_000 + ((row * 7919L) % 100_003));
        int nested = arena.AddStruct(inner, Rows, Validity.NonNullable, [values]);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [nested]);

        string path = Temp();
        try
        {
            await using (VortexFileWriter writer = VortexFileWriter.Create(path, schema, Options(
                new Dictionary<string, EncodingHint> { ["outer.deep"] = EncodingHint.Zstd })))
            {
                using RecordBatch batch = new RecordBatch(arena, root, 0);
                await writer.WriteAsync(batch, ct);
                await writer.CompleteAsync(ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            Assert.Equal(Rows, file.RowCount);

            // The one column of a file whose root is not a struct answers to the empty path.
            string flat = Temp();
            try
            {
                await using (VortexFileWriter writer = VortexFileWriter.Create(flat, i64, Options(
                    new Dictionary<string, EncodingHint> { [string.Empty] = EncodingHint.Zstd })))
                {
                    using RecordBatch batch = new RecordBatch(arena, values, 0);
                    await writer.WriteAsync(batch, ct);
                    WriteReport report = await writer.CompleteAsync(ct);
                    Assert.All(report.Columns[0].Encodings, scheme => Assert.Equal("Zstd", scheme));
                }
            }
            finally
            {
                System.IO.File.Delete(flat);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AFloatWithNoShortDecimalFormIsWrittenAsAlpRdWhenAskedAndReadsBack()
    {
        // Sevenths have no short decimal form, so ALP refuses them and only the hint asks for the
        // split; `Auto` weighs it against zstd and the plain form instead.
        Decoders.EnsureRegistered();
        string plainPath = Temp();
        string hintedPath = Temp();
        try
        {
            (DType schema, CanonicalArena plainArena, int plainRoot) = Sevenths();
            await WriteOneAsync(plainPath, schema, plainArena, plainRoot, Options(null), CancellationToken.None);
            (_, CanonicalArena hintedArena, int hintedRoot) = Sevenths();
            WriteReport hinted = await WriteOneAsync(
                hintedPath, schema, hintedArena, hintedRoot,
                Options(new Dictionary<string, EncodingHint> { ["x"] = EncodingHint.AlpRd }), CancellationToken.None);

            Assert.NotEmpty(hinted.Columns[0].Encodings);
            Assert.All(hinted.Columns[0].Encodings, scheme => Assert.Equal(nameof(EncodingHint.AlpRd), scheme));
            Assert.Equal(await ReadAsync(plainPath), await ReadAsync(hintedPath));
        }
        finally
        {
            System.IO.File.Delete(plainPath);
            System.IO.File.Delete(hintedPath);
        }
    }

    /// <summary>One f64 column of sixteen thousand sevenths.</summary>
    private static (DType Schema, CanonicalArena Arena, int Root) Sevenths()
    {
        const int rows = 16_384;
        DTypeArena types = new DTypeArena();
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        DType schema = types.Struct(["x"], [f64], Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();
        Vorticity.Buffers.VortexBuffer values = arena.Allocate(rows * sizeof(double), sizeof(double), out Span<byte> destination);
        Span<double> doubles = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(destination);
        for (int row = 0; row < rows; row++)
        {
            doubles[row] = row / 7.0;
        }

        int column = arena.AddPrimitive(f64, rows, Validity.NonNullable, PType.F64, values);
        return (schema, arena, arena.AddStruct(schema, rows, Validity.NonNullable, [column]));
    }

    // ------------------------------------------------------------------------------ plumbing

    private static string Temp() =>
        Path.Combine(Path.GetTempPath(), $"vorticity-hint-{Guid.NewGuid():N}.vortex");

    private static VortexWriteOptions Options(IReadOnlyDictionary<string, EncodingHint>? hints) =>
        new VortexWriteOptions
        {
            RowBlockSize = 2_048,
            DataBlockTargetBytes = 1 << 14,
            Identity = Pinned,
            WritePolicy = WritePolicy.None,
            EncodingHints = hints,
        };

    private static async Task<WriteReport> WriteOneAsync(
        string path, DType schema, CanonicalArena arena, int root, VortexWriteOptions options, CancellationToken ct)
    {
        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
        using (RecordBatch batch = new RecordBatch(arena, root, 0))
        {
            await writer.WriteAsync(batch, ct);
        }

        return await writer.CompleteAsync(ct);
    }

    private static async Task<(WriteReport Report, string Path)> WriteAsync(
        Fixture fixture, IReadOnlyDictionary<string, EncodingHint>? hints)
    {
        string path = Temp();
        await using VortexFileWriter writer = VortexFileWriter.Create(path, fixture.Schema, Options(hints));
        await fixture.WriteAsync(writer, 4_096);
        return (await writer.CompleteAsync(), path);
    }

    private static async Task<List<string>> ReadAsync(string path)
    {
        List<string> rows = [];
        await using VortexFile file = await VortexFile.OpenAsync(path);
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync())
        {
            for (int row = 0; row < batch.RowCount; row++)
            {
                StringBuilder text = new StringBuilder();
                for (int field = 0; field < batch.FieldCount; field++)
                {
                    ListShapes.Render(batch.Column(field), row, text);
                    text.Append(' ');
                }

                rows.Add(text.ToString());
            }
        }

        return rows;
    }

    /// <summary>
    /// Three columns: dense integers a bit-packing wants, text a dictionary wants only at the end,
    /// and a progression nothing beats.
    /// </summary>
    private sealed class Fixture
    {
        private readonly CanonicalArena _arena = new CanonicalArena();
        private readonly int _root;
        private readonly int _rows;

        internal Fixture(int rows)
        {
            _rows = rows;
            DTypeArena types = new DTypeArena();
            DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
            DType utf8 = types.Utf8(Nullability.NonNullable);
            Schema = types.Struct(["dense", "text", "step"], [i64, utf8, i64], Nullability.NonNullable);

            // Dense but not a progression: the values fit seventeen bits over a frame of reference.
            int dense = Longs(_arena, i64, rows, row => 4_000_000 + ((row * 7919L) % 50_000));
            int text = Strings(
                _arena, utf8, rows,
                row => row < rows - 4_096
                    ? "value-" + (row * 7919 % 100_003).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "kind-" + (row % 4).ToString(System.Globalization.CultureInfo.InvariantCulture));
            int step = Longs(_arena, i64, rows, row => 700_000 + (row * 3L));
            _root = _arena.AddStruct(Schema, rows, Validity.NonNullable, [dense, text, step]);
            Rendered = [];
            using RecordBatch batch = new RecordBatch(_arena, _root, 0);
            for (int row = 0; row < rows; row++)
            {
                StringBuilder line = new StringBuilder();
                for (int field = 0; field < batch.FieldCount; field++)
                {
                    ListShapes.Render(batch.Column(field), row, line);
                    line.Append(' ');
                }

                Rendered.Add(line.ToString());
            }
        }

        internal DType Schema { get; }

        internal List<string> Rendered { get; }

        internal async Task WriteAsync(VortexFileWriter writer, int batchRows)
        {
            for (int start = 0; start < _rows; start += batchRows)
            {
                int count = Math.Min(batchRows, _rows - start);
                int slice = CanonicalSlice.SliceAcross(_arena, _arena, _root, start, count);
                using RecordBatch batch = new RecordBatch(_arena, slice, start);
                await writer.WriteAsync(batch);
            }
        }

        internal static int Longs(CanonicalArena arena, DType dtype, int rows, Func<int, long> value)
        {
            VortexBuffer buffer = arena.Allocate(rows * sizeof(long), sizeof(long), out Span<byte> bytes);
            Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
            for (int row = 0; row < rows; row++)
            {
                values[row] = value(row);
            }

            return arena.AddPrimitive(dtype, rows, Validity.NonNullable, PType.I64, buffer);
        }

        private static int Strings(CanonicalArena arena, DType dtype, int rows, Func<int, string> value)
        {
            byte[][] utf8 = new byte[rows][];
            int heap = 0;
            for (int row = 0; row < rows; row++)
            {
                utf8[row] = Encoding.UTF8.GetBytes(value(row));
                heap += utf8[row].Length > 12 ? utf8[row].Length : 0;
            }

            VortexBuffer data = arena.Allocate(Math.Max(heap, 1), 1, out Span<byte> dataBytes);
            VortexBuffer views = arena.Allocate(rows * 16, 16, out Span<byte> view);
            view.Clear();
            int offset = 0;
            for (int row = 0; row < rows; row++)
            {
                Span<byte> one = view.Slice(row * 16, 16);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(one, (uint)utf8[row].Length);
                if (utf8[row].Length <= 12)
                {
                    utf8[row].CopyTo(one[4..]);
                    continue;
                }

                utf8[row].AsSpan(0, 4).CopyTo(one[4..]);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(one[12..], offset);
                utf8[row].CopyTo(dataBytes[offset..]);
                offset += utf8[row].Length;
            }

            return arena.AddVarBinView(dtype, rows, Validity.NonNullable, views, [data]);
        }
    }
}
