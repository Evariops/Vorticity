using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Types;

namespace Vorticity.Columns;

/// <summary>A fixed-width primitive column, viewed as its exact .NET element type.</summary>
/// <typeparam name="T">
/// <see cref="byte"/>, <see cref="ushort"/>, <see cref="uint"/>, <see cref="ulong"/>,
/// <see cref="sbyte"/>, <see cref="short"/>, <see cref="int"/>, <see cref="long"/>,
/// <see cref="Half"/>, <see cref="float"/> or <see cref="double"/>, matching the column's
/// <see cref="PType"/>.
/// </typeparam>
/// <remarks>
/// The match must be exact: <see cref="byte"/> and <see cref="sbyte"/> are not interchangeable, nor
/// are <see cref="int"/> and <see cref="uint"/>, and a half-precision column is read as
/// <see cref="Half"/> rather than widened. Nullability is not carried by the type: a nullable
/// column has the same element type, its null rows hold an unspecified value, and the caller tells
/// them apart with <see cref="IsValid"/>.
/// <see cref="Values"/> is borrowed from the owning <see cref="RecordBatch"/> and is invalid once
/// that batch is disposed.
/// </remarks>
public readonly ref struct PrimitiveColumn<T>
    where T : unmanaged
{
    private readonly RecordBatch _batch;
    private readonly int _node;

    internal PrimitiveColumn(RecordBatch batch, int node)
    {
        _batch = batch;
        _node = node;
    }

    /// <summary>Rows in this column.</summary>
    public int Length => _batch.Node(_node).Length;

    /// <summary>The column's physical type.</summary>
    public PType PType => _batch.Node(_node).PType;

    /// <summary>Whether row <paramref name="index"/> is not null.</summary>
    /// <param name="index">0-based row index, below <see cref="Length"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public bool IsValid(int index) => ColumnCore.IsValid(_batch, _node, index);

    /// <summary>
    /// The values, zero-copy: exactly <see cref="Length"/> elements. Null rows occupy their slot
    /// and hold an unspecified value. Invalid after the owning batch is disposed.
    /// </summary>
    /// <exception cref="VortexFormatException">
    /// The decoded buffer is shorter than <see cref="Length"/> elements.
    /// </exception>
    public ReadOnlySpan<T> Values
    {
        get
        {
            CanonicalNode node = _batch.Node(_node);

            // MemoryMarshal.Cast rather than VortexBuffer.Cast<T>: the latter demands the base
            // address be a multiple of sizeof(T), which is stricter than the format guarantees, so
            // it would refuse a legal file over an alignment scalar span access never needs.
            ReadOnlySpan<T> all = MemoryMarshal.Cast<byte, T>(node.Values.Span);
            if (all.Length < node.Length)
            {
                ColumnsThrow.Format(
                    $"A primitive column of {node.Length} rows needs " +
                    $"{(long)node.Length * Unsafe.SizeOf<T>()} bytes; the buffer holds " +
                    $"{node.Values.Length}.");
            }

            return all[..node.Length];
        }
    }

    /// <summary>The value of row <paramref name="index"/>.</summary>
    /// <param name="index">0-based row index, below <see cref="Length"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public T this[int index]
    {
        get
        {
            ReadOnlySpan<T> values = Values;
            ColumnCore.CheckRow(index, values.Length);
            return values[index];
        }
    }

    /// <summary>
    /// Whether <typeparamref name="T"/> is the exact .NET spelling of <paramref name="ptype"/>.
    /// </summary>
    internal static bool Matches(PType ptype) => ptype switch
    {
        PType.U8 => typeof(T) == typeof(byte),
        PType.U16 => typeof(T) == typeof(ushort),
        PType.U32 => typeof(T) == typeof(uint),
        PType.U64 => typeof(T) == typeof(ulong),
        PType.I8 => typeof(T) == typeof(sbyte),
        PType.I16 => typeof(T) == typeof(short),
        PType.I32 => typeof(T) == typeof(int),
        PType.I64 => typeof(T) == typeof(long),
        PType.F16 => typeof(T) == typeof(Half),
        PType.F32 => typeof(T) == typeof(float),
        PType.F64 => typeof(T) == typeof(double),
        _ => false,
    };
}
