using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Compute;
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
        return Core(context, in node, dtype, length, wanted: default, selective: false, first: 0, produced: length);
    }

    /// <inheritdoc/>
    public override bool DecodesRange(ArrayDecodeContext context, in ArrayNode node) => true;

    /// <summary>The closed form again, started at the range's first row.</summary>
    /// <inheritdoc/>
    public override int DecodeRange(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false, first: start, produced: count);
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
        return Core(context, in node, dtype, length, wanted, selective: true, first: 0, produced: wanted.Length);
    }

    /// <inheritdoc/>
    public override bool EvaluatesWithoutFullDecode => true;

    /// <summary>A predicate on the values is the question it asks of their integers.</summary>
    /// <inheritdoc/>
    public override bool TryEvaluate(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, Compute.EncodedPredicate predicate,
        ref Compute.PredicateBits bits)
    {
        ArgumentNullException.ThrowIfNull(context);
        PType ptype = CompressedValues.RequireIntegerPrimitive(dtype, Id);
        return Compute.IntegerQuestion.TryOf(predicate, ptype, out Compute.IntegerQuestion question)
            && TryEvaluateIntegers(context, in node, dtype, length, in question, ref bits);
    }

    /// <summary>
    /// Answers in closed form: the values of a range are those of a run of rows of the progression, found by two
    /// divisions, and a member is the one row it falls on, if any; no value is generated.
    /// </summary>
    /// <inheritdoc/>
    public override bool TryEvaluateIntegers(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, in Compute.IntegerQuestion question,
        ref Compute.PredicateBits bits)
    {
        ArgumentNullException.ThrowIfNull(context);
        Progression progression = Progression.Of(context, in node, dtype, length);
        switch (question.Kind)
        {
            case Compute.QuestionKind.None:
            case Compute.QuestionKind.All:
                bits.Fill(question.Kind == Compute.QuestionKind.All);
                return true;

            case Compute.QuestionKind.Range:
                bits.Fill(false);

                // The range's patterns, one run or two where it wraps, each the values of one run of the type or two
                // where it crosses the sign.
                UInt128 end = (UInt128)question.Low + question.Span;
                UInt128 patterns = (UInt128)question.Mask + 1;
                if (end < patterns)
                {
                    SelectPatterns(in progression, question.Low, (ulong)end, length, ref bits);
                }
                else
                {
                    SelectPatterns(in progression, question.Low, question.Mask, length, ref bits);
                    SelectPatterns(in progression, 0, (ulong)(end - patterns), length, ref bits);
                }

                if (question.Negated)
                {
                    bits.Invert();
                }

                return true;

            default:
                bits.Fill(false);
                foreach (Int128 member in question.Members)
                {
                    if (question.TryPattern(member, out ulong pattern)
                        && progression.RowOf(progression.ValueOf(pattern), length) is int row and >= 0)
                    {
                        bits.Set(row, selected: true);
                    }
                }

                return true;
        }
    }

    /// <summary>Selects the rows whose pattern lies from <paramref name="low"/> to <paramref name="high"/>, as values of the type.</summary>
    private static void SelectPatterns(in Progression progression, ulong low, ulong high, int length, ref Compute.PredicateBits bits)
    {
        int width = progression.PType.ByteWidth() * 8;
        ulong half = 1UL << (width - 1);
        if (!progression.PType.IsSignedInteger() || high < half)
        {
            SelectValues(in progression, low, high, length, ref bits);
        }
        else if (low >= half)
        {
            SelectValues(in progression, progression.ValueOf(low), progression.ValueOf(high), length, ref bits);
        }
        else
        {
            SelectValues(in progression, low, half - 1, length, ref bits);
            SelectValues(in progression, progression.ValueOf(half), progression.ValueOf(high), length, ref bits);
        }
    }

    /// <summary>Selects the rows whose value lies from <paramref name="low"/> to <paramref name="high"/>: one run of rows.</summary>
    private static void SelectValues(in Progression progression, Int128 low, Int128 high, int length, ref Compute.PredicateBits bits)
    {
        Int128 step = progression.Step;
        Int128 first;
        Int128 last;
        if (step == 0)
        {
            (first, last) = progression.Base >= low && progression.Base <= high ? (Int128.Zero, (Int128)(length - 1)) : (Int128.One, Int128.Zero);
        }
        else if (step > 0)
        {
            first = CeilDiv(low - progression.Base, step);
            last = FloorDiv(high - progression.Base, step);
        }
        else
        {
            first = CeilDiv(high - progression.Base, step);
            last = FloorDiv(low - progression.Base, step);
        }

        first = Int128.Max(first, 0);
        last = Int128.Min(last, length - 1);
        if (first <= last)
        {
            bits.SetRange((int)first, (int)(last - first) + 1);
        }
    }

    private static Int128 FloorDiv(Int128 value, Int128 divisor)
    {
        Int128 quotient = value / divisor;
        return value % divisor != 0 && (value < 0) != (divisor < 0) ? quotient - 1 : quotient;
    }

    private static Int128 CeilDiv(Int128 value, Int128 divisor) => -FloorDiv(-value, divisor);

    /// <summary>A sequence's base and step as values of its type, read and checked once.</summary>
    private readonly struct Progression
    {
        private Progression(PType ptype, ulong baseBits, ulong multiplierBits, bool ascending, ulong magnitude)
        {
            PType = ptype;
            BaseBits = baseBits;
            MultiplierBits = multiplierBits;
            Ascending = ascending;
            Magnitude = magnitude;
        }

        internal PType PType { get; }

        internal ulong BaseBits { get; }

        internal ulong MultiplierBits { get; }

        internal bool Ascending { get; }

        internal ulong Magnitude { get; }

        /// <summary>The first value, as a value of the type.</summary>
        internal Int128 Base => ValueOf(BaseBits);

        /// <summary>The step, signed.</summary>
        internal Int128 Step => Ascending ? (Int128)Magnitude : -(Int128)Magnitude;

        /// <summary>A pattern of the type as its value: signed or not.</summary>
        internal Int128 ValueOf(ulong pattern)
        {
            int width = PType.ByteWidth() * 8;
            ulong mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
            pattern &= mask;
            return PType.IsSignedInteger() && (pattern >> (width - 1)) != 0 ? (Int128)pattern - (Int128.One << width) : pattern;
        }

        /// <summary>The row holding <paramref name="value"/>, or -1 when no row does.</summary>
        internal int RowOf(Int128 value, int length)
        {
            Int128 offset = value - Base;
            Int128 step = Step;
            if (step == 0)
            {
                return offset == 0 && length > 0 ? 0 : -1;
            }

            if (offset % step != 0)
            {
                return -1;
            }

            Int128 row = offset / step;
            return row >= 0 && row < length ? (int)row : -1;
        }

        /// <summary>The node's progression, checked as a decode checks it.</summary>
        /// <exception cref="VortexFormatException">The node is not a sequence, or its last value does not fit its type.</exception>
        internal static Progression Of(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
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

            bool ascending;
            ulong magnitude;
            ulong multiplierBits;
            if (multiplierPType == PType.U64)
            {
                ulong step = multiplierScalar.AsUInt64;
                ascending = true;
                magnitude = step;
                multiplierBits = step;
            }
            else
            {
                long step = multiplierScalar.AsInt64;
                ascending = step >= 0;
                magnitude = step >= 0 ? (ulong)step : (ulong)(-(step + 1)) + 1UL;
                multiplierBits = unchecked((ulong)step);
            }

            ulong baseBits = ReadBaseBits(baseScalar, ptype);
            EnsureLastExpressible(ptype, baseBits, ascending, magnitude, length);
            return new Progression(ptype, baseBits, multiplierBits, ascending, magnitude);
        }
    }

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted, bool selective, int first, int produced)
    {
        Progression progression = Progression.Of(context, in node, dtype, length);
        PType ptype = progression.PType;
        ulong baseBits = progression.BaseBits;
        ulong multiplierBits = progression.MultiplierBits;

        int width = ptype.ByteWidth();
        int total = ArrayDecodeContext.CheckedMultiply(produced, width, "Sequence values");
        // Left uninitialized: every arm of the switch below generates all `produced` elements.
        VortexBuffer output = CompressedValues.AllocateUninitialized(
            context, total, width, Id, out Span<byte> destination);

        switch (width)
        {
            case 1:
                Generate<byte>(destination, baseBits, multiplierBits, wanted, selective, first);
                break;
            case 2:
                Generate<ushort>(destination, baseBits, multiplierBits, wanted, selective, first);
                break;
            case 4:
                Generate<uint>(destination, baseBits, multiplierBits, wanted, selective, first);
                break;
            default:
                Generate<ulong>(destination, baseBits, multiplierBits, wanted, selective, first);
                break;
        }

        // A sequence has no validity of its own: every generated row is valid.
        return context.Canonical.AddPrimitive(
            dtype, produced, Validity.FromNullability(dtype.Nullability), ptype, output);
    }

    private static void Generate<T>(
        Span<byte> destination, ulong baseBits, ulong multiplierBits, ReadOnlySpan<int> wanted,
        bool selective, int first)
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

        // A range starts where its first row falls in the progression; the multiply wraps exactly
        // as accumulating up to it would.
        start = unchecked(start + (T.CreateTruncating((uint)first) * step));
        Compute.Progression.Fill(values, start, step);
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
