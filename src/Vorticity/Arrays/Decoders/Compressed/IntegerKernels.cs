using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
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
    //
    // Four vectors a step, all loaded before any is stored, and walked by reference: a vector a
    // step indexed its spans twice, each with a bound check, and stored each sum before the next
    // load. The caller checked that the two spans are as long.
    private static void AddWrapping<T>(ReadOnlySpan<byte> source, Span<byte> destination, T reference)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ReadOnlySpan<T> src = MemoryMarshal.Cast<byte, T>(source);
        Span<T> dst = MemoryMarshal.Cast<byte, T>(destination);
        ref T from = ref MemoryMarshal.GetReference(src);
        ref T into = ref MemoryMarshal.GetReference(dst);
        nuint length = (nuint)Math.Min(src.Length, dst.Length);

        nuint i = 0;
        if (Vector.IsHardwareAccelerated && length >= (nuint)Vector<T>.Count)
        {
            Vector<T> offset = new Vector<T>(reference);
            nuint lanes = (nuint)Vector<T>.Count;
            for (; i + (4 * lanes) <= length; i += 4 * lanes)
            {
                Vector<T> a = Vector.LoadUnsafe(ref from, i) + offset;
                Vector<T> b = Vector.LoadUnsafe(ref from, i + lanes) + offset;
                Vector<T> c = Vector.LoadUnsafe(ref from, i + (2 * lanes)) + offset;
                Vector<T> d = Vector.LoadUnsafe(ref from, i + (3 * lanes)) + offset;
                a.StoreUnsafe(ref into, i);
                b.StoreUnsafe(ref into, i + lanes);
                c.StoreUnsafe(ref into, i + (2 * lanes));
                d.StoreUnsafe(ref into, i + (3 * lanes));
            }

            for (; i + lanes <= length; i += lanes)
            {
                (Vector.LoadUnsafe(ref from, i) + offset).StoreUnsafe(ref into, i);
            }
        }

        for (; i < length; i++)
        {
            Unsafe.Add(ref into, i) = unchecked(Unsafe.Add(ref from, i) + reference);
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
    /// <c>destination[i] = a[i] * scaleA + b[i] * scaleB + c[i]</c>, wrapping, in one pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The arithmetic of a widen and scale followed by two scaled adds, with the read-modify-write
    /// passes over the destination gone: three reads and one write per element. Splitting it back
    /// into three passes makes the destination traffic dominate a date-time-parts scan, so it stays
    /// fused.
    /// </para>
    /// <para>
    /// The three types are resolved before the loop, nested the way <c>RowKernels.Gather</c>
    /// resolves its codes, so only the shapes a file actually uses are ever instantiated.
    /// </para>
    /// <para>
    /// Wrapping: <c>CreateTruncating</c>, never checked nor saturating, since the reference widens
    /// with Rust's <c>as</c>, which keeps a u64's low 64 bits; and <c>unchecked</c>, because the
    /// reference multiplies i64 in release mode and a hostile file must produce a wrong timestamp
    /// rather than an exception we would not share with it.
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

        // AVX-512 has the 64-bit multiply the scalar loop spends its time in, eight lanes wide
        // (vpmullq), and a widening load for every part type (vpmovsx/vpmovzx): eight rows a step,
        // the same low 64 bits as the scalar line. Neither NEON nor AVX2 has the multiply.
        if (Avx512DQ.IsSupported)
        {
            Vector512<long> timesA = Vector512.Create(scaleA);
            Vector512<long> timesB = Vector512.Create(scaleB);
            for (; i <= count - 8; i += 8)
            {
                (Widen512(ref sc) + (Widen512(ref sb) * timesB) + (Widen512(ref sa) * timesA)).StoreUnsafe(ref into);
                sa = ref Unsafe.Add(ref sa, 8);
                sb = ref Unsafe.Add(ref sb, 8);
                sc = ref Unsafe.Add(ref sc, 8);
                into = ref Unsafe.Add(ref into, 8);
            }

            count -= i;
            i = 0;
        }

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

    /// <summary>
    /// Eight values at <paramref name="source"/> as <see langword="long"/>, as
    /// <see cref="long.CreateTruncating{TOther}(TOther)"/> widens each: sign-extended from a signed
    /// type, zero-extended from an unsigned one, reinterpreted from a 64-bit one. Reads exactly
    /// eight values, the byte types through one 64-bit load.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<long> Widen512<T>(ref T source)
        where T : unmanaged
    {
        if (typeof(T) == typeof(long) || typeof(T) == typeof(ulong))
        {
            return Vector512.LoadUnsafe(ref Unsafe.As<T, long>(ref source));
        }

        if (typeof(T) == typeof(int))
        {
            return Avx512F.ConvertToVector512Int64(Vector256.LoadUnsafe(ref Unsafe.As<T, int>(ref source)));
        }

        if (typeof(T) == typeof(uint))
        {
            return Avx512F.ConvertToVector512Int64(Vector256.LoadUnsafe(ref Unsafe.As<T, uint>(ref source)));
        }

        if (typeof(T) == typeof(short))
        {
            return Avx512F.ConvertToVector512Int64(Vector128.LoadUnsafe(ref Unsafe.As<T, short>(ref source)));
        }

        if (typeof(T) == typeof(ushort))
        {
            return Avx512F.ConvertToVector512Int64(Vector128.LoadUnsafe(ref Unsafe.As<T, ushort>(ref source)));
        }

        Vector128<ulong> eight = Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<T, byte>(ref source)));
        return typeof(T) == typeof(sbyte)
            ? Avx512F.ConvertToVector512Int64(eight.AsSByte())
            : Avx512F.ConvertToVector512Int64(eight.AsByte());
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
