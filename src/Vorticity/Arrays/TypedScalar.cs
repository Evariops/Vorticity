// Phase 1 contract §0a C3 and §8.5: a protobuf ScalarValue cannot be interpreted without its
// DType. spec/proto/scalar.proto has twelve untyped kinds and the reference narrows each one using
// the DType it is decoded against (vortex-array-0.86.1/src/scalar/proto.rs,
// `ScalarValue::from_proto`). Phase 0's ScalarProtobuf.ReadValue stays untyped; this is the typed
// interpreter every consumer of a wire ScalarValue goes through: vortex.constant's buffer 0,
// fastlanes.for's metadata, vortex.sparse's fill value, vortex.sequence's base and multiplier,
// every ArrayStats min/max/sum and every zone-map cell.
//
// Two rules do the most damage when forgotten:
//   * a Decimal scalar is bytes_value carrying the LITTLE-ENDIAN two's-complement unscaled value,
//     whose LENGTH selects the storage width - there is no dedicated decimal kind;
//   * an Extension dtype's scalar is interpreted against its STORAGE dtype, not the extension.
using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Vorticity.Types.Serialization;

namespace Vorticity.Arrays;

/// <summary>
/// A wire <c>ScalarValue</c> interpreted against a <see cref="DType"/>. No allocation beyond the
/// <see cref="ScalarStore"/> nodes <see cref="TypedScalarReader.Read"/> appends.
/// </summary>
/// <remarks>
/// <para>
/// The whole value is validated against the dtype at <see cref="TypedScalarReader.Read"/> time, so
/// every accessor below is a checked read of an already-legal value and
/// <see cref="GetElement"/> is O(1) - a lazily rescanned protobuf would be quadratic in the
/// element count, which a file controls.
/// </para>
/// <para>
/// Byte spans point into the <see cref="ScalarStore"/> and are valid until that store is next
/// written to or cleared.
/// </para>
/// </remarks>
public readonly ref struct TypedScalar
{
    private readonly ScalarValue _value;
    private readonly DType _dtype;

    internal TypedScalar(ScalarValue value, DType dtype)
    {
        _value = value;
        _dtype = dtype;
    }

    /// <summary><see langword="true"/> when the wire carried an explicit <c>null_value</c>.</summary>
    public bool IsNull => _value.Kind == ScalarValueKind.Null;

    /// <summary>
    /// The dtype this value was interpreted against. For an extension dtype this is its
    /// <see cref="DType.StorageType"/>, because that is what the wire kinds are matched against.
    /// </summary>
    public DType DType => _dtype;

    /// <summary>The wire <c>oneof</c> case that was present.</summary>
    public ScalarValueKind WireKind => _value.Kind;

    /// <summary>
    /// The nested typed scalar of a variant value: RFC 0015's <c>(dtype, value)</c> pair.
    /// </summary>
    /// <exception cref="VortexFormatException">The value is not a variant.</exception>
    public Scalar AsVariantScalar
    {
        get
        {
            RequireKind(ScalarValueKind.Variant);
            return _value.AsVariant;
        }
    }

    /// <summary>The untyped handle, for a caller that wants to keep the value in a store.</summary>
    public ScalarValue Value => _value;

    /// <summary>The boolean payload.</summary>
    /// <exception cref="VortexFormatException">The value is null or is not a boolean.</exception>
    public bool AsBool
    {
        get
        {
            RequireKind(ScalarValueKind.Bool);
            return _value.AsBool;
        }
    }

    /// <summary>
    /// The signed integer payload. Both <c>int64_value</c> and <c>uint64_value</c> are accepted -
    /// upstream takes either against an integer ptype for backward compatibility - and the value
    /// was range-checked against the dtype's ptype at read time.
    /// </summary>
    /// <exception cref="VortexFormatException">The value is null or is not an integer.</exception>
    public long AsInt64
    {
        get
        {
            switch (_value.Kind)
            {
                case ScalarValueKind.Int64:
                    return _value.AsInt64;
                case ScalarValueKind.UInt64:
                    ulong raw = _value.AsUInt64;
                    if (raw > long.MaxValue)
                    {
                        ArraysThrow.Format($"Scalar {raw} does not fit a signed 64-bit integer.");
                    }

                    return (long)raw;
                default:
                    return ThrowKind<long>(_value.Kind, "an integer");
            }
        }
    }

    /// <summary>
    /// The unsigned integer payload. Both integer wire kinds are accepted; a negative
    /// <c>int64_value</c> is rejected.
    /// </summary>
    /// <exception cref="VortexFormatException">The value is null, not an integer, or negative.</exception>
    public ulong AsUInt64
    {
        get
        {
            switch (_value.Kind)
            {
                case ScalarValueKind.UInt64:
                    return _value.AsUInt64;
                case ScalarValueKind.Int64:
                    long signed = _value.AsInt64;
                    if (signed < 0)
                    {
                        ArraysThrow.Format($"Scalar {signed} does not fit an unsigned integer.");
                    }

                    return (ulong)signed;
                default:
                    return ThrowKind<ulong>(_value.Kind, "an integer");
            }
        }
    }

    /// <summary>
    /// The binary16 payload. <c>f16_value</c> carries the raw bits as a varint, and a plain
    /// <c>uint64_value</c> is accepted too because f16 used to be serialized that way.
    /// </summary>
    /// <exception cref="VortexFormatException">The value is null or is not a binary16.</exception>
    public Half AsF16
    {
        get
        {
            switch (_value.Kind)
            {
                case ScalarValueKind.F16:
                    return _value.AsF16;
                case ScalarValueKind.UInt64:
                    return BitConverter.UInt16BitsToHalf((ushort)_value.AsUInt64);
                default:
                    return ThrowKind<Half>(_value.Kind, "a binary16");
            }
        }
    }

    /// <summary>The binary32 payload.</summary>
    /// <exception cref="VortexFormatException">The value is null or is not a binary32.</exception>
    public float AsF32
    {
        get
        {
            RequireKind(ScalarValueKind.F32);
            return _value.AsF32;
        }
    }

    /// <summary>The binary64 payload.</summary>
    /// <exception cref="VortexFormatException">The value is null or is not a binary64.</exception>
    public double AsF64
    {
        get
        {
            RequireKind(ScalarValueKind.F64);
            return _value.AsF64;
        }
    }

    /// <summary>The UTF-8 bytes of a <c>string_value</c> or <c>bytes_value</c>.</summary>
    /// <exception cref="VortexFormatException">The value is null or carries no bytes.</exception>
    public ReadOnlySpan<byte> AsUtf8 => Bytes();

    /// <summary>The bytes of a <c>bytes_value</c> or <c>string_value</c>.</summary>
    /// <exception cref="VortexFormatException">The value is null or carries no bytes.</exception>
    public ReadOnlySpan<byte> AsBinary => Bytes();

    /// <summary>
    /// The decimal payload: <c>bytes_value</c> read as little-endian two's complement, sign
    /// extended to <see cref="Int256"/>, with the precision and scale coming from the dtype.
    /// </summary>
    /// <exception cref="VortexFormatException">
    /// The value is null, is not a <c>bytes_value</c>, or the dtype is not a Decimal.
    /// </exception>
    public VortexDecimal AsDecimal
    {
        get
        {
            RequireKind(ScalarValueKind.Bytes);
            if (_dtype.IsDefault || _dtype.Kind != DTypeKind.Decimal)
            {
                ArraysThrow.Format("A decimal scalar requires a Decimal dtype.");
            }

            ReadOnlySpan<byte> bytes = _value.AsBytes;
            RequireDecimalWidth(bytes.Length);

            // Sign extend the stored width out to 32 bytes; the wire order is little-endian
            // (vortex-array-0.86.1/src/scalar/proto.rs writes DecimalValue::I256(v).to_le_bytes()).
            Span<byte> le = stackalloc byte[Int256.ByteCount];
            le.Fill((bytes[^1] & 0x80) != 0 ? (byte)0xFF : (byte)0x00);
            bytes.CopyTo(le);
            return new VortexDecimal(Int256.FromLittleEndianBytes(le), _dtype.Precision, _dtype.Scale);
        }
    }

    /// <summary>
    /// The SERIALIZED width of a decimal scalar's <c>bytes_value</c>: 1, 2, 4, 8, 16 or 32 bytes.
    /// </summary>
    /// <remarks>
    /// Upstream's <c>bytes_from_proto</c> (vortex-array-0.86.1/src/scalar/proto.rs) picks the
    /// <c>DecimalValue</c> variant from exactly this length, so it - not the dtype's precision - is
    /// what decides a canonicalized constant's <c>values_type</c>.
    /// </remarks>
    /// <exception cref="VortexFormatException">
    /// The value is null, is not a <c>bytes_value</c>, or is not one of the six widths.
    /// </exception>
    internal int DecimalWidth
    {
        get
        {
            RequireKind(ScalarValueKind.Bytes);
            int length = _value.AsBytes.Length;
            RequireDecimalWidth(length);
            return length;
        }
    }

    /// <summary>Number of elements in a <c>list_value</c>: list elements, or struct fields.</summary>
    /// <exception cref="VortexFormatException">The value is null or is not a list.</exception>
    public int ElementCount
    {
        get
        {
            RequireKind(ScalarValueKind.List);
            return _value.ListCount;
        }
    }

    /// <summary>Element <paramref name="index"/>, typed against its own element or field dtype.</summary>
    /// <param name="index">0-based, below <see cref="ElementCount"/>.</param>
    /// <exception cref="VortexFormatException">
    /// The value is not a list, the index is out of range, or the dtype has no element at that
    /// position.
    /// </exception>
    public TypedScalar GetElement(int index)
    {
        RequireKind(ScalarValueKind.List);
        int count = _value.ListCount;
        if ((uint)index >= (uint)count)
        {
            ArraysThrow.ChildIndex(index, count);
        }

        return new TypedScalar(
            _value.GetListElement(index),
            TypedScalarReader.ElementDType(_dtype, index));
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with repeated copies of this value in
    /// <paramref name="ptype"/>'s native little-endian representation. A destination exactly one
    /// element wide writes exactly one value, which is what the sequence decoder wants; a longer
    /// one is what the constant decoder wants.
    /// </summary>
    /// <param name="destination">
    /// A whole number of <paramref name="ptype"/>-sized elements. A null scalar writes zeros: the
    /// values behind a null row are unspecified and the validity carries the truth.
    /// </param>
    /// <param name="ptype">The physical type to render as.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="destination"/> is not a whole number of elements.
    /// </exception>
    /// <exception cref="VortexFormatException">
    /// <paramref name="ptype"/> is undefined, or the wire kind cannot produce it.
    /// </exception>
    public void WriteTo(Span<byte> destination, PType ptype)
    {
        int width = ptype.ByteWidth();
        if (destination.Length % width != 0)
        {
            throw new ArgumentException(
                $"A destination of {destination.Length} bytes is not a whole number of " +
                $"{width}-byte {ptype.Name()} elements.",
                nameof(destination));
        }

        if (destination.IsEmpty)
        {
            return;
        }

        Span<byte> one = stackalloc byte[8];
        one = one[..width];

        if (_value.Kind != ScalarValueKind.Null)
        {
            switch (ptype)
            {
                case PType.U8:
                    one[0] = checked((byte)AsUInt64);
                    break;
                case PType.U16:
                    BinaryPrimitives.WriteUInt16LittleEndian(one, checked((ushort)AsUInt64));
                    break;
                case PType.U32:
                    BinaryPrimitives.WriteUInt32LittleEndian(one, checked((uint)AsUInt64));
                    break;
                case PType.U64:
                    BinaryPrimitives.WriteUInt64LittleEndian(one, AsUInt64);
                    break;
                case PType.I8:
                    one[0] = unchecked((byte)checked((sbyte)AsInt64));
                    break;
                case PType.I16:
                    BinaryPrimitives.WriteInt16LittleEndian(one, checked((short)AsInt64));
                    break;
                case PType.I32:
                    BinaryPrimitives.WriteInt32LittleEndian(one, checked((int)AsInt64));
                    break;
                case PType.I64:
                    BinaryPrimitives.WriteInt64LittleEndian(one, AsInt64);
                    break;
                case PType.F16:
                    BinaryPrimitives.WriteUInt16LittleEndian(one, BitConverter.HalfToUInt16Bits(AsF16));
                    break;
                case PType.F32:
                    BinaryPrimitives.WriteSingleLittleEndian(one, AsF32);
                    break;
                default:
                    BinaryPrimitives.WriteDoubleLittleEndian(one, AsF64);
                    break;
            }
        }
        else
        {
            one.Clear();
        }

        // One element written, then doubled: log2(n) calls to `memmove` rather than n copies of
        // one element. `vortex.constant` is nothing but this loop, and it read 11x the reference
        // on the 1M-row axis while doing no work at all beyond filling a buffer.
        RowKernels.Tile(destination, one);
    }

    internal static void RequireDecimalWidth(int length)
    {
        // 1/2/4/8/16/32 select i8/i16/i32/i64/i128/i256. Anything else is malformed.
        if (length is not (1 or 2 or 4 or 8 or 16 or 32))
        {
            ArraysThrow.Format(
                $"A decimal scalar's bytes_value is {length} bytes; 1, 2, 4, 8, 16 and 32 are the " +
                "whole domain.");
        }
    }

    private ReadOnlySpan<byte> Bytes()
    {
        ScalarValueKind kind = _value.Kind;
        if (kind is not (ScalarValueKind.String or ScalarValueKind.Bytes))
        {
            // ThrowKind<T> cannot be instantiated with a ref struct, so the throw is a statement.
            ThrowKind<int>(kind, "a string or bytes value");
        }

        return _value.AsBytes;
    }

    private void RequireKind(ScalarValueKind expected)
    {
        if (_value.Kind != expected)
        {
            ThrowKind<int>(_value.Kind, expected.ToString());
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static T ThrowKind<T>(ScalarValueKind actual, string expected) =>
        throw new VortexFormatException($"Scalar value is {actual}; this member requires {expected}.");
}

/// <summary>Reads a wire <c>ScalarValue</c> against a <see cref="DType"/>.</summary>
public static class TypedScalarReader
{
    /// <summary>
    /// Reads a bare <c>vortex.scalar.ScalarValue</c> message body against
    /// <paramref name="dtype"/> and validates the whole value tree against it.
    /// </summary>
    /// <param name="message">The message body: no outer tag, no outer length prefix.</param>
    /// <param name="dtype">
    /// The dtype the value must satisfy. An Extension dtype is replaced by its storage dtype
    /// before anything is matched - forgetting that turns every temporal constant into a type
    /// error.
    /// </param>
    /// <param name="store">The store the value nodes are appended to.</param>
    /// <param name="types">The arena a nested <c>variant_value</c>'s dtype would be read into.</param>
    /// <returns>The interpreted value.</returns>
    /// <remarks>
    /// An empty message body is <see cref="ScalarValueKind.Absent"/>, which for every call site
    /// that reaches this method is malformed: the null case is an explicit <c>null_value</c>,
    /// tag 1.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="types"/> is null.</exception>
    /// <exception cref="VortexFormatException">
    /// The message is malformed, carries no kind, or carries a kind the dtype does not admit.
    /// </exception>
    /// <exception cref="VortexUnsupportedException">
    /// The value is a union or variant scalar, which Phase 1 does not model.
    /// </exception>
    public static TypedScalar Read(
        ReadOnlySpan<byte> message,
        DType dtype,
        ScalarStore store,
        DTypeArena types)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(types);

        if (dtype.IsDefault)
        {
            throw new ArgumentException("A scalar cannot be interpreted without a dtype.", nameof(dtype));
        }

        ScalarValue value = ScalarProtobuf.ReadValue(message, store, types);
        if (value.IsAbsent)
        {
            ArraysThrow.Format("Scalar value missing kind.");
        }

        DType effective = Unwrap(dtype);
        Validate(value, effective, 1);
        return new TypedScalar(value, effective);
    }

    /// <summary>
    /// Wraps an already-parsed <see cref="ScalarValue"/> and validates it against
    /// <paramref name="dtype"/>. Used where the value did not come from a message body - a
    /// <c>ScalarValue</c> the caller built, or one already in a store.
    /// </summary>
    /// <param name="value">The untyped value.</param>
    /// <param name="dtype">The dtype it must satisfy.</param>
    /// <returns>The interpreted value.</returns>
    /// <exception cref="VortexFormatException">The value is absent or the dtype does not admit it.</exception>
    /// <exception cref="VortexUnsupportedException">The value is a union or variant scalar.</exception>
    public static TypedScalar Interpret(ScalarValue value, DType dtype)
    {
        if (dtype.IsDefault)
        {
            throw new ArgumentException("A scalar cannot be interpreted without a dtype.", nameof(dtype));
        }

        if (value.IsAbsent)
        {
            ArraysThrow.Format("Scalar value missing kind.");
        }

        DType effective = Unwrap(dtype);
        Validate(value, effective, 1);
        return new TypedScalar(value, effective);
    }

    /// <summary>
    /// The dtype element <paramref name="index"/> of a composite <paramref name="dtype"/> is
    /// interpreted against, already unwrapped.
    /// </summary>
    /// <param name="dtype">A List, FixedSizeList or Struct dtype.</param>
    /// <param name="index">The element or field position.</param>
    internal static DType ElementDType(DType dtype, int index)
    {
        switch (dtype.Kind)
        {
            case DTypeKind.List:
            case DTypeKind.FixedSizeList:
                return Unwrap(dtype.ElementType);
            case DTypeKind.Struct:
                return Unwrap(dtype.GetField(index));
            default:
                return ArraysThrow.Format<DType>(
                    $"A {dtype.Kind} dtype has no element {index}.");
        }
    }

    /// <summary>
    /// Replaces an Extension dtype with its storage dtype. One line upstream
    /// (<c>let dtype = match dtype { DType::Extension(ext) =&gt; ext.storage_dtype(), _ =&gt; dtype };</c>);
    /// looped here because a storage dtype may itself be an extension, and bounded by
    /// <see cref="VortexLimits.MaxDTypeDepth"/> so a pathological chain cannot spin.
    /// </summary>
    /// <param name="dtype">The declared dtype.</param>
    internal static DType Unwrap(DType dtype)
    {
        int guard = 0;
        while (!dtype.IsDefault && dtype.Kind == DTypeKind.Extension)
        {
            VortexLimits.CheckDepth(++guard, VortexLimits.MaxDTypeDepth, "Extension dtype");
            dtype = dtype.StorageType;
        }

        return dtype;
    }

    private static void Validate(ScalarValue value, DType dtype, int depth)
    {
        VortexLimits.CheckDepth(depth, VortexLimits.MaxDTypeDepth, "Scalar");

        switch (value.Kind)
        {
            case ScalarValueKind.Absent:
                ArraysThrow.Format("Scalar value missing kind.");
                return;

            case ScalarValueKind.Null:
                // Legal against any dtype: nullability is a property of the array, not of the
                // scalar (upstream builds ScalarValue::Null for every dtype).
                return;

            case ScalarValueKind.Bool:
                RequireDTypeKind(dtype, DTypeKind.Bool, "bool_value");
                return;

            case ScalarValueKind.Int64:
                RequireIntegerPType(dtype, "int64_value", out PType signedTarget);
                if (!FitsSigned(value.AsInt64, signedTarget))
                {
                    ArraysThrow.Format(
                        $"Scalar {value.AsInt64} does not fit {signedTarget.Name()}.");
                }

                return;

            case ScalarValueKind.UInt64:
            {
                // uint64_value also carries f16 bits, because f16 used to be serialized as a u64.
                PType unsignedTarget = RequirePrimitive(dtype, "uint64_value");
                if (unsignedTarget is PType.F32 or PType.F64)
                {
                    ArraysThrow.Format(
                        $"uint64_value cannot be interpreted as {unsignedTarget.Name()}.");
                }

                if (!FitsUnsigned(value.AsUInt64, unsignedTarget))
                {
                    ArraysThrow.Format(
                        $"Scalar {value.AsUInt64} does not fit {unsignedTarget.Name()}.");
                }

                return;
            }

            case ScalarValueKind.F32:
                RequireExactPType(dtype, PType.F32, "f32_value");
                return;

            case ScalarValueKind.F64:
                RequireExactPType(dtype, PType.F64, "f64_value");
                return;

            case ScalarValueKind.F16:
                // Phase 0's reader already rejects an f16_value above 16 bits.
                RequireExactPType(dtype, PType.F16, "f16_value");
                return;

            case ScalarValueKind.String:
                RequireBinaryLike(dtype, "string_value", allowDecimal: false);
                return;

            case ScalarValueKind.Bytes:
                RequireBinaryLike(dtype, "bytes_value", allowDecimal: true);
                if (dtype.Kind == DTypeKind.Decimal)
                {
                    TypedScalar.RequireDecimalWidth(value.AsBytes.Length);
                }

                return;

            case ScalarValueKind.List:
                ValidateList(value, dtype, depth);
                return;

            case ScalarValueKind.Variant:
            {
                // RFC 0015: a variant scalar is a nested (dtype, value) pair, and the dtype it
                // pairs with on the outside is `variant`. Validating the nested half against its
                // OWN dtype is what makes `variant(i32 = 1)` a checked value rather than an opaque
                // blob -- and the nesting is charged against the depth budget like any other.
                RequireDTypeKind(dtype, DTypeKind.Variant, "variant_value");
                Scalar nested = value.AsVariant;
                if (nested.DType.IsDefault)
                {
                    ArraysThrow.Format("A variant scalar carries no dtype for its value.");
                }

                Validate(nested.Value, Unwrap(nested.DType), depth + 1);
                return;
            }

            default:
                // ScalarValueKind.Union, and any tag a future proto adds.
                ThrowUnsupportedScalar("union");
                return;
        }
    }

    private static void ValidateList(ScalarValue value, DType dtype, int depth)
    {
        int count = value.ListCount;
        switch (dtype.Kind)
        {
            case DTypeKind.List:
            case DTypeKind.FixedSizeList:
            {
                DType element = Unwrap(dtype.ElementType);
                for (int i = 0; i < count; i++)
                {
                    Validate(value.GetListElement(i), element, depth + 1);
                }

                return;
            }

            case DTypeKind.Struct:
            {
                if (count != dtype.FieldCount)
                {
                    ArraysThrow.Format(
                        $"A struct scalar carries {count} values; the dtype declares " +
                        $"{dtype.FieldCount} fields.");
                }

                for (int i = 0; i < count; i++)
                {
                    Validate(value.GetListElement(i), Unwrap(dtype.GetField(i)), depth + 1);
                }

                return;
            }

            case DTypeKind.Map:
                // Contract §8.4 puts Map out of Phase 1 scope: there is no canonical form for it,
                // so a map scalar has nowhere to go.
                ThrowUnsupportedScalar("vortex.map");
                return;

            default:
                ArraysThrow.Format($"list_value cannot be interpreted as {dtype.Kind}.");
                return;
        }
    }

    private static void RequireDTypeKind(DType dtype, DTypeKind expected, string wireKind)
    {
        if (dtype.Kind != expected)
        {
            ArraysThrow.Format($"{wireKind} requires a {expected} dtype; this one is {dtype.Kind}.");
        }
    }

    private static PType RequirePrimitive(DType dtype, string wireKind)
    {
        if (dtype.Kind != DTypeKind.Primitive)
        {
            ArraysThrow.Format($"{wireKind} requires a Primitive dtype; this one is {dtype.Kind}.");
        }

        return dtype.PType;
    }

    private static void RequireIntegerPType(DType dtype, string wireKind, out PType ptype)
    {
        ptype = RequirePrimitive(dtype, wireKind);
        if (!ptype.IsInteger())
        {
            ArraysThrow.Format($"{wireKind} cannot be interpreted as {ptype.Name()}.");
        }
    }

    private static void RequireExactPType(DType dtype, PType expected, string wireKind)
    {
        PType actual = RequirePrimitive(dtype, wireKind);
        if (actual != expected)
        {
            ArraysThrow.Format(
                $"{wireKind} requires {expected.Name()}; this dtype is {actual.Name()}.");
        }
    }

    private static void RequireBinaryLike(DType dtype, string wireKind, bool allowDecimal)
    {
        switch (dtype.Kind)
        {
            case DTypeKind.Utf8:
            case DTypeKind.Binary:
                return;
            case DTypeKind.Decimal when allowDecimal:
                return;
            default:
                ArraysThrow.Format(
                    $"{wireKind} requires a Utf8, Binary{(allowDecimal ? " or Decimal" : string.Empty)} " +
                    $"dtype; this one is {dtype.Kind}.");
                return;
        }
    }

    private static bool FitsSigned(long value, PType ptype) => ptype switch
    {
        PType.I8 => value >= sbyte.MinValue && value <= sbyte.MaxValue,
        PType.I16 => value >= short.MinValue && value <= short.MaxValue,
        PType.I32 => value >= int.MinValue && value <= int.MaxValue,
        PType.I64 => true,
        PType.U8 => value >= 0 && value <= byte.MaxValue,
        PType.U16 => value >= 0 && value <= ushort.MaxValue,
        PType.U32 => value >= 0 && value <= uint.MaxValue,
        PType.U64 => value >= 0,
        _ => false,
    };

    private static bool FitsUnsigned(ulong value, PType ptype) => ptype switch
    {
        PType.U8 => value <= byte.MaxValue,
        PType.U16 => value <= ushort.MaxValue,
        PType.U32 => value <= uint.MaxValue,
        PType.U64 => true,
        PType.I8 => value <= (ulong)sbyte.MaxValue,
        PType.I16 => value <= (ulong)short.MaxValue,
        PType.I32 => value <= int.MaxValue,
        PType.I64 => value <= long.MaxValue,
        PType.F16 => value <= ushort.MaxValue,
        _ => false,
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowUnsupportedScalar(string id) =>
        throw new VortexUnsupportedException(
            id,
            VortexComponentKind.DType,
            "Phase 1 models no canonical form for this dtype.");
}
