// Transcribed from spec/flatbuffers/dtype.fbs (`enum PType: uint8`) and spec/proto/dtype.proto
// (`enum PType`). Both list U8, U16, U32, U64, I8, I16, I32, I64, F16, F32, F64 in that order,
// so the unsigned family occupies 0..3, the signed family 4..7 and the floats 8..10.
using System;
using System.Runtime.CompilerServices;

namespace Vorticity.Types;

/// <summary>
/// Physical type of a <see cref="DTypeKind.Primitive"/> dtype. The numeric values are the wire
/// tags shared by <c>dtype.fbs</c> and <c>dtype.proto</c>.
/// </summary>
public enum PType : byte
{
    /// <summary>Unsigned 8-bit integer.</summary>
    U8 = 0,

    /// <summary>Unsigned 16-bit integer.</summary>
    U16 = 1,

    /// <summary>Unsigned 32-bit integer.</summary>
    U32 = 2,

    /// <summary>Unsigned 64-bit integer.</summary>
    U64 = 3,

    /// <summary>Signed 8-bit integer.</summary>
    I8 = 4,

    /// <summary>Signed 16-bit integer.</summary>
    I16 = 5,

    /// <summary>Signed 32-bit integer.</summary>
    I32 = 6,

    /// <summary>Signed 64-bit integer.</summary>
    I64 = 7,

    /// <summary>IEEE-754 binary16.</summary>
    F16 = 8,

    /// <summary>IEEE-754 binary32.</summary>
    F32 = 9,

    /// <summary>IEEE-754 binary64.</summary>
    F64 = 10,
}

/// <summary>Classification and width helpers for <see cref="PType"/>.</summary>
public static class PTypeExtensions
{
    /// <summary>Highest defined <see cref="PType"/> tag. Anything above it is malformed.</summary>
    internal const byte MaxPType = (byte)PType.F64;

    // Indexed by the wire tag. A ReadOnlySpan<byte> over a constant array literal is compiled to a
    // pointer into the assembly's data section: no allocation, no static constructor.
    private static ReadOnlySpan<byte> Widths => [1, 2, 4, 8, 1, 2, 4, 8, 2, 4, 8];

    // Interned literals: returning one of these allocates nothing.
    private static readonly string[] Names =
        ["u8", "u16", "u32", "u64", "i8", "i16", "i32", "i64", "f16", "f32", "f64"];

    /// <summary>
    /// Width in bytes of one value: 1, 2, 4, 8, 1, 2, 4, 8, 2, 4, 8 for U8..F64.
    /// </summary>
    /// <exception cref="VortexFormatException">
    /// <paramref name="p"/> is not a defined tag. A <see cref="PType"/> reaching this method comes
    /// from a file, so an undefined tag is malformed input rather than an argument error
    /// (docs/03-architecture.md section 5).
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ByteWidth(this PType p)
    {
        uint tag = (uint)p;
        if (tag > MaxPType)
        {
            ThrowUndefined(p);
        }

        return Widths[(int)tag];
    }

    /// <summary>True for I8, I16, I32 and I64.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSignedInteger(this PType p) => (uint)(p - PType.I8) <= (uint)(PType.I64 - PType.I8);

    /// <summary>True for U8, U16, U32 and U64.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsUnsignedInteger(this PType p) => (uint)p <= (uint)PType.U64;

    /// <summary>True for any of the eight integer types.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInteger(this PType p) => (uint)p <= (uint)PType.I64;

    /// <summary>True for F16, F32 and F64.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFloat(this PType p) => (uint)(p - PType.F16) <= (uint)(PType.F64 - PType.F16);

    /// <summary>
    /// Lower-case short name used by <see cref="DType.ToString"/> and by <c>vxdump</c>:
    /// <c>"u8"</c>, <c>"i32"</c>, <c>"f64"</c>, ... Culture-invariant by construction.
    /// </summary>
    /// <exception cref="VortexFormatException"><paramref name="p"/> is not a defined tag.</exception>
    public static string Name(this PType p)
    {
        uint tag = (uint)p;
        if (tag > MaxPType)
        {
            ThrowUndefined(p);
        }

        return Names[(int)tag];
    }

    /// <summary>
    /// Guards a <see cref="PType"/> tag read from a file: true only for 0..10. The enum is a
    /// <c>uint8</c> on the wire, so 11..255 are expressible and must be rejected before use.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDefined(PType p) => (uint)p <= MaxPType;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUndefined(PType p) =>
        throw new VortexFormatException(
            $"PType tag {(byte)p} is not defined; the schema defines 0..{MaxPType}.");
}
