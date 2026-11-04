using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Decodes <c>vortex.alp</c> into the float array it encodes: a float whose decimal form is short
/// is stored as a scaled integer, and the values the encoder could not represent that way are
/// carried verbatim as patches, which is what makes the encoding lossless despite the rounding.
/// </summary>
/// <remarks>
/// The children are the encoded integers, then, when the metadata declares patches, the patch
/// indices and the patch values, and last the per-chunk index offsets when those are declared too.
/// There is no validity child: the array's nullability rides on its encoded integers.
/// </remarks>
internal sealed class AlpDecoder : ArrayDecoder
{
    /// <summary>The wire id.</summary>
    public const string Id = "vortex.alp";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly AlpDecoder Instance = new AlpDecoder();

    private AlpDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.alp"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Alp;

    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecode => true;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false, start: 0, count: length);
    }

    /// <summary>ALP is pointwise, so a range is the encoded integers' range, scaled.</summary>
    /// <inheritdoc/>
    public override bool DecodesRange(ArrayDecodeContext context, in ArrayNode node)
    {
        ArgumentNullException.ThrowIfNull(context);
        return node.ChildCount >= 1 && context.ChildDecodesRange(in node, 0);
    }

    /// <summary>
    /// The encoded integers' range, scaled, with the patches of the range applied: the patch set is
    /// read whole, decoded once for every range of the node, and its first patch in the range found
    /// by a binary search.
    /// </summary>
    /// <inheritdoc/>
    public override int DecodeRange(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false, start, count);
    }

    /// <summary>
    /// ALP is pointwise, so a take reaches straight through it to the integers underneath.
    /// </summary>
    /// <remarks>
    /// One output row comes from one input row, scaled, with the exceptions carried as patches, so
    /// a take never needs a row it was not asked for. It reaches the bit-packing underneath, which
    /// is where the saving is.
    /// </remarks>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted, selective: true, start: 0, count: wanted.Length);
    }

    /// <inheritdoc/>
    public override bool EvaluatesWithoutFullDecode => true;

    /// <summary>
    /// Answers a comparison, a range or a set of the floats on their encoded integers: a float is its integer scaled,
    /// <c>e * 10^f * 10^-e'</c>, which never decreases as the integer grows, so the floats on one side of a literal are
    /// the integers on one side of a threshold, found by halves against the decode itself and so exact whatever the
    /// rounding. The integers' encoding then answers the range, and each exception is judged on its own float.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An equality is the integers from the first that decodes at the literal or past it to the first that decodes past
    /// it: none when the literal falls between two of them. A NaN satisfies no comparison but an inequality, which every
    /// row satisfies; <c>-0.0</c> equals <c>0.0</c>. A set is the integers of each member, declined when a member would
    /// be more than a few of them.
    /// </para>
    /// </remarks>
    /// <inheritdoc/>
    public override bool TryEvaluate(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, Compute.EncodedPredicate predicate,
        ref Compute.PredicateBits bits)
    {
        ArgumentNullException.ThrowIfNull(context);
        Scaling scaling = Scaling.Of(in node, dtype, out AlpMetadata metadata);
        int width = scaling.Single ? 32 : 64;
        DType encodedType = context.Types.Primitive(scaling.Single ? PType.I32 : PType.I64, dtype.Nullability);
        bool answered;
        switch (predicate.Kind)
        {
            case Compute.EncodedPredicateKind.Compare when TryDouble(predicate.Literal, out double wanted):
            {
                Compute.IntegerQuestion question = Compute.IntegerQuestion.OfRange(scaling.Compare(predicate.Op, wanted), width, signed: true);
                answered = context.TryEvaluateIntegersChild(in node, 0, encodedType, length, in question, ref bits);
                break;
            }

            case Compute.EncodedPredicateKind.Between
                when TryDouble(predicate.Literal, out double low) && TryDouble(predicate.High, out double high):
            {
                Compute.IntegerRange range = double.IsNaN(low) || double.IsNaN(high)
                    ? new Compute.IntegerRange(1, 0, Negated: false)
                    : new Compute.IntegerRange(
                        predicate.LowInclusive ? scaling.AtOrPast(low) : scaling.Past(low),
                        (predicate.HighInclusive ? scaling.Past(high) : scaling.AtOrPast(high)) - 1,
                        Negated: false);
                Compute.IntegerQuestion question = Compute.IntegerQuestion.OfRange(range, width, signed: true);
                answered = context.TryEvaluateIntegersChild(in node, 0, encodedType, length, in question, ref bits);
                break;
            }

            case Compute.EncodedPredicateKind.In:
                answered = TryMembers(context, in node, encodedType, length, in scaling, predicate.Values, width, ref bits);
                break;

            default:
                return false;
        }

        if (!answered)
        {
            return false;
        }

        if (metadata.HasPatches)
        {
            ReadOnlySpan<byte> values = Patches.Read(
                context, in node, 1, dtype, metadata.Patches, length, retained: false, Id, out Patches patches);
            for (int i = 0; i < patches.Count; i++)
            {
                double value = scaling.Single
                    ? BinaryPrimitives.ReadSingleLittleEndian(values[(i * sizeof(float))..])
                    : BinaryPrimitives.ReadDoubleLittleEndian(values[(i * sizeof(double))..]);
                bits.Set(patches.GetPosition(i), Holds(predicate, value));
            }
        }

        return true;
    }

    /// <summary>The members of a set as the integers that decode to each, asked of the integers' encoding.</summary>
    private static bool TryMembers(
        ArrayDecodeContext context, in ArrayNode node, DType encodedType, int length, in Scaling scaling,
        ReadOnlySpan<Expressions.FilterLiteral> members, int width, ref Compute.PredicateBits bits)
    {
        Int128[] rented = ArrayPool<Int128>.Shared.Rent(Math.Max(1, members.Length * MostIntegersAMember));
        try
        {
            int count = 0;
            foreach (Expressions.FilterLiteral member in members)
            {
                if (!TryDouble(member, out double value))
                {
                    return false;
                }

                if (double.IsNaN(value))
                {
                    continue;
                }

                Int128 first = scaling.AtOrPast(value);
                Int128 end = scaling.Past(value);
                if (end - first > MostIntegersAMember)
                {
                    return false;
                }

                for (Int128 integer = first; integer < end; integer++)
                {
                    rented[count++] = integer;
                }
            }

            Span<Int128> integers = rented.AsSpan(0, count);
            integers.Sort();
            int distinct = 0;
            for (int i = 0; i < integers.Length; i++)
            {
                if (distinct == 0 || integers[i] != integers[distinct - 1])
                {
                    integers[distinct++] = integers[i];
                }
            }

            Compute.IntegerQuestion question = distinct == 0
                ? Compute.IntegerQuestion.Settled(width, selected: false)
                : Compute.IntegerQuestion.OfMembers(integers[..distinct], width, signed: true);
            return context.TryEvaluateIntegersChild(in node, 0, encodedType, length, in question, ref bits);
        }
        finally
        {
            ArrayPool<Int128>.Shared.Return(rented);
        }
    }

    /// <summary>The most integers one member of a set may decode from before the set declines.</summary>
    private const int MostIntegersAMember = 4;

    /// <summary>A literal a float column compares as a double, as the comparison kernels read it; false for another kind.</summary>
    private static bool TryDouble(Expressions.FilterLiteral literal, out double value)
    {
        switch (literal.Kind)
        {
            case Expressions.FilterLiteralKind.Float:
                value = literal.FloatValue;
                return true;
            case Expressions.FilterLiteralKind.Signed:
                value = literal.SignedValue;
                return true;
            case Expressions.FilterLiteralKind.Unsigned:
                value = literal.UnsignedValue;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    /// <summary>Whether an exception's float satisfies the predicate, under IEEE 754 as the comparison kernels judge it.</summary>
    private static bool Holds(Compute.EncodedPredicate predicate, double value)
    {
        switch (predicate.Kind)
        {
            case Compute.EncodedPredicateKind.Compare:
                TryDouble(predicate.Literal, out double wanted);
                return predicate.Op switch
                {
                    Expressions.ComparisonOp.Equal => value == wanted,
                    Expressions.ComparisonOp.NotEqual => value != wanted,
                    Expressions.ComparisonOp.Less => value < wanted,
                    Expressions.ComparisonOp.LessOrEqual => value <= wanted,
                    Expressions.ComparisonOp.Greater => value > wanted,
                    _ => value >= wanted,
                };

            case Compute.EncodedPredicateKind.Between:
                TryDouble(predicate.Literal, out double low);
                TryDouble(predicate.High, out double high);
                return (predicate.LowInclusive ? value >= low : value > low) && (predicate.HighInclusive ? value <= high : value < high);

            default:
                foreach (Expressions.FilterLiteral member in predicate.Values)
                {
                    if (TryDouble(member, out double candidate) && value == candidate)
                    {
                        return true;
                    }
                }

                return false;
        }
    }

    /// <summary>A node's scaling: its exponents, checked against their tables, and the decode of one integer.</summary>
    private readonly struct Scaling
    {
        private Scaling(bool single, int exponentE, int exponentF)
        {
            Single = single;
            ExponentE = exponentE;
            ExponentF = exponentF;
        }

        /// <summary>Whether the floats are single precision, encoded as <see cref="int"/>.</summary>
        internal bool Single { get; }

        internal int ExponentE { get; }

        internal int ExponentF { get; }

        /// <summary>The float integer <paramref name="encoded"/> decodes to, by the decode's own arithmetic, widened.</summary>
        internal double Decode(Int128 encoded) => Single
            ? (double)((int)encoded * AlpTables.F10Single[ExponentF] * AlpTables.If10Single[ExponentE])
            : (long)encoded * AlpTables.F10Double[ExponentF] * AlpTables.If10Double[ExponentE];

        /// <summary>The first integer that decodes at <paramref name="value"/> or past it; one past the greatest when none does.</summary>
        internal Int128 AtOrPast(double value) => First(value, past: false);

        /// <summary>The first integer that decodes past <paramref name="value"/>; one past the greatest when none does.</summary>
        internal Int128 Past(double value) => First(value, past: true);

        /// <summary>The integers <c>float op wanted</c> selects, the decode never decreasing.</summary>
        internal Compute.IntegerRange Compare(Expressions.ComparisonOp op, double wanted)
        {
            if (double.IsNaN(wanted))
            {
                return new Compute.IntegerRange(1, 0, Negated: op == Expressions.ComparisonOp.NotEqual);
            }

            Int128 least = Single ? int.MinValue : long.MinValue;
            Int128 greatest = Single ? int.MaxValue : long.MaxValue;
            return op switch
            {
                Expressions.ComparisonOp.Equal => new Compute.IntegerRange(AtOrPast(wanted), Past(wanted) - 1, Negated: false),
                Expressions.ComparisonOp.NotEqual => new Compute.IntegerRange(AtOrPast(wanted), Past(wanted) - 1, Negated: true),
                Expressions.ComparisonOp.Less => new Compute.IntegerRange(least, AtOrPast(wanted) - 1, Negated: false),
                Expressions.ComparisonOp.LessOrEqual => new Compute.IntegerRange(least, Past(wanted) - 1, Negated: false),
                Expressions.ComparisonOp.Greater => new Compute.IntegerRange(Past(wanted), greatest, Negated: false),
                _ => new Compute.IntegerRange(AtOrPast(wanted), greatest, Negated: false),
            };
        }

        /// <summary>The first integer whose decode is at or past <paramref name="value"/>, or past it, by halves over the encoded type.</summary>
        private Int128 First(double value, bool past)
        {
            Int128 low = Single ? int.MinValue : long.MinValue;
            Int128 high = (Single ? (Int128)int.MaxValue : long.MaxValue) + 1;
            while (low < high)
            {
                Int128 middle = low + ((high - low) >> 1);
                double decoded = Decode(middle);
                if (past ? decoded > value : decoded >= value)
                {
                    high = middle;
                }
                else
                {
                    low = middle + 1;
                }
            }

            return low;
        }

        /// <summary>The node's scaling, checked as a decode checks it.</summary>
        /// <exception cref="VortexFormatException">The node is not ALP of floats, or an exponent is past its table.</exception>
        internal static Scaling Of(in ArrayNode node, DType dtype, out AlpMetadata metadata)
        {
            ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
            metadata = AlpMetadata.Read(node.Metadata);

            // `DType::Primitive(F32 | F64, n)`, and nothing else, is accepted upstream.
            if (dtype.Kind != DTypeKind.Primitive ||
                dtype.PType is not (PType.F32 or PType.F64))
            {
                CompressedThrow.Format($"{Id} decodes f32 or f64; this node's dtype is {dtype}.");
            }

            bool single = dtype.PType == PType.F32;
            int exponentE = CheckExponent(
                metadata.ExponentE, single ? AlpTables.If10Single.Length : AlpTables.If10Double.Length, "exp_e");
            int exponentF = CheckExponent(
                metadata.ExponentF, single ? AlpTables.F10Single.Length : AlpTables.F10Double.Length, "exp_f");

            int expectedChildren = !metadata.HasPatches
                ? 1
                : metadata.Patches.HasChunkOffsets ? 4 : 3;
            ArrayDecodeContext.RequireChildCount(node.ChildCount, expectedChildren, Id);
            return new Scaling(single, exponentE, exponentF);
        }
    }

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted, bool selective, int start, int count)
    {
        Scaling scaling = Scaling.Of(in node, dtype, out AlpMetadata metadata);
        bool isSingle = scaling.Single;
        PType encodedPType = isSingle ? PType.I32 : PType.I64;
        int width = isSingle ? sizeof(float) : sizeof(double);
        int exponentE = scaling.ExponentE;
        int exponentF = scaling.ExponentF;

        DType encodedType = context.Types.Primitive(encodedPType, dtype.Nullability);
        bool whole = !selective && start == 0 && count == length;
        int encodedIndex = selective
            ? context.DecodeChildSelected(in node, 0, encodedType, length, wanted)
            : whole
                ? context.DecodeChild(in node, 0, encodedType, length)
                : context.DecodeChildRange(in node, 0, encodedType, length, start, count);
        int produced = count;
        CanonicalNode encoded = CanonicalSupport.RequirePrimitiveChild(
            context, encodedIndex, encodedPType, produced, Id + " encoded");

        int total = ArrayDecodeContext.CheckedMultiply(produced, width, Id + " values");
        // Uninitialized: both DecodeSingle and DecodeDouble write all `produced` values, and
        // `produced == 0` means `total == 0`, so there is no uncovered case. Patches only overwrite.
        VortexBuffer output = CanonicalSupport.AllocateUninitialized(
            context, total, width, out Span<byte> destination);
        if (produced != 0)
        {
            ReadOnlySpan<byte> source = encoded.Values.Span;
            if (isSingle)
            {
                AlpTables.DecodeSingle(
                    MemoryMarshal.Cast<byte, int>(source)[..produced],
                    MemoryMarshal.Cast<byte, float>(destination),
                    exponentE,
                    exponentF);
            }
            else
            {
                AlpTables.DecodeDouble(
                    MemoryMarshal.Cast<byte, long>(source)[..produced],
                    MemoryMarshal.Cast<byte, double>(destination),
                    exponentE,
                    exponentF);
            }
        }

        if (metadata.HasPatches)
        {
            ApplyPatches(context, in node, dtype, length, in metadata, width, destination, wanted, selective, start, count);
        }

        return context.Canonical.AddPrimitive(dtype, produced, encoded.Validity, dtype.PType, output);
    }

    /// <summary>
    /// Overwrites the rows the encoder could not represent with the values it stored verbatim.
    /// </summary>
    /// <remarks>
    /// A patch overwrites its row outright rather than combining with what the decode wrote there,
    /// so the patches are applied after the scaling, over the whole array, the selection or the
    /// range alike. Anything short of the whole array reads the whole patch set, decoded once for
    /// every visit of the node; the patch values are floats at the array's own dtype, the originals
    /// the encoder could not represent, not encoded integers.
    /// </remarks>
    private static void ApplyPatches(
        ArrayDecodeContext context,
        in ArrayNode node,
        DType dtype,
        int length,
        in AlpMetadata metadata,
        int width,
        Span<byte> destination,
        ReadOnlySpan<int> wanted,
        bool selective,
        int start,
        int count)
    {
        bool whole = !selective && start == 0 && count == length;
        ReadOnlySpan<byte> source = Patches.Read(
            context, in node, 1, dtype, metadata.Patches, length, retained: !whole, Id, out Patches patches);
        if (selective)
        {
            Patches.ApplySelected(in patches, source, width, wanted, destination);
            return;
        }

        if (whole)
        {
            patches.ApplyAll(source, width, destination);
            return;
        }

        Patches.ApplyRange(in patches, source, width, start, count, destination);
    }

    /// <summary>
    /// Bounds a file-supplied exponent by the length of the table it indexes; nothing else keeps
    /// the lookup in memory.
    /// </summary>
    private static int CheckExponent(uint value, int tableLength, string name)
    {
        if (value >= (uint)tableLength)
        {
            CompressedThrow.Format(
                $"{Id} declares {name} = {value}; the scaling table has {tableLength} entries.");
        }

        return (int)value;
    }
}
