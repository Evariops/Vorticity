using System;
using System.Buffers;
using System.Numerics;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Decodes <c>vortex.datetimeparts</c> back into a canonical timestamp extension: a timestamp split
/// into days, seconds and subseconds parts, recomposed as
/// <c>days * 86400 * divisor + seconds * divisor + subseconds</c>, where the divisor is the number
/// of sub-second units per second taken from the extension dtype, since the parts themselves carry
/// no unit. The arithmetic wraps rather than saturating: a file whose parts were built for another
/// unit yields wrong timestamps instead of an error, which is what the format asks of a reader.
/// </summary>
internal sealed class DateTimePartsDecoder : ArrayDecoder
{
    /// <summary>The wire id.</summary>
    public const string Id = "vortex.datetimeparts";

    private const long SecondsPerDay = 86_400;

    /// <summary>The shared, stateless instance.</summary>
    public static readonly DateTimePartsDecoder Instance = new DateTimePartsDecoder();

    private DateTimePartsDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.datetimeparts"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.DateTimeParts;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false);
    }

    /// <summary>
    /// The selection goes into the three parts, and the recomposition runs over the rows that come
    /// back.
    /// </summary>
    /// <remarks>
    /// The recomposition is a per-row multiply-accumulate and dominates a scan of this encoding, so
    /// decoding the node whole to deliver a handful of rows pays it for every row instead. Nothing
    /// about the arithmetic changes: the three parts are positional, output row <c>i</c> needs row
    /// <c>i</c> of each part and nothing else, which is what makes the selection pushable.
    /// </remarks>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted, selective: true);
    }

    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecode => true;

    /// <inheritdoc/>
    public override bool EvaluatesWithoutFullDecode => true;

    /// <summary>
    /// Answers a comparison or a range of the timestamps on their parts, never recomposing one: a timestamp is its days,
    /// then its seconds, then its subseconds, in that order, so it lies in a range when its days lie strictly inside the
    /// range's, or on the range's first day past its time of day, or on its last day short of it. Each of those is a
    /// question each part's encoding answers in its own patterns, and the answers are joined a word at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order holds while the seconds are less than a day and the subseconds less than a second, and while no day
    /// recomposes past the timestamp's range, which the decode would wrap: those are asked first, and a node that a
    /// writer did not make so declines. A set declines: its members would each be a question.
    /// </para>
    /// <para>
    /// A question a time of day settles is not asked: a range from midnight needs no seconds on its first day, one to
    /// the last subsecond of a day none on its last.
    /// </para>
    /// </remarks>
    /// <inheritdoc/>
    public override bool TryEvaluate(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, Compute.EncodedPredicate predicate,
        ref Compute.PredicateBits bits)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 3, Id);
        CanonicalSupport.RequireKind(dtype, DTypeKind.Extension, Id);
        long divisor = SubsecondsPerSecond(dtype, dtype.StorageType);
        DateTimePartsMetadata metadata = DateTimePartsMetadata.Read(node.Metadata);
        RequireIntegerPart(metadata.DaysPType, "days");
        RequireIntegerPart(metadata.SecondsPType, "seconds");
        RequireIntegerPart(metadata.SubsecondsPType, "subseconds");
        if (!Compute.IntegerQuestion.TryOf(predicate, PType.I64, out Compute.IntegerQuestion question)
            || question.Kind == Compute.QuestionKind.Members)
        {
            return false;
        }

        DType days = context.Types.Primitive(metadata.DaysPType, dtype.Nullability);
        DType seconds = context.Types.Primitive(metadata.SecondsPType, Nullability.NonNullable);
        DType subseconds = context.Types.Primitive(metadata.SubsecondsPType, Nullability.NonNullable);
        if (question.Kind != Compute.QuestionKind.Range)
        {
            // Every row or none: the days' own answer, which carries the nulls.
            Compute.IntegerQuestion settled = Compute.IntegerQuestion.Settled(
                metadata.DaysPType.ByteWidth() * 8, question.Kind == Compute.QuestionKind.All);
            return context.TryEvaluateIntegersChild(in node, 0, days, length, in settled, ref bits);
        }

        // The range as values: a range cut to the timestamps' does not wrap.
        Int128 low = (long)question.Low;
        Int128 high = low + question.Span;
        if (high > long.MaxValue)
        {
            return false;
        }

        long day = SecondsPerDay * divisor;
        int words = Compute.PredicateBits.WordsOf(length);
        ulong[] rented = ArrayPool<ulong>.Shared.Rent(Math.Max(1, words * Scratches));
        try
        {
            Span<ulong> scratch = rented.AsSpan(0, words * Scratches);
            Span<ulong> known = scratch.Slice(0, words);
            Span<ulong> part = scratch.Slice(words, words);
            Span<ulong> answer = scratch.Slice(2 * words, words);
            Span<ulong> ofDay = scratch.Slice(3 * words, words);
            Span<ulong> within = scratch.Slice(4 * words, words);

            // The parts the order holds for: seconds within a day, subseconds within a second, days that do not wrap.
            if (!AskAll(context, in node, 1, seconds, length, 0, SecondsPerDay - 1, part, known)
                || !AskAll(context, in node, 2, subseconds, length, 0, divisor - 1, part, known)
                || !Ask(context, in node, 0, days, length, CeilDiv(long.MinValue, day), FloorDiv(long.MaxValue - (day - 1), day), part, known, out bool unknown)
                || !Covers(part, known, unknown, length))
            {
                return false;
            }

            (Int128 firstDay, Int128 firstTime) = DivMod(low, day);
            (Int128 lastDay, Int128 lastTime) = DivMod(high, day);
            answer.Clear();
            if (firstDay == lastDay)
            {
                // One day: its rows from the first time of day to the last.
                if (!Ask(context, in node, 0, days, length, firstDay, firstDay, answer, known, out unknown)
                    || !TimeOfDay(context, in node, seconds, subseconds, length, divisor, firstTime, from: true, within, part, known)
                    || !TimeOfDay(context, in node, seconds, subseconds, length, divisor, lastTime, from: false, ofDay, part, known))
                {
                    return false;
                }

                And(answer, within);
                And(answer, ofDay);
            }
            else
            {
                // The days strictly inside, then the first day past its time and the last day short of its own.
                if (!Ask(context, in node, 0, days, length, firstDay + 1, lastDay - 1, answer, known, out unknown)
                    || !Ask(context, in node, 0, days, length, firstDay, firstDay, ofDay, known, out _)
                    || !TimeOfDay(context, in node, seconds, subseconds, length, divisor, firstTime, from: true, within, part, known))
                {
                    return false;
                }

                And(ofDay, within);
                Or(answer, ofDay);
                if (!Ask(context, in node, 0, days, length, lastDay, lastDay, ofDay, known, out _)
                    || !TimeOfDay(context, in node, seconds, subseconds, length, divisor, lastTime, from: false, within, part, known))
                {
                    return false;
                }

                And(ofDay, within);
                Or(answer, ofDay);
            }

            answer.CopyTo(bits.Truth);
            bits.Trim();
            if (unknown)
            {
                // The days' nulls, which every answer on them shares: the last one asked left them in `known`.
                bits.ApplyKnown(known);
            }

            if (question.Negated)
            {
                bits.Invert();
            }

            return true;
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(rented);
        }
    }

    /// <summary>The bitmaps the parts' answers are joined in: the known rows, a part's answer, the answer, two more.</summary>
    private const int Scratches = 5;

    /// <summary>
    /// The rows whose time of day is at <paramref name="time"/> or past it (<paramref name="from"/>), or at it or before:
    /// the seconds strictly past, or the same seconds with the subseconds past, as two questions of the seconds and one of
    /// the subseconds; every row when the time is the day's first, or its last for the rows before it.
    /// </summary>
    private static bool TimeOfDay(
        ArrayDecodeContext context, in ArrayNode node, DType seconds, DType subseconds, int length, long divisor, Int128 time,
        bool from, Span<ulong> answer, Span<ulong> part, Span<ulong> known)
    {
        Int128 second = time / divisor;
        Int128 subsecond = time % divisor;
        if (from ? time == 0 : time == (SecondsPerDay * divisor) - 1)
        {
            answer.Fill(ulong.MaxValue);
            return true;
        }

        // The seconds strictly on the range's side, then the same second with the subseconds on it.
        bool asked = from
            ? Ask(context, in node, 1, seconds, length, second + 1, SecondsPerDay - 1, answer, known, out _)
            : Ask(context, in node, 1, seconds, length, 0, second - 1, answer, known, out _);
        if (!asked || !Ask(context, in node, 1, seconds, length, second, second, part, known, out _))
        {
            return false;
        }

        bool whole = from ? subsecond == 0 : subsecond == divisor - 1;
        if (!whole)
        {
            Span<ulong> same = part;
            ulong[] rented = ArrayPool<ulong>.Shared.Rent(Math.Max(1, part.Length));
            try
            {
                Span<ulong> ofSecond = rented.AsSpan(0, part.Length);
                if (!(from
                        ? Ask(context, in node, 2, subseconds, length, subsecond, divisor - 1, ofSecond, known, out _)
                        : Ask(context, in node, 2, subseconds, length, 0, subsecond, ofSecond, known, out _)))
                {
                    return false;
                }

                And(same, ofSecond);
            }
            finally
            {
                ArrayPool<ulong>.Shared.Return(rented);
            }
        }

        Or(answer, part);
        return true;
    }

    /// <summary>
    /// Asks part <paramref name="child"/> the rows whose value lies from <paramref name="low"/> to <paramref name="high"/>,
    /// into <paramref name="truth"/>, its known rows into <paramref name="known"/>.
    /// </summary>
    private static bool Ask(
        ArrayDecodeContext context, in ArrayNode node, int child, DType type, int length, Int128 low, Int128 high,
        Span<ulong> truth, Span<ulong> known, out bool unknown)
    {
        Compute.PredicateBits answer = new Compute.PredicateBits(truth, known, length);
        Compute.IntegerQuestion question = Compute.IntegerQuestion.OfRange(
            new Compute.IntegerRange(low, high, Negated: false), type.PType.ByteWidth() * 8, type.PType.IsSignedInteger());
        bool asked = context.TryEvaluateIntegersChild(in node, child, type, length, in question, ref answer);
        unknown = asked && answer.Unknown;
        return asked;
    }

    /// <summary>Whether every row of a part that has no nulls lies from <paramref name="low"/> to <paramref name="high"/>.</summary>
    private static bool AskAll(
        ArrayDecodeContext context, in ArrayNode node, int child, DType type, int length, Int128 low, Int128 high,
        Span<ulong> truth, Span<ulong> known) =>
        Ask(context, in node, child, type, length, low, high, truth, known, out bool unknown) && Covers(truth, known, unknown, length);

    /// <summary>Whether <paramref name="truth"/> holds every known row.</summary>
    private static bool Covers(ReadOnlySpan<ulong> truth, ReadOnlySpan<ulong> known, bool unknown, int length)
    {
        long selected = 0;
        long held = 0;
        for (int w = 0; w < truth.Length; w++)
        {
            selected += BitOperations.PopCount(truth[w]);
            held += unknown ? BitOperations.PopCount(known[w]) : 0;
        }

        return selected == (unknown ? held : length);
    }

    private static void And(Span<ulong> into, ReadOnlySpan<ulong> other)
    {
        for (int w = 0; w < into.Length; w++)
        {
            into[w] &= other[w];
        }
    }

    private static void Or(Span<ulong> into, ReadOnlySpan<ulong> other)
    {
        for (int w = 0; w < into.Length; w++)
        {
            into[w] |= other[w];
        }
    }

    /// <summary>The floor of <paramref name="value"/> over <paramref name="divisor"/>, and what it leaves, never negative.</summary>
    private static (Int128 Quotient, Int128 Remainder) DivMod(Int128 value, Int128 divisor)
    {
        Int128 quotient = FloorDiv(value, divisor);
        return (quotient, value - (quotient * divisor));
    }

    private static Int128 FloorDiv(Int128 value, Int128 divisor)
    {
        Int128 quotient = value / divisor;
        return value % divisor != 0 && (value < 0) != (divisor < 0) ? quotient - 1 : quotient;
    }

    private static Int128 CeilDiv(Int128 value, Int128 divisor) => -FloorDiv(-value, divisor);

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted, bool selective)
    {
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 3, Id);
        CanonicalSupport.RequireKind(dtype, DTypeKind.Extension, Id);

        DType storageDType = dtype.StorageType;
        long divisor = SubsecondsPerSecond(dtype, storageDType);

        DateTimePartsMetadata metadata = DateTimePartsMetadata.Read(node.Metadata);
        RequireIntegerPart(metadata.DaysPType, "days");
        RequireIntegerPart(metadata.SecondsPType, "seconds");
        RequireIntegerPart(metadata.SubsecondsPType, "subseconds");

        // The array's nullability rides on `days` alone; the other two are always non-nullable.
        CanonicalNode days = DecodePart(
            context, in node, 0, "days", metadata.DaysPType, dtype.Nullability, length,
            wanted, selective);
        CanonicalNode seconds = DecodePart(
            context, in node, 1, "seconds", metadata.SecondsPType, Nullability.NonNullable, length,
            wanted, selective);
        CanonicalNode subseconds = DecodePart(
            context, in node, 2, "subseconds", metadata.SubsecondsPType, Nullability.NonNullable,
            length, wanted, selective);

        int produced = selective ? wanted.Length : length;
        int storageIndex = produced == 0
            ? context.Canonical.AddPrimitive(
                storageDType, 0, days.Validity, PType.I64, VortexBuffer.Empty)
            : Recompose(context, storageDType, produced, divisor, days, seconds, subseconds);

        return context.Canonical.AddExtension(dtype, produced, storageIndex);
    }

    private static int Recompose(
        ArrayDecodeContext context,
        DType storageDType,
        int length,
        long divisor,
        CanonicalNode days,
        CanonicalNode seconds,
        CanonicalNode subseconds)
    {
        int total = ArrayDecodeContext.CheckedMultiply(length, sizeof(long), "DateTimeParts values");

        // Left uninitialized because the fused pass below assigns every element exactly once: it
        // opens with `=`, not `+=`, so zeroing the buffer first would be a wasted walk over it.
        VortexBuffer output = CompressedValues.AllocateUninitialized(
            context, total, sizeof(long), Id, out Span<byte> destination);
        Span<long> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(destination);

        // One pass, not three: splitting this into a write and two read-modify-writes, each
        // monomorphic on a single part, would walk the output once per part, and the recomposition
        // is the bulk of what a scan of this encoding costs. The three part types are resolved
        // before the loop instead, so the loop that runs is monomorphic in all three and only the
        // shapes a file actually uses are instantiated.
        IntegerKernels.Recompose(
            days.Values.Span, days.PType,
            seconds.Values.Span, seconds.PType,
            subseconds.Values.Span, subseconds.PType,
            values,
            unchecked(SecondsPerDay * divisor),
            divisor);

        return context.Canonical.AddPrimitive(
            storageDType, length, days.Validity, PType.I64, output);
    }

    /// <summary>
    /// Decodes one part and checks it is the primitive of the declared width and length.
    /// </summary>
    private static CanonicalNode DecodePart(
        ArrayDecodeContext context,
        in ArrayNode node,
        int childIndex,
        string name,
        PType ptype,
        Nullability nullability,
        int length,
        ReadOnlySpan<int> wanted,
        bool selective)
    {
        DType childType = context.Types.Primitive(ptype, nullability);
        int index = selective
            ? context.DecodeChildSelected(in node, childIndex, childType, length, wanted)
            : context.DecodeChild(in node, childIndex, childType, length);
        index = CanonicalSupport.ExpandIfConstant(context, index);
        CanonicalNode child = context.Canonical.GetNode(index);
        int produced = selective ? wanted.Length : length;

        if (child.Kind != CanonicalKind.Primitive)
        {
            CompressedThrow.ChildKind(Id, name, child.Kind, "a Primitive");
        }

        if (child.PType != ptype)
        {
            CompressedThrow.Format(
                $"{Id}'s {name} child decoded as {child.PType.Name()}; {ptype.Name()} was declared.");
        }

        if (child.Length != produced)
        {
            CompressedThrow.ChildLength(Id, name, child.Length, produced);
        }

        // The kernels index `produced` elements out of this buffer; a short one is a file defect,
        // not a reason to read past the end.
        CanonicalSupport.RequireExactBuffer(child.Values, produced, ptype.ByteWidth(), What(childIndex));
        return child;
    }

    /// <summary>
    /// Child <paramref name="childIndex"/> as a message names it: a constant, so that naming it
    /// costs nothing on a decode that never fails.
    /// </summary>
    private static string What(int childIndex) => childIndex switch
    {
        0 => Id + " days",
        1 => Id + " seconds",
        _ => Id + " subseconds",
    };

    /// <summary>
    /// The number of sub-second units in one second, for the timestamp unit this dtype declares.
    /// </summary>
    /// <param name="dtype">The extension dtype the node must produce.</param>
    /// <param name="storage">Its storage dtype, which must be <c>i64</c>.</param>
    /// <exception cref="VortexFormatException">
    /// The extension is not <c>vortex.timestamp</c>, or its unit is <c>Days</c>, which has no
    /// sub-second decomposition at all.
    /// </exception>
    private static long SubsecondsPerSecond(DType dtype, DType storage)
    {
        ReadOnlySpan<byte> id = dtype.ExtensionIdUtf8;
        ExtensionDTypeRegistry.RequireSupported(id);
        if (ExtensionDTypeRegistry.Resolve(id) != ExtensionKind.Timestamp)
        {
            CompressedThrow.Format(
                $"{Id} produces a timestamp; this node's dtype is the extension " +
                $"'{System.Text.Encoding.UTF8.GetString(id)}'.");
        }

        TimestampOptions options = ExtensionDTypeRegistry.ReadTimestamp(dtype.ExtensionMetadata, storage);
        return options.Unit switch
        {
            VortexTimeUnit.Nanoseconds => 1_000_000_000,
            VortexTimeUnit.Microseconds => 1_000_000,
            VortexTimeUnit.Milliseconds => 1_000,
            VortexTimeUnit.Seconds => 1,
            _ => CompressedThrow.Format<long>(
                $"{Id} cannot decode a timestamp in whole days: the encoding's seconds and " +
                "subseconds parts have no meaning at that unit."),
        };
    }

    private static void RequireIntegerPart(PType ptype, string name)
    {
        if (!ptype.IsInteger())
        {
            CompressedThrow.Format($"{Id}'s {name}_ptype is {ptype.Name()}; an integer is required.");
        }
    }
}
