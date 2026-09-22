// The C# half of the row-encoding cross-check.
//
// THIS FILE AND tools/row-vectors/src/main.rs ARE TWO HALVES OF ONE TABLE. Each case name here
// must build the same columns the Rust function of the same name builds. Nothing enforces that
// mechanically - but nothing needs to: if the two sides build different inputs, the encoded bytes
// differ and RowVectorTests fails naming the case. A silent pass is not among the outcomes.
//
// Regenerate the vectors with:
//   cd tools/row-vectors && CARGO_NET_GIT_FETCH_WITH_CLI=true cargo run --release -- \
//       ../../tests/Vorticity.Conformance/row-vectors/vectors.jsonl
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Conformance.RowVectors;

/// <summary>Builds the columns each named vector case was produced from.</summary>
internal sealed class RowVectorCases : IDisposable
{
    private readonly DTypeArena _types = new DTypeArena();

    internal CanonicalArena Arena { get; } = new CanonicalArena();

    /// <summary>Every case name the Rust generator emits.</summary>
    internal static IReadOnlyList<string> Names { get; } =
    [
        "i8_asc", "i8_desc", "i16_asc", "i16_desc",
        "i32_nulls_first", "i32_nulls_last", "i32_desc_nulls_first", "i32_desc_nulls_last",
        "i64_asc", "i64_desc", "u8_asc", "u16_desc", "u32_nulls_last", "u64_asc",
        "f16_asc", "f32_asc", "f32_desc", "f64_asc", "f64_desc",
        "bool_asc", "bool_desc_nulls_last", "null_dtype", "null_dtype_nulls_last",
        "utf8_asc", "utf8_desc", "utf8_asc_nulls_last", "utf8_desc_nulls_last",
        "binary_asc", "binary_desc",
        "decimal_p2_i8", "decimal_p4_i16", "decimal_p9_i32", "decimal_p18_i64",
        "decimal_p38_i128", "decimal_p38_i128_desc", "decimal_p7_from_i64", "decimal_nullable",
        "struct_fixed_nullable", "struct_fixed_nullable_desc",
        "struct_varlen_nullable", "struct_varlen_nullable_desc",
        "struct_mixed", "struct_nested",
        "fsl_i32_nullable", "fsl_i32_nullable_desc", "fsl_utf8_nullable", "fsl_zero",
        "multi_column_mixed",
    ];

