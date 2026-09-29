// `IndexPolicy.Auto`: every cheap builder starts at block 0, and the
// statistics and the budget decide what survives.
//
// ONE COLUMN PER VERDICT, and the verdicts are arithmetic. A Bloom filter at 1 % costs about 1,2
// bytes per distinct value, and `Auto` keeps it under 2 % of its column's bytes -- so it pays on wide
// values that do not compress, and nowhere near a column that bit-packs to a byte a row. `Auto`
// builds no postings: the measurement that decided it is in IndexWriter. So:
//
//   ts      sorted, unique     Bloom given up inside its first block: its distinct values alone
//                              outweigh the share, before the zone map could say it climbs
//   blob    unique, 384 random Bloom kept: leaves, generations and a root (1,4 % of a column that does not compress)
//   tenant  two per block      no block filter (under the floor); generation filters kept
//   status  five values        dictionary probe kept; Bloom has nothing to hold
//
// And whatever survives, the scan returns the same rows with the indexes on and off.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class AutoIndexTests
{
    private const int Block = 1_024;
    private const int Chunk = 8 * Block;
    private const int Chunks = 6;
    private const int Rows = Chunk * Chunks;
    /// <summary>
    /// Wide enough that a Bloom filter pays: at 1 % a block's filter is 2 KiB after the power-of-two
    /// rounding, its generation's the same again per block, and the root of the three generations
    /// two thirds of that again (a level costs what the one below it costs when the values
    /// do not repeat). 384 incompressible bytes a row make that 1,4 % of the column, under `Auto`'s
    /// 2 %; 256 made it 2,1 % once the root was built, and 1,6 % before.
    /// </summary>
    private const int BlobLength = 384;

    private static readonly string[] Names = ["ts", "blob", "tenant", "status"];
    private static readonly string[] Statuses = ["open", "closed", "pending", "void", "held"];

    private static long Ts(int row) => 1_700_000_000_000L + (row * 7L) + (row % 3);

    private static ulong Mix(ulong x)
    {
        x ^= x >> 33;
        x *= 0xFF51AFD7ED558CCDUL;
        x ^= x >> 33;
        x *= 0xC4CEB9FE1A85EC53UL;
        return x ^ (x >> 33);
    }

    /// <summary>
    /// <see cref="BlobLength"/> bytes no compressor can shorten, unique per row. The offset takes
    /// sixteen bits of the mixed word: at eight, the words past byte 255 of a row were another row's.
    /// </summary>
    private static byte[] Blob(int row)
    {
        byte[] bytes = new byte[BlobLength];
        for (int i = 0; i < BlobLength; i += sizeof(ulong))
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                bytes.AsSpan(i), Mix(((ulong)row << 16) | (uint)i));
        }

        return bytes;
    }

    /// <summary>Two tenants per block, out of five hundred, alternating; no order across blocks.</summary>
    private static long Tenant(int row)
    {
        int block = row / Block;
        return (long)(Mix((ulong)(block * 2 + (row % 2))) % 500);
    }

    private static string Status(int row) => Statuses[(row * 7919) % Statuses.Length];

    [Fact]
    public async Task EachColumnKeepsWhatPaysAndReportsWhatDidNot()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(WritePolicy.Auto);
        WriteReport report = written.Report;

        IndexWriteReport tsBloom = Find(report, "ts", IndexKinds.BloomSbbf);
        Assert.Equal(IndexOutcome.Abandoned, tsBloom.Outcome);
        Assert.StartsWith("Auto gave it up", tsBloom.Reason, StringComparison.Ordinal);
        Assert.Equal(0, tsBloom.Bytes);

        IndexWriteReport blobBloom = Find(report, "blob", IndexKinds.BloomSbbf);
        Assert.True(blobBloom.Outcome == IndexOutcome.Built, blobBloom.Reason);
        Assert.True(blobBloom.Runs > 0);

        // Two tenants a block is under the floor of a block filter, and thirty-odd a generation is
        // over it: the tree that survives has bare generation nodes -- no leaf -- under a root.
        IndexWriteReport tenantBloom = Find(report, "tenant", IndexKinds.BloomSbbf);
        Assert.True(tenantBloom.Outcome == IndexOutcome.Built, tenantBloom.Reason);
        Assert.Equal(1, tenantBloom.Runs);
        Assert.Equal((Rows / Block / 16) + 1, tenantBloom.Generations);

        Assert.Equal(IndexOutcome.Abandoned, Find(report, "status", IndexKinds.BloomSbbf).Outcome);
        IndexWriteReport statusProbe = Find(report, "status", IndexKinds.DictProbe);
        Assert.True(statusProbe.Outcome == IndexOutcome.Built, statusProbe.Reason);

        // Neither postings nor sorted runs are ever Auto's.
        Assert.DoesNotContain(report.Indexes, index => index.Kind is IndexKinds.SortedRuns or IndexKinds.PostingsBlocks);
    }

    [Fact]
    public async Task AFileIndexedAfterItsWriteJudgesEachColumnAgainstItsOwnBytes()
    {
        // The indexer adds each chunk's bytes to its own column, as the writer does: the blob's
        // filter is 1,4 % of the blob's bytes and kept, and would be many times the share of any
        // other column's.
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(WritePolicy.None);
        IndexFragment fragment = await VortexFileIndexer.BuildFragmentAsync(
            written.File, WritePolicy.Auto, new RowRange(0, Rows), storeToken: "after", cancellationToken: TestContext.Current.CancellationToken);

        IndexWriteReport blobBloom = FindIn(fragment.Reports, "blob", IndexKinds.BloomSbbf);
        Assert.True(blobBloom.Outcome == IndexOutcome.Built, blobBloom.Reason);
        Assert.Equal(IndexOutcome.Abandoned, FindIn(fragment.Reports, "ts", IndexKinds.BloomSbbf).Outcome);
    }

    [Fact]
    public void AFirstBlockThatClimbsGivesTheBloomUpOnlyUnderAuto()
    {
        using BloomBuilder auto = new BloomBuilder(IndexSpec.Bloom()) { AutoShare = IndexWriter.AutoBloomShare };
        using BloomBuilder asked = new BloomBuilder(IndexSpec.Bloom());
        auto.FirstBlock(sorted: true);
        asked.FirstBlock(sorted: true);

        Assert.Contains("sorted", auto.Abandoned, StringComparison.Ordinal);
        Assert.Null(asked.Abandoned);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABloomOverBlocksThatHoldOneSetIsGivenUp(bool shifted)
    {
        // Twenty values cycling: every block holds all twenty, and a block filter would say "maybe"
        // for each of them everywhere. Shifted by the block, the sets differ and the filter prunes.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(["cycle"], [i64], Nullability.NonNullable);
        VortexBuffer buffer = arena.Allocate(Chunk * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int row = 0; row < Chunk; row++)
        {
            values[row] = (row % 20) + (shifted ? row / Block : 0);
        }

        int column = arena.AddPrimitive(i64, Chunk, Validity.NonNullable, PType.I64, buffer);
        using RecordBatch batch = new RecordBatch(
            arena, arena.AddStruct(schema, Chunk, Validity.NonNullable, [column]), 0);
        await using VortexFileWriter writer = VortexFileWriter.Create(
            new StreamSegmentSink(System.IO.Stream.Null), schema,
            new VortexWriteOptions { RowBlockSize = Block, DataBlockTargetBytes = null, IndexBudgetPerMille = 1_000_000, WritePolicy = WritePolicy.Auto });
        await writer.WriteAsync(batch, CancellationToken.None);
        IndexWriteReport bloom = Find(await writer.CompleteAsync(CancellationToken.None), "cycle", IndexKinds.BloomSbbf);

        if (shifted)
        {
            // It bit-packs to a few kilobytes, so the written-bytes share may still condemn it; the
            // one verdict it must not get is this test's.
            Assert.DoesNotContain("same", bloom.Reason ?? string.Empty, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(IndexOutcome.Abandoned, bloom.Outcome);
            Assert.Contains("same 20 values", bloom.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheDefaultWritesNoIndexAndAutoIsAskedFor()
    {
        Decoders.EnsureRegistered();
        Assert.Equal(WritePolicy.None, new VortexWriteOptions().WritePolicy);
        await using Written plain = await Written.CreateAsync(new VortexWriteOptions().WritePolicy);
        Assert.False(plain.File.HasIndexDirectory);
        Assert.Empty(plain.Report.Indexes);

        await using Written auto = await Written.CreateAsync(WritePolicy.Auto);
        Assert.True(auto.File.HasIndexDirectory);
        Assert.True(Find(auto.Report, "blob", IndexKinds.BloomSbbf).Outcome == IndexOutcome.Built);
    }

    [Fact]
    public async Task AnAbandonedIndexCostsLittleAndTheDataNothing()
    {
        // Auto judges after every chunk, so a builder it gives up on stops after the chunk that
        // condemned it: what already went out is the whole waste.
        Decoders.EnsureRegistered();
        await using Written auto = await Written.CreateAsync(WritePolicy.Auto);
        await using Written none = await Written.CreateAsync(WritePolicy.None);
        long surviving = 0;
        foreach (IndexWriteReport index in auto.Report.Indexes)
        {
            surviving += index.Bytes;
        }

        long wasted = auto.Report.Bytes.Indexes - surviving;
        Assert.True(wasted >= 0);
        Assert.True(
            wasted * 20 < auto.Report.Bytes.Data,
            string.Create(CultureInfo.InvariantCulture, $"{wasted} bytes wasted against {auto.Report.Bytes.Data} of data"));

        // The data is the same; only the alignment padding in front of a segment can move, by less
        // than a segment's alignment per chunk and column.
        long moved = Math.Abs(auto.Report.Bytes.Data - none.Report.Bytes.Data);
        Assert.InRange(moved, 0, 64L * Chunks * Names.Length);
    }

    public static TheoryData<string> Filters() =>
    [
        string.Create(CultureInfo.InvariantCulture, $"tenant = {Tenant(3 * Block)}"),
        "tenant = 501",
        "status = held",
        "status = nope",
        "blob = " + Convert.ToHexString(Blob(12_345)),
        "blob = 00",
        string.Create(CultureInfo.InvariantCulture, $"ts = {Ts(700)}"),
    ];

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task AnAutoFileReadsTheSameRowsWithTheIndexesOnAndOff(string text)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(WritePolicy.Auto);
        VortexExpr filter = Parse(text);
        long expected = Oracle(text);
        Assert.Equal(expected, await written.File.ScanBuilder().Where(filter).CountAsync(ct));
        Assert.Equal(expected, await written.File.ScanBuilder().Where(filter).WithIndexes(false).CountAsync(ct));
    }

    [Fact]
    public async Task TheSurvivingIndexesPruneWhatTheZoneMapCannot()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(WritePolicy.Auto);

        // The tenant's generation filters keep the generations that hold it, sixteen blocks each, and
        // the zone map -- narrow here, two tenants a block -- cuts inside them.
        long tenant = Tenant(3 * Block);
        ScanExplanation byTenant = await written.File.ScanBuilder().Where(Parse(
            string.Create(CultureInfo.InvariantCulture, $"tenant = {tenant}"))).ExplainAsync(ct);
        int generations = 0;
        for (int g = 0; g * 16 < Rows / Block; g++)
        {
            int first = g * 16 * Block;
            if (Holding(row => row >= first && row < first + (16 * Block) && Tenant(row) == tenant) > 0)
            {
                generations++;
            }
        }

        Assert.InRange(byTenant.LiveBlocks, Holding(row => Tenant(row) == tenant), generations * 16);
        Assert.Contains(byTenant.Pruning, step => step.Structure == "bloom filter" && step.BlocksPruned > 0);

        ScanExplanation byBlob = await written.File.ScanBuilder().Where(Parse("blob = " + Convert.ToHexString(Blob(12_345)))).ExplainAsync(ct);
        Assert.InRange(byBlob.LiveBlocks, 1, 4);
    }

    private static IndexWriteReport Find(WriteReport report, string path, string kind) =>
        report.Index(path, kind) ?? throw new InvalidOperationException($"no report for {path}/{kind}");

    private static IndexWriteReport FindIn(IReadOnlyList<IndexWriteReport> reports, string column, string kind) =>
        reports.FirstOrDefault(report => report.Column == column && report.Kind == kind)
        ?? throw new InvalidOperationException($"no report for {column}/{kind}");

    private static int Holding(Func<int, bool> predicate)
    {
        HashSet<int> blocks = [];
        for (int row = 0; row < Rows; row++)
        {
            if (predicate(row))
            {
                blocks.Add(row / Block);
            }
        }

        return blocks.Count;
    }

    private static long Oracle(string text)
    {
        string[] parts = text.Split(' ');
        long count = 0;
        for (int row = 0; row < Rows; row++)
        {
            bool match = parts[0] switch
            {
                "status" => Status(row) == parts[2],
                "blob" => Convert.ToHexString(Blob(row)) == parts[2],
                "tenant" => Tenant(row) == long.Parse(parts[2], CultureInfo.InvariantCulture),
                _ => Ts(row) == long.Parse(parts[2], CultureInfo.InvariantCulture),
            };
            count += match ? 1 : 0;
        }

        return count;
    }

    private static VortexExpr Parse(string text)
    {
        string[] parts = text.Split(' ');
        FilterLiteral literal = parts[0] switch
        {
            "status" => FilterLiteral.From(parts[2]),
            "blob" => FilterLiteral.From(Convert.FromHexString(parts[2])),
            _ => FilterLiteral.From(long.Parse(parts[2], CultureInfo.InvariantCulture)),
        };
        return Expr.Eq(Expr.Field(parts[0]), Expr.Literal(literal));
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

        internal static async Task<Written> CreateAsync(WritePolicy policy)
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"vorticity-auto-{Guid.NewGuid():N}.vortex");
            WriteReport report = await WriteAsync(path, new VortexWriteOptions
            {
                RowBlockSize = Block,
                DataBlockTargetBytes = null,
                WritePolicy = policy,
            });
            return new Written(path, await VortexFile.OpenAsync(path), report);
        }

        public async ValueTask DisposeAsync()
        {
            await File.DisposeAsync();
            System.IO.File.Delete(Path);
        }

        private static async Task<WriteReport> WriteAsync(string path, VortexWriteOptions options)
        {
            DTypeArena types = new DTypeArena();
            CanonicalArena arena = new CanonicalArena();
            DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
            DType utf8 = types.Utf8(Nullability.NonNullable);
            DType binary = types.Binary(Nullability.NonNullable);
            DType schema = types.Struct(Names, [i64, binary, i64, utf8], Nullability.NonNullable);

            await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
            for (int start = 0; start < Rows; start += Chunk)
            {
                int[] columns =
                [
                    Longs(arena, i64, start, Chunk, Ts),
                    Views(arena, binary, start, Chunk, Blob),
                    Longs(arena, i64, start, Chunk, Tenant),
                    Views(arena, utf8, start, Chunk, row => Encoding.UTF8.GetBytes(Status(row))),
                ];
                int root = arena.AddStruct(schema, Chunk, Validity.NonNullable, columns);
                using RecordBatch batch = new RecordBatch(arena, root, start);
                await writer.WriteAsync(batch, CancellationToken.None);
            }

            return await writer.CompleteAsync(CancellationToken.None);
        }

        private static int Longs(CanonicalArena arena, DType dtype, int start, int count, Func<int, long> value)
        {
            VortexBuffer buffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> bytes);
            Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
            for (int i = 0; i < count; i++)
            {
                values[i] = value(start + i);
            }

            return arena.AddPrimitive(dtype, count, Validity.NonNullable, PType.I64, buffer);
        }

        private static int Views(CanonicalArena arena, DType dtype, int start, int count, Func<int, byte[]> value)
        {
            List<byte[]> values = [];
            int heap = 0;
            for (int i = 0; i < count; i++)
            {
                byte[] bytes = value(start + i);
                values.Add(bytes);
                heap += bytes.Length > 12 ? bytes.Length : 0;
            }

            VortexBuffer data = arena.Allocate(Math.Max(heap, 1), 1, out Span<byte> dataBytes);
            VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> viewBytes);
            viewBytes.Clear();
            int offset = 0;
            for (int i = 0; i < count; i++)
            {
                byte[] bytes = values[i];
                Span<byte> view = viewBytes.Slice(i * 16, 16);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view, bytes.Length);
                if (bytes.Length <= 12)
                {
                    bytes.CopyTo(view[4..]);
                    continue;
                }

                bytes.AsSpan(0, 4).CopyTo(view[4..]);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view[8..], 0);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view[12..], offset);
                bytes.CopyTo(dataBytes[offset..]);
                offset += bytes.Length;
            }

            return arena.AddVarBinView(dtype, count, Validity.NonNullable, views, [data]);
        }
    }
}
