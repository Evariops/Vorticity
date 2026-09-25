using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>
/// A frozen copy of the transform <c>ArrayBlobWriter</c> applied to a block before packing it, as
/// it was: the lanes for a block without nulls, a validity test and a typed read per row for one
/// with them. Kept as the baseline of <see cref="TransformBenchmarks"/>.
/// </summary>
internal static class TransformOriginal
{
    internal static void TransformBlock(
        ReadOnlySpan<byte> values, in ValidityMask mask, PType ptype, int start, int count,
        in BitPackPlan plan, int elementBits, Span<ulong> wide)
    {
        if (mask.AllValid)
        {
            TransformValid(values, ptype, start, count, plan, elementBits, wide);
        }
        else
        {
            TransformMasked(values, in mask, ptype, start, count, plan, elementBits, wide);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TransformValid(
        ReadOnlySpan<byte> values, PType ptype, int start, int count, in BitPackPlan plan,
        int elementBits, Span<ulong> wide)
    {
        int done = TransformLanes(values, ptype.ByteWidth(), start, count, plan.Transform, plan.Reference, wide);
        switch (ptype.ByteWidth())
        {
            case 1: TransformRows<byte>(values, done, start, count, plan, elementBits, wide); return;
            case 2: TransformRows<ushort>(values, done, start, count, plan, elementBits, wide); return;
            case 4: TransformRows<uint>(values, done, start, count, plan, elementBits, wide); return;
            default: TransformRows<ulong>(values, done, start, count, plan, elementBits, wide); return;
        }
    }

    private static void TransformRows<T>(
        ReadOnlySpan<byte> values, int done, int start, int count, in BitPackPlan plan,
        int elementBits, Span<ulong> wide)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ReadOnlySpan<T> block = MemoryMarshal.Cast<byte, T>(values).Slice(start, count);
        for (int i = done; i < block.Length; i++)
        {
            wide[i] = BitPackPlan.Encode(
                ulong.CreateTruncating(block[i]), plan.Transform, plan.Reference, elementBits);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TransformMasked(
        ReadOnlySpan<byte> values, in ValidityMask mask, PType ptype, int start, int count,
        in BitPackPlan plan, int elementBits, Span<ulong> wide)
    {
        for (int i = 0; i < count; i++)
        {
            int row = start + i;
            wide[i] = mask.IsValid(row)
                ? BitPackPlan.Encode(
                    CompressedValues.ReadUnsigned(values, ToUnsigned(ptype), row),
                    plan.Transform, plan.Reference, elementBits)
                : 0;
        }
    }

    private static int TransformLanes(
        ReadOnlySpan<byte> values, int byteWidth, int start, int count,
        BitPackTransform transform, ulong reference, Span<ulong> wide)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            return 0;
        }

        bool frame = transform == BitPackTransform.Frame;
        ref ulong destination = ref MemoryMarshal.GetReference(wide);
        int i = 0;
        if (byteWidth == sizeof(ulong))
        {
            ref ulong source = ref Unsafe.Add(
                ref MemoryMarshal.GetReference(MemoryMarshal.Cast<byte, ulong>(values)), start);
            Vector128<ulong> origin = Vector128.Create(reference);
            for (; i + 2 <= count; i += 2)
            {
                Vector128<ulong> value = Vector128.LoadUnsafe(ref source, (nuint)i);
                Vector128<ulong> encoded = frame
                    ? value - origin
                    : (value << 1) ^ Vector128.ShiftRightArithmetic(value.AsInt64(), 63).AsUInt64();
                encoded.StoreUnsafe(ref destination, (nuint)i);
            }
        }
        else if (byteWidth == sizeof(uint))
        {
            ref uint source = ref Unsafe.Add(
                ref MemoryMarshal.GetReference(MemoryMarshal.Cast<byte, uint>(values)), start);
            Vector128<uint> origin = Vector128.Create(unchecked((uint)reference));
            for (; i + 4 <= count; i += 4)
            {
                Vector128<uint> value = Vector128.LoadUnsafe(ref source, (nuint)i);
                Vector128<uint> encoded = frame
                    ? value - origin
                    : (value << 1) ^ Vector128.ShiftRightArithmetic(value.AsInt32(), 31).AsUInt32();
                Vector128.WidenLower(encoded).StoreUnsafe(ref destination, (nuint)i);
                Vector128.WidenUpper(encoded).StoreUnsafe(ref destination, (nuint)(i + 2));
            }
        }

        return i;
    }

    private static PType ToUnsigned(PType ptype) =>
        ptype.IsSignedInteger() ? (PType)(ptype - PType.I8) : ptype;
}
