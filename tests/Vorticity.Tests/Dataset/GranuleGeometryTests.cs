// The geometry of a dataset object's chunks (spec N, N8 and N9): each chunk's encoding tree and, for a buffer whose rows
// take bytes of their own length, where each granule starts in it; and the reads of a part of a chunk by its granules.
//
// THE ORACLE IS THE FILE AND THE ROWS: a tree is the bytes its chunk's segment ends with, read from the store; a mark starts
// its chunk's first granule at 0 and never goes back; the rows a read of some granules returns are those LINQ keeps, while
// it reads a small part of the object.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.Arrays;
using Vorticity.Layouts;
using Vorticity.Serialization.Schemas;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class GranuleGeometryTests : IClassFixture<GranuleGeometryTests.Fixture>
{
    private static readonly DateTimeOffset Noon = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly Fixture _fixture;

    public GranuleGeometryTests(Fixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The merged object keeps each column's geometry: a tree for each of its chunks, the bytes its segment ends with, and for
    /// the message, compressed by FSST, a mark a chunk that starts each of its granules in the compressed bytes, the first at
    /// 0, none going back.
    /// </summary>
    [Fact]
    public async Task EachChunksTreeIsTheOneItsSegmentEndsWithAndItsMarksStartItsGranules()
    {
        await using VortexDataset dataset = await VortexDataset.OpenAsync(_fixture.Store, new DatasetOptions(), Ct);
        PositionedObject held = await dataset.ScanBuilder().ObjectsAsync(Ct).SingleAsync(Ct);
        int[] fields = [.. Enumerable.Range(0, held.Entry.Contents!.Marks.Count)];
        ColumnMarks?[] marks = await dataset.ReadMarksAsync(held.Entry, fields, Ct);
        await using ObjectLease lease = await dataset.RentAsync(held.Entry, Ct);
        LayoutTree tree = lease.File.LayoutTree;
        int trees = 0;
        int marked = 0;
        foreach (int field in fields)
        {
            ColumnMarks column = marks[field] ?? throw new InvalidOperationException($"column {field} keeps no geometry");
            List<LayoutNode> leaves = [];
            Leaves(tree.Root.GetChild(field), leaves);
            Assert.Equal(leaves.Count, column.Chunks.Length);
            for (int c = 0; c < leaves.Count; c++)
            {
                SegmentSpec spec = lease.File.SegmentSpecs[(int)leaves[c].Segments[0]];
                byte[] segment = new byte[spec.Length];
                await _fixture.Store.ReadAsync(held.Entry.Key, (long)spec.Offset, segment, null, Ct);
                int length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(segment.AsSpan(segment.Length - 4));
                Assert.True(segment.AsSpan(segment.Length - 4 - length, length).SequenceEqual(column.Chunks[c].Tree.Span), $"column {field}, chunk {c}: the tree is not the segment's");
                trees++;

                int granules = (int)((leaves[c].RowCount + column.GranuleRows - 1) / column.GranuleRows);
                foreach (ChunkMark mark in column.Chunks[c].Marks)
                {
                    Assert.Equal(granules, mark.Starts.Length);
                    Assert.Equal(0, mark.Starts[0]);
                    Assert.True(Ascending(mark.Starts), $"column {field}, chunk {c}: a start goes back");
                    marked++;
                }
            }
        }

        Console.WriteLine($"GEOMETRY: {trees} trees equal to their segments' tails, {marked} chunks' marks, granules of {marks[0]!.GranuleRows} rows");
        Assert.True(marked > 0, "no chunk of the message keeps marks");
    }

    /// <summary>
    /// The calls of tenants drawn at random, each a run of the object's rows a granule or two long: each read of its rows,
    /// which reads its granules alone in each column, returns the rows LINQ keeps, row for row, and all of them read a small
    /// part of the object.
    /// </summary>
    [Fact]
    public async Task AReadOfSomeGranulesReturnsTheRowsOfTheirChunks()
    {
        await using CountingObjectStore store = new CountingObjectStore(_fixture.Store);
        await using VortexSession session = VortexSession.Create(_ => { });
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct);
        PositionedObject held = await dataset.ScanBuilder().ObjectsAsync(Ct).SingleAsync(Ct);
        Random random = new Random(0x6E1);
        long read = 0;
        const int Asked = 30;
        for (int i = 0; i < Asked; i++)
        {
            string tenant = Tenant(random.Next(Tenants));
            store.Reset();
            List<Event> rows = await dataset.Scan<Event>().Where(e => e.Tenant == tenant).ToRecordsAsync(Ct).ToListAsync(Ct);
            read += store.BytesRead;
            Assert.Equal(
                _fixture.Rows.Where(e => e.Tenant == tenant).OrderBy(e => e.At).Select(e => e.ToString()),
                rows.OrderBy(e => e.At).Select(e => e.ToString()));
        }

        Console.WriteLine($"GRANULES: {Asked} tenants read in {read:N0} bytes, {read / Asked:N0} each, of an object of {held.Entry.Bytes:N0}");
        Assert.True(read / Asked * 8 < held.Entry.Bytes, $"{read / Asked} bytes a tenant of an object of {held.Entry.Bytes}: whole chunks were read");
    }

    private const int Tenants = 300;

    private static string Tenant(int index) => $"tenant-{index:D3}";

    private static bool Ascending(long[] starts)
    {
        for (int g = 1; g < starts.Length; g++)
        {
            if (starts[g] < starts[g - 1])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The flat chunks of a column's layout, in the order its writer wrote them: under its zones and its chunking.</summary>
    private static void Leaves(LayoutNode node, List<LayoutNode> into)
    {
        switch (node.Encoding)
        {
            case LayoutEncodingId.Flat:
                into.Add(node);
                break;
            case LayoutEncodingId.Chunked:
                for (int c = 0; c < node.ChildCount; c++)
                {
                    Leaves(node.GetChild(c), into);
                }

                break;
            case LayoutEncodingId.Dict:
                Leaves(node.GetChild(1), into);
                break;
            default:
                Leaves(node.GetChild(0), into);
                break;
        }
    }

    /// <summary>
    /// 300 000 calls of 300 tenants 7 ms apart, keyed by tenant then instant, the message compressed by FSST: three appends
    /// merged into one object that keeps its contents and its geometry.
    /// </summary>
    public sealed class Fixture : IAsyncLifetime
    {
        internal MemoryObjectStore Store { get; } = new MemoryObjectStore();

        internal Event[] Rows { get; private set; } = [];

        public async ValueTask InitializeAsync()
        {
            Random random = new Random(0x6E0);
            Rows = [.. Enumerable.Range(0, 300_000).Select(i => new Event(
                Tenant(random.Next(Tenants)),
                Noon.AddMilliseconds(i * 7),
                $"GET /api/v2/items/{random.Next(100_000)}?page={random.Next(50)} HTTP/1.1 200",
                random.NextInt64(1L << 40)))];
            await using VortexDataset dataset = await VortexDataset.CreateAsync(Store, Event.Schema, new DatasetOptions
            {
                Layout = new DatasetLayout
                {
                    SortKey = [LayoutValue.Column(nameof(Event.Tenant)), LayoutValue.Column(nameof(Event.At))],
                    IndexedPrefix = 1,
                    Organization = LayoutOrganization.Leveled,
                },
                ContentsObjectBytes = 1,
                Write = new VortexWriteOptions { Hints = ImmutableDictionary<string, EncodingHint>.Empty.Add(nameof(Event.Message), EncodingHint.Fsst) },
            }, TestContext.Current.CancellationToken);
            for (int from = 0; from < Rows.Length; from += 100_000)
            {
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Event>(Rows[from..(from + 100_000)], TestContext.Current.CancellationToken);
                await dataset.AppendAsync(draft, TestContext.Current.CancellationToken);
            }

            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1, TargetBytesAtLevelOne = 1L << 30 }, TestContext.Current.CancellationToken) is not null)
            {
            }
        }

        public async ValueTask DisposeAsync() => await Store.DisposeAsync();
    }

    [VortexRecord]
    public partial record struct Event(string Tenant, DateTimeOffset At, string Message, long Size);
}
