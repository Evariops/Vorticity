using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Arrays;

internal partial class CanonicalArena
{
    /// <summary>Adds a dictionary node: a code per row into a child of distinct values.</summary>
    /// <param name="dtype">The dtype this node produces.</param>
    /// <param name="length">Row count.</param>
    /// <param name="validity">The row validity: null where the code is null or names a null value.</param>
    /// <param name="codes">Exactly <c>length * 4</c> bytes of <c>u32</c> codes, each below the values' length.</param>
    /// <param name="valuesIndex">The child holding the distinct values in code order.</param>
    /// <returns>The new node's index.</returns>
    /// <remarks>
    /// The codes are not checked here: every producer has already bounded them, once, where they
    /// were widened, and a gather or a slice of a bounded set stays bounded.
    /// </remarks>
    /// <exception cref="VortexFormatException"><paramref name="codes"/> is the wrong length.</exception>
    public int AddDictionary(DType dtype, int length, Validity validity, VortexBuffer codes, int valuesIndex)
    {
        RequireExactLength(codes.Length, length, sizeof(uint), "Dictionary");
        RequireChild(valuesIndex);
        CanonicalRecord r = New(CanonicalKind.Dictionary, dtype, length, validity);
        r.PType = PType.U32;
        r.BufferA = codes;
        r.ChildStart = AddChild(valuesIndex);
        r.ChildCount = 1;
        return Commit(ref r);
    }

    /// <summary>Adds a run-end node: an exclusive end per run into a child of one value per run.</summary>
    /// <param name="dtype">The dtype this node produces.</param>
    /// <param name="length">Row count.</param>
    /// <param name="validity">The row validity, which is each run's value validity over its rows.</param>
    /// <param name="ends">One <c>u32</c> per run, strictly increasing, the last equal to <paramref name="length"/>.</param>
    /// <param name="valuesIndex">The child holding one value per run.</param>
    /// <returns>The new node's index.</returns>
    /// <exception cref="VortexFormatException">The ends disagree with the values or with the row count.</exception>
    public int AddRunEnd(DType dtype, int length, Validity validity, VortexBuffer ends, int valuesIndex)
    {
        RequireChild(valuesIndex);
        int runs = _records[valuesIndex].Length;
        RequireExactLength(ends.Length, runs, sizeof(uint), "RunEnd");
        bool shaped = runs == 0
            ? length == 0
            : MemoryMarshal.Cast<byte, uint>(ends.Span)[runs - 1] == (uint)length;
        if (!shaped)
        {
            ArraysThrow.Format($"A run-end node of {length} rows must end its {runs} runs at row {length}.");
        }

        CanonicalRecord r = New(CanonicalKind.RunEnd, dtype, length, validity);
        r.PType = PType.U32;
        r.BufferA = ends;
        r.ChildStart = AddChild(valuesIndex);
        r.ChildCount = 1;
        return Commit(ref r);
    }

    /// <summary>
    /// The node itself, or the canonical twin of a dictionary or run-end node; a constant stays a
    /// constant.
    /// </summary>
    /// <param name="nodeIndex">Any node.</param>
    /// <returns>A node in a canonical form.</returns>
    internal int Decoded(int nodeIndex) =>
        RecordRef(nodeIndex).Kind is CanonicalKind.Dictionary or CanonicalKind.RunEnd
            ? MaterializeEncoded(nodeIndex)
            : nodeIndex;

    /// <summary>
    /// The node with every dictionary or run-end node beneath it decoded: the node itself when there
    /// is none, otherwise new records along the path down to each one, sharing everything else.
    /// </summary>
    /// <param name="nodeIndex">Any node.</param>
    /// <returns>A node no encoded node is reachable from.</returns>
    /// <remarks>
    /// For a consumer that walks a whole tree and reads values everywhere, the writer first among
    /// them: one pass at its door spares every pass behind it a case of its own.
    /// </remarks>
    internal int DecodedTree(int nodeIndex) => DecodedTree(nodeIndex, depth: 1);

