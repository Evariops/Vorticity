using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.RowEncoding;

/// <summary>
/// The row encoding as the key encoder of a composite index, for <c>IndexPolicy.ForKey</c>. The
/// encoding of the leading columns alone is a byte prefix of the whole tuple's, so a prefix query is
/// a seek and a walk. These bytes outlive the process, so <see cref="Format"/> names the layout they
/// follow and lets a reader see that its seek keys do not compare with an index written under
/// another one.
/// </summary>
public sealed class RowKeyEncoder : IKeyEncoder
{
    private readonly RowSortField[] _fields;

    /// <summary>
    /// An encoder taking one sort field per key column, in key order; a single field applies to
    /// every column, so one encoder then serves keys of any width.
    /// </summary>
    /// <param name="fields">The sort fields; none means ascending with nulls first for every column.</param>
    public RowKeyEncoder(params ReadOnlySpan<RowSortField> fields)
    {
        _fields = fields.IsEmpty ? [RowSortField.Ascending] : fields.ToArray();
        StringBuilder format = new StringBuilder("vortex-row ").Append(RowEncoder.VortexVersion).Append(' ');
        for (int i = 0; i < _fields.Length; i++)
        {
            format.Append(i == 0 ? string.Empty : ",")
                .Append(_fields[i].Descending ? "desc" : "asc")
                .Append(_fields[i].NullsFirst ? "-nf" : "-nl");
        }

        Format = format.ToString();
    }

    /// <inheritdoc/>
    public string Format { get; }

    /// <summary>The sort fields, in key order.</summary>
    public ReadOnlySpan<RowSortField> Fields => _fields;

    /// <inheritdoc/>
    public IEncodedKeys Encode(BatchView columns) => RowEncoder.Encode(columns, _fields);
}

/// <summary>The single-tuple overloads.</summary>
public static partial class RowEncoder
{
    /// <summary>
    /// The row encoding of one tuple, given as a record whose members are the key's columns in key
    /// order: the seek key of a composite index, or its prefix when the record names fewer columns.
    /// </summary>
    /// <typeparam name="TKey">A record of the key columns; its schema gives the widths and nullability the bytes depend on.</typeparam>
    /// <param name="key">The tuple.</param>
    /// <param name="fields">One sort field per member, one for every member, or none for ascending with nulls first.</param>
    /// <returns>The key's bytes.</returns>
    /// <exception cref="VortexUnsupportedException">A member's type has no order the format defines.</exception>
    public static byte[] EncodeKey<TKey>(in TKey key, params ReadOnlySpan<RowSortField> fields)
        where TKey : IVortexRecord<TKey>
    {
        VortexSessionOptions options = VortexSession.Default.Options;
        DType dtype = VortexTypes.ToDType(TKey.Schema, new DTypeArena());
        StructStore store = (StructStore)ColumnStores.Create(dtype, options.EnginePool, options.Extensions);
        CanonicalArena arena = new CanonicalArena(8, options.EnginePool);
        try
        {
            ColumnsBuilder<TKey> builder = new ColumnsBuilder<TKey>(store, WriteBinding.Map(typeof(TKey), TKey.Schema, store.Type.FieldArray), null);
            TKey.WriteRows(builder, new ReadOnlySpan<TKey>(in key));
            CanonicalNode root = arena.GetNode(store.Build(arena, 1));
            int count = root.FieldCount;
            Span<int> columns = count <= 32 ? stackalloc int[count] : new int[count];
            for (int i = 0; i < count; i++)
            {
                columns[i] = Storage(arena, root.GetFieldIndex(i));
            }

            using RowKeys keys = Encode(arena, columns, PerColumn(fields, count));
            return keys.Row(0).ToArray();
        }
        finally
        {
            arena.Reset();
            store.Release();
        }
    }

    /// <summary>
    /// The row encoding of one tuple: the seek key of a composite cursor, or its prefix when fewer
    /// values than key columns are given.
    /// </summary>
    /// <remarks>
    /// The dtypes are inferred from the literals -- signed as <c>i64</c>, unsigned as <c>u64</c>, a
    /// float as <c>f64</c>, bytes as <c>utf8</c>, all non-nullable -- and the bytes depend on them,
    /// so a key column of another width or a nullable one needs the overload taking dtypes.
    /// </remarks>
    internal static byte[] EncodeKey(ReadOnlySpan<FilterLiteral> values, ReadOnlySpan<RowSortField> fields)
    {
        DTypeArena types = new DTypeArena();
        DType[] dtypes = new DType[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            dtypes[i] = values[i].Kind switch
            {
                FilterLiteralKind.Bool => types.Bool(Nullability.NonNullable),
                FilterLiteralKind.Signed => types.Primitive(PType.I64, Nullability.NonNullable),
                FilterLiteralKind.Unsigned => types.Primitive(PType.U64, Nullability.NonNullable),
                FilterLiteralKind.Float => types.Primitive(PType.F64, Nullability.NonNullable),
                FilterLiteralKind.Bytes => types.Utf8(Nullability.NonNullable),
                _ => throw new ArgumentException(
                    "A null value has no dtype to infer; use the overload that takes the column dtypes.",
                    nameof(values)),
            };
        }

        return EncodeKey(values, dtypes, fields);
    }

