// Rows a copy moved, under a composite key. The writer copies a batch of a few rows to wait for the
// next ones, an append copies the last chunk it reopens, and a scan's owned batches are copies. A
// copy that started each buffer where the one before it ended left a column of 64-bit values four
// bytes off its boundary behind 32-bit values over an odd number of rows, or behind the offsets a
// list window is rebased to just before the copy, and the row encoding of the key refused them.
// Every test here puts a narrow column in front of a wide one, or a list after them, moves the rows
// through a path that copies them, and reads the composite key back.
//
// THE ORACLE IS THE ENCODER ITSELF, one tuple at a time, as in `CompositeKeyTests`: the key index
// holds every non-null tuple's `EncodeKey` bytes, in bytewise order with the row breaking ties.
using System;
using System.Collections.Generic;
using System.IO;
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
using Vorticity.IO;
using Vorticity.Keys;
using Vorticity.Layouts;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Keys;

public sealed class CopiedRowKeyTests
{
    /// <summary>The rows of every file: the batch plans below end on a batch small enough to wait.</summary>
    private const int Rows = 296;

    private static readonly RowSortField Asc = RowSortField.Ascending;

    /// <summary>
    /// A narrow column in front of a wide one, for pairs of widths where an odd number of narrow
    /// values leaves the wide ones off their boundary, the narrow one nullable or not; then text,
    /// which sets how few rows a batch must hold to wait, a list, which is rebased before it is
    /// copied, or nothing.
    /// </summary>
    private static readonly Dictionary<string, (PType Narrow, PType Wide, bool Nullable, Tail Tail)> Catalogue = new()
    {
        ["u8 then u16, then text"] = (PType.U8, PType.U16, false, Tail.Text),
        ["i8 then f16, then text"] = (PType.I8, PType.F16, false, Tail.Text),
        ["nullable i8 then i64"] = (PType.I8, PType.I64, true, Tail.None),
        ["i16 then i32"] = (PType.I16, PType.I32, false, Tail.None),
        ["nullable u16 then f64, then text"] = (PType.U16, PType.F64, true, Tail.Text),
        ["f32 then f64, then text"] = (PType.F32, PType.F64, false, Tail.Text),
        ["u32 then u64"] = (PType.U32, PType.U64, false, Tail.None),
        ["u64 then u64, then a list"] = (PType.U64, PType.U64, false, Tail.List),
        ["nullable i32 then i64, then a list"] = (PType.I32, PType.I64, true, Tail.List),
    };

    public static TheoryData<string> Shapes => [.. Catalogue.Keys];

