// Every sentinel byte the format has, in one place.
//
// They live here rather than inline at each call site because the single easiest way to get this
// format wrong is to invert a sentinel that must not be inverted. Reading the six functions
// together makes the rule visible: `descending` touches the EMPTY and NON-EMPTY variable-width
// sentinels and nothing else. The fixed-width pair and both null sentinels are chosen by
// `nullsFirst` alone.
//
// Transcribed from vortex-row/src/codec.rs at 0.86.1 (`varlen_null_sentinel`,
// `varlen_empty_sentinel`, `varlen_non_empty_sentinel`, `RowSortField::null_sentinel`,
// `RowSortField::non_null_sentinel`, `child_canonical_null_byte`).
using Vorticity.Types;

namespace Vorticity.RowEncoding;

/// <summary>The leading bytes that classify a value before any value byte is compared.</summary>
internal static class RowSentinels
{
    /// <summary>
    /// The fixed-width non-null sentinel. Always <c>0x01</c>: both null choices (<c>0x00</c> and
    /// <c>0x02</c>) sit on either side of it, which is what lets null placement move without
    /// moving the non-null values relative to each other.
    /// </summary>
    internal const byte FixedNonNull = 0x01;

    /// <summary>The fixed-width null sentinel, never inverted by <c>descending</c>.</summary>
    /// <param name="field">The column's options.</param>
    /// <returns><c>0x00</c> when nulls sort first, <c>0x02</c> when they sort last.</returns>
    internal static byte FixedNull(RowSortField field) => field.NullsFirst ? (byte)0x00 : (byte)0x02;

    /// <summary>The variable-width null sentinel, never inverted by <c>descending</c>.</summary>
    /// <param name="field">The column's options.</param>
    /// <returns><c>0x00</c> when nulls sort first, <c>0xFF</c> when they sort last.</returns>
    internal static byte VarNull(RowSortField field) => field.NullsFirst ? (byte)0x00 : (byte)0xFF;

    /// <summary>The variable-width empty-value sentinel.</summary>
    /// <param name="field">The column's options.</param>
    /// <returns><c>0x01</c>, or its complement <c>0xFE</c> when descending.</returns>
    internal static byte VarEmpty(RowSortField field) => field.Descending ? (byte)0xFE : (byte)0x01;

    /// <summary>The variable-width non-empty-value sentinel.</summary>
    /// <param name="field">The column's options.</param>
    /// <returns><c>0x02</c>, or its complement <c>0xFD</c> when descending.</returns>
    internal static byte VarNonEmpty(RowSortField field) => field.Descending ? (byte)0xFD : (byte)0x02;

    /// <summary>
    /// The single byte a child contributes when its parent struct or fixed-size list row is null.
    /// </summary>
    /// <remarks>
    /// Chosen by the CHILD's dtype but with the PARENT's (inherited) field, because option
    /// inheritance is identity: a nested field uses its root column's <see cref="RowSortField"/>
    /// unchanged.
    /// </remarks>
    /// <param name="childDType">The child's dtype.</param>
    /// <param name="field">The inherited options.</param>
    /// <returns>The variable-width null sentinel for Utf8/Binary, the fixed-width one otherwise.</returns>
    internal static byte ChildCanonicalNull(DType childDType, RowSortField field) =>
        childDType.Kind is DTypeKind.Utf8 or DTypeKind.Binary
            ? VarNull(field)
            : FixedNull(field);
}
