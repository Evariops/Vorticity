using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.RowEncoding;

/// <summary>
/// The row encoding as the key encoder of a composite index. The encoding of the leading columns
/// alone is a byte prefix of the whole tuple's, so a prefix query is a seek and a walk. These bytes
/// outlive the process, so <see cref="Format"/> names the layout they follow and lets a reader see
/// that its seek keys do not compare with an index written under another one.
/// </summary>
internal sealed class RowKeyEncoder : IKeyEncoder
{
    private readonly RowSortField[] _fields;

    /// <summary>
    /// An encoder taking one sort field per key column, in key order; a single field applies to
    /// every column, so one encoder then serves keys of any width.
    /// </summary>
    public RowKeyEncoder(params RowSortField[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Length == 0)
        {
            throw new ArgumentException("A key has at least one column.", nameof(fields));
        }

        _fields = [.. fields];
        StringBuilder format = new StringBuilder("vortex-row ").Append(RowEncoder.VortexVersion).Append(' ');
        for (int i = 0; i < fields.Length; i++)
        {
            format.Append(i == 0 ? string.Empty : ",")
                .Append(fields[i].Descending ? "desc" : "asc")
                .Append(fields[i].NullsFirst ? "-nf" : "-nl");
        }

        Format = format.ToString();
    }

    /// <inheritdoc/>
    public string Format { get; }

    /// <summary>The sort fields, in key order.</summary>
    public ReadOnlySpan<RowSortField> Fields => _fields;

    /// <inheritdoc/>
    public IEncodedKeys Encode(CanonicalArena arena, ReadOnlySpan<int> columns)
    {
        if (_fields.Length != 1 || columns.Length == 1)
        {
            return RowEncoder.Encode(arena, columns, _fields);
        }

        RowSortField[] fields = new RowSortField[columns.Length];
        fields.AsSpan().Fill(_fields[0]);
        return RowEncoder.Encode(arena, columns, fields);
    }
}

/// <summary>The single-tuple overloads.</summary>
internal static partial class RowEncoder
{
    /// <summary>
    /// The row encoding of one tuple: the seek key of a composite cursor, or its prefix when fewer
    /// values than key columns are given.
    /// </summary>
    /// <remarks>
    /// The dtypes are inferred from the literals -- signed as <c>i64</c>, unsigned as <c>u64</c>, a
    /// float as <c>f64</c>, bytes as <c>utf8</c>, all non-nullable -- and the bytes depend on them,
    /// so a key column of another width or a nullable one needs the overload taking dtypes.
    /// </remarks>
    public static byte[] EncodeKey(ReadOnlySpan<FilterLiteral> values, ReadOnlySpan<RowSortField> fields)
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
    public static byte[] EncodeKey(
        ReadOnlySpan<FilterLiteral> values, ReadOnlySpan<DType> dtypes, ReadOnlySpan<RowSortField> fields)
    {
        if (values.Length != dtypes.Length || values.Length != fields.Length || values.Length == 0)
        {
            throw new ArgumentException(
                $"{values.Length} values, {dtypes.Length} dtypes and {fields.Length} sort fields: one of each per key column.",
                nameof(values));
        }

        CanonicalArena arena = new CanonicalArena();
        try
        {
            int[] columns = new int[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                columns[i] = OneValue(arena, values[i], dtypes[i]);
            }

            using RowKeys keys = Encode(arena, columns, fields);
            return keys.Row(0).ToArray();
        }
        finally
        {
            arena.Reset();
        }
    }

    private static int OneValue(CanonicalArena arena, FilterLiteral value, DType dtype)
    {
        bool isNull = value.Kind == FilterLiteralKind.Null;
        if (isNull && !dtype.IsNullable)
        {
            throw new ArgumentException($"A null cannot be a value of the non-nullable {dtype.Kind} column.", nameof(value));
        }

        Validity validity = isNull ? Validity.AllInvalid : Validity.FromNullability(dtype.Nullability);
        switch (dtype.Kind)
        {
            case DTypeKind.Bool:
            {
                VortexBuffer bits = arena.Allocate(1, 1, out Span<byte> bit);
                bit[0] = (byte)(!isNull && Expect(value, FilterLiteralKind.Bool).BoolValue ? 1 : 0);
                return arena.AddBool(dtype, 1, validity, bits, 0);
            }

            case DTypeKind.Primitive:
            {
                PType ptype = dtype.PType;
                int width = ptype.ByteWidth();
                VortexBuffer buffer = arena.Allocate(width, width, out Span<byte> bytes);
                bytes.Clear();
                if (!isNull)
                {
                    WritePrimitive(value, ptype, bytes);
                }

                return arena.AddPrimitive(dtype, 1, validity, ptype, buffer);
            }

            case DTypeKind.Utf8 or DTypeKind.Binary:
            {
                ReadOnlySpan<byte> text = isNull ? default : Expect(value, FilterLiteralKind.Bytes).BytesValue;
                VortexBuffer views = arena.Allocate(16, 16, out Span<byte> view);
                view.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)text.Length);
                if (text.Length <= 12)
                {
                    text.CopyTo(view[4..]);
                    return arena.AddVarBinView(dtype, 1, validity, views, default);
                }

                VortexBuffer data = arena.Allocate(text.Length, 1, out Span<byte> heap);
                text.CopyTo(heap);
                text[..4].CopyTo(view[4..]);
                return arena.AddVarBinView(dtype, 1, validity, views, [data]);
            }

            default:
                throw new VortexUnsupportedException(
                    dtype.Kind.ToString(), ComponentKind.DType, "A seek key is built for boolean, primitive, utf8 and binary columns.");
        }
    }

    private static void WritePrimitive(FilterLiteral value, PType ptype, Span<byte> bytes)
    {
        if (ptype.IsFloat())
        {
            double d = Expect(value, FilterLiteralKind.Float).FloatValue;
            switch (ptype)
            {
                case PType.F16:
                    BinaryPrimitives.WriteHalfLittleEndian(bytes, (Half)d);
                    break;
                case PType.F32:
                    BinaryPrimitives.WriteSingleLittleEndian(bytes, (float)d);
                    break;
                default:
                    BinaryPrimitives.WriteDoubleLittleEndian(bytes, d);
                    break;
            }

            return;
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

            bits = unchecked((ulong)signed);
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

        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(bits >> (8 * i));
        }
    }

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
