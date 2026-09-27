using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
/// A distinct walk moves from a key to the next by looking where it stands: the entries that
/// follow, then farther ones, within what is decoded, and the whole column or run only when the
/// key goes on past it. Every key is visited once, at its first row, whichever of those it took.
/// </summary>
public sealed class DistinctWalkTests
{
    /// <summary>Rows per zone of the sorted column.</summary>
    private const int Block = 256;

    /// <summary>Entries per segment of the sorted runs.</summary>
    private const int SegmentEntries = 64;

    /// <summary>
    /// The rows of each key, in key order: one row, as many as a segment and one either side, and
    /// runs of several segments and zones.
    /// </summary>
    private static readonly int[] Scattered = [1, 1, 2, 3, 63, 64, 65, 1, 200, 1, 1_000, 5, 2_500, 1, 1, 130, 7, 4_100, 2, 1];

    /// <summary>
    /// Keys that fill whole zones, starting and ending on their edges, between zones shared by
    /// several keys: a walk lands on a zone's first row going forward and on its last going back.
    /// Written a chunk per zone, so that a zone is a whole chunk rather than a slice of one.
    /// </summary>
    private static readonly int[] OnZones = [512, 256, 3, 253, 256, 1, 1, 254, 300, 2];

    [Theory]
    [InlineData(KeySourceKind.SortedColumn, false)]
    [InlineData(KeySourceKind.SortedColumn, true)]
    [InlineData(KeySourceKind.SortedRuns, false)]
    [InlineData(KeySourceKind.SortedRuns, true)]
    public async Task EveryKeyIsVisitedOnceAtItsFirstRowBothWays(KeySourceKind source, bool onZones)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int[] lengths = onZones ? OnZones : Scattered;
        (long[] sorted, long[] scrambled) = Columns(lengths);
        (long[] firstRow, _) = Bounds(source == KeySourceKind.SortedColumn ? sorted : scrambled, lengths.Length);

        string path = Path.Combine(Path.GetTempPath(), $"vorticity-distinct-{Guid.NewGuid():N}.vortex");
        try
        {
            await WriteAsync(path, sorted, scrambled, chunkPerZone: onZones, ct);
            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            await using KeyCursor cursor = await file
                .Keys(source == KeySourceKind.SortedColumn ? "sorted" : "scrambled")
                .Distinct()
                .WithSource(source)
                .OpenAsync(ct);

            int key = 0;
            for (bool ok = await cursor.SeekFirstAsync(ct); ok; ok = await cursor.NextAsync(ct))
            {
                Assert.Equal(key, cursor.Key.SignedValue);
                Assert.Equal(firstRow[key], cursor.Row);
                key++;
            }

            Assert.Equal(lengths.Length, key);
            for (bool ok = await cursor.SeekLastAsync(ct); ok; ok = await cursor.PrevAsync(ct))
            {
                key--;
                Assert.Equal(key, cursor.Key.SignedValue);
                Assert.Equal(firstRow[key], cursor.Row);
            }

            Assert.Equal(0, key);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // Key to key on a cursor over every entry: forward to each key's first entry, back to the
    // previous key's last, which in a run is its entry of the highest row.
    [Theory]
    [InlineData(KeySourceKind.SortedColumn, false)]
    [InlineData(KeySourceKind.SortedColumn, true)]
    [InlineData(KeySourceKind.SortedRuns, false)]
    [InlineData(KeySourceKind.SortedRuns, true)]
    public async Task NextKeyReachesEachKeysFirstEntryAndPrevKeyThePreviousKeysLast(KeySourceKind source, bool onZones)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int[] lengths = onZones ? OnZones : Scattered;
        (long[] sorted, long[] scrambled) = Columns(lengths);
        (long[] firstRow, long[] lastRow) = Bounds(source == KeySourceKind.SortedColumn ? sorted : scrambled, lengths.Length);

        string path = Path.Combine(Path.GetTempPath(), $"vorticity-keys-{Guid.NewGuid():N}.vortex");
        try
        {
            await WriteAsync(path, sorted, scrambled, chunkPerZone: onZones, ct);
            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            await using KeyCursor cursor = await file
                .Keys(source == KeySourceKind.SortedColumn ? "sorted" : "scrambled")
                .WithSource(source)
                .OpenAsync(ct);

            Assert.True(await cursor.SeekFirstAsync(ct));
            for (int key = 1; key < lengths.Length; key++)
            {
                Assert.True(await cursor.NextKeyAsync(ct));
                Assert.Equal(key, cursor.Key.SignedValue);
                Assert.Equal(firstRow[key], cursor.Row);
            }

            Assert.False(await cursor.NextKeyAsync(ct));
            Assert.True(await cursor.SeekLastAsync(ct));
            for (int key = lengths.Length - 2; key >= 0; key--)
            {
                Assert.True(await cursor.PrevKeyAsync(ct));
                Assert.Equal(key, cursor.Key.SignedValue);
                Assert.Equal(lastRow[key], cursor.Row);
            }

            Assert.False(await cursor.PrevKeyAsync(ct));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>The keys in order, as many rows each as <paramref name="lengths"/> says, and the same rows shuffled.</summary>
    private static (long[] Sorted, long[] Scrambled) Columns(int[] lengths)
    {
        long[] sorted = [.. lengths.SelectMany((length, key) => Enumerable.Repeat((long)key, length))];
        long[] scrambled = (long[])sorted.Clone();
        new Random(25).Shuffle(scrambled);
        return (sorted, scrambled);
    }

    /// <summary>Each key's first and last row in <paramref name="column"/>.</summary>
    private static (long[] First, long[] Last) Bounds(long[] column, int keys)
    {
        long[] first = new long[keys];
        long[] last = new long[keys];
        Array.Fill(first, -1);
        for (int row = 0; row < column.Length; row++)
        {
            long key = column[row];
            first[key] = first[key] < 0 ? row : first[key];
            last[key] = row;
        }

        return (first, last);
    }

    private static async Task WriteAsync(string path, long[] sorted, long[] scrambled, bool chunkPerZone, CancellationToken ct)
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(["sorted", "scrambled"], [i64, i64], Nullability.NonNullable);
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = Block,
            DataBlockTargetBytes = chunkPerZone ? null : new VortexWriteOptions().DataBlockTargetBytes,
            IndexBudgetPerMille = 1_000_000,
            WritePolicy = WritePolicy.None.For("scrambled", IndexSpec.SortedRuns.WithSegmentEntries(SegmentEntries)),
        };

        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
        CanonicalArena arena = new CanonicalArena();
        int root = arena.AddStruct(schema, sorted.Length, Validity.NonNullable, [Column(arena, i64, sorted), Column(arena, i64, scrambled)]);
        using (RecordBatch batch = new RecordBatch(arena, root, 0))
        {
            await writer.WriteAsync(batch, ct);
        }

        await writer.CompleteAsync(ct);
    }

    private static int Column(CanonicalArena arena, DType dtype, long[] values)
    {
        VortexBuffer buffer = arena.Allocate(values.Length * sizeof(long), sizeof(long), out Span<byte> bytes);
        values.CopyTo(MemoryMarshal.Cast<byte, long>(bytes));
        return arena.AddPrimitive(dtype, values.Length, Validity.NonNullable, PType.I64, buffer);
    }
}
