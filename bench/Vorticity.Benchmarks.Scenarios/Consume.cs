using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Bench.Scenarios;

/// <summary>
/// What a caller that uses the values of a scan reads of each batch, and what every scenario does
/// with one: each byte of every value of every row the batch delivers, once.
/// </summary>
/// <remarks>
/// <para>
/// A benchmark that counts rows times a reader that hands out values nobody reads. A column stored
/// plain is then, for a reader that maps its file, a view no page of is ever touched, and for one
/// that copies, a copy; a decoded column is written and never read again. Neither is what a caller
/// pays, since a caller reads the values: the scenarios read them, as the reference's harness reads
/// its arrays (tools/vxbench-rs, <c>consume</c>), byte for byte the same bytes.
/// </para>
/// <para>
/// The bytes: of a string, its 16-byte view and, out of line, the bytes the view names; of a list,
/// its offsets and sizes, and of its elements only the ranges its rows name; of a fixed-size list,
/// the elements of its rows; of a struct, its fields; of a dictionary, each row's code and the
/// value it names, as that row of the values would be read; of every node, the validity bits of
/// its rows; of a constant, its one element. They are summed as 64-bit words, which costs what
/// memory takes to deliver them and nothing a compiler could leave out.
/// </para>
/// </remarks>
public static class Consume
{
    /// <summary>What the reads summed, where no compiler can drop it.</summary>
    private static ulong _read;

    /// <summary>What every read so far summed: the same bytes read give the same sum, wherever they lie.</summary>
    public static ulong Read => Volatile.Read(ref _read);

    /// <summary>Reads every value of the rows <paramref name="batch"/> delivers.</summary>
    /// <param name="batch">The batch.</param>
    /// <returns>Its rows, as <see cref="RecordBatch.RowCount"/> counts them.</returns>
    public static long Batch(RecordBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return Selected(batch.Arena, batch.RootIndex, batch.RowCount, batch.SelectionWords);
    }

    /// <summary>Reads every value of the rows <paramref name="batch"/> delivers.</summary>
    /// <param name="batch">The batch, by index and name.</param>
    /// <returns>Its rows, as <see cref="BatchView.RowCount"/> counts them.</returns>
    public static long View(BatchView batch) =>
        Selected(batch.Arena, batch.Node, batch.RowCount, batch.SelectionWords);

    /// <summary>Reads every value of the rows <paramref name="batch"/> delivers.</summary>
    /// <typeparam name="TRecord">The record the batch's columns bind.</typeparam>
    /// <param name="batch">The batch, as a record's columns.</param>
    /// <returns>Its rows, as <see cref="Columns{TRecord}.RowCount"/> counts them.</returns>
    public static long Typed<TRecord>(Columns<TRecord> batch) =>
        Selected(batch.Arena, batch.Node, batch.RowCount, batch.SelectionWords);

    /// <summary>Reads the rows of node <paramref name="root"/> that <paramref name="selection"/> keeps, every row when it is empty.</summary>
    private static long Selected(CanonicalArena arena, int root, int rows, ReadOnlySpan<ulong> selection)
    {
        ulong sum = 0;
        if (selection.IsEmpty)
        {
            sum = Rows(arena, root, 0, rows);
        }
        else
        {
            // A batch delivered whole with the rows a filter kept: those rows, run by run.
            int row = 0;
            while (row < rows)
            {
                if ((selection[row >> 6] & (1UL << (row & 63))) == 0)
                {
                    row++;
                    continue;
                }

                int start = row;
                while (row < rows && (selection[row >> 6] & (1UL << (row & 63))) != 0)
                {
                    row++;
                }

                sum += Rows(arena, root, start, row - start);
            }
        }

        // One add a batch, atomic because the lanes of a scan read theirs at once.
        Interlocked.Add(ref _read, sum);
        return rows;
    }