    private int DecodedTree(int nodeIndex, int depth)
    {
        VortexLimits.CheckDepth(depth, VortexLimits.MaxArrayDepth, "Canonical decode");

        // By value: the records below may grow the array.
        CanonicalRecord record = RecordRef(nodeIndex);
        switch (record.Kind)
        {
            case CanonicalKind.Dictionary:
            case CanonicalKind.RunEnd:
                return MaterializeEncoded(nodeIndex);

            case CanonicalKind.Struct:
                return DecodedFields(nodeIndex, in record, depth);

            case CanonicalKind.Extension:
            {
                int storage = _children[record.ChildStart];
                int decoded = DecodedTree(storage, depth + 1);
                return decoded == storage ? nodeIndex : AddExtension(record.DType, record.Length, decoded);
            }

            case CanonicalKind.ListView:
            case CanonicalKind.FixedSizeList:
            {
                int elements = _children[record.ChildStart];
                int decoded = DecodedTree(elements, depth + 1);
                if (decoded == elements)
                {
                    return nodeIndex;
                }

                return record.Kind == CanonicalKind.ListView
                    ? AddListView(
                        record.DType, record.Length, record.Validity, decoded,
                        record.BufferA, record.PType, record.BufferB, record.SizePType)
                    : AddFixedSizeList(record.DType, record.Length, record.Validity, decoded, record.FixedSize);
            }

            default:
                return nodeIndex;
        }
    }

    /// <summary>The <see cref="CanonicalKind.Struct"/> arm of <see cref="DecodedTree(int)"/>.</summary>
    private int DecodedFields(int nodeIndex, in CanonicalRecord record, int depth)
    {
        int count = record.ChildCount;
        int first = -1;
        int firstDecoded = -1;
        for (int i = 0; i < count && first < 0; i++)
        {
            int child = _children[record.ChildStart + i];
            int decoded = DecodedTree(child, depth + 1);
            if (decoded != child)
            {
                first = i;
                firstDecoded = decoded;
            }
        }

        if (first < 0)
        {
            return nodeIndex;
        }

        int start = ReserveChildren(count);
        for (int i = 0; i < count; i++)
        {
            int child = _children[record.ChildStart + i];
            int decoded = i < first ? child : i == first ? firstDecoded : DecodedTree(child, depth + 1);

            // Stored after the recursion: a deeper rebuild may have grown the child array.
            _children[start + i] = decoded;
        }

        CanonicalRecord copy = record;
        copy.ChildStart = start;
        copy.ChildCount = count;
        return Commit(ref copy);
    }

    /// <summary>Expands a dictionary or run-end node into a canonical twin, once.</summary>
    /// <param name="nodeIndex">A node of kind <see cref="CanonicalKind.Dictionary"/> or <see cref="CanonicalKind.RunEnd"/>.</param>
    /// <returns>The twin's index: a Bool, Primitive, Decimal or VarBinView node of the same rows and validity.</returns>
    /// <remarks>
    /// Memoized on the record, as <see cref="MaterializeConstant"/> is and for its reasons. A null
    /// row of a dictionary's twin is zeroed; a null run's rows repeat the run's value bytes, which
    /// is what the run-end decoder writes too.
    /// </remarks>
    internal int MaterializeEncoded(int nodeIndex)
    {
        ref CanonicalRecord source = ref RecordRefMutable(nodeIndex);
        if (source.Kind is not (CanonicalKind.Dictionary or CanonicalKind.RunEnd))
        {
            ArraysThrow.Kind(source.Kind, "Dictionary or RunEnd");
        }

        if (source.Materialized >= 0)
        {
            return source.Materialized;
        }

        // By value: the twin's records may grow the array under a reference.
        CanonicalRecord node = source;
        CanonicalRecord values = _records[_children[node.ChildStart]];
        int twin = node.Kind == CanonicalKind.Dictionary
            ? ExpandDictionary(in node, in values)
            : ExpandRuns(in node, in values);

        RecordRefMutable(nodeIndex).Materialized = twin;
        return twin;
    }

