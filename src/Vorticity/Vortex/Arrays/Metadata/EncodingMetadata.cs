using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Vorticity.Types;
using Vorticity.Types.Serialization;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// Metadata shapes shared across encodings: the empty-metadata validator and the bare
/// <c>ScalarValue</c> that <c>fastlanes.for</c> carries.
/// </summary>
internal static class EncodingMetadata
{
    /// <summary>
    /// Rejects a non-empty metadata payload for an encoding whose metadata must be empty.
    /// </summary>
    /// <param name="metadata">
    /// The raw <c>ArrayNode.metadata</c> / <c>Layout.metadata</c> bytes. An <em>absent</em>
    /// FlatBuffers field arrives here as an empty span and is accepted: absent and zero-length
    /// are the same thing for these encodings.
    /// </param>
    /// <param name="encodingId">The encoding id, used in the exception message.</param>
    /// <exception cref="VortexFormatException"><paramref name="metadata"/> is non-empty.</exception>
    /// <remarks>
    /// The list this guards is <c>vortex.null</c>, <c>vortex.primitive</c>,
    /// <c>vortex.varbinview</c>, <c>vortex.struct</c>, <c>vortex.chunked</c> (the <em>array</em>,
    /// not the layout — see <see cref="ChunkedLayoutMetadata"/>), <c>vortex.masked</c>,
    /// <c>vortex.fixed_size_list</c>, <c>vortex.ext</c>, <c>vortex.bytebool</c> and
    /// <c>vortex.zigzag</c>.
    /// <para>
    /// <c>vortex.constant</c> is deliberately not on that list. Its metadata is empty in every
    /// file a conformant writer produces, but the reference implementation ignores the field
    /// entirely and reads the scalar from buffer 0, so rejecting a stray byte here would reject a
    /// file others read happily.
    /// </para>
    /// </remarks>
    public static void RequireEmpty(ReadOnlySpan<byte> metadata, string encodingId)
    {
        if (!metadata.IsEmpty)
        {
            ThrowNotEmpty(encodingId, metadata.Length);
        }
    }

    /// <summary>
    /// Reads <c>fastlanes.for</c>'s metadata: a bare Protobuf <c>ScalarValue</c> carrying the
    /// frame-of-reference value, with no enclosing message and no dtype.
    /// </summary>
    /// <param name="metadata">The raw metadata bytes of the <c>fastlanes.for</c> node.</param>
    /// <param name="store">Store the value node is appended to.</param>
    /// <param name="arena">Arena for the dtype nested inside a <c>variant_value</c>, if any.</param>
    /// <returns>
    /// The untyped reference value. It is interpreted against the node's inherited DType later —
    /// a wire <c>ScalarValue</c> carries no type tag of its own.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="arena"/> is null.</exception>
    /// <exception cref="VortexFormatException">
    /// The payload is malformed, or empty. An empty payload decodes to
    /// <see cref="ScalarValueKind.Absent"/>, which is no frame-of-reference value at all, so it is
    /// rejected here instead of producing a silently wrong column.
    /// </exception>
    public static ScalarValue ReadReferenceScalar(
        ReadOnlySpan<byte> metadata, ScalarStore store, DTypeArena arena)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(arena);

        ScalarValue value = ScalarProtobuf.ReadValue(metadata, store, arena);
        if (value.IsAbsent)
        {
            ThrowAbsentReference();
        }

        return value;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNotEmpty(string encodingId, int length) =>
        throw new VortexFormatException(
            $"Encoding '{encodingId}' requires empty metadata; the node carries {length} bytes.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowAbsentReference() =>
        throw new VortexFormatException(
            "fastlanes.for metadata carries no ScalarValue case: the frame-of-reference value is " +
            "absent, which decodes to a null reference and is rejected.");
}
