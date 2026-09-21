using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Decodes <c>vortex.sequence</c> into a materialized primitive array: row i is
/// <c>base + i * multiplier</c>, with no children and no buffers, everything coming from the
/// metadata. A zero-length sequence is malformed, the multiplier's physical type is the one the
/// wire tag names rather than the array's, so the step keeps its signedness but not its width, and
/// the last value must fit the output type, which is checked before a single value is generated.
/// </summary>
internal sealed class SequenceDecoder : ArrayDecoder
{
    private const string Id = "vortex.sequence";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly SequenceDecoder Instance = new();

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.sequence"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Sequence;

    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecode => true;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false);
    }

    /// <summary>
    /// <c>A[i] = base + i * multiplier</c> is a closed form, so a take evaluates it at the wanted
    /// indices and generates nothing else.
    /// </summary>
    /// <remarks>
    /// The cheapest specialization there is: a take generates one value per wanted row rather than
    /// one per row of the node.
    /// </remarks>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted, selective: true);
    }

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted, bool selective)
    {
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 0, Id);

        PType ptype = CompressedValues.RequireIntegerPrimitive(dtype, Id);

        if (length <= 0)
        {
            CompressedThrow.Format($"{Id} length must be greater than zero; got {length}.");
        }

        SequenceMetadata metadata = SequenceMetadata.Read(node.Metadata, context.Scalars, context.Types);

        // The base is validated against the output ptype, so a base that does not narrow
        // losslessly is rejected here rather than silently truncated.
        DType baseType = context.Types.Primitive(ptype, Nullability.NonNullable);
        TypedScalar baseScalar = TypedScalarReader.Interpret(metadata.Base, baseType);
        if (baseScalar.IsNull)
        {
            CompressedThrow.Format($"{Id} base value cannot be null.");
        }

        PType multiplierPType = metadata.Multiplier.Kind switch
        {
            ScalarValueKind.Int64 => PType.I64,
            ScalarValueKind.UInt64 => PType.U64,
            _ => CompressedThrow.Format<PType>(
                $"{Id} multiplier must be an integer scalar; the wire carried " +
                $"{metadata.Multiplier.Kind}."),
        };

        DType multiplierType = context.Types.Primitive(multiplierPType, Nullability.NonNullable);
        TypedScalar multiplierScalar = TypedScalarReader.Interpret(metadata.Multiplier, multiplierType);

        bool multiplierAscending;
        ulong multiplierMagnitude;
        ulong multiplierBits;
        if (multiplierPType == PType.U64)
        {
            ulong step = multiplierScalar.AsUInt64;
            multiplierAscending = true;
            multiplierMagnitude = step;
            multiplierBits = step;
        }
        else
        {
            long step = multiplierScalar.AsInt64;
            multiplierAscending = step >= 0;
            multiplierMagnitude = step >= 0 ? (ulong)step : (ulong)(-(step + 1)) + 1UL;
            multiplierBits = unchecked((ulong)step);
        }

        ulong baseBits = ReadBaseBits(baseScalar, ptype);
        EnsureLastExpressible(ptype, baseBits, multiplierAscending, multiplierMagnitude, length);

        int width = ptype.ByteWidth();
        int produced = selective ? wanted.Length : length;
        int total = ArrayDecodeContext.CheckedMultiply(produced, width, "Sequence values");
        // Left uninitialized: every arm of the switch below generates all `produced` elements.
        VortexBuffer output = CompressedValues.AllocateUninitialized(
            context, total, width, Id, out Span<byte> destination);

        switch (width)
        {
            case 1:
                Generate<byte>(destination, baseBits, multiplierBits, wanted, selective);
                break;
            case 2:
                Generate<ushort>(destination, baseBits, multiplierBits, wanted, selective);
                break;
            case 4:
                Generate<uint>(destination, baseBits, multiplierBits, wanted, selective);
                break;
            default:
                Generate<ulong>(destination, baseBits, multiplierBits, wanted, selective);
                break;
        }

        // A sequence has no validity of its own: every generated row is valid.
        return context.Canonical.AddPrimitive(
            dtype, produced, Validity.FromNullability(dtype.Nullability), ptype, output);
    }

    private static void Generate<T>(
        Span<byte> destination, ulong baseBits, ulong multiplierBits, ReadOnlySpan<int> wanted,
        bool selective)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        Span<T> values = MemoryMarshal.Cast<byte, T>(destination);
        T step = T.CreateTruncating(multiplierBits);
        T start = T.CreateTruncating(baseBits);

        if (selective)
        {
            // Multiply rather than accumulate: the wanted rows are scattered, so there is no run to
            // accumulate along, and `base + i * step` wraps exactly as the running sum would.
            for (int i = 0; i < wanted.Length; i++)
            {
                values[i] = unchecked(start + (T.CreateTruncating((uint)wanted[i]) * step));
            }

            return;
        }

        // The accumulator is a loop-carried dependency one add deep, so the serial loop runs at the
        // latency of an add per value however wide the machine is. `base + i * step` is the same
        // sequence with no dependency at all: seed one vector with the first `lanes` values and
        // advance it by `lanes * step`, which wraps exactly as the running sum does because
        // two's-complement addition is associative.
        int index = 0;
        T accumulator = start;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<T>.Count)
        {
            int lanes = Vector<T>.Count;
            Span<T> seed = stackalloc T[lanes];
            T value = start;
            for (int k = 0; k < lanes; k++)
            {
                seed[k] = value;
                value = unchecked(value + step);
            }

            T laneStep = unchecked(value - start);
            Vector<T> bump = new Vector<T>(laneStep);

            // Four accumulators rather than one, and no bounds check in the loop. A single vector
            // still leaves a loop-carried add between consecutive stores, so the loop runs at the
            // latency of that add where the store units should be the only bound; four independent
            // chains let the machine retire four stores in the time one dependency step takes. The
            // base reference is taken once, so the stores address the span without re-checking it
            // per iteration.
            ref T destinationRef = ref MemoryMarshal.GetReference(values);
            Vector<T> v0 = new Vector<T>(seed);
            Vector<T> v1 = unchecked(v0 + bump);
            Vector<T> v2 = unchecked(v1 + bump);
            Vector<T> v3 = unchecked(v2 + bump);
            Vector<T> quadBump = new Vector<T>(
                unchecked(laneStep + laneStep + laneStep + laneStep));

            int quad = lanes * 4;
            for (; index <= values.Length - quad; index += quad)
            {
                v0.StoreUnsafe(ref destinationRef, (nuint)index);
                v1.StoreUnsafe(ref destinationRef, (nuint)(index + lanes));
                v2.StoreUnsafe(ref destinationRef, (nuint)(index + (2 * lanes)));
                v3.StoreUnsafe(ref destinationRef, (nuint)(index + (3 * lanes)));
                v0 = unchecked(v0 + quadBump);
                v1 = unchecked(v1 + quadBump);
                v2 = unchecked(v2 + quadBump);
                v3 = unchecked(v3 + quadBump);
            }

            // v0 is still the vector for `index`, because all four advanced together.
            for (; index <= values.Length - lanes; index += lanes)
            {
                v0.StoreUnsafe(ref destinationRef, (nuint)index);
                v0 = unchecked(v0 + bump);
            }

            accumulator = v0[0];
        }

        for (; index < values.Length; index++)
        {
            values[index] = accumulator;
            accumulator = unchecked(accumulator + step);
        }
    }

    private static ulong ReadBaseBits(TypedScalar baseScalar, PType ptype)
    {
        // The scalar was already range-checked against `ptype`, so the two's-complement bits of
        // either wire kind are exactly the output representation once truncated.
        return ptype.IsSignedInteger()
            ? unchecked((ulong)baseScalar.AsInt64)
            : baseScalar.AsUInt64;
    }

    // Refuses a sequence whose last value would not fit the output type. The room is computed in
    // the ptype's own signedness so a large unsigned base stays exact, and the test is expressed as
    // `steps <= room / magnitude` so the product that would overflow is never formed.
    private static void EnsureLastExpressible(
        PType ptype, ulong baseBits, bool ascending, ulong magnitude, int length)
    {
        ulong steps = (ulong)(length - 1);
        if (steps == 0 || magnitude == 0)
        {
            return;
        }

        ulong room;
        if (ptype.IsSignedInteger())
        {
            long value = SignExtend(baseBits, ptype);
            long max = (long)MaxValueAsUInt64(ptype);
            long bound = ascending ? max : -max - 1;
            room = AbsoluteDifference(value, bound);
        }
        else
        {
            room = ascending ? MaxValueAsUInt64(ptype) - baseBits : baseBits;
        }

        if (steps > room / magnitude)
        {
            CompressedThrow.Format(
                $"{Id}'s final value is not expressible in {ptype.Name()}: {length} rows of " +
                $"step magnitude {magnitude} from the given base overflow it.");
        }
    }

    private static long SignExtend(ulong bits, PType ptype) => ptype switch
    {
        PType.I8 => (sbyte)bits,
        PType.I16 => (short)bits,
        PType.I32 => (int)bits,
        _ => unchecked((long)bits),
    };

    private static ulong MaxValueAsUInt64(PType ptype) => ptype switch
    {
        PType.U8 => byte.MaxValue,
        PType.U16 => ushort.MaxValue,
        PType.U32 => uint.MaxValue,
        PType.U64 => ulong.MaxValue,
        PType.I8 => (ulong)sbyte.MaxValue,
        PType.I16 => (ulong)short.MaxValue,
        PType.I32 => (ulong)int.MaxValue,
        _ => (ulong)long.MaxValue,
    };

    private static ulong AbsoluteDifference(long a, long b) =>
        a >= b ? unchecked((ulong)a - (ulong)b) : unchecked((ulong)b - (ulong)a);
}