    private int ExpandDictionary(in CanonicalRecord node, in CanonicalRecord values)
    {
        int rows = node.Length;
        int cardinality = values.Length;
        ReadOnlySpan<byte> codes = node.BufferA.Span;
        Validity validity = node.Validity;

        if (values.Kind == CanonicalKind.Bool)
        {
            VortexBuffer bits = VortexBuffer.Empty;
            Span<byte> target = default;
            if (rows > 0)
            {
                bits = Allocate(CanonicalSupport.BitmapByteCount(rows), 8, out target);
            }

            if (rows == 0 || validity.Kind == ValidityKind.AllInvalid)
            {
                return AddBool(node.DType, rows, validity, bits, 0);
            }

            ReadOnlySpan<uint> typed = MemoryMarshal.Cast<byte, uint>(codes)[..rows];
            ReadOnlySpan<byte> source = values.BufferA.Span;
            int offset = values.BitOffset;
            bool masked = RowBits(validity, out ReadOnlySpan<byte> rowBits, out int rowOffset);
            for (int row = 0; row < rows; row++)
            {
                if (masked && !CanonicalSupport.BitAt(rowBits, rowOffset + row))
                {
                    continue;
                }

                uint code = typed[row];
                if (code >= (uint)cardinality)
                {
                    ThrowCode(code, row, cardinality);
                }

                if (CanonicalSupport.BitAt(source, offset + (int)code))
                {
                    CanonicalSupport.SetBit(target, row);
                }
            }

            return AddBool(node.DType, rows, validity, bits, 0);
        }

        int width = WidthOf(in values);
        VortexBuffer buffer = VortexBuffer.Empty;
        if (rows > 0)
        {
            buffer = AllocateUninitialized(checked(rows * width), width, out Span<byte> into);
            int bad;
            if (validity.Kind == ValidityKind.AllInvalid)
            {
                into.Clear();
                bad = -1;
            }
            else if (validity.IsAllValid)
            {
                bad = RowKernels.Gather(codes, PType.U32, values.BufferA.Span, width, cardinality, into, rows);
            }
            else
            {
                // The row validity masks the gather, so a null row is zeroed rather than copied.
                RowBits(validity, out ReadOnlySpan<byte> rowBits, out int rowOffset);
                bad = RowKernels.GatherMasked(
                    codes, PType.U32, values.BufferA.Span, width, cardinality, into, rows,
                    rowBits, rowOffset, default, 0, valuesAllValid: true, outputBits: default);
            }

            if (bad >= 0)
            {
                ThrowCode(MemoryMarshal.Cast<byte, uint>(codes)[bad], bad, cardinality);
            }
        }

        return Publish(in node, in values, buffer);
    }

    private int ExpandRuns(in CanonicalRecord node, in CanonicalRecord values)
    {
        int rows = node.Length;
        ReadOnlySpan<uint> ends = MemoryMarshal.Cast<byte, uint>(node.BufferA.Span)[..values.Length];

        if (values.Kind == CanonicalKind.Bool)
        {
            VortexBuffer bits = VortexBuffer.Empty;
            Span<byte> target = default;
            if (rows > 0)
            {
                bits = Allocate(CanonicalSupport.BitmapByteCount(rows), 8, out target);
            }

            ReadOnlySpan<byte> source = values.BufferA.Span;
            int start = 0;
            for (int run = 0; run < ends.Length; run++)
            {
                int end = (int)ends[run];
                if (CanonicalSupport.BitAt(source, values.BitOffset + run))
                {
                    BitmapKernels.SetRange(target, start, end - start);
                }

                start = end;
            }

            return AddBool(node.DType, rows, node.Validity, bits, 0);
        }

        int width = WidthOf(in values);
        VortexBuffer buffer = VortexBuffer.Empty;
        if (rows > 0)
        {
            // Uninitialized: the runs tile [0, rows) exactly, since the ends strictly increase and
            // the last is the row count, and a descending end throws in the slice before a byte of
            // it could be skipped.
            buffer = AllocateUninitialized(checked(rows * width), width, out Span<byte> into);
            ReadOnlySpan<byte> run = values.BufferA.Span;
            switch (width)
            {
                case 1:
                    Repeat<byte>(ends, run, into);
                    break;
                case 2:
                    Repeat<ushort>(ends, run, into);
                    break;
                case 4:
                    Repeat<uint>(ends, run, into);
                    break;
                case 8:
                    Repeat<ulong>(ends, run, into);
                    break;
                case 16:
                    Repeat<Vector128<byte>>(ends, run, into);
                    break;
                default:
                    int start = 0;
                    for (int r = 0; r < ends.Length; r++)
                    {
                        int end = (int)ends[r];
                        RowKernels.Tile(into[(start * width)..(end * width)], run.Slice(r * width, width));
                        start = end;
                    }

                    break;
            }
        }

        return Publish(in node, in values, buffer);
    }

