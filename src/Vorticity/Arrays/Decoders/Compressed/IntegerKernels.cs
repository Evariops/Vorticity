using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// The element-wise integer kernels the compressed decoders need, written once and monomorphized
/// over the physical widths so that the type switch happens once per node, never once per element.
/// They are correct on signed types too: two's-complement addition and the zigzag identity are
/// bit-pattern operations, so the same bits come out whether the operands are read as signed or as
/// unsigned, and the wrapping needs no signed variant to guard it.
/// </summary>
internal static class IntegerKernels
{
    /// <summary>
    /// <c>destination[i] = source[i] + reference</c>, wrapping, over elements of
    /// <paramref name="elementWidth"/> bytes.
    /// </summary>
    /// <param name="source">The encoded values.</param>
    /// <param name="destination">Same byte length as <paramref name="source"/>.</param>
    /// <param name="elementWidth">1, 2, 4 or 8.</param>
    /// <param name="reference">The reference value's low bits.</param>
    /// <exception cref="ArgumentException">The spans disagree or the width is not supported.</exception>
    public static void AddWrapping(
        ReadOnlySpan<byte> source, Span<byte> destination, int elementWidth, ulong reference)
    {
        RequireSameLength(source.Length, destination.Length);
        switch (elementWidth)
        {
            case 1:
                AddWrapping<byte>(source, destination, (byte)reference);
                break;
            case 2:
                AddWrapping<ushort>(source, destination, (ushort)reference);
                break;
            case 4:
                AddWrapping<uint>(source, destination, (uint)reference);
                break;
            case 8:
                AddWrapping<ulong>(source, destination, reference);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(elementWidth), elementWidth, "Integer element widths are 1, 2, 4 and 8.");
        }
    }

    /// <summary>
    /// <c>destination[i] = (source[i] &gt;&gt; 1) ^ -(source[i] &amp; 1)</c>, the zigzag inverse,
    /// over elements of <paramref name="elementWidth"/> bytes.
    /// </summary>
    /// <param name="source">The zigzag-encoded unsigned values.</param>
    /// <param name="destination">Same byte length as <paramref name="source"/>.</param>
    /// <param name="elementWidth">1, 2, 4 or 8.</param>
    /// <exception cref="ArgumentException">The spans disagree or the width is not supported.</exception>
    public static void ZigZagDecode(ReadOnlySpan<byte> source, Span<byte> destination, int elementWidth)
    {
        RequireSameLength(source.Length, destination.Length);
        switch (elementWidth)
        {
            case 1:
                ZigZagDecode<byte>(source, destination);
                break;
            case 2:
                ZigZagDecode<ushort>(source, destination);
                break;
            case 4:
                ZigZagDecode<uint>(source, destination);
                break;
            case 8:
                ZigZagDecode<ulong>(source, destination);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(elementWidth), elementWidth, "Integer element widths are 1, 2, 4 and 8.");
        }
    }

    // Pointwise, contiguous and branch-free: the shape a vector unit exists for. The loop is
    // written out because the jit does not vectorize a generic loop over `IBinaryInteger<T>`, and
    // it uses `Vector<T>` rather than a fixed width so the same source widens to whatever the
    // machine offers. The guard must stay `Vector.IsHardwareAccelerated`, a question about the
    // machine: `Vector<T>.IsSupported` asks about the type, is true everywhere, and would send a
    // run with intrinsics disabled down an emulated vector path instead of the scalar tail this
    // guard exists to leave behind.
    private static void AddWrapping<T>(ReadOnlySpan<byte> source, Span<byte> destination, T reference)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ReadOnlySpan<T> src = MemoryMarshal.Cast<byte, T>(source);
        Span<T> dst = MemoryMarshal.Cast<byte, T>(destination);

        int i = 0;
        if (Vector.IsHardwareAccelerated && src.Length >= Vector<T>.Count)
        {
            Vector<T> offset = new Vector<T>(reference);
            int lanes = Vector<T>.Count;
            for (; i <= src.Length - lanes; i += lanes)
            {
                (Vector.LoadUnsafe(in src[i]) + offset).StoreUnsafe(ref dst[i]);
            }
        }

        for (; i < src.Length; i++)
        {
            dst[i] = unchecked(src[i] + reference);
        }
    }

    private static void ZigZagDecode<T>(ReadOnlySpan<byte> source, Span<byte> destination)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ReadOnlySpan<T> src = MemoryMarshal.Cast<byte, T>(source);
        Span<T> dst = MemoryMarshal.Cast<byte, T>(destination);

        int i = 0;
        if (Vector.IsHardwareAccelerated && src.Length >= Vector<T>.Count)
        {
            Vector<T> ones = new Vector<T>(T.One);
            int lanes = Vector<T>.Count;
            for (; i <= src.Length - lanes; i += lanes)
            {
                Vector<T> value = Vector.LoadUnsafe(in src[i]);

                // The same identity as the scalar line: shift the magnitude down and XOR with the
                // sign extended from the low bit, which is 0 or all-ones.
                Vector<T> sign = Vector<T>.Zero - (value & ones);
                (ShiftRightOne(value) ^ sign).StoreUnsafe(ref dst[i]);
            }
        }

        for (; i < src.Length; i++)
        {
            T value = src[i];
            dst[i] = (value >> 1) ^ unchecked(T.Zero - (value & T.One));
        }
    }

    /// <summary>
    /// <c>destination[i] = (long)source[i] * scale</c>, wrapping, widening from any integer
    /// physical type.
    /// </summary>
    /// <param name="source">The values, little-endian, at least <c>destination.Length</c> of them.</param>
    /// <param name="ptype">The element's physical type; must be an integer.</param>
    /// <param name="destination">The i64 accumulator.</param>
    /// <param name="scale">The multiplier applied to every widened value.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ptype"/> is not an integer.</exception>
    public static void WidenScaled(
        ReadOnlySpan<byte> source, PType ptype, Span<long> destination, long scale)
    {
        switch (ptype)
        {
            case PType.U8: WidenScaled<byte>(source, destination, scale); break;
            case PType.U16: WidenScaled<ushort>(source, destination, scale); break;
            case PType.U32: WidenScaled<uint>(source, destination, scale); break;
            case PType.U64: WidenScaled<ulong>(source, destination, scale); break;
            case PType.I8: WidenScaled<sbyte>(source, destination, scale); break;
            case PType.I16: WidenScaled<short>(source, destination, scale); break;
            case PType.I32: WidenScaled<int>(source, destination, scale); break;
            case PType.I64: WidenScaled<long>(source, destination, scale); break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(ptype), ptype, "Widening needs an integer physical type.");
        }
    }

    /// <summary>
    /// <c>destination[i] += (long)source[i] * scale</c>, wrapping, widening from any integer
    /// physical type.
    /// </summary>
    /// <param name="source">The values, little-endian, at least <c>destination.Length</c> of them.</param>
    /// <param name="ptype">The element's physical type; must be an integer.</param>
    /// <param name="destination">The i64 accumulator.</param>
    /// <param name="scale">The multiplier applied to every widened value.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ptype"/> is not an integer.</exception>
    public static void AddWidenScaled(
        ReadOnlySpan<byte> source, PType ptype, Span<long> destination, long scale)
    {
        switch (ptype)
        {
            case PType.U8: AddWidenScaled<byte>(source, destination, scale); break;
            case PType.U16: AddWidenScaled<ushort>(source, destination, scale); break;
            case PType.U32: AddWidenScaled<uint>(source, destination, scale); break;
            case PType.U64: AddWidenScaled<ulong>(source, destination, scale); break;
            case PType.I8: AddWidenScaled<sbyte>(source, destination, scale); break;
            case PType.I16: AddWidenScaled<short>(source, destination, scale); break;
            case PType.I32: AddWidenScaled<int>(source, destination, scale); break;
            case PType.I64: AddWidenScaled<long>(source, destination, scale); break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(ptype), ptype, "Widening needs an integer physical type.");
        }
    }

    /// <summary>
    /// <c>destination[i] = a[i] * scaleA + b[i] * scaleB + c[i]</c>, wrapping, in one pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same arithmetic as <see cref="WidenScaled(ReadOnlySpan{byte}, PType, Span{long},
    /// long)"/> followed by two <see cref="AddWidenScaled(ReadOnlySpan{byte}, PType, Span{long},
    /// long)"/>, with the read-modify-write passes over the destination gone: three reads and one
    /// write per element. Splitting it back into three passes makes the destination traffic
    /// dominate a date-time-parts scan, so it stays fused.
    /// </para>
    /// <para>
    /// The three types are resolved before the loop, nested the way <c>RowKernels.Gather</c>
    /// resolves its codes, so only the shapes a file actually uses are ever instantiated.
    /// </para>
    /// <para>
    /// The wrapping is the three kernels' wrapping, unchanged: <c>CreateTruncating</c> and
    /// <c>unchecked</c>, because the reference multiplies i64 in release mode and a hostile file
    /// must produce a wrong timestamp rather than an exception we would not share with it.
    /// </para>
    /// </remarks>
    /// <param name="a">The first part's values, little-endian.</param>
    /// <param name="aPType">The first part's physical type; must be an integer.</param>
    /// <param name="b">The second part's values.</param>
    /// <param name="bPType">The second part's physical type; must be an integer.</param>
    /// <param name="c">The third part's values, added unscaled.</param>
    /// <param name="cPType">The third part's physical type; must be an integer.</param>
    /// <param name="destination">The i64 output; every element is assigned.</param>
    /// <param name="scaleA">The multiplier for <paramref name="a"/>.</param>
    /// <param name="scaleB">The multiplier for <paramref name="b"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">A physical type is not an integer.</exception>
    public static void Recompose(
        ReadOnlySpan<byte> a, PType aPType,
        ReadOnlySpan<byte> b, PType bPType,
        ReadOnlySpan<byte> c, PType cPType,
        Span<long> destination,
        long scaleA,
        long scaleB)
    {
        switch (aPType)
        {
            case PType.U8: RecomposeB<byte>(a, b, bPType, c, cPType, destination, scaleA, scaleB); break;
            case PType.U16: RecomposeB<ushort>(a, b, bPType, c, cPType, destination, scaleA, scaleB); break;
            case PType.U32: RecomposeB<uint>(a, b, bPType, c, cPType, destination, scaleA, scaleB); break;
            case PType.U64: RecomposeB<ulong>(a, b, bPType, c, cPType, destination, scaleA, scaleB); break;
            case PType.I8: RecomposeB<sbyte>(a, b, bPType, c, cPType, destination, scaleA, scaleB); break;
            case PType.I16: RecomposeB<short>(a, b, bPType, c, cPType, destination, scaleA, scaleB); break;
            case PType.I32: RecomposeB<int>(a, b, bPType, c, cPType, destination, scaleA, scaleB); break;
            case PType.I64: RecomposeB<long>(a, b, bPType, c, cPType, destination, scaleA, scaleB); break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(aPType), aPType, "Widening needs an integer physical type.");
        }
    }

    private static void RecomposeB<TA>(
        ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, PType bPType, ReadOnlySpan<byte> c, PType cPType,
        Span<long> destination, long scaleA, long scaleB)
        where TA : unmanaged, IBinaryInteger<TA>
    {
        switch (bPType)
        {
            case PType.U8: RecomposeC<TA, byte>(a, b, c, cPType, destination, scaleA, scaleB); break;
            case PType.U16: RecomposeC<TA, ushort>(a, b, c, cPType, destination, scaleA, scaleB); break;
            case PType.U32: RecomposeC<TA, uint>(a, b, c, cPType, destination, scaleA, scaleB); break;
            case PType.U64: RecomposeC<TA, ulong>(a, b, c, cPType, destination, scaleA, scaleB); break;
            case PType.I8: RecomposeC<TA, sbyte>(a, b, c, cPType, destination, scaleA, scaleB); break;
            case PType.I16: RecomposeC<TA, short>(a, b, c, cPType, destination, scaleA, scaleB); break;
            case PType.I32: RecomposeC<TA, int>(a, b, c, cPType, destination, scaleA, scaleB); break;
            case PType.I64: RecomposeC<TA, long>(a, b, c, cPType, destination, scaleA, scaleB); break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(bPType), bPType, "Widening needs an integer physical type.");
        }
    }

    private static void RecomposeC<TA, TB>(
        ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c, PType cPType,
        Span<long> destination, long scaleA, long scaleB)
        where TA : unmanaged, IBinaryInteger<TA>
        where TB : unmanaged, IBinaryInteger<TB>
    {
        switch (cPType)
        {
            case PType.U8: RecomposeCore<TA, TB, byte>(a, b, c, destination, scaleA, scaleB); break;
            case PType.U16: RecomposeCore<TA, TB, ushort>(a, b, c, destination, scaleA, scaleB); break;
            case PType.U32: RecomposeCore<TA, TB, uint>(a, b, c, destination, scaleA, scaleB); break;
            case PType.U64: RecomposeCore<TA, TB, ulong>(a, b, c, destination, scaleA, scaleB); break;
            case PType.I8: RecomposeCore<TA, TB, sbyte>(a, b, c, destination, scaleA, scaleB); break;
            case PType.I16: RecomposeCore<TA, TB, short>(a, b, c, destination, scaleA, scaleB); break;
            case PType.I32: RecomposeCore<TA, TB, int>(a, b, c, destination, scaleA, scaleB); break;
            case PType.I64: RecomposeCore<TA, TB, long>(a, b, c, destination, scaleA, scaleB); break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(cPType), cPType, "Widening needs an integer physical type.");
        }
    }

    private static void RecomposeCore<TA, TB, TC>(
        ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c,
        Span<long> destination, long scaleA, long scaleB)
        where TA : unmanaged, IBinaryInteger<TA>
        where TB : unmanaged, IBinaryInteger<TB>
        where TC : unmanaged, IBinaryInteger<TC>
    {
        int count = destination.Length;

        // Sliced to the row count here, so every index below is in range: four rows a step, each a
        // load of each part and two multiply-adds, and no bounds check or loop test between them.
        ref TA sa = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<byte, TA>(a)[..count]);
        ref TB sb = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<byte, TB>(b)[..count]);
        ref TC sc = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<byte, TC>(c)[..count]);
        ref long into = ref MemoryMarshal.GetReference(destination);

        // The cursors move rather than an index being formed per row, so each load is at a constant
        // offset from its cursor.
        int i = 0;
        for (; i <= count - 4; i += 4)
        {
            into = Row(ref sa, ref sb, ref sc, 0, scaleA, scaleB);
            Unsafe.Add(ref into, 1) = Row(ref sa, ref sb, ref sc, 1, scaleA, scaleB);
            Unsafe.Add(ref into, 2) = Row(ref sa, ref sb, ref sc, 2, scaleA, scaleB);
            Unsafe.Add(ref into, 3) = Row(ref sa, ref sb, ref sc, 3, scaleA, scaleB);
            sa = ref Unsafe.Add(ref sa, 4);
            sb = ref Unsafe.Add(ref sb, 4);
            sc = ref Unsafe.Add(ref sc, 4);
            into = ref Unsafe.Add(ref into, 4);
        }

        for (int k = 0; i < count; i++, k++)
        {
            Unsafe.Add(ref into, k) = Row(ref sa, ref sb, ref sc, k, scaleA, scaleB);
        }

        // Two multiply-adds: the subseconds plus the seconds scaled, plus the days scaled.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long Row(ref TA sa, ref TB sb, ref TC sc, int k, long scaleA, long scaleB) => unchecked(
            long.CreateTruncating(Unsafe.Add(ref sc, k)) +
            (long.CreateTruncating(Unsafe.Add(ref sb, k)) * scaleB) +
            (long.CreateTruncating(Unsafe.Add(ref sa, k)) * scaleA));
    }

    // CreateTruncating, never CreateChecked or CreateSaturating: the reference widens with Rust's
    // `as` cast (num_traits AsPrimitive), which keeps the low 64 bits of a u64 rather than clamping
    // it to i64::MAX. CompressedValues.ReadInteger saturates instead, and is right to - its callers
    // compare against a non-negative bound - but a value-producing path must not.
    private static void WidenScaled<T>(ReadOnlySpan<byte> source, Span<long> destination, long scale)
        where T : unmanaged, IBinaryInteger<T>
    {
        ReadOnlySpan<T> src = MemoryMarshal.Cast<byte, T>(source);
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = unchecked(long.CreateTruncating(src[i]) * scale);
        }
    }

    private static void AddWidenScaled<T>(ReadOnlySpan<byte> source, Span<long> destination, long scale)
        where T : unmanaged, IBinaryInteger<T>
    {
        ReadOnlySpan<T> src = MemoryMarshal.Cast<byte, T>(source);
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = unchecked(destination[i] + (long.CreateTruncating(src[i]) * scale));
        }
    }

    /// <summary>
    /// One logical right shift, reinterpreting to the concrete lane type.
    /// </summary>
    /// <remarks>
    /// <c>Vector.ShiftRightLogical</c> has no overload open in the lane type, so the vector is
    /// reinterpreted to the one <typeparamref name="T"/> actually is. The typeof comparisons are
    /// compile-time constants for a value-type instantiation and the JIT drops the dead branches;
    /// <c>Vector.As</c> is a no-op reinterpretation, not a conversion.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<T> ShiftRightOne<T>(Vector<T> value)
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            return Vector.As<byte, T>(Vector.ShiftRightLogical(Vector.As<T, byte>(value), 1));
        }

        if (typeof(T) == typeof(ushort))
        {
            return Vector.As<ushort, T>(Vector.ShiftRightLogical(Vector.As<T, ushort>(value), 1));
        }

        if (typeof(T) == typeof(uint))
        {
            return Vector.As<uint, T>(Vector.ShiftRightLogical(Vector.As<T, uint>(value), 1));
        }

        return Vector.As<ulong, T>(Vector.ShiftRightLogical(Vector.As<T, ulong>(value), 1));
    }

    private static void RequireSameLength(int source, int destination)
    {
        if (source != destination)
        {
            throw new ArgumentException(
                $"An element-wise kernel needs equal spans; got {source} and {destination} bytes.",
                nameof(destination));
        }
    }
}
