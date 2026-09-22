using System;
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
