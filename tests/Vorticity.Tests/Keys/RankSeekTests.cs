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
using Vorticity.Indexes;
using Vorticity.Keys;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Keys;

/// <summary>
/// A seek by rank over sorted runs lands on the entry of that rank in (key, row) order, whatever
/// the runs: one or many, short or long, sharing their keys; and the cursor steps on from there
/// both ways.
/// </summary>
public sealed class RankSeekTests
{
    /// <summary>Entries per segment of a run.</summary>
    private const int SegmentEntries = 64;

    /// <summary>Rows per block, each block a chunk of its own.</summary>
    private const int Block = 64;

    /// <summary>The distinct keys the rows draw from, few enough that every key recurs across the runs.</summary>
    private const int Keys = 300;

    // A write, then appends, each of whole blocks: a run each, up to the three a file keeps. The
    // runs are one block long or many, and all of them spread over the keys.
    [Theory]
    [InlineData(new[] { 2_048 }, false)]
    [InlineData(new[] { 64, 1_984 }, false)]
    [InlineData(new[] { 1_984, 64 }, true)]
    [InlineData(new[] { 64, 1_344, 640 }, false)]
    [InlineData(new[] { 640, 64, 1_344 }, true)]
    public async Task EveryRankLandsOnItsEntryAndTheCursorStepsOnBothWays(int[] runs, bool strings)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Random random = new Random(26);
        long[] keys = [.. Enumerable.Range(0, runs.Sum()).Select(_ => (long)random.Next(Keys))];
        (long Key, long Row)[] oracle = [.. keys.Select((key, row) => (key, (long)row)).OrderBy(entry => entry.key).ThenBy(entry => entry.Item2)];

        string path = Path.Combine(Path.GetTempPath(), $"vorticity-rank-{Guid.NewGuid():N}.vortex");
        try
        {
            await WriteAsync(path, runs, keys, strings, ct);
            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            KeyPlan plan = await file.Keys("key").WithSource(KeySourceKind.SortedRuns).ExplainAsync(ct);
            Assert.Equal(runs.Length, plan.Runs);
            await using KeyCursor cursor = await file.Keys("key").WithSource(KeySourceKind.SortedRuns).OpenAsync(ct);

            for (int rank = 0; rank < oracle.Length; rank++)
            {
                Assert.True(await cursor.SeekRankAsync(rank, ct), $"rank {rank}");
                AssertAt(cursor, oracle, rank, strings);
                if (rank % 37 != 0)
                {
                    continue;
                }

                // The runs stand where the seek left them: steps go on in order, and back.
                int at = rank;
                for (int step = 0; step < 3 && at + 1 < oracle.Length; step++)
                {
                    Assert.True(await cursor.NextAsync(ct));
                    AssertAt(cursor, oracle, ++at, strings);
                }

                for (int step = 0; step < 5 && at > 0; step++)
                {
                    Assert.True(await cursor.PrevAsync(ct));
                    AssertAt(cursor, oracle, --at, strings);
                }
            }

            Assert.False(await cursor.SeekRankAsync(oracle.Length, ct));
            Assert.False(await cursor.SeekRankAsync(-1, ct));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static void AssertAt(KeyCursor cursor, (long Key, long Row)[] oracle, int rank, bool strings)
    {
        if (strings)
        {
            Assert.Equal(Text(oracle[rank].Key), cursor.Key.BytesValue.ToArray());
        }
        else
        {
            Assert.Equal(oracle[rank].Key, cursor.Key.SignedValue);
        }

        Assert.Equal(oracle[rank].Row, cursor.Row);
    }

    private static byte[] Text(long key) => Encoding.ASCII.GetBytes($"key-{key:D5}");

    /// <summary>A write, then an append per further run, each of whole blocks.</summary>
    private static async Task WriteAsync(string path, int[] runs, long[] keys, bool strings, CancellationToken ct)
    {
        DTypeArena types = new DTypeArena();
        DType dtype = strings ? types.Utf8(Nullability.NonNullable) : types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(["key"], [dtype], Nullability.NonNullable);
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = Block,
            DataBlockTargetBytes = null,
            IndexBudgetPerMille = 1_000_000,
            WritePolicy = WritePolicy.None.For("key", IndexSpec.SortedRuns.WithSegmentEntries(SegmentEntries)),
        };

        int start = 0;
        foreach (int count in runs)
        {
            await using VortexFileWriter writer = start == 0
                ? VortexFileWriter.Create(path, schema, options)
                : await VortexFileWriter.AppendAsync(path, options, ct);
            CanonicalArena arena = new CanonicalArena();
            ReadOnlySpan<long> part = keys.AsSpan(start, count);
            int column = strings ? Strings(arena, dtype, part) : Integers(arena, dtype, part);
            int root = arena.AddStruct(schema, count, Validity.NonNullable, [column]);
            using (RecordBatch batch = new RecordBatch(arena, root, start))
            {
                await writer.WriteAsync(batch, ct);
            }

            await writer.CompleteAsync(ct);
            start += count;
        }
    }

    private static int Integers(CanonicalArena arena, DType dtype, ReadOnlySpan<long> keys)
    {
        VortexBuffer buffer = arena.Allocate(keys.Length * sizeof(long), sizeof(long), out Span<byte> bytes);
        keys.CopyTo(MemoryMarshal.Cast<byte, long>(bytes));
        return arena.AddPrimitive(dtype, keys.Length, Validity.NonNullable, PType.I64, buffer);
    }

    /// <summary>Keys of nine bytes, each held inline in its view.</summary>
    private static int Strings(CanonicalArena arena, DType dtype, ReadOnlySpan<long> keys)
    {
        VortexBuffer views = arena.Allocate(keys.Length * 16, 16, out Span<byte> viewBytes);
        viewBytes.Clear();
        for (int i = 0; i < keys.Length; i++)
        {
            byte[] text = Text(keys[i]);
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view, text.Length);
            text.CopyTo(view[4..]);
        }

        return arena.AddVarBinView(dtype, keys.Length, Validity.NonNullable, views, []);
    }
}