    /// <summary>The sum of the bytes of rows <c>[start, start + count)</c> of node <paramref name="index"/>.</summary>
    private static ulong Rows(CanonicalArena arena, int index, int start, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        // An extension's validity is its storage's, which the storage reads.
        CanonicalNode node = arena.GetNode(index);
        ulong sum = node.Kind == CanonicalKind.Extension ? 0 : Validity(arena, node.Validity, start, count);
        switch (node.Kind)
        {
            case CanonicalKind.Null:
                return sum;
            case CanonicalKind.Bool:
                return sum + Bits(node.Bits.Span, node.BitOffset + start, count);
            case CanonicalKind.Primitive:
            {
                int width = node.PType.ByteWidth();
                return sum + Words(node.Values.Span.Slice(start * width, count * width));
            }

            case CanonicalKind.Decimal:
            {
                int width = DecimalStorage.ByteWidth(node.Storage);
                return sum + Words(node.Values.Span.Slice(start * width, count * width));
            }

            case CanonicalKind.VarBinView:
                return sum + Strings(node, start, count);
            case CanonicalKind.ListView:
                return sum + Lists(arena, node, start, count);
            case CanonicalKind.FixedSizeList:
            {
                int size = (int)node.FixedSize;
                return sum + Rows(arena, node.ElementsIndex, start * size, count * size);
            }

            case CanonicalKind.Struct:
                for (int field = 0; field < node.FieldCount; field++)
                {
                    sum += Rows(arena, node.GetFieldIndex(field), start, count);
                }

                return sum;
            case CanonicalKind.Extension:
                return sum + Rows(arena, node.StorageIndex, start, count);
            case CanonicalKind.Constant:
                return sum + Words(node.ConstantElement);
            case CanonicalKind.Dictionary:
                return sum + Entries(arena, node, start, count);
            case CanonicalKind.RunEnd:
                return sum + Words(node.RunEnds.Span)
                    + Rows(arena, node.EncodedValuesIndex, 0, arena.GetNode(node.EncodedValuesIndex).Length);
            default:
                throw new NotSupportedException($"A batch delivered a {node.Kind} node, which nothing here reads.");
        }
    }

    /// <summary>The validity bits of the rows, when the node has a bitmap.</summary>
    private static ulong Validity(CanonicalArena arena, Validity validity, int start, int count)
    {
        if (validity.Kind != ValidityKind.Bitmap)
        {
            return 0;
        }

        CanonicalNode bits = arena.GetNode(validity.CanonicalNodeIndex);
        return Bits(bits.Bits.Span, bits.BitOffset + start, count);
    }

    /// <summary>The bytes that hold bits <c>[first, first + count)</c>.</summary>
    private static ulong Bits(ReadOnlySpan<byte> bits, int first, int count)
    {
        int from = first >> 3;
        int to = (first + count + 7) >> 3;
        return Words(bits[from..to]);
    }

    /// <summary>The views of the rows and, out of line, the bytes each names.</summary>
    /// <remarks>
    /// The views as one span of words, then the bytes of the views that name them: a column of short
    /// strings, all of them inside their views, costs one vector pass and a look at each length.
    /// </remarks>
    private static ulong Strings(CanonicalNode node, int start, int count)
    {
        ReadOnlySpan<byte> views = node.Views.Span.Slice(start * 16, count * 16);
        ReadOnlySpan<VortexBuffer> data = node.DataBuffers;
        ReadOnlySpan<byte> bytes = default;
        int current = -1;
        ulong sum = Words(views);

        // Each view as two words, its length the low half of the first: a slice and a checked read a
        // row cost a column of short strings, all inside their views, 2.3 cycles a row on arm64.
        ref ulong at = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<byte, ulong>(views));
        for (int row = 0; row < count; row++)
        {
            ulong head = Unsafe.Add(ref at, row * 2);
            int length = (int)(uint)head;
            if (length > 12)
            {
                ulong tail = Unsafe.Add(ref at, (row * 2) + 1);
                int buffer = (int)(uint)tail;
                if (buffer != current)
                {
                    bytes = data[buffer].Span;
                    current = buffer;
                }

                sum += Short(bytes.Slice((int)(tail >> 32), length));
            }
        }