    /// <summary>Fills each run's rows with its value, the value width resolved by the caller.</summary>
    private static void Repeat<T>(ReadOnlySpan<uint> ends, ReadOnlySpan<byte> values, Span<byte> destination)
        where T : unmanaged
    {
        ReadOnlySpan<T> source = MemoryMarshal.Cast<byte, T>(values)[..ends.Length];
        Span<T> target = MemoryMarshal.Cast<byte, T>(destination);
        int start = 0;
        for (int run = 0; run < ends.Length; run++)
        {
            int end = (int)ends[run];
            target[start..end].Fill(source[run]);
            start = end;
        }
    }

    /// <summary>Publishes the expanded values as the canonical kind of the values child.</summary>
    private int Publish(in CanonicalRecord node, in CanonicalRecord values, VortexBuffer buffer) =>
        values.Kind switch
        {
            CanonicalKind.Primitive => AddPrimitive(node.DType, node.Length, node.Validity, values.PType, buffer),
            CanonicalKind.Decimal => AddDecimal(
                node.DType, node.Length, node.Validity, values.Storage, values.Precision, values.Scale, buffer),
            _ => AddViewsSharing(node.DType, node.Length, node.Validity, buffer, values.DataBufferStart, values.DataBufferCount),
        };

    /// <summary>A VarBinView node whose views name the data buffers of another node of this arena.</summary>
    private int AddViewsSharing(
        DType dtype, int length, Validity validity, VortexBuffer views, int dataStart, int dataCount)
    {
        RequireExactLength(views.Length, length, CanonicalSupport.ViewSize, "VarBinView");
        CanonicalRecord r = New(CanonicalKind.VarBinView, dtype, length, validity);
        r.BufferA = views;
        r.DataBufferStart = _dataBufferCount;
        r.DataBufferCount = dataCount;
        for (int i = 0; i < dataCount; i++)
        {
            AddDataBuffer(_dataBuffers[dataStart + i]);
        }

        return Commit(ref r);
    }

    /// <summary>Bytes per value of a values child an encoded node may carry.</summary>
    private static int WidthOf(in CanonicalRecord values) => values.Kind switch
    {
        CanonicalKind.Primitive => values.PType.ByteWidth(),
        CanonicalKind.Decimal => DecimalStorage.ByteWidth(values.Storage),
        CanonicalKind.VarBinView => CanonicalSupport.ViewSize,
        _ => ArraysThrow.Format<int>(
            $"An encoded node's values are {values.Kind}; Bool, Primitive, Decimal and VarBinView are the domain."),
    };

    /// <summary>The bitmap behind a validity, when it has one.</summary>
    private bool RowBits(Validity validity, out ReadOnlySpan<byte> bits, out int bitOffset)
    {
        if (validity.Kind != ValidityKind.Bitmap)
        {
            bits = default;
            bitOffset = 0;
            return false;
        }

        ref readonly CanonicalRecord bitmap = ref _records[validity.CanonicalNodeIndex];
        bits = bitmap.BufferA.Span;
        bitOffset = bitmap.BitOffset;
        return true;
    }

    [DoesNotReturn]
    private static void ThrowCode(uint code, int row, int cardinality) =>
        ArraysThrow.Format($"Dictionary code {code} at row {row} is outside [0, {cardinality}).");
}
