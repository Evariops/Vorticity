// A slice's key columns taken from their codes: a column the slice is sorted by, its codes naming its values exactly,
// comes out of a table of its values by rank, its strings past their views copied in the slice's order; a float, whose
// zeros code alike, is gathered row by row, and every row keeps its own bits.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SortedSliceTests
{
    /// <summary>
    /// Four slices of 300 rows, in batches of 70 that the slices' ends cut, sorted by a name, a code and a weight: names in
    /// padded views, some within their views and some past them, a code of five values, and a weight that is now and then
    /// a negative zero. Each slice holds its own rows sorted by the order the first slice settled, ties in the order they
    /// came, each name's bytes, and each weight's bits, a negative zero a negative zero.
    /// </summary>
    [Fact]
    public async Task ASlicesKeysComeFromTheirCodesAndKeepTheirValues()
    {
        const int Slices = 4;
        const int SliceRows = 300;
        const int BatchRows = 70;
        string[] names = ["a", "bb", "twelve-bytes", "a-name-past-its-view-0", "a-name-past-its-view-1", "a-name-past-its-view-22"];
        double[] weights = [-0.0, 0.0, 1.5, 2.5];
        Random random = new Random(11);
        Ranked[] rows = [.. Enumerable.Range(0, Slices * SliceRows).Select(i => new Ranked(
            Noon.AddSeconds(i), names[random.Next(names.Length)], random.Next(5) - 2, weights[random.Next(weights.Length)], random.NextInt64(1_000_000)))];
        DType schema = VortexTypes.ToDType(Ranked.Schema, new DTypeArena());
        long micros = Noon.ToUnixTimeMilliseconds() * 1000;
        SliceTable table = new SliceTable(
            TimeUnit.Microseconds,
            [.. Enumerable.Range(0, Slices).Select(s => new TimeSlice(micros + (s * SliceRows * 1_000_000L), micros + ((s + 1) * SliceRows * 1_000_000L), (long)s * SliceRows, SliceRows))],
            [schema.IndexOfField(nameof(Ranked.Name)), schema.IndexOfField(nameof(Ranked.Code)), schema.IndexOfField(nameof(Ranked.Weight))]);
        List<Ranked[]> written = [];
        IReadOnlyList<int> order;
        using (SortedSlices slices = new SortedSlices(table, schema.IndexOfField(nameof(Ranked.At)), schema))
        {
            for (int start = 0; start < rows.Length; start += BatchRows)
            {
                using RecordBatch batch = RankedBatch(schema, rows.AsSpan(start, Math.Min(BatchRows, rows.Length - start)));
                await slices.AppendAsync(batch, (slice, token) =>
                {
                    Ranked[] sorted = new Ranked[slice.Batch.RowCount];
                    Ranked.ReadRows(slice.Batch.As<Ranked>(), sorted);
                    written.Add(sorted);
                    return ValueTask.CompletedTask;
                }, Ct);
            }

            await slices.DrainAsync();
            order = [.. slices.Order];
        }

        Assert.Equal(3, order.Count);
        Assert.Equal(Slices, written.Count);
        for (int s = 0; s < Slices; s++)
        {
            (Ranked Row, int At)[] arrived = [.. rows.Skip(s * SliceRows).Take(SliceRows).Select((row, i) => (row, i))];
            Array.Sort(arrived, (left, right) =>
            {
                foreach (int entry in order)
                {
                    int compared = Compare(schema, entry, left.Row, right.Row);
                    if (compared != 0)
                    {
                        return compared;
                    }
                }

                return left.At.CompareTo(right.At);
            });
            Ranked[] expected = [.. arrived.Select(p => p.Row)];
            Assert.Equal(expected.Length, written[s].Length);
            for (int row = 0; row < expected.Length; row++)
            {
                Ranked want = expected[row];
                Ranked got = written[s][row];
                Assert.Equal((want.At, want.Name, want.Code, want.Size), (got.At, got.Name, got.Code, got.Size));
                Assert.Equal(BitConverter.DoubleToInt64Bits(want.Weight), BitConverter.DoubleToInt64Bits(got.Weight));
            }
        }
    }

    [VortexRecord]
    public partial record struct Ranked(DateTimeOffset At, string Name, int Code, double Weight, long Size);

    /// <summary>How two rows compare on the field <paramref name="entry"/>: a name by its bytes, a code and a weight as numbers, the zeros alike.</summary>
    private static int Compare(DType schema, int entry, Ranked left, Ranked right) =>
        entry == schema.IndexOfField(nameof(Ranked.Name)) ? string.CompareOrdinal(left.Name, right.Name)
        : entry == schema.IndexOfField(nameof(Ranked.Code)) ? left.Code.CompareTo(right.Code)
        : left.Weight.CompareTo(right.Weight);

    /// <summary>The rows <paramref name="rows"/> as a batch of their own arena, the names in padded views.</summary>
    private static RecordBatch RankedBatch(DType schema, ReadOnlySpan<Ranked> rows)
    {
        CanonicalArena arena = new CanonicalArena();
        int count = rows.Length;
        long[] at = new long[count];
        int[] codes = new int[count];
        double[] weights = new double[count];
        long[] sizes = new long[count];
        byte[][] names = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            at[i] = rows[i].At.ToUnixTimeMilliseconds() * 1000;
            codes[i] = rows[i].Code;
            weights[i] = rows[i].Weight;
            sizes[i] = rows[i].Size;
            names[i] = Encoding.UTF8.GetBytes(rows[i].Name);
        }

        DType instant = schema.GetField(schema.IndexOfField(nameof(Ranked.At)));
        int[] fields =
        [
            arena.AddExtension(instant, count, arena.AddPrimitive(instant.StorageType, count, Validity.NonNullable, PType.I64, Buffer(arena, at))),
            PaddedViews(arena, schema.GetField(schema.IndexOfField(nameof(Ranked.Name))), names),
            arena.AddPrimitive(schema.GetField(schema.IndexOfField(nameof(Ranked.Code))), count, Validity.NonNullable, PType.I32, Buffer(arena, codes)),
            arena.AddPrimitive(schema.GetField(schema.IndexOfField(nameof(Ranked.Weight))), count, Validity.NonNullable, PType.F64, Buffer(arena, weights)),
            arena.AddPrimitive(schema.GetField(schema.IndexOfField(nameof(Ranked.Size))), count, Validity.NonNullable, PType.I64, Buffer(arena, sizes)),
        ];
        return new RecordBatch(arena, arena.AddStruct(schema, count, Validity.NonNullable, fields), 0);
    }

    /// <summary>The views of <paramref name="values"/>, each one's bytes inside it and zeros past them, or its first four and where its bytes lie.</summary>
    private static int PaddedViews(CanonicalArena arena, DType dtype, byte[][] values)
    {
        int past = values.Where(value => value.Length > 12).Sum(value => value.Length);
        VortexBuffer views = arena.Allocate(values.Length * 16, 16, out Span<byte> viewBytes);
        VortexBuffer data = arena.Allocate(Math.Max(past, 1), 1, out Span<byte> heap);
        int at = 0;
        for (int i = 0; i < values.Length; i++)
        {
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            view.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(view, values[i].Length);
            if (values[i].Length <= 12)
            {
                values[i].CopyTo(view[4..]);
                continue;
            }

            values[i].AsSpan(0, 4).CopyTo(view[4..]);
            BinaryPrimitives.WriteInt32LittleEndian(view[12..], at);
            values[i].CopyTo(heap[at..]);
            at += values[i].Length;
        }

        return arena.AddVarBinView(dtype, values.Length, Validity.NonNullable, views, past > 0 ? [data] : [], padded: true);
    }

    private static VortexBuffer Buffer<T>(CanonicalArena arena, T[] values)
        where T : unmanaged
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(values.AsSpan());
        VortexBuffer buffer = arena.Allocate(bytes.Length, Math.Max(Marshal.SizeOf<T>(), 1), out Span<byte> into);
        bytes.CopyTo(into);
        return buffer;
    }
}
