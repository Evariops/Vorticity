// The composite key of a locating index, and the seek key a cursor over it needs - docs/10-indexes.md
// §6.5 and docs/12-index-reads.md §4.6.
//
// THE CORE WRITES WHAT IT IS HANDED. A composite index is keyed by the row encoding of the tuple, and
// the core does not row-encode (docs/09-contracts.md §3); this package fills its `IKeyEncoder` slot,
// and gives a reader the one-tuple encoding that produces the same bytes, which is all a byte-keyed
// cursor needs to seek. The encoding of the leading columns alone is a byte prefix of the encoding of
// the whole tuple, so a prefix query is a seek and a walk while the key starts with the prefix.
//
// THE FORMAT IS NAMED, because these bytes now outlive the process: the index records
// `RowKeyEncoder.Format` and a cursor reports it, so a reader built on another release of the row
// format can tell that its seek keys do not compare with the index's.
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

/// <summary>The row encoding as the key encoder of a composite index.</summary>
public sealed class RowKeyEncoder : IKeyEncoder
{
    private readonly RowSortField[] _fields;

    /// <summary>An encoder for keys of <paramref name="fields"/>.Length columns, or of any width with one field.</summary>
    /// <param name="fields">One sort field per key column, in key order; a single one applies to every column.</param>
    /// <exception cref="ArgumentException">No field was given.</exception>
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
    /// <remarks>One sort field given at construction applies to every column, so one encoder serves keys of any width.</remarks>
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
public static partial class RowEncoder
{
    /// <summary>
    /// The row encoding of one tuple: the seek key of a composite cursor, or its prefix when fewer
    /// values than key columns are given.
    /// </summary>
    /// <param name="values">The leading key values, in key order; <see cref="FilterLiteral.Null"/> for a null.</param>
    /// <param name="fields">One sort field per value.</param>
    /// <returns>The key's bytes.</returns>
    /// <remarks>
    /// The dtypes are inferred from the literals -- a signed value as <c>i64</c>, an unsigned one as
    /// <c>u64</c>, a float as <c>f64</c>, bytes as <c>utf8</c>, all non-nullable -- and the bytes
    /// depend on them: when a key column is of another width or nullable, use the overload that
    /// takes the column dtypes.
    /// </remarks>
    /// <exception cref="ArgumentException">The counts differ, or a null is given without a nullable dtype.</exception>
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

    /// <summary>The row encoding of one tuple, at the key columns' own dtypes.</summary>
    /// <param name="values">The leading key values, in key order.</param>
    /// <param name="dtypes">Each value's column dtype: its width and nullability shape the bytes.</param>
    /// <param name="fields">One sort field per value.</param>
    /// <returns>The key's bytes.</returns>
    /// <exception cref="ArgumentException">
    /// The counts differ, a value does not fit its dtype, or a null is given for a non-nullable dtype.
    /// </exception>
    /// <exception cref="VortexUnsupportedException">A dtype has no row encoding.</exception>
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

    /// <summary>A one-row column holding <paramref name="value"/> at <paramref name="dtype"/>.</summary>
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
                    dtype.Kind.ToString(), "dtype", "A seek key is built for boolean, primitive, utf8 and binary columns.");
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