    /// <summary>
    /// The row encoding of one tuple at the key columns' own dtypes, whose width and nullability
    /// shape the bytes.
    /// </summary>
    internal static byte[] EncodeKey(
        ReadOnlySpan<FilterLiteral> values, ReadOnlySpan<DType> dtypes, ReadOnlySpan<RowSortField> fields)
    {
        byte[] key = new byte[KeyLength(values, dtypes, fields)];
        WriteKey(values, dtypes, fields, key);
        return key;
    }

    /// <summary>The length of the row encoding of one tuple, the bytes <see cref="WriteKey"/> writes.</summary>
    internal static int KeyLength(
        ReadOnlySpan<FilterLiteral> values, ReadOnlySpan<DType> dtypes, ReadOnlySpan<RowSortField> fields)
    {
        CheckTuple(values, dtypes, fields);
        int length = 0;
        for (int i = 0; i < values.Length; i++)
        {
            length += ValueLength(values[i], dtypes[i]);
        }

        return length;
    }

    /// <summary>
    /// Writes the row encoding of one tuple into <paramref name="destination"/>, which has room for
    /// <see cref="KeyLength"/> bytes, allocating nothing: each value is written as the column encoder
    /// writes a row of one value of its dtype.
    /// </summary>
    /// <returns>The bytes written.</returns>
    internal static int WriteKey(
        ReadOnlySpan<FilterLiteral> values, ReadOnlySpan<DType> dtypes, ReadOnlySpan<RowSortField> fields,
        Span<byte> destination)
    {
        CheckTuple(values, dtypes, fields);
        int written = 0;
        for (int i = 0; i < values.Length; i++)
        {
            written += WriteValue(values[i], dtypes[i], fields[i], destination[written..]);
        }

        return written;
    }

    private static void CheckTuple(
        ReadOnlySpan<FilterLiteral> values, ReadOnlySpan<DType> dtypes, ReadOnlySpan<RowSortField> fields)
    {
        if (values.Length != dtypes.Length || values.Length != fields.Length || values.Length == 0)
        {
            throw new ArgumentException(
                $"{values.Length} values, {dtypes.Length} dtypes and {fields.Length} sort fields: one of each per key column.",
                nameof(values));
        }
    }

    /// <summary>The bytes one value of <paramref name="dtype"/> takes in a row.</summary>
    internal static int ValueLength(FilterLiteral value, DType dtype)
    {
        bool isNull = IsNull(value, dtype);
        switch (dtype.Kind)
        {
            case DTypeKind.Bool:
                return 2;

            case DTypeKind.Primitive:
                return dtype.PType.ByteWidth() + 1;

            case DTypeKind.Utf8 or DTypeKind.Binary:
                return BytesLength(isNull ? 0 : Expect(value, FilterLiteralKind.Bytes).BytesValue.Length);

            default:
                throw Unkeyed(dtype);
        }
    }

    /// <summary>The bytes a utf8 or binary value of <paramref name="length"/> bytes takes in a row; a null takes as many as an empty one.</summary>
    internal static int BytesLength(int length) =>
        length == 0
            ? RowWidths.VarEmptySize
            : 1 + (((length + RowWidths.VarBlockData - 1) / RowWidths.VarBlockData) * RowWidths.VarBlockTotal);

    /// <summary>
    /// Writes a non-null utf8 or binary value as a row of its column holds it, into
    /// <paramref name="destination"/>, which has room for <see cref="BytesLength"/> bytes: for a key
    /// whose bytes are lent rather than held by a literal.
    /// </summary>
    /// <returns>The bytes written.</returns>
    internal static int WriteBytes(ReadOnlySpan<byte> value, RowSortField field, Span<byte> destination)
    {
        if (value.IsEmpty)
        {
            destination[0] = RowSentinels.VarEmpty(field);
            return RowWidths.VarEmptySize;
        }

        destination[0] = RowSentinels.VarNonEmpty(field);
        return 1 + RowBytes.WriteVarBody(value, destination[1..], field.Descending);
    }

