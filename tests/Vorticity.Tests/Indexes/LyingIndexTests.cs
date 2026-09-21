// Lying indexes: forged runs that are unsorted, keys outside their stated min/max, rows at or
// beyond RowCount, overlapping runs. Class I fields throw VortexFormatException before any read
// out of bounds; Class II lies produce a wrong order and never a fault, and VerifyStatistics
// catches them.
//
// THE DIRECTORY'S OWN LIES — overlapping or out-of-order runs, rows past the file, payloads past
// the data — are refused at parse, and IndexDirectoryTests holds each one. What is left is the
// PAYLOAD: bytes the directory points at and cannot vouch for. Two campaigns feed them to every
// reader of indexes this library has — the pruning chain under filters, the exact cover of a
// count, key cursors with and without rows, a distinct walk, and a scan in key order:
//
//   * random mutations inside the payload regions of a written file, a fixed seed, every
//     iteration held to the fuzzer's invariant (only a format or unsupported exception escapes,
//     within a time budget), with and without VerifyStatistics;
//   * structured lies built from the file's own payloads: two segments of a sorted run swap their
//     keys — well-formed arrays whose keys sit outside the bounds the directory states and out of
//     order across the seam — and a run's rows are replaced by another run's. The walk without
//     VerifyStatistics may be wrong and must not fault; with it, it must refuse.
//
// THE DIRECTORY CARRIES A CHECKSUM PER REGION, so a region whose bytes are not the ones written
// claims nothing, and a key source refuses it: a zeroed Bloom filter never drops a row. What no
// reader can catch is a liar who forges the checksums too; the structured lies above do exactly
// that, and hold such a file to "no fault", or to a refusal under VerifyStatistics, which is all
// anyone can promise.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Keys;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class LyingIndexTests
{
    private const int Rows = 12_000;
    private const int Block = 1_024;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private static readonly string[] Names = ["id", "v", "s", "f", "t"];

    public static TheoryData<int> Policies() => new() { 0, 1 };

    [Theory]
    [MemberData(nameof(Policies))]
    public async Task MutatedPayloadsFailCleanlyOrReadWithoutAFault(int policy)
    {
        Decoders.EnsureRegistered();
        byte[] original = await WriteAsync(policy);
        List<IndexSegment> payloads = await PayloadsAsync(original);
        Assert.True(payloads.Count > 20, $"only {payloads.Count} payload regions to mutate");
        await ExerciseAsync(original, policy, verify: true);

        Random random = new Random(20260917 + policy);
        int clean = 0;
        int refused = 0;
        for (int i = 0; i < 160; i++)
        {
            byte[] bytes = (byte[])original.Clone();
            IndexSegment target = payloads[random.Next(payloads.Count)];
            Mutate(bytes, target, random);
            bool verify = i % 2 == 1;
            try
            {
                await ExerciseAsync(bytes, policy, verify);
                clean++;
            }
            catch (VortexFormatException)
            {
                refused++;
            }
            catch (VortexUnsupportedException)
            {
                refused++;
            }
            catch (Exception error)
            {
                Assert.Fail($"iteration {i} (payload at {target.Offset}, verify {verify}) threw {error.GetType().Name}: {error.Message}\n{error.StackTrace}");
            }
        }

        // Both outcomes must be reached, or the campaign proved nothing about one of them.
        Assert.True(clean > 10 && refused > 10, $"{clean} read through, {refused} refused");
    }

    [Fact]
    public async Task SwappedKeysAreRefusedByTheirChecksumsAndAForgedSwapByVerification()
    {
        Decoders.EnsureRegistered();
        byte[] original = await WriteAsync(0);
        (IndexSegment a, IndexSegment b) = await SwappablePairAsync(original, keys: true);
        byte[] swapped = Swap(original, a, b);

        // The checksums of the directory catch the swap, whatever the option: the regions'
        // bytes are not the ones written.
        await Assert.ThrowsAsync<VortexFormatException>(() => WalkAsync(swapped, "id", verify: false));
        await Assert.ThrowsAsync<VortexFormatException>(() => WalkAsync(swapped, "id", verify: true));

        // A liar who forges the checksums as well tells a Class II lie. Without verification the
        // walk is whatever the lie says, and it ends.
        byte[] bytes = await ForgeChecksumsAsync(swapped, (a, b.Checksum), (b, a.Checksum));
        List<long> walked = await WalkAsync(bytes, "id", verify: false);
        Assert.NotEmpty(walked);

        // With it, the first segment read that lies stops the walk.
        await Assert.ThrowsAsync<VortexFormatException>(() => WalkAsync(bytes, "id", verify: true));

        // And the untouched file walks clean under the same option.
        List<long> honest = await WalkAsync(original, "id", verify: true);
        Assert.Equal(Rows, honest.Count);
    }

    [Fact]
    public async Task AZeroedFilterIsCaughtByItsChecksumAndCostsNoRow()
    {
        // A zeroed filter would drop rows, and no structural check can see it. The bits
        // of the root filter of `f` are zeroed, the array framing and the node's header kept: a
        // well-formed tree whose root says every value is absent.
        Decoders.EnsureRegistered();
        byte[] original = await WriteAsync(0);
        List<IndexSegment> filters = await FiltersAsync(original, column: 3);
        Assert.NotEmpty(filters);
        byte[] zeroed = (byte[])original.Clone();
        foreach (IndexSegment filter in filters)
        {
            ZeroRootFilter(zeroed, filter);
        }

        VortexExpr present = Expr.Eq(Expr.Field("f"), Expr.Literal(FilterLiteral.From(12.5)));
        long expected = await CountAsync(original, present, indexes: false);
        Assert.True(expected > 0);

        // The checksums refuse the zeroed regions: the filter claims nothing, no row is lost.
        Assert.Equal(expected, await CountAsync(zeroed, present, indexes: true));

        // The same zeroed bits under forged checksums drop the rows: what the checksum is for.
        (IndexSegment, ulong?)[] forgedSums = new (IndexSegment, ulong?)[filters.Count];
        for (int i = 0; i < filters.Count; i++)
        {
            forgedSums[i] = (filters[i], System.IO.Hashing.XxHash3.HashToUInt64(
                zeroed.AsSpan(checked((int)filters[i].Offset), checked((int)filters[i].Length))));
        }

        byte[] forged = await ForgeChecksumsAsync(zeroed, forgedSums);
        Assert.True(await CountAsync(forged, present, indexes: true) < expected);
    }

    [Fact]
    public async Task RowsFromAnotherRunAreRefusedOrStayInsideTheirRun()
    {
        Decoders.EnsureRegistered();
        byte[] original = await WriteAsync(0);
        (IndexSegment a, IndexSegment b) = await SwappablePairAsync(original, keys: false);

        // Unforged, the checksums refuse the swap; forged, the lie reaches the rows' own checks.
        byte[] swapped = Swap(original, a, b);
        await Assert.ThrowsAsync<VortexFormatException>(() => WalkAsync(swapped, "v", verify: false));
        byte[] bytes = await ForgeChecksumsAsync(swapped, (a, b.Checksum), (b, a.Checksum));
        foreach (bool verify in new[] { false, true })
        {
            try
            {
                List<long> rows = await WalkAsync(bytes, "v", verify);
                Assert.All(rows, row => Assert.InRange(row, 0, Rows - 1));
            }
            catch (VortexFormatException)
            {
                // A row past its run is Class I, and refusing is the answer.
            }
        }
    }

    // ------------------------------------------------------------------------------ the readers

    /// <summary>
    /// Every reader of indexes over one file. The key columns follow the policy: the columns with
    /// sorted runs, plus `id`, which is sorted and so has a cursor from its statistics either way;
    /// the distinct walk is over a column with postings.
    /// </summary>
    private static async Task ExerciseAsync(byte[] bytes, int policy, bool verify)
    {
        (string Column, FilterLiteral Probe)[] keyed = policy == 0
            ? [("id", FilterLiteral.From(700L)), ("v", FilterLiteral.From(700L))]
            : [("id", FilterLiteral.From(700L)), ("f", FilterLiteral.From(12.5))];
        string distinctColumn = policy == 0 ? "s" : "v";
        string orderColumn = policy == 0 ? "v" : "f";
        using CancellationTokenSource timeout = new CancellationTokenSource(Budget);
        Stopwatch clock = Stopwatch.StartNew();
        await using MemorySegmentSource source = new MemorySegmentSource(bytes);
        await using VortexFile file = await VortexFile.OpenAsync(source, Options(verify), timeout.Token);

        VortexExpr[] filters =
        [
            Expr.Eq(Expr.Field("v"), Expr.Literal(FilterLiteral.From(417L))),
            Expr.Eq(Expr.Field("s"), Expr.Literal(FilterLiteral.From("s42"))),
            Expr.Eq(Expr.Field("f"), Expr.Literal(FilterLiteral.From(12.5))),
            Expr.And(
                Expr.Ge(Expr.Field("id"), Expr.Literal(FilterLiteral.From(3_000L))),
                Expr.Lt(Expr.Field("id"), Expr.Literal(FilterLiteral.From(5_000L)))),
            Expr.In(Expr.Field("v"), [FilterLiteral.From(1L), FilterLiteral.From(999L)]),
            Expr.StartsWith(Expr.Field("t"), FilterLiteral.From("word4")),
            Expr.Like(Expr.Field("t"), FilterLiteral.From("%rd7%")),
        ];

        foreach (VortexExpr filter in filters)
        {
            _ = await file.Scan().Where(filter).CountAsync(timeout.Token);
            await foreach (RecordBatch batch in file.Scan().Where(filter).ExecuteAsync().WithCancellation(timeout.Token))
            {
                Touch(batch);
            }
        }

        foreach ((string column, FilterLiteral probe) in keyed)
        {
            await using KeyCursor cursor = await file.Keys(column).OpenAsync(timeout.Token);
            int steps = 0;
            for (bool ok = await cursor.SeekFirstAsync(timeout.Token); ok && steps < 400; ok = await cursor.NextAsync(timeout.Token))
            {
                _ = cursor.Row;
                steps++;
            }

            _ = await cursor.SeekAsync(probe, SeekOp.AtOrAfter, timeout.Token);
            _ = await cursor.SeekLastAsync(timeout.Token);
            _ = await cursor.PrevAsync(timeout.Token);
            _ = await cursor.RankAsync(probe, timeout.Token);
            _ = await cursor.KeyCountAsync(timeout.Token);
        }

        await using (KeyCursor distinct = await file.Keys(distinctColumn).Distinct().OpenAsync(timeout.Token))
        {
            int keys = 0;
            for (bool ok = await distinct.SeekFirstAsync(timeout.Token); ok && keys < 400; ok = await distinct.NextAsync(timeout.Token))
            {
                _ = distinct.KeyBytes.Length;
                keys++;
            }
        }

        int ordered = 0;
        await foreach (RecordBatch batch in file.Scan().InKeyOrder(orderColumn).ExecuteAsync().WithCancellation(timeout.Token))
        {
            Touch(batch);
            ordered += (int)batch.RowCount;
            if (ordered >= 2_000)
            {
                break;
            }
        }

        Assert.True(clock.Elapsed < Budget, $"an iteration took {clock.Elapsed}");
    }

    private static async Task<List<long>> WalkAsync(byte[] bytes, string column, bool verify)
    {
        await using MemorySegmentSource source = new MemorySegmentSource(bytes);
        await using VortexFile file = await VortexFile.OpenAsync(source, Options(verify), CancellationToken.None);
        await using KeyCursor cursor = await file.Keys(column).WithSource(KeySourceKind.SortedRuns).OpenAsync();
        List<long> rows = [];
        for (bool ok = await cursor.SeekFirstAsync(); ok && rows.Count <= 2 * Rows; ok = await cursor.NextAsync())
        {
            rows.Add(cursor.Row);
        }

        return rows;
    }

    private static VortexOpenOptions Options(bool verify) => new VortexOpenOptions
    {
        LeaveSourceOpen = true,
        Read = new VortexReadOptions { VerifyStatistics = verify },
    };

    private static void Touch(RecordBatch batch)
    {
        for (int field = 0; field < batch.FieldCount; field++)
        {
            VortexColumn column = batch.Column(field);
            for (int row = 0; row < batch.RowCount; row++)
            {
                _ = column.IsValid(row);
            }
        }
    }

    // ------------------------------------------------------------------------------ the lies

    private static void Mutate(byte[] bytes, IndexSegment target, Random random)
    {
        int start = checked((int)target.Offset);
        int length = checked((int)target.Length);
        switch (random.Next(4))
        {
            case 0:
                // A word inside the region: a length, a count, a key or a row.
                if (length >= 4)
                {
                    int at = start + (random.Next(length - 3) & ~3);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), (uint)random.Next());
                }

                return;

            case 1:
                // An extreme value: the Class I fields are lengths and offsets.
                if (length >= 8)
                {
                    int at = start + (random.Next(length - 7) & ~7);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                        bytes.AsSpan(at), random.Next(2) == 0 ? ulong.MaxValue : 0x7FFF_FFFF_FFFF_FFFFUL);
                }

                return;

            case 2:
                // The region's tail, where an array blob keeps its flatbuffer.
                int tail = Math.Min(length, 64);
                for (int i = 0; i < 4; i++)
                {
                    bytes[start + length - 1 - random.Next(tail)] ^= (byte)(1 << random.Next(8));
                }

                return;

            default:
                // A filter that claims nothing is present, or a region of garbage.
                bytes.AsSpan(start, length).Fill(random.Next(2) == 0 ? (byte)0 : (byte)0xFF);
                return;
        }
    }

    /// <summary>
    /// The file with its directory rewritten in place, the named regions' checksums replaced: the
    /// liar who knows the format. The directory keeps its length, since a checksum is a fixed64.
    /// </summary>
    private static async Task<byte[]> ForgeChecksumsAsync(byte[] bytes, params (IndexSegment Segment, ulong? Checksum)[] changes)
    {
        await using MemorySegmentSource source = new MemorySegmentSource(bytes);
        await using VortexFile file = await VortexFile.OpenAsync(source, Options(false), CancellationToken.None);
        Assert.True(file.TryGetMetadataIndex(IndexDirectory.MetadataKeyUtf8, out int index));
        SegmentSpec spec = file.GetMetadataSegment(index);
        IndexDirectory? directory = await file.ReadIndexDirectoryAsync();
        Assert.NotNull(directory);

        // The helper is only honest if a directory re-serializes to its own bytes.
        Span<byte> stored = bytes.AsSpan(checked((int)spec.Offset), checked((int)spec.Length));
        Assert.True(directory.ToBytes().AsSpan().SequenceEqual(stored), "a read directory does not re-serialize to its bytes");

        IndexSegment Replace(IndexSegment segment)
        {
            foreach ((IndexSegment target, ulong? checksum) in changes)
            {
                if (target.Offset == segment.Offset)
                {
                    return segment with { Checksum = checksum };
                }
            }

            return segment;
        }

        List<IndexEntry> entries = [];
        foreach (IndexEntry entry in directory.Entries)
        {
            List<IndexRun> runs = [];
            foreach (IndexRun run in entry.Runs)
            {
                runs.Add(run with { Payload = [.. System.Linq.Enumerable.Select(run.Payload, Replace)] });
            }

            entries.Add(entry with { Runs = runs });
        }

        byte[] rewritten = (directory with { Entries = entries }).ToBytes();
        Assert.Equal(stored.Length, rewritten.Length);
        byte[] forged = (byte[])bytes.Clone();
        rewritten.CopyTo(forged, checked((int)spec.Offset));
        return forged;
    }

    /// <summary>Every Bloom tree's root region of one top-level column.</summary>
    private static async Task<List<IndexSegment>> FiltersAsync(byte[] bytes, uint column)
    {
        await using MemorySegmentSource source = new MemorySegmentSource(bytes);
        await using VortexFile file = await VortexFile.OpenAsync(source, Options(false), CancellationToken.None);
        IndexDirectory? directory = await file.ReadIndexDirectoryAsync();
        Assert.NotNull(directory);
        List<IndexSegment> filters = [];
        foreach (IndexEntry entry in directory.Entries)
        {
            if (entry.Kind == IndexKinds.BloomSbbf && entry.ColumnPath is [var path] && path == column)
            {
                foreach (IndexRun run in entry.Runs)
                {
                    filters.AddRange(run.Payload);
                }
            }
        }

        return filters;
    }

    /// <summary>
    /// Zeroes the filter words of the root node an array blob holds, and keeps everything else: the
    /// node's header and child sizes, and the blob's framing. The u32 buffer starts the blob.
    /// </summary>
    private static void ZeroRootFilter(byte[] bytes, IndexSegment blob)
    {
        Span<byte> region = bytes.AsSpan(checked((int)blob.Offset), checked((int)blob.Length));
        uint tag = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(region);
        int listed = (tag >> 24) != 0 ? 0 : (int)((tag >> 16) & 0xFF);
        int filterBlocks = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(region[8..]));
        Assert.True(filterBlocks > 0, "the root has no filter to zero");
        int start = (BloomNode.HeaderWords + listed) * sizeof(uint);
        region.Slice(start, filterBlocks * SplitBlockBloom.BytesPerBlock).Clear();
    }

    private static async Task<long> CountAsync(byte[] bytes, VortexExpr filter, bool indexes)
    {
        await using MemorySegmentSource source = new MemorySegmentSource(bytes);
        await using VortexFile file = await VortexFile.OpenAsync(source, Options(false), CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().Where(filter).WithIndexes(indexes).ExecuteAsync())
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    private static byte[] Swap(byte[] original, IndexSegment a, IndexSegment b)
    {
        byte[] bytes = (byte[])original.Clone();
        original.AsSpan(checked((int)a.Offset), checked((int)a.Length)).CopyTo(bytes.AsSpan(checked((int)b.Offset)));
        original.AsSpan(checked((int)b.Offset), checked((int)b.Length)).CopyTo(bytes.AsSpan(checked((int)a.Offset)));
        return bytes;
    }

    /// <summary>
    /// Two payloads of the same byte length in the sorted runs of a column: the keys of two
    /// segments of <c>id</c>, or the rows of two runs of <c>v</c>.
    /// </summary>
    private static async Task<(IndexSegment A, IndexSegment B)> SwappablePairAsync(byte[] bytes, bool keys)
    {
        await using MemorySegmentSource source = new MemorySegmentSource(bytes);
        await using VortexFile file = await VortexFile.OpenAsync(source, Options(false), CancellationToken.None);
        IndexDirectory? directory = await file.ReadIndexDirectoryAsync();
        Assert.NotNull(directory);
        uint column = keys ? 0u : 1u;
        List<(IndexSegment Segment, int Run)> candidates = [];
        foreach (IndexEntry entry in directory.Entries)
        {
            if (entry.Kind != IndexKinds.SortedRuns || entry.ColumnPath.Count != 1 || entry.ColumnPath[0] != column)
            {
                continue;
            }

            for (int r = 0; r < entry.Runs.Count; r++)
            {
                IReadOnlyList<IndexSegment> payload = entry.Runs[r].Payload;
                for (int p = keys ? 0 : 1; p < payload.Count; p += 2)
                {
                    candidates.Add((payload[p], r));
                }
            }
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            for (int j = i + 1; j < candidates.Count; j++)
            {
                // ANY TWO SEGMENTS: a column has one run and the last chunk's, so the
                // rows of "another run" are those of another segment of the same one, whose range
                // covers the whole file.
                if (candidates[i].Segment.Length == candidates[j].Segment.Length
                    && !bytes.AsSpan(checked((int)candidates[i].Segment.Offset), checked((int)candidates[i].Segment.Length))
                        .SequenceEqual(bytes.AsSpan(checked((int)candidates[j].Segment.Offset), checked((int)candidates[j].Segment.Length))))
                {
                    return (candidates[i].Segment, candidates[j].Segment);
                }
            }
        }

        Assert.Fail($"no two {(keys ? "key" : "row")} payloads of one length among {candidates.Count}");
        return default;
    }

    private static async Task<List<IndexSegment>> PayloadsAsync(byte[] bytes)
    {
        await using MemorySegmentSource source = new MemorySegmentSource(bytes);
        await using VortexFile file = await VortexFile.OpenAsync(source, Options(false), CancellationToken.None);
        IndexDirectory? directory = await file.ReadIndexDirectoryAsync();
        Assert.NotNull(directory);
        List<IndexSegment> payloads = [];
        foreach (IndexEntry entry in directory.Entries)
        {
            foreach (IndexRun run in entry.Runs)
            {
                foreach (IndexSegment segment in run.Payload)
                {
                    if (segment.Length > 0)
                    {
                        payloads.Add(segment);
                    }
                }
            }
        }

        return payloads;
    }

    // ------------------------------------------------------------------------------ the file

    private static WritePolicy Policy(int policy) => policy == 0
        ? WritePolicy.None
            .For("id", IndexPolicy.SortedRuns.WithSegmentEntries(256))
            .For("v", IndexPolicy.SortedRuns.WithSegmentEntries(512))
            .For("s", IndexPolicy.Postings.WithSegmentEntries(64))
            .For("f", IndexPolicy.Bloom(resolutions: 3))
            .For("t", IndexPolicy.NgramBloom(resolutions: 3))
        : WritePolicy.None
            .For("id", IndexPolicy.Bloom(resolutions: 3))
            .For("v", IndexPolicy.Postings)
            .For("s", IndexPolicy.Bloom(resolutions: 2))
            .For("f", IndexPolicy.SortedRuns)
            .For("t", IndexPolicy.NgramPostings(caseInsensitive: true).WithSegmentEntries(128));

    private static readonly DTypeArena Types = new DTypeArena();

    private static readonly DType Schema = Types.Struct(
        Names,
        [
            Types.Primitive(PType.I64, Nullability.NonNullable),
            Types.Primitive(PType.I32, Nullability.Nullable),
            Types.Utf8(Nullability.NonNullable),
            Types.Primitive(PType.F64, Nullability.NonNullable),
            Types.Utf8(Nullability.NonNullable),
        ],
        Nullability.NonNullable);

    private static int? V(int row) => row % 17 == 0 ? null : (int)((row * 7919L) % 1_000);

    private static string S(int row) => "s" + ((row * 31) % 211).ToString(CultureInfo.InvariantCulture);

    private static double F(int row) => ((row * 13) % 101) / 4.0;

    private static string T(int row) => "word" + ((row * 17) % 97).ToString(CultureInfo.InvariantCulture) + " text";

    private static async Task<byte[]> WriteAsync(int policy)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-lying-{Guid.NewGuid():N}.vortex");
        try
        {
            VortexWriteOptions options = new VortexWriteOptions
            {
                RowBlockSize = Block,
                DataBlockTargetBytes = 1L << 14,
                IndexBudgetPerMille = 1_000_000,
                Indexes = Policy(policy),
            };
            await using (VortexFileWriter writer = VortexFileWriter.Create(path, Schema, options))
            {
                for (int start = 0; start < Rows; start += 1_500)
                {
                    int count = Math.Min(1_500, Rows - start);
                    CanonicalArena arena = new CanonicalArena();
                    try
                    {
                        int root = arena.AddStruct(
                            Schema,
                            count,
                            Validity.NonNullable,
                            [Longs(arena, start, count), Ints(arena, start, count), Strings(arena, 2, start, count, S), Doubles(arena, start, count), Strings(arena, 4, start, count, T)]);
                        using RecordBatch batch = new RecordBatch(arena, root, start);
                        await writer.WriteAsync(batch);
                    }
                    finally
                    {
                        arena.Reset();
                    }
                }

                await writer.CompleteAsync();
            }

            return await System.IO.File.ReadAllBytesAsync(path);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static int Longs(CanonicalArena arena, int start, int count)
    {
        VortexBuffer buffer = arena.Allocate(count * 8, 8, out Span<byte> bytes);
        Span<long> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < count; i++)
        {
            values[i] = start + i;
        }

        return arena.AddPrimitive(Schema.GetField(0), count, Validity.NonNullable, PType.I64, buffer);
    }

    private static int Doubles(CanonicalArena arena, int start, int count)
    {
        VortexBuffer buffer = arena.Allocate(count * 8, 8, out Span<byte> bytes);
        Span<double> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(bytes);
        for (int i = 0; i < count; i++)
        {
            values[i] = F(start + i);
        }

        return arena.AddPrimitive(Schema.GetField(3), count, Validity.NonNullable, PType.F64, buffer);
    }

    private static int Ints(CanonicalArena arena, int start, int count)
    {
        VortexBuffer buffer = arena.Allocate(count * 4, 4, out Span<byte> bytes);
        Span<int> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(bytes);
        VortexBuffer bits = arena.Allocate(Math.Max((count + 7) / 8, 1), 8, out Span<byte> raw);
        raw.Clear();
        for (int i = 0; i < count; i++)
        {
            int? v = V(start + i);
            values[i] = v ?? 0;
            if (v is not null)
            {
                raw[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        int mask = arena.AddBool(Types.Bool(Nullability.NonNullable), count, Validity.NonNullable, bits, 0);
        return arena.AddPrimitive(Schema.GetField(1), count, Validity.Bitmap(mask), PType.I32, buffer);
    }

    private static int Strings(CanonicalArena arena, int field, int start, int count, Func<int, string> value)
    {
        VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> bytes);
        bytes.Clear();
        for (int i = 0; i < count; i++)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value(start + i));
            Span<byte> view = bytes.Slice(i * 16, 16);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)utf8.Length);
            utf8.CopyTo(view[4..]);
        }

        return arena.AddVarBinView(Schema.GetField(field), count, Validity.NonNullable, views, [VortexBuffer.Empty]);
    }
}