    /// <summary>The columns for one case, as canonical node indices.</summary>
    /// <param name="name">A name from <see cref="Names"/>.</param>
    /// <returns>One node index per column.</returns>
    /// <exception cref="ArgumentException">The name is not a known case.</exception>
    internal int[] Build(string name)
    {
        switch (name)
        {
            case "i8_asc":
            case "i8_desc":
                return [Primitive<sbyte>(PType.I8, [-128, -1, 0, 1, 127])];

            case "i16_asc":
            case "i16_desc":
                return [Primitive<short>(PType.I16, [-32768, -1, 0, 1, 32767])];

            case "i32_nulls_first":
            case "i32_nulls_last":
            case "i32_desc_nulls_first":
            case "i32_desc_nulls_last":
                return [Int32Optional()];

            case "i64_asc":
            case "i64_desc":
                return [Primitive<long>(PType.I64, [long.MinValue, -1, 0, 1, long.MaxValue])];

            case "u8_asc":
                return [Primitive<byte>(PType.U8, [0, 1, 127, 128, 255])];

            case "u16_desc":
                return [Primitive<ushort>(PType.U16, [0, 1, 32767, 32768, 65535])];

            case "u32_nulls_last":
                return [Primitive<uint>(PType.U32, [0, 0, uint.MaxValue], [true, false, true])];

            case "u64_asc":
                return [Primitive<ulong>(PType.U64, [0, 1, ulong.MaxValue])];

            case "f16_asc":
                return
                [
                    Primitive<Half>(
                        PType.F16,
                        [
                            (Half)(-1.5f),
                            BitConverter.UInt16BitsToHalf(0x8000),
                            (Half)0f,
                            (Half)1.5f,
                            Half.NegativeInfinity,
                            Half.PositiveInfinity,
                        ]),
                ];

            case "f32_asc":
            case "f32_desc":
                return
                [
                    Primitive<float>(
                        PType.F32,
                        [
                            -1.5f,
                            BitConverter.UInt32BitsToSingle(0x8000_0000),
                            0.0f,
                            1.5f,
                            float.NegativeInfinity,
                            float.PositiveInfinity,
                            BitConverter.UInt32BitsToSingle(0x7FC0_0000),
                            BitConverter.UInt32BitsToSingle(0xFFC0_0000),
                        ]),
                ];

            case "f64_asc":
            case "f64_desc":
                return
                [
                    Primitive<double>(
                        PType.F64,
                        [
                            -1.5,
                            BitConverter.UInt64BitsToDouble(0x8000_0000_0000_0000),
                            0.0,
                            1.5,
                            double.NegativeInfinity,
                            double.PositiveInfinity,
                            BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0000),
                            BitConverter.UInt64BitsToDouble(0xFFF8_0000_0000_0000),
                        ]),
                ];

            case "bool_asc":
            case "bool_desc_nulls_last":
                return [Bool([false, true, false], [true, true, false])];

            case "null_dtype":
            case "null_dtype_nulls_last":
                return [Arena.AddNull(_types.Null(Nullability.Nullable), 3)];

            case "utf8_asc":
            case "utf8_desc":
            case "utf8_asc_nulls_last":
            case "utf8_desc_nulls_last":
                return [VarBin(Text(), DTypeKind.Utf8)];

            case "binary_asc":
            case "binary_desc":
                return [VarBin(BinaryValues(), DTypeKind.Binary)];

            case "decimal_p2_i8":
                return [Decimal([-99, 0, 99], 2, 1, DecimalStorageType.I8)];

            case "decimal_p4_i16":
                return [Decimal([-9999, 0, 9999], 4, 2, DecimalStorageType.I16)];

            case "decimal_p9_i32":
                return [Decimal([-999_999_999, 0, 999_999_999], 9, 3, DecimalStorageType.I32)];

            case "decimal_p18_i64":
                return
                [
                    Decimal(
                        [-999_999_999_999_999_999L, 0, 999_999_999_999_999_999L],
                        18, 6, DecimalStorageType.I64),
                ];

            case "decimal_p38_i128":
            case "decimal_p38_i128_desc":
            {
                Int128 max = Int128.Parse(
                    "99999999999999999999999999999999999999", CultureInfo.InvariantCulture);
                return [Decimal([-max, 0, max], 38, 10, DecimalStorageType.I128)];
            }

            case "decimal_p7_from_i64":
                return [Decimal([484, -5, 91], 7, 5, DecimalStorageType.I64)];

            case "decimal_nullable":
                return
                [
                    Decimal(
                        [484, 10_000_000_000_000, 91], 7, 5, DecimalStorageType.I64,
                        [true, false, true]),
                ];

            case "struct_fixed_nullable":
            case "struct_fixed_nullable_desc":
            {
                int a = Primitive<int>(PType.I32, [0x1234_5678, 9, -1]);
                return [Struct(["a"], [a], 3, [false, true, false])];
            }

            case "struct_varlen_nullable":
            case "struct_varlen_nullable_desc":
            {
                int name2 = VarBin(
                    [Utf8("short"), Utf8("x"), Utf8("much longer text data")], DTypeKind.Utf8);
                return [Struct(["name"], [name2], 3, [false, true, false])];
            }

            case "struct_mixed":
            {
                int a = Primitive<int>(PType.I32, [1, 0, -7, 0], [true, false, true, true]);
                int b = VarBin([Utf8("aa"), [], null, Utf8("z")], DTypeKind.Utf8);
                return [Struct(["a", "b"], [a, b], 4, [true, true, true, false])];
            }

            case "struct_nested":
            {
                int y = Primitive<int>(PType.I32, [7, -7]);
                int inner = Struct(["y"], [y], 2);
                return [Struct(["x"], [inner], 2, [true, false])];
            }

            case "fsl_i32_nullable":
            case "fsl_i32_nullable_desc":
            {
                int elements = Primitive<int>(
                    PType.I32, [9, 9, 1, 0, -3, 4], [true, true, true, false, true, true]);
                return [FixedSizeList(elements, 2, 3, [false, true, true])];
            }

            case "fsl_utf8_nullable":
            {
                int elements = VarBin(
                    [
                        Utf8("a value long enough to cross the block boundary twice over, easily"),
                        Utf8("b"),
                        [],
                        null,
                    ],
                    DTypeKind.Utf8);
                return [FixedSizeList(elements, 2, 2, [false, true])];
            }

            case "fsl_zero":
            {
                int elements = Primitive<int>(PType.I32, []);
                return [FixedSizeList(elements, 0, 3, [true, false, true])];
            }

            case "multi_column_mixed":
            {
                byte[]?[] text = Text();
                int numbers = Int32Optional();
                int words = VarBin(text.AsSpan(0, 5).ToArray(), DTypeKind.Utf8);
                int flags = Bool(
                    [false, true, false, true, false], [true, true, false, true, true]);
                return [numbers, words, flags];
            }

            default:
                throw new ArgumentException($"Unknown vector case '{name}'.", nameof(name));
        }
    }

    /// <summary>
    /// The columns of one case as the batch the public encoder takes: one field per column, in
    /// order, typed by the column's dtype, with no struct above them.
    /// </summary>
    /// <param name="columns">The node indices <see cref="Build"/> returned.</param>
    /// <returns>A view over the arena's columns.</returns>
    internal BatchView Batch(int[] columns)
    {
        VortexField[] fields = new VortexField[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            fields[i] = new VortexField(
                "c" + i.ToString(CultureInfo.InvariantCulture),
                VortexTypes.FromDType(Arena.GetNode(columns[i]).DType));
        }

        return new BatchView(Arena, columns, VortexSchema.Create(fields));
    }

    public void Dispose() => Arena.Reset();

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>The Rust side's `text`: the block boundary from every angle.</summary>
    private static byte[]?[] Text()
    {
        string long64 = new string('x', 64);
        return
        [
            [],
            Utf8("a"),
            Utf8(long64[..31]),
            Utf8(long64[..32]),
            Utf8(long64[..33]),
            Utf8(long64),
            null,
        ];
    }

    private static byte[]?[] BinaryValues()
    {
        byte[] ramp = new byte[40];
        for (int i = 0; i < ramp.Length; i++)
        {
            ramp[i] = (byte)i;
        }

        return [[], [0x00], [0xFF, 0x00, 0x80], ramp, null];
    }

    private int Int32Optional() =>
        Primitive<int>(PType.I32, [1, 0, -1, int.MinValue, int.MaxValue], [true, false, true, true, true]);

    private VortexBuffer Bytes(ReadOnlySpan<byte> data, int alignment = 16)
    {
        if (data.IsEmpty)
        {
            return VortexBuffer.Empty;
        }

        VortexBuffer buffer = Arena.Allocate(data.Length, alignment, out Span<byte> destination);
        data.CopyTo(destination);
        return buffer;
    }

    private Validity Validity(bool[]? valid)
    {
        if (valid is null)
        {
            return Vorticity.Arrays.Validity.NonNullable;
        }

        byte[] raw = new byte[Math.Max((valid.Length + 7) / 8, 1)];
        for (int i = 0; i < valid.Length; i++)
        {
            if (valid[i])
            {
                raw[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        int bits = Arena.AddBool(
            _types.Bool(Nullability.NonNullable), valid.Length,
            Vorticity.Arrays.Validity.NonNullable, Bytes(raw, 8), 0);
        return Vorticity.Arrays.Validity.Bitmap(bits);
    }

    private static Nullability Null(bool[]? valid) =>
        valid is null ? Nullability.NonNullable : Nullability.Nullable;

    private int Primitive<T>(PType ptype, ReadOnlySpan<T> values, bool[]? valid = null)
        where T : unmanaged
    {
        ReadOnlySpan<byte> raw = System.Runtime.InteropServices.MemoryMarshal.AsBytes(values);
        return Arena.AddPrimitive(
            _types.Primitive(ptype, Null(valid)), values.Length, Validity(valid), ptype, Bytes(raw));
    }

    private int Bool(ReadOnlySpan<bool> values, bool[]? valid = null)
    {
        byte[] raw = new byte[Math.Max((values.Length + 7) / 8, 1)];
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i])
            {
                raw[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        return Arena.AddBool(
            _types.Bool(Null(valid)), values.Length, Validity(valid), Bytes(raw, 8), 0);
    }

    private int VarBin(ReadOnlySpan<byte[]?> values, DTypeKind kind)
    {
        int length = values.Length;
        byte[] views = new byte[Math.Max(length * 16, 1)];
        List<byte> heap = [];
        bool[] valid = new bool[length];
        bool anyNull = false;

        for (int i = 0; i < length; i++)
        {
            byte[]? value = values[i];
            valid[i] = value is not null;
            if (value is null)
            {
                anyNull = true;
                continue;
            }

            Span<byte> view = views.AsSpan(i * 16, 16);
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)value.Length);
            if (value.Length <= 12)
            {
                value.CopyTo(view[4..]);
            }
            else
            {
                value.AsSpan(0, 4).CopyTo(view[4..8]);
                BinaryPrimitives.WriteUInt32LittleEndian(view[8..12], 0);
                BinaryPrimitives.WriteUInt32LittleEndian(view[12..16], (uint)heap.Count);
                heap.AddRange(value);
            }
        }

        Nullability nullability = anyNull ? Nullability.Nullable : Nullability.NonNullable;
        Validity validity = anyNull ? Validity(valid) : Vorticity.Arrays.Validity.NonNullable;
        DType dtype = kind == DTypeKind.Utf8 ? _types.Utf8(nullability) : _types.Binary(nullability);
        Span<VortexBuffer> buffers = heap.Count == 0
            ? [VortexBuffer.Empty]
            : [Bytes(heap.ToArray())];
        return Arena.AddVarBinView(dtype, length, validity, Bytes(views), buffers);
    }

    private int Decimal(
        ReadOnlySpan<Int128> unscaled, byte precision, sbyte scale,
        DecimalStorageType storage, bool[]? valid = null)
    {
        int width = DecimalStorage.ByteWidth(storage);
        byte[] raw = new byte[Math.Max(unscaled.Length * width, 1)];
        Span<byte> scratch = stackalloc byte[16];
        for (int i = 0; i < unscaled.Length; i++)
        {
            BinaryPrimitives.WriteInt128LittleEndian(scratch, unscaled[i]);
            scratch[..width].CopyTo(raw.AsSpan(i * width, width));
        }

        return Arena.AddDecimal(
            _types.Decimal(precision, scale, Null(valid)), unscaled.Length, Validity(valid),
            storage, precision, scale, Bytes(raw, 16));
    }

    private int Struct(ReadOnlySpan<string> fieldNames, ReadOnlySpan<int> children, int rows, bool[]? valid = null)
    {
        Span<DType> fieldTypes = new DType[children.Length];
        for (int i = 0; i < children.Length; i++)
        {
            fieldTypes[i] = Arena.GetNode(children[i]).DType;
        }

        return Arena.AddStruct(
            _types.Struct(fieldNames, fieldTypes, Null(valid)), rows, Validity(valid), children);
    }

    private int FixedSizeList(int elements, uint size, int rows, bool[]? valid = null)
    {
        DType elementType = Arena.GetNode(elements).DType;
        return Arena.AddFixedSizeList(
            _types.FixedSizeList(elementType, size, Null(valid)), rows, Validity(valid), elements, size);
    }
}
