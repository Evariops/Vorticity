// A deletion vector out of line: written once into the commit object that made it, as an index
// fragment is, and named by its entry by reference.
//
// THE READS ARE HELD ELSEWHERE: the deletion-vector oracle and the change fuzzer run with every vector
// out of line, and answer as a dataset rewriting its objects does. What these tests hold is the rest
// of a vector's life: its entry's bytes, the commit object it keeps alive and that a repack empties,
// a byte torn in it, and the leaf it no longer swells.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetVectorOutOfLineTests
{
    private static readonly string[] Cities = ["Paris", "Lyon", "Nice", "Lille"];

    [Fact]
    public void AnEntryNamesItsVectorByReferenceAndCountsItWithoutReadingIt()
    {
        ObjectEntry inline = new ObjectEntry(CommitKey.ForData("0000000a"), 7, 200, 4_096, 3)
            .WithDeletions(DeletionVector.Of([.. Enumerable.Range(0, 60).Select(row => (long)row * 3)]));
        byte[] encoded = inline.EncodedDeletions.ToArray();
        PageReference at = new PageReference(9, 100, encoded.Length, XxHash128.HashToUInt128(encoded));
        byte[] bytes = inline.WithVectorAt(at).ToBytes();

        ObjectEntry read = ObjectEntry.FromBytes(bytes.AsMemory());
        Assert.Equal((at, false, 60L, encoded.Length, 0), (read.VectorAt, read.IsResolved, read.DeletedRows, read.VectorBytes, read.InlineVectorBytes));
        Assert.Throws<InvalidOperationException>(() => read.Deletions);
        Assert.True(read.SameDeletions(inline));
        Assert.Equal(inline.ToBytes().Length - encoded.Length + PageReference.Bytes - TreePage.VarintBytes((ulong)encoded.Length) + 1, bytes.Length);

        // Read in place, as a page's tally and a repack read it.
        Assert.Equal(encoded.Length, ObjectEntry.TallyOf(bytes).VectorBytes);
        Assert.True(ObjectEntry.NamesAny(bytes, [9]));
        Assert.False(ObjectEntry.NamesAny(bytes, [8]));
        Assert.True(read.NamesAny([9]));

        // A length of zero is a reference, to the entry's end, or the entry is refused.
        Assert.Throws<CommitFormatException>(() => ObjectEntry.FromBytes(bytes.AsMemory(0, bytes.Length - 1)));
    }

    [Fact]
    public async Task AVectorKeepsTheCommitObjectItLiesInUntilARepackMovesIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
        await using MemoryObjectStore store = new MemoryObjectStore { TimeProvider = clock };
        await using VortexDataset dataset = await CreateAsync(store, Options() with { RetentionWindow = TimeSpan.FromHours(1), TimeProvider = clock }, ct);

        // The first delete writes its vector and the leaf; the second writes the leaf again, so that
        // the first's commit object holds nothing live but the vector.
        await dataset.DeleteAsync(EveryOther(0, 10), ct);
        ulong first = dataset.Version;
        await dataset.DeleteAsync(EveryOther(1_000, 1_010), ct);
        Assert.Equal(first, VectorOf(await EntriesAsync(dataset), 0).VectorAt.Version);

        clock.Advance(TimeSpan.FromHours(2));
        VacuumResult kept = await dataset.VacuumAsync(new VacuumOptions { TimeProvider = clock, RepackBelow = 1.0 }, ct);
        Assert.DoesNotContain(CommitKey.For(first), kept.Deleted);
        Assert.Contains(first, kept.Sparse);

        (ulong repacked, OperationOutcome outcome) = await dataset.RepackAsync([first], ct);
        Assert.Equal(OperationOutcome.Applied, outcome);
        Assert.Equal(repacked, VectorOf(await EntriesAsync(dataset), 0).VectorAt.Version);
        Assert.Equal(90, await RowsAsync(dataset, ct));
        Assert.True((await dataset.VerifyAsync(cancellationToken: ct)).Holds);

        clock.Advance(TimeSpan.FromHours(2));
        Assert.Contains(CommitKey.For(first), (await dataset.VacuumAsync(new VacuumOptions { TimeProvider = clock }, ct)).Deleted);
        await using VortexDataset fresh = await VortexDataset.OpenAsync(store, Options(), ct);
        Assert.Equal(90, await RowsAsync(fresh, ct));
    }

    [Fact]
    public async Task AVectorTornInItsCommitObjectIsRefusedByReadersAndNamedByVerify()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, Options(), ct);
        await dataset.DeleteAsync(EveryOther(0, 10), ct);
        await dataset.DeleteAsync(EveryOther(1_000, 1_010), ct);
        PageReference vector = VectorOf(await EntriesAsync(dataset), 0).VectorAt;

        // A byte of the vector flipped where it lies.
        string key = CommitKey.For(vector.Version);
        long length = (await store.HeadAsync(key, ct))!.Value.Length;
        byte[] bytes;
        using (ObjectRange range = await store.GetRangeAsync(key, 0, (int)length, ct))
        {
            bytes = range.Bytes.ToArray();
        }

        CommitObject commit = CommitObject.Open(bytes, bytes.Length);
        bytes[(int)(commit.HeaderEnd + vector.Offset)] ^= 0x01;
        await store.DeleteAsync([key], ct);
        await store.PutIfAbsentAsync(key, bytes, ct);

        await using VortexDataset cold = await VortexDataset.OpenAsync(store, Options(), ct);
        await Assert.ThrowsAsync<TornCommitException>(() => RowsAsync(cold, ct));
        DatasetVerification verified = await cold.VerifyAsync(cancellationToken: ct);
        Assert.False(verified.Holds);
        Assert.Contains(verified.Problems, problem => problem.Contains(FormattableString.Invariant($"its vector in version {vector.Version}"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALeafHoldsAReferenceWhereItHeldAVector()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Dictionary<int, int> leaves = [];
        foreach (int inline in (int[])[1, 1 << 20])
        {
            await using MemoryObjectStore store = new MemoryObjectStore();
            await using VortexDataset dataset = await CreateAsync(store, Options() with { InlineVectorBytes = inline }, ct, rows: 400);
            await dataset.DeleteAsync(EveryOther(0, 400), ct);
            await dataset.DeleteAsync(EveryOther(1_000, 1_400), ct);
            DatasetTree level = dataset.Levels[0];
            leaves[inline] = (await dataset.Pages.ReadPageAsync(level.Root, ct)).Length;
        }

        // Two vectors of two hundred runs each: in line, the leaf carries both, out of line a reference each.
        Assert.True(leaves[1] < leaves[1 << 20] - 500, $"a leaf of {leaves[1]} bytes against {leaves[1 << 20]}");
    }

    private static DatasetOptions Options() => new DatasetOptions
    {
        Seed = 0x0FF_11E,
        ClusteringKey = ["Key"],
        Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 2 << 10 },
        MarkedObjectBytes = 0,
        MarkedShare = 1,
        InlineVectorBytes = 1,
    };

    /// <summary>The keys of <c>[from, to)</c> that the even ones are, as one filter.</summary>
    private static VortexExpr EveryOther(long from, long to)
    {
        VortexExpr? any = null;
        for (long key = from; key < to; key += 2)
        {
            VortexExpr one = Expr.Eq(Expr.Field("Key"), Expr.Literal(FilterLiteral.From(key)));
            any = any is null ? one : Expr.Or(any, one);
        }

        return any!;
    }

    /// <summary>The entry of the object whose keys start at <paramref name="first"/>.</summary>
    private static ObjectEntry VectorOf(List<PositionedObject> objects, long first) =>
        objects.Single(held => held.Entry.Summaries.TryGet("Key", out ColumnSummary key) && key.Min.Equals(FilterLiteral.From(first))).Entry;

    private static async Task<List<PositionedObject>> EntriesAsync(VortexDataset dataset)
    {
        List<PositionedObject> objects = [];
        await foreach (PositionedObject held in dataset.WalkAsync(null, 0, long.MaxValue, null, default))
        {
            objects.Add(held);
        }

        return objects;
    }

    /// <summary>A clustered dataset of two objects of <paramref name="rows"/> rows in level 0, keys from 0 and from 1 000.</summary>
    private static async Task<VortexDataset> CreateAsync(IObjectStore store, DatasetOptions options, CancellationToken ct, int rows = 50)
    {
        Decoders.EnsureRegistered();
        VortexDataset dataset = await VortexDataset.CreateAsync(store, ChangeRow.Schema, options, ct);
        foreach (int part in (int[])[0, 1])
        {
            ChangeRow[] objectRows = [.. Enumerable.Range(0, rows).Select(i => new ChangeRow((part * 1_000L) + i, i * 0.5, Cities[i % Cities.Length]))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<ChangeRow>(objectRows, ct);
            await dataset.AppendAsync(draft, ct);
        }

        return dataset;
    }

    private static async Task<long> RowsAsync(VortexDataset dataset, CancellationToken ct) =>
        (await dataset.Scan<ChangeRow>().ToRecordsAsync(ct).ToListAsync(ct)).Count;
}