    /// <summary>
    /// Writes one value as <see cref="RowEncodeKernel"/> writes a row of it: a sentinel, then the
    /// ordered value big-endian for a fixed width, or the value in blocks for a variable one.
    /// </summary>
    /// <returns>The bytes written.</returns>
    internal static int WriteValue(FilterLiteral value, DType dtype, RowSortField field, Span<byte> destination)
    {
        bool isNull = IsNull(value, dtype);
        switch (dtype.Kind)
        {
            case DTypeKind.Bool:
                if (isNull)
                {
                    destination[0] = RowSentinels.FixedNull(field);
                    destination[1] = 0;
                }
                else
                {
                    destination[0] = RowSentinels.FixedNonNull;
                    byte order = Expect(value, FilterLiteralKind.Bool).BoolValue ? (byte)0x02 : (byte)0x01;
                    destination[1] = field.Descending ? (byte)(order ^ 0xFF) : order;
                }

                return 2;

            case DTypeKind.Primitive:
            {
                PType ptype = dtype.PType;
                int width = ptype.ByteWidth();
                Span<byte> slot = destination[..(width + 1)];
                if (isNull)
                {
                    // A null's value bytes are zeros even descending: the fill is not a value.
                    slot[0] = RowSentinels.FixedNull(field);
                    slot[1..].Clear();
                    return width + 1;
                }

                ulong mask = width == sizeof(ulong) ? ulong.MaxValue : (1UL << (8 * width)) - 1;
                ulong ordered = Ordered(value, ptype, width);
                if (field.Descending)
                {
                    ordered = ~ordered & mask;
                }

                slot[0] = RowSentinels.FixedNonNull;
                for (int i = 0; i < width; i++)
                {
                    slot[1 + i] = (byte)(ordered >> (8 * (width - 1 - i)));
                }

                return width + 1;
            }

            case DTypeKind.Utf8 or DTypeKind.Binary:
            {
                if (isNull)
                {
                    destination[0] = RowSentinels.VarNull(field);
                    return RowWidths.VarNullSize;
                }

                return WriteBytes(Expect(value, FilterLiteralKind.Bytes).BytesValue, field, destination);
            }

            default:
                throw Unkeyed(dtype);
        }
    }

    /// <summary>
    /// The value's bits at the column's width, mapped as <see cref="IRowOrdering{T}"/> maps them so
    /// that their unsigned order is the values' order.
    /// </summary>
    private static ulong Ordered(FilterLiteral value, PType ptype, int width)
    {
        ulong sign = 1UL << ((8 * width) - 1);
        ulong mask = width == sizeof(ulong) ? ulong.MaxValue : (1UL << (8 * width)) - 1;
        if (ptype.IsFloat())
        {
            double d = Expect(value, FilterLiteralKind.Float).FloatValue;
            ulong raw = ptype switch
            {
                PType.F16 => BitConverter.HalfToUInt16Bits((Half)d),
                PType.F32 => BitConverter.SingleToUInt32Bits((float)d),
                _ => BitConverter.DoubleToUInt64Bits(d),
            };

            // A non-negative gets its sign bit set, a negative every bit flipped.
            return raw ^ ((raw & sign) == 0 ? sign : mask);
        }

        ulong bits;
        if (value.Kind == FilterLiteralKind.Signed)
        {
            long signed = value.SignedValue;
            if (!Fits(signed, ptype))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"{signed} does not fit a {ptype} column."), nameof(value));
            }

            bits = unchecked((ulong)signed) & mask;
        }
        else
        {
            ulong unsigned = Expect(value, FilterLiteralKind.Unsigned).UnsignedValue;
            if (!Fits(unsigned, ptype))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"{unsigned} does not fit a {ptype} column."), nameof(value));
            }

            bits = unsigned;
        }

        // Two's complement puts the negatives above the positives; flipping the sign bit moves them below.
        return ptype.IsSignedInteger() ? bits ^ sign : bits;
    }

    private static bool IsNull(FilterLiteral value, DType dtype)
    {
        bool isNull = value.Kind == FilterLiteralKind.Null;
        if (isNull && !dtype.IsNullable)
        {
            throw new ArgumentException($"A null cannot be a value of the non-nullable {dtype.Kind} column.", nameof(value));
        }

        return isNull;
    }

    private static VortexUnsupportedException Unkeyed(DType dtype) =>
        new VortexUnsupportedException(
            dtype.Kind.ToString(), ComponentKind.DType, "A seek key is built for boolean, primitive, utf8 and binary columns.");

    private static bool Fits(long value, PType ptype) => ptype switch
    {
        PType.I8 => value is >= sbyte.MinValue and <= sbyte.MaxValue,
        PType.I16 => value is >= short.MinValue and <= short.MaxValue,
        PType.I32 => value is >= int.MinValue and <= int.MaxValue,
        PType.I64 => true,
        PType.U8 => value is >= 0 and <= byte.MaxValue,
        PType.U16 => value is >= 0 and <= ushort.MaxValue,
        PType.U32 => value is >= 0 and <= uint.MaxValue,
        _ => value >= 0,
    };

    private static bool Fits(ulong value, PType ptype) => ptype switch
    {
        PType.I8 => value <= (ulong)sbyte.MaxValue,
        PType.I16 => value <= (ulong)short.MaxValue,
        PType.I32 => value <= int.MaxValue,
        PType.I64 => value <= long.MaxValue,
        PType.U8 => value <= byte.MaxValue,
        PType.U16 => value <= ushort.MaxValue,
        PType.U32 => value <= uint.MaxValue,
        _ => true,
    };

    private static FilterLiteral Expect(FilterLiteral value, FilterLiteralKind kind) =>
        value.Kind == kind
            ? value
            : throw new ArgumentException($"A {value.Kind} value does not fit a column of {kind} values.", nameof(value));
}