    /// <summary>What follows the two key columns.</summary>
    private enum Tail
    {
        None,
        Text,
        List,
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task RowsThatWaitInTheWriterKeepTheirCompositeKey(string name)
    {
        // A batch of one to five rows waits for the next ones, copied into the writer's own arena,
        // and goes through alone when a batch too big to wait comes after it, or when the file is
        // completed: the composite index then encodes the copy.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        Shape shape = new Shape(Catalogue[name]);

        using MemoryStream stream = new MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), shape.Schema, KeyOptions()))
        {
            int[] plan = [3, 70, 1, 70, 5, 70];
            int start = 0;
            for (int b = 0; start < Rows; b++)
            {
                int count = Math.Min(plan[b % plan.Length], Rows - start);
                await shape.WriteAsync(writer, start, count, ct);
                start += count;
            }

            await writer.CompleteAsync(ct);
        }

        await using MemorySegmentSource source = new MemorySegmentSource(stream.ToArray());
        await using VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions(), ct);
        await AssertKeysAsync(shape, file, ct);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task OwnedBatchesOfAScanKeepTheirCompositeKey(string name)
    {
        // `ToBatchesAsync` hands over copies, as `Columns<T>.ToOwned()` does: batches of 71 rows,
        // too many to wait, go through a writer that indexes the composite key as they are. A scan
        // evens its batches out, so each one reads a range of its own. The source is stored
        // canonical, so that the copies hold the values themselves and not a dictionary the writer
        // would expand into buffers of its own.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        Shape shape = new Shape(Catalogue[name]);

        using MemoryStream plain = new MemoryStream();
        VortexWriteOptions canonical = new VortexWriteOptions { Compression = CompressionProfile.None };
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(plain), shape.Schema, canonical))
        {
            await shape.WriteAsync(writer, 0, Rows, ct);
            await writer.CompleteAsync(ct);
        }

        using MemoryStream keyed = new MemoryStream();
        await using (MemorySegmentSource bytes = new MemorySegmentSource(plain.ToArray()))
        await using (VortexFile file = await VortexFile.OpenAsync(bytes, new VortexOpenOptions(), ct))
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(keyed), shape.Schema, KeyOptions()))
        {
            for (int start = 0; start < Rows; start += 71)
            {
                await foreach (RecordBatch batch in file.Scan().Rows(new RowRange(start, Math.Min(start + 71, Rows))).ToBatchesAsync(ct))
                {
                    using (batch)
                    {
                        await writer.WriteAsync(batch, ct);
                    }
                }
            }

            await writer.CompleteAsync(ct);
        }

        await using MemorySegmentSource source = new MemorySegmentSource(keyed.ToArray());
        await using VortexFile written = await VortexFile.OpenAsync(source, new VortexOpenOptions(), ct);
        await AssertKeysAsync(shape, written, ct);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task AnAppendCopiesTheChunkItReopensAndKeepsTheCompositeKey(string name)
    {
        // 167 rows in blocks of 64 end on a chunk of 39, which an append reads back, copies and writes
        // again under the composite index, before the rows it adds.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        Shape shape = new Shape(Catalogue[name]);
        VortexWriteOptions options = KeyOptions() with { RowBlockSize = 64 };
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-copied-keys-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexFileWriter.Create(path, shape.Schema, options))
            {
                await shape.WriteAsync(writer, 0, 167, ct);
                await writer.CompleteAsync(ct);
            }

            await using (VortexFileWriter writer = await VortexFileWriter.AppendAsync(path, options, ct))
            {
                await shape.WriteAsync(writer, 167, Rows - 167, ct);
                await writer.CompleteAsync(ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            await AssertKeysAsync(shape, file, ct);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>A sorted run on the tuple (k, n), encoded by the row encoder, and room for it in any budget.</summary>
    private static VortexWriteOptions KeyOptions() => new VortexWriteOptions
    {
        IndexBudgetPerMille = 1_000_000,
        WritePolicy = WritePolicy.None.ForKey(["k", "n"], IndexSpec.SortedRuns),
        KeyEncoder = new RowKeyEncoder(Asc),
    };

    /// <summary>The walk of the composite key: every non-null tuple, in the order the oracle sorts them.</summary>
    private static async Task AssertKeysAsync(Shape shape, VortexFile file, CancellationToken ct)
    {
        List<(byte[] Key, long Row)> oracle = shape.Oracle();
        await using KeyCursor cursor = await file.Keys("k", "n").OpenAsync(ct);
        Assert.Equal(oracle.Count, cursor.EntryCount);

        int index = 0;
        for (bool ok = await cursor.SeekFirstAsync(ct); ok; ok = await cursor.NextAsync(ct))
        {
            Assert.True(cursor.KeyBytes.SequenceEqual(oracle[index].Key), $"entry {index}: the key differs");
            Assert.Equal(oracle[index].Row, cursor.Row);
            index++;
        }

        Assert.Equal(oracle.Count, index);
    }

    /// <summary>The rows of a test, built once in an arena of their own and written as windows of it.</summary>
    private sealed class Shape
    {
        private readonly DTypeArena _types = new DTypeArena();
        private readonly CanonicalArena _arena = new CanonicalArena();
        private readonly PType _narrow;
        private readonly PType _wide;
        private readonly bool _nullable;
        private readonly int _root;

        internal Shape((PType Narrow, PType Wide, bool Nullable, Tail Tail) shape)
        {
            _narrow = shape.Narrow;
            _wide = shape.Wide;
            _nullable = shape.Nullable;

            List<string> names = ["n", "k"];
            List<int> columns = [Primitive(_narrow, _nullable, Narrow), Primitive(_wide, false, Wide)];
            switch (shape.Tail)
            {
                case Tail.Text:
                    names.Add("t");
                    columns.Add(Text());
                    break;
                case Tail.List:
                    names.Add("l");
                    columns.Add(List());
                    break;
            }

            DType[] types = new DType[columns.Count];
            for (int i = 0; i < types.Length; i++)
            {
                types[i] = _arena.GetNode(columns[i]).DType;
            }

            Schema = _types.Struct([.. names], types, Nullability.NonNullable);
            _root = _arena.AddStruct(Schema, Rows, Validity.NonNullable, [.. columns]);
        }

        internal DType Schema { get; }

        /// <summary>Writes rows <c>[start, start + count)</c> as one batch: a window of the rows, as a scan hands one over.</summary>
        internal async Task WriteAsync(VortexFileWriter writer, int start, int count, CancellationToken ct)
        {
            CanonicalArena windows = new CanonicalArena();
            int window = CanonicalSlice.SliceAcross(_arena, windows, _root, start, count);
            using (RecordBatch batch = new RecordBatch(windows, window, start))
            {
                await writer.WriteAsync(batch, ct);
            }

            windows.Reset();
        }

        /// <summary>Every tuple without a null, encoded alone and sorted, the row breaking ties.</summary>
        internal List<(byte[] Key, long Row)> Oracle()
        {
            DType wide = _types.Primitive(_wide, Nullability.NonNullable);
            DType narrow = _types.Primitive(_narrow, _nullable ? Nullability.Nullable : Nullability.NonNullable);
            List<(byte[] Key, long Row)> entries = [];
            for (int row = 0; row < Rows; row++)
            {
                if (_nullable && IsNull(row))
                {
                    continue;
                }

                entries.Add((RowEncoder.EncodeKey([Literal(_wide, Wide(row)), Literal(_narrow, Narrow(row))], [wide, narrow], [Asc, Asc]), row));
            }

            entries.Sort((a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key) is var o && o != 0 ? o : a.Row.CompareTo(b.Row));
            return entries;
        }

        private static long Narrow(int row) => (row * 7) % 101;

        /// <summary>Few distinct values, so that the narrow column orders the rows that share one.</summary>
        private static long Wide(int row) => (row * 31) % 97;

        private static bool IsNull(int row) => row % 5 == 2;

        private static FilterLiteral Literal(PType ptype, long value) =>
            ptype.IsFloat() ? FilterLiteral.From((double)value) : FilterLiteral.From(value);

        private int Primitive(PType ptype, bool nullable, Func<int, long> value)
        {
            int width = ptype.ByteWidth();
            VortexBuffer buffer = _arena.Allocate(Rows * width, 16, out Span<byte> bytes);
            for (int row = 0; row < Rows; row++)
            {
                Span<byte> slot = bytes.Slice(row * width, width);
                switch (ptype)
                {
                    case PType.U8 or PType.I8:
                        slot[0] = (byte)value(row);
                        break;
                    case PType.U16 or PType.I16:
                        MemoryMarshal.Write(slot, (short)value(row));
                        break;
                    case PType.F16:
                        MemoryMarshal.Write(slot, (Half)value(row));
                        break;
                    case PType.U32 or PType.I32:
                        MemoryMarshal.Write(slot, (int)value(row));
                        break;
                    case PType.F32:
                        MemoryMarshal.Write(slot, (float)value(row));
                        break;
                    case PType.F64:
                        MemoryMarshal.Write(slot, (double)value(row));
                        break;
                    default:
                        MemoryMarshal.Write(slot, value(row));
                        break;
                }
            }

            Validity validity = Validity.NonNullable;
            if (nullable)
            {
                VortexBuffer bits = _arena.Allocate((Rows + 7) / 8, 8, out Span<byte> raw);
                for (int row = 0; row < Rows; row++)
                {
                    if (!IsNull(row))
                    {
                        raw[row >> 3] |= (byte)(1 << (row & 7));
                    }
                }

                validity = Validity.Bitmap(_arena.AddBool(_types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0));
            }

            DType dtype = _types.Primitive(ptype, nullable ? Nullability.Nullable : Nullability.NonNullable);
            return _arena.AddPrimitive(dtype, Rows, validity, ptype, buffer);
        }

        /// <summary>Text, a third of it too long to sit in its view.</summary>
        private int Text()
        {
            byte[][] values = new byte[Rows][];
            int heap = 0;
            for (int row = 0; row < Rows; row++)
            {
                values[row] = Encoding.UTF8.GetBytes(row % 3 == 0 ? $"a value long enough to spill, row {row}" : $"t{row % 40}");
                heap += values[row].Length > 12 ? values[row].Length : 0;
            }

            VortexBuffer data = _arena.Allocate(heap, 1, out Span<byte> dataBytes);
            VortexBuffer views = _arena.Allocate(Rows * 16, 16, out Span<byte> viewBytes);
            int offset = 0;
            for (int row = 0; row < Rows; row++)
            {
                Span<byte> view = viewBytes.Slice(row * 16, 16);
                byte[] value = values[row];
                MemoryMarshal.Write(view, value.Length);
                if (value.Length <= 12)
                {
                    value.CopyTo(view[4..]);
                    continue;
                }

                value.AsSpan(0, 4).CopyTo(view[4..]);
                MemoryMarshal.Write(view[12..], offset);
                value.CopyTo(dataBytes[offset..]);
                offset += value.Length;
            }

            return _arena.AddVarBinView(_types.Utf8(Nullability.NonNullable), Rows, Validity.NonNullable, views, [data]);
        }

        /// <summary>A list of zero to three 64-bit values a row, which a window shares whole until the writer rebases it.</summary>
        private int List()
        {
            int total = 0;
            for (int row = 0; row < Rows; row++)
            {
                total += row % 4;
            }

            DType i64 = _types.Primitive(PType.I64, Nullability.NonNullable);
            VortexBuffer elementBuffer = _arena.Allocate(total * sizeof(long), 16, out Span<byte> elementBytes);
            VortexBuffer offsetBuffer = _arena.Allocate(Rows * sizeof(uint), 16, out Span<byte> offsetBytes);
            VortexBuffer sizeBuffer = _arena.Allocate(Rows * sizeof(uint), 16, out Span<byte> sizeBytes);
            Span<long> elements = MemoryMarshal.Cast<byte, long>(elementBytes);
            Span<uint> offsets = MemoryMarshal.Cast<byte, uint>(offsetBytes);
            Span<uint> sizes = MemoryMarshal.Cast<byte, uint>(sizeBytes);
            int at = 0;
            for (int row = 0; row < Rows; row++)
            {
                offsets[row] = (uint)at;
                sizes[row] = (uint)(row % 4);
                for (int j = 0; j < row % 4; j++)
                {
                    elements[at++] = (row * 10L) + j;
                }
            }

            int child = _arena.AddPrimitive(i64, total, Validity.NonNullable, PType.I64, elementBuffer);
            return _arena.AddListView(
                _types.List(i64, Nullability.NonNullable), Rows, Validity.NonNullable, child, offsetBuffer, PType.U32, sizeBuffer, PType.U32);
        }
    }
}
