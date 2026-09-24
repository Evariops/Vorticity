using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Benchmarks;

/// <summary>
/// What a file's values weigh as flat arrays, whatever their encodings: the reference a throughput
/// in gigabytes a second is taken against.
/// </summary>
/// <remarks>
/// The measure is Arrow's plain layout, so that it belongs to neither implementation: 8 bytes a
/// 64-bit value, a text or binary value's bytes and a 4-byte offset, a bit a boolean, a bit a row
/// for a column that can be null, a 4-byte offset a row for a list and its elements. A constant
/// column weighs what it would written out. Both readers deliver the same rows, so one figure
/// serves both sides of a comparison.
/// </remarks>
internal static class PlainSize
{
    /// <summary>The rows of the file at <paramref name="path"/> and the plain bytes of each of its top-level columns.</summary>
    internal static async Task<(long Rows, long[] Columns)> OfFileAsync(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None).ConfigureAwait(false);
        long rows = 0;
        long[]? columns = null;
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync()
            .WithCancellation(CancellationToken.None).ConfigureAwait(false))
        {
            rows += batch.RowCount;
            CanonicalArena arena = batch.Arena;
            CanonicalNode root = arena.GetNode(batch.RootIndex);
            if (root.Kind == CanonicalKind.Struct)
            {
                columns ??= new long[root.FieldCount];
                for (int field = 0; field < root.FieldCount; field++)
                {
                    columns[field] += Of(arena, root.GetFieldIndex(field));
                }
            }
            else
            {
                columns ??= new long[1];
                columns[0] += Of(arena, batch.RootIndex);
            }
        }

        return (rows, columns ?? []);
    }

    /// <summary>The plain bytes of node <paramref name="index"/> and everything under it.</summary>
    private static long Of(CanonicalArena arena, int index)
    {
        CanonicalNode node = arena.GetNode(index);
        long rows = node.Length;
        long validity = node.Validity.Kind == ValidityKind.Bitmap ? (rows + 7) / 8 : 0;
        long values = node.Kind switch
        {
            CanonicalKind.Null => 0,
            CanonicalKind.Bool => (rows + 7) / 8,
            CanonicalKind.Primitive => rows * node.PType.ByteWidth(),
            CanonicalKind.Decimal => rows * DecimalStorage.ByteWidth(node.Storage),
            CanonicalKind.VarBinView => TextBytes(node) + (4 * (rows + 1)),
            CanonicalKind.ListView => (4 * (rows + 1)) + Elements(arena, node),
            CanonicalKind.FixedSizeList => Of(arena, node.ElementsIndex),
            CanonicalKind.Struct => Fields(arena, node),
            CanonicalKind.Extension => Of(arena, node.StorageIndex),
            CanonicalKind.Constant => Constant(node),
            _ => arena.ByteSize(index),
        };

        return validity + values;
    }

    /// <summary>The bytes of every value of a text or binary node: the length each view starts with.</summary>
    private static long TextBytes(CanonicalNode node)
    {
        ReadOnlySpan<byte> views = node.Views.Span;
        long bytes = 0;
        for (int row = 0; row < node.Length; row++)
        {
            bytes += BinaryPrimitives.ReadInt32LittleEndian(views.Slice(row * 16, 4));
        }

        return bytes;
    }

    /// <summary>
    /// The elements a list node's rows name: its child's plain bytes in the proportion its sizes add
    /// up to, since a batch cut from a larger chunk can share that chunk's whole child.
    /// </summary>
    private static long Elements(CanonicalArena arena, CanonicalNode node)
    {
        int child = node.ElementsIndex;
        long length = arena.GetNode(child).Length;
        if (length == 0)
        {
            return 0;
        }

        ReadOnlySpan<byte> sizes = node.Sizes.Span;
        int width = node.SizePType.ByteWidth();
        long named = 0;
        for (int row = 0; row < node.Length; row++)
        {
            ReadOnlySpan<byte> size = sizes.Slice(row * width, width);
            named += width switch
            {
                1 => size[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(size),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(size),
                _ => (long)BinaryPrimitives.ReadUInt64LittleEndian(size),
            };
        }

        return Of(arena, child) * Math.Min(named, length) / length;
    }

    private static long Fields(CanonicalArena arena, CanonicalNode node)
    {
        long bytes = 0;
        for (int field = 0; field < node.FieldCount; field++)
        {
            bytes += Of(arena, node.GetFieldIndex(field));
        }

        return bytes;
    }

    /// <summary>A constant column, as it would weigh written out.</summary>
    private static long Constant(CanonicalNode node)
    {
        long rows = node.Length;
        DType dtype = node.DType;
        return dtype.Kind switch
        {
            DTypeKind.Null => 0,
            DTypeKind.Bool => (rows + 7) / 8,
            DTypeKind.Primitive => rows * dtype.PType.ByteWidth(),
            DTypeKind.Utf8 or DTypeKind.Binary => (rows * (node.ConstantElement.Length + 4)) + 4,
            _ => rows * node.ConstantElement.Length,
        };
    }
}
