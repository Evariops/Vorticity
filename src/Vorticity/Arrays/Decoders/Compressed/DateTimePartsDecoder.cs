// vortex.datetimeparts - vortex-datetime-parts-0.86.1/src/array.rs `deserialize` and
// src/canonical.rs `decode_to_temporal`.
//
// A timestamp split into three integer columns so each compresses on its own terms: `days` has low
// cardinality and bit-packs, `seconds` is bounded by 86400, and `subseconds` is often constant.
// Recomposition is one multiply-accumulate per part:
//
//     value = days * 86400 * divisor + seconds * divisor + subseconds
//
// where the divisor is the number of sub-second units per second, read from the extension dtype's
// OWN metadata rather than from this node: the parts carry no unit, so a file whose dtype says
// milliseconds and whose parts were built for microseconds is indistinguishable from a correct one.
// That is a class II hint (docs/08-semantics.md §5) - it cannot corrupt memory, only values.
//
// Three details that are easy to get subtly wrong, all of them visible in the reference:
//
//   * VALIDITY LIVES IN `days`. `seconds` and `subseconds` are deserialized NonNullable whatever
//     the array's own nullability is, and the extension's validity is its storage's.
//   * THE ARITHMETIC WRAPS. Upstream widens with num_traits' `as_()`, which is Rust's `as` cast,
//     and multiplies i64 in release mode - so a hostile file produces a wrong timestamp, never a
//     panic. IntegerKernels.WidenScaled/AddWidenScaled reproduce that exactly (CreateTruncating and
//     `unchecked`), because saturating here would make us disagree with the reference on the very
//     inputs a fuzzer finds first.
//   * TimeUnit::Days HAS NO DIVISOR. Upstream panics on it
//     ("cannot decode into TimeUnit::D"); a panic is not an option for a reader of untrusted input,
//     so it is a format error here.
using System;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>Decodes <c>vortex.datetimeparts</c> back into a canonical timestamp extension.</summary>
public sealed class DateTimePartsDecoder : ArrayDecoder
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
            context, in node, 0, "days", metadata.DaysPType, dtype.Nullability, length);
        CanonicalNode seconds = DecodePart(
            context, in node, 1, "seconds", metadata.SecondsPType, Nullability.NonNullable, length);
        CanonicalNode subseconds = DecodePart(
            context, in node, 2, "subseconds", metadata.SubsecondsPType, Nullability.NonNullable, length);

        int storageIndex = length == 0
            ? context.Canonical.AddPrimitive(
                storageDType, 0, days.Validity, PType.I64, VortexBuffer.Empty)
            : Recompose(context, storageDType, length, divisor, days, seconds, subseconds);

        return context.Canonical.AddExtension(dtype, length, storageIndex);
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

        // PERF-AUDIT-v2.md R1. UNINITIALIZED, because the fused pass below assigns every element
        // exactly once -- it opens with `=`, not `+=`, which is precisely what the three-pass form
        // could not do. At a million rows that is 8 MB of memset in front of 8 MB of stores.
        VortexBuffer output = CompressedValues.AllocateUninitialized(
            context, total, sizeof(long), Id, out Span<byte> destination);
        Span<long> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(destination);

        // ONE PASS, NOT THREE. The three-pass form -- a write then two read-modify-writes, each
        // monomorphic on one part -- walked the 8 MB output three times and touched ~40 MB of
        // traffic for 24 MB of data. Measured by short-circuiting the whole recomposition on
        // `datetimeparts` at a million rows: it is **93,2 %** of that scan (1 900 us against 130),
        // on an axis that reads **1.18**, slower than the reference.
        //
        // The three types are resolved before the loop, nested the way `RowKernels.Gather` resolves
        // its codes, so the loop that runs is monomorphic in all three. Only the shapes a file
        // actually uses are instantiated, and the corpus has exactly one: I64 days, I32 seconds,
        // I32 subseconds.
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
        int length)
    {
        DType childType = context.Types.Primitive(ptype, nullability);
        int index = context.DecodeChild(in node, childIndex, childType, length);
        CanonicalNode child = context.Canonical.GetNode(index);

        if (child.Kind != CanonicalKind.Primitive)
        {
            CompressedThrow.ChildKind(Id, name, child.Kind, "a Primitive");
        }

        if (child.PType != ptype)
        {
            CompressedThrow.Format(
                $"{Id}'s {name} child decoded as {child.PType.Name()}; {ptype.Name()} was declared.");
        }

        if (child.Length != length)
        {
            CompressedThrow.ChildLength(Id, name, child.Length, length);
        }

        // The kernels index `length` elements out of this buffer; a short one is a file defect, not
        // a reason to read past the end.
        CanonicalSupport.RequireExactBuffer(child.Values, length, ptype.ByteWidth(), $"{Id} {name}");
        return child;
    }

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
