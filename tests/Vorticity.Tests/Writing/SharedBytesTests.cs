// Views that keep a run of repeated values' bytes once, standing for the copy of each row's bytes they replace: a shredded
// column's metadata gathered thin, a shredded text leaf. An encoder sizes a column by its data buffers, and a column whose
// buffers hold fewer bytes than its rows name would be planned otherwise, and written otherwise.
//
// THE ORACLE IS THE COPY: the column that shares its repeats writes, compressed or not, the bytes its copy of each row's
// bytes writes, and so do a filter and a window of it against the same of the copy.
using System;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Layouts;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class SharedBytesTests
{
    /// <summary>
    /// Runs of rows of one value, long and short, a copy that shares them and one that copies each row's bytes: the shared
    /// one, standing for the other's bytes, writes its bytes, and so do a filter and a window of each.
    /// </summary>
    [Fact]
    public void ViewsThatShareTheirRepeatsWriteWhatTheirCopyWrites()
    {
        DTypeArena types = new DTypeArena();
        DType utf8 = types.Utf8(Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();
        int source = Runs(arena, utf8, new Random(7), 6_000);
        int copied = arena.CopyFrom(arena, source, viewRepeats: true);
        int shared = arena.CopyFrom(arena, source, shareRepeats: true);
        Assert.True(
            arena.GetNode(shared).GetDataBuffer(0).Length < arena.GetNode(copied).GetDataBuffer(0).Length / 4,
            "the copy that shares repeats keeps a run's bytes once");
        arena.StandFor(shared, arena.NamedDataBytes(shared));

        int[] picks = [.. Picks(6_000)];
        AssertSameBlobs(arena, copied, shared);
        AssertSameBlobs(arena, CanonicalFilter.Apply(arena, copied, picks), CanonicalFilter.Apply(arena, shared, picks));
        AssertSameBlobs(
            arena,
            CanonicalSlice.SliceAcross(arena, arena, copied, 1_000, 3_000),
            CanonicalSlice.SliceAcross(arena, arena, shared, 1_000, 3_000));
    }

    private static void AssertSameBlobs(CanonicalArena arena, int expected, int actual)
    {
        foreach (bool compress in new[] { true, false })
        {
            using ArrayBlobWriter.Workspace one = new ArrayBlobWriter.Workspace { Account = TestAccount.Shared };
            using ArrayBlobWriter.Workspace other = new ArrayBlobWriter.Workspace { Account = TestAccount.Shared };
            using ArrayBlobWriter.BlobLease copy = ArrayBlobWriter.Write(
                one, arena, expected, new EncodingDictionary(ComponentKind.Array, Vorticity.Editions.EditionRegistry.Newest), compress);
            using ArrayBlobWriter.BlobLease shares = ArrayBlobWriter.Write(
                other, arena, actual, new EncodingDictionary(ComponentKind.Array, Vorticity.Editions.EditionRegistry.Newest), compress);
            Assert.True(
                copy.Memory.Span.SequenceEqual(shares.Memory.Span),
                $"compressed {compress}: {shares.Length} bytes from the views that share repeats against {copy.Length} from the copy");
        }
    }

    /// <summary>Every row of a run of three, and the first of each run of seven.</summary>
    private static IEnumerable<int> Picks(int rows)
    {
        for (int row = 0; row < rows; row++)
        {
            if (row % 3 == 0 || row % 7 == 1)
            {
                yield return row;
            }
        }
    }

    /// <summary>
    /// <paramref name="rows"/> texts in runs of one to forty rows of the same value, a value one of sixteen texts of twenty to
    /// ninety bytes or one of four of a few, each row's bytes its own in the data buffer, as a decoder lays them.
    /// </summary>
    private static int Runs(CanonicalArena arena, DType dtype, Random random, int rows)
    {
        byte[][] values = new byte[20][];
        for (int i = 0; i < values.Length; i++)
        {
            int length = i < 16 ? random.Next(20, 90) : random.Next(1, 12);
            values[i] = new byte[length];
            for (int b = 0; b < length; b++)
            {
                values[i][b] = (byte)('a' + random.Next(26));
            }
        }

        List<byte[]> column = [];
        while (column.Count < rows)
        {
            byte[] value = values[random.Next(values.Length)];
            for (int run = random.Next(1, 41); run > 0 && column.Count < rows; run--)
            {
                column.Add(value);
            }
        }

        int total = 0;
        foreach (byte[] value in column)
        {
            total += value.Length;
        }

        VortexBuffer data = arena.Allocate(total, 1, out Span<byte> dataBytes);
        VortexBuffer views = arena.Allocate(rows * 16, 16, out Span<byte> viewBytes);
        int at = 0;
        for (int row = 0; row < rows; row++)
        {
            byte[] value = column[row];
            Span<byte> view = viewBytes.Slice(row * 16, 16);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)value.Length);
            if (value.Length <= 12)
            {
                value.CopyTo(view[4..]);
            }
            else
            {
                value.AsSpan(0, 4).CopyTo(view[4..]);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(view[12..], (uint)at);
            }

            value.CopyTo(dataBytes[at..]);
            at += value.Length;
        }

        return arena.AddVarBinView(dtype, rows, Validity.NonNullable, views, [data]);
    }
}