        return sum;
    }

    /// <summary>
    /// Each row's code and the value it names, read as that row of the values would be: a dictionary
    /// spares the reader its values' materialization, not the caller its values.
    /// </summary>
    /// <remarks>
    /// The values are read through the codes as a typed span, with no slice and no call a row: a
    /// million codes read a slice and a call at a time into one sum took 0.62 ms on arm64, where the
    /// reference reads the values it materialized in 0.40.
    /// </remarks>
    private static ulong Entries(CanonicalArena arena, CanonicalNode node, int start, int count)
    {
        ReadOnlySpan<uint> codes = MemoryMarshal.Cast<byte, uint>(node.Codes.Span.Slice(start * sizeof(uint), count * sizeof(uint)));
        int index = node.EncodedValuesIndex;
        CanonicalNode values = arena.GetNode(index);
        bool nulls = values.Validity.Kind == ValidityKind.Bitmap;
        if (values.Kind == CanonicalKind.Primitive)
        {
            ReadOnlySpan<byte> bytes = values.Values.Span;
            if (nulls)
            {
                CanonicalNode validity = arena.GetNode(values.Validity.CanonicalNodeIndex);
                ReadOnlySpan<byte> bits = validity.Bits.Span;
                int offset = validity.BitOffset;
                switch (values.PType.ByteWidth())
                {
                    case 1: return Gather<byte>(codes, bytes, bits, offset);
                    case 2: return Gather<ushort>(codes, bytes, bits, offset);
                    case 4: return Gather<uint>(codes, bytes, bits, offset);
                    case 8: return Gather<ulong>(codes, bytes, bits, offset);
                }
            }
            else
            {
                switch (values.PType.ByteWidth())
                {
                    case 1: return Gather<byte>(codes, bytes);
                    case 2: return Gather<ushort>(codes, bytes);
                    case 4: return Gather<uint>(codes, bytes);
                    case 8: return Gather<ulong>(codes, bytes);
                }
            }
        }
        else if (values.Kind == CanonicalKind.VarBinView && !nulls)
        {
            return Views(codes, values);
        }

        // Values of any other kind, or text with nulls of its own, a row at a time.
        ulong sum = 0;
        foreach (uint code in codes)
        {
            sum += code + Rows(arena, index, (int)code, 1);
        }

        return sum;
    }

    /// <summary>
    /// Each row's code and the value it names, four rows at a time into four sums, as
    /// <see cref="Words"/> keeps several: one sum would wait on its own add at every row.
    /// </summary>
    private static ulong Gather<T>(ReadOnlySpan<uint> codes, ReadOnlySpan<byte> bytes)
        where T : unmanaged, IBinaryInteger<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(bytes);
        ref uint at = ref MemoryMarshal.GetReference(codes);
        ulong first = 0;
        ulong second = 0;
        ulong third = 0;
        ulong fourth = 0;
        int i = 0;
        for (; i <= codes.Length - 4; i += 4)
        {
            uint a = Unsafe.Add(ref at, i);
            uint b = Unsafe.Add(ref at, i + 1);
            uint c = Unsafe.Add(ref at, i + 2);
            uint d = Unsafe.Add(ref at, i + 3);
            first += a + Bytes(values[(int)a]);
            second += b + Bytes(values[(int)b]);
            third += c + Bytes(values[(int)c]);
            fourth += d + Bytes(values[(int)d]);
        }

        for (; i < codes.Length; i++)
        {
            uint code = codes[i];
            first += code + Bytes(values[(int)code]);
        }

        return first + second + third + fourth;
    }

    /// <summary>
    /// <see cref="Gather{T}(ReadOnlySpan{uint}, ReadOnlySpan{byte})"/> for values with nulls of their
    /// own: each row's value and, as <see cref="Rows"/> reads one row, the byte that holds its bit.
    /// </summary>
    private static ulong Gather<T>(ReadOnlySpan<uint> codes, ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> bits, int offset)
        where T : unmanaged, IBinaryInteger<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(bytes);
        ref uint at = ref MemoryMarshal.GetReference(codes);
        ulong first = 0;
        ulong second = 0;
        ulong third = 0;
        ulong fourth = 0;
        int i = 0;
        for (; i <= codes.Length - 4; i += 4)
        {
            uint a = Unsafe.Add(ref at, i);
            uint b = Unsafe.Add(ref at, i + 1);
            uint c = Unsafe.Add(ref at, i + 2);
            uint d = Unsafe.Add(ref at, i + 3);
            first += a + bits[(offset + (int)a) >> 3] + Bytes(values[(int)a]);
            second += b + bits[(offset + (int)b) >> 3] + Bytes(values[(int)b]);
            third += c + bits[(offset + (int)c) >> 3] + Bytes(values[(int)c]);
            fourth += d + bits[(offset + (int)d) >> 3] + Bytes(values[(int)d]);
        }

        for (; i < codes.Length; i++)
        {
            uint code = codes[i];
            first += code + bits[(offset + (int)code) >> 3] + Bytes(values[(int)code]);
        }

        return first + second + third + fourth;
    }

    /// <summary>
    /// The bytes of one value, summed as <see cref="Short"/> sums them: a byte or a word whole, and the
    /// bytes of a narrower value one by one, folded in the register.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Bytes<T>(T value)
        where T : unmanaged, IBinaryInteger<T>
    {
        ulong word = ulong.CreateTruncating(value);
        if (Unsafe.SizeOf<T>() == 2)
        {
            return (word & 0xFF) + (word >> 8);
        }

        if (Unsafe.SizeOf<T>() == 4)
        {
            ulong pairs = (word & 0x00FF00FF) + ((word >> 8) & 0x00FF00FF);
            return (pairs & 0xFFFF) + (pairs >> 16);
        }

        return word;
    }

    /// <summary>
    /// Each row's code, the view it names and, out of line, the view's bytes: the views read as two
    /// words each, with no slice a row.
    /// </summary>
    private static ulong Views(ReadOnlySpan<uint> codes, CanonicalNode values)
    {
        ReadOnlySpan<ulong> views = MemoryMarshal.Cast<byte, ulong>(values.Views.Span);
        ReadOnlySpan<VortexBuffer> data = values.DataBuffers;
        ReadOnlySpan<byte> bytes = default;
        int current = -1;
        ulong sum = 0;
        foreach (uint code in codes)
        {
            // The row's view, length and prefix then buffer and offset, and the bytes it names.
            int at = (int)code << 1;
            ulong head = views[at];
            ulong tail = views[at + 1];
            sum += code + head + tail;
            int length = (int)(uint)head;
            if (length > 12)
            {
                int buffer = (int)(uint)tail;
                if (buffer != current)
                {
                    bytes = data[buffer].Span;
                    current = buffer;
                }

                sum += Short(bytes.Slice((int)(tail >> 32), length));
            }
        }

        return sum;
    }

    /// <summary>The offsets and sizes of the rows and the elements they name, as runs.</summary>
    private static ulong Lists(CanonicalArena arena, CanonicalNode node, int start, int count)
    {
        int offsetWidth = node.OffsetPType.ByteWidth();
        int sizeWidth = node.SizePType.ByteWidth();
        ReadOnlySpan<byte> offsets = node.Offsets.Span.Slice(start * offsetWidth, count * offsetWidth);
        ReadOnlySpan<byte> sizes = node.Sizes.Span.Slice(start * sizeWidth, count * sizeWidth);
        int elements = node.ElementsIndex;
        ulong sum = Words(offsets) + Words(sizes);

        // The widths chosen once, outside the loop over the rows, as the reference's `at` is inlined.
        return sum + offsetWidth switch
        {
            1 => Elements<byte>(arena, elements, offsets, sizes, sizeWidth),
            2 => Elements<ushort>(arena, elements, offsets, sizes, sizeWidth),
            4 => Elements<uint>(arena, elements, offsets, sizes, sizeWidth),
            _ => Elements<ulong>(arena, elements, offsets, sizes, sizeWidth),
        };
    }

    /// <summary><see cref="Elements{TOffset, TSize}"/> at the width of the sizes.</summary>
    private static ulong Elements<TOffset>(CanonicalArena arena, int elements, ReadOnlySpan<byte> offsets, ReadOnlySpan<byte> sizes, int sizeWidth)
        where TOffset : unmanaged, IBinaryInteger<TOffset> => sizeWidth switch
    {
        1 => Elements<TOffset, byte>(arena, elements, offsets, sizes),
        2 => Elements<TOffset, ushort>(arena, elements, offsets, sizes),
        4 => Elements<TOffset, uint>(arena, elements, offsets, sizes),
        _ => Elements<TOffset, ulong>(arena, elements, offsets, sizes),
    };

    /// <summary>
    /// The elements the rows name, as runs: rows that follow each other in the elements are one run.
    /// Offsets and sizes are never negative, so they are read unsigned. An empty row is not singled
    /// out: where it lies at the end of the run it changes nothing, and elsewhere it ends the run
    /// and starts an empty one, at no byte read twice.
    /// </summary>
    private static ulong Elements<TOffset, TSize>(CanonicalArena arena, int elements, ReadOnlySpan<byte> offsetBytes, ReadOnlySpan<byte> sizeBytes)
        where TOffset : unmanaged, IBinaryInteger<TOffset>
        where TSize : unmanaged, IBinaryInteger<TSize>
    {
        ReadOnlySpan<TOffset> offsets = MemoryMarshal.Cast<byte, TOffset>(offsetBytes);
        ReadOnlySpan<TSize> sizes = MemoryMarshal.Cast<byte, TSize>(sizeBytes);
        ulong sum = 0;
        long from = 0;
        long to = 0;
        for (int row = 0; row < sizes.Length && row < offsets.Length; row++)
        {
            long length = long.CreateTruncating(sizes[row]);
            long first = long.CreateTruncating(offsets[row]);
            if (first != to)
            {
                if (to > from)
                {
                    sum += Rows(arena, elements, (int)from, (int)(to - from));
                }

                from = first;
            }

            to = first + length;
        }

        if (to > from)
        {
            sum += Rows(arena, elements, (int)from, (int)(to - from));
        }

        return sum;
    }

    /// <summary>
    /// <see cref="Words"/> for the bytes of one value, a string's mostly: inlined where it is read,
    /// as the reference's <c>words</c> is, and handing a long one to the vector loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Short(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 128)
        {
            return Words(bytes);
        }

        // Read through a reference, as the reference's loop is compiled: a slice a word, and a checked
        // read of it, made a short string cost more than its bytes.
        ref byte at = ref MemoryMarshal.GetReference(bytes);
        ulong sum = 0;
        int i = 0;
        for (; i <= bytes.Length - sizeof(ulong); i += sizeof(ulong))
        {
            sum += Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref at, i));
        }

        for (; i < bytes.Length; i++)
        {
            sum += Unsafe.Add(ref at, i);
        }

        return sum;
    }

    /// <summary>
    /// The sum of <paramref name="bytes"/> as 64-bit words, the tail a byte at a time: every byte
    /// read, at the speed memory gives. <c>words</c> in the reference's harness.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    internal static ulong Words(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<ulong> words = MemoryMarshal.Cast<byte, ulong>(bytes);
        ref ulong at = ref MemoryMarshal.GetReference(words);
        ulong sum = 0;
        int i = 0;
        if (Vector512.IsHardwareAccelerated && words.Length >= 16)
        {
            Vector512<ulong> even = Vector512<ulong>.Zero;
            Vector512<ulong> odd = Vector512<ulong>.Zero;
            for (; i <= words.Length - 16; i += 16)
            {
                even += Vector512.LoadUnsafe(ref at, (nuint)i);
                odd += Vector512.LoadUnsafe(ref at, (nuint)(i + 8));
            }

            sum = Vector512.Sum(even + odd);
        }
        else if (Vector256.IsHardwareAccelerated && words.Length >= 8)
        {
            Vector256<ulong> even = Vector256<ulong>.Zero;
            Vector256<ulong> odd = Vector256<ulong>.Zero;
            for (; i <= words.Length - 8; i += 8)
            {
                even += Vector256.LoadUnsafe(ref at, (nuint)i);
                odd += Vector256.LoadUnsafe(ref at, (nuint)(i + 4));
            }

            sum = Vector256.Sum(even + odd);
        }
        else if (Vector128.IsHardwareAccelerated && words.Length >= 8)
        {
            // Arm64 has 128-bit vectors only. Four sums, two words each, as LLVM unrolls the
            // reference's loop there: one would wait on its own add at every load.
            Vector128<ulong> first = Vector128<ulong>.Zero;
            Vector128<ulong> second = Vector128<ulong>.Zero;
            Vector128<ulong> third = Vector128<ulong>.Zero;
            Vector128<ulong> fourth = Vector128<ulong>.Zero;
            for (; i <= words.Length - 8; i += 8)
            {
                first += Vector128.LoadUnsafe(ref at, (nuint)i);
                second += Vector128.LoadUnsafe(ref at, (nuint)(i + 2));
                third += Vector128.LoadUnsafe(ref at, (nuint)(i + 4));
                fourth += Vector128.LoadUnsafe(ref at, (nuint)(i + 6));
            }

            sum = Vector128.Sum(first + second + third + fourth);
        }

        for (; i < words.Length; i++)
        {
            sum += words[i];
        }

        for (int b = words.Length * sizeof(ulong); b < bytes.Length; b++)
        {
            sum += bytes[b];
        }

        return sum;
    }
}
