using Vorticity.Types;

namespace Vorticity.RowEncoding;

/// <summary>
/// The leading bytes that classify a value before any value byte is compared. Gathered here so the
/// rule stays visible: <c>descending</c> inverts the empty and non-empty variable-width sentinels
/// and nothing else, while both null sentinels and the fixed-width pair follow <c>nullsFirst</c>.
/// </summary>
internal static class RowSentinels
{
    /// <summary>
    /// The fixed-width non-null sentinel. Both null choices sit on either side of it, so null
    /// placement moves without moving the non-null values relative to each other.
    /// </summary>
    internal const byte FixedNonNull = 0x01;

    /// <summary>The fixed-width null sentinel, never inverted by <c>descending</c>.</summary>
    internal static byte FixedNull(RowSortField field) => field.NullsFirst ? (byte)0x00 : (byte)0x02;

    /// <summary>The variable-width null sentinel, never inverted by <c>descending</c>.</summary>
    internal static byte VarNull(RowSortField field) => field.NullsFirst ? (byte)0x00 : (byte)0xFF;

    /// <summary>The variable-width empty-value sentinel.</summary>
    internal static byte VarEmpty(RowSortField field) => field.Descending ? (byte)0xFE : (byte)0x01;

    /// <summary>The variable-width non-empty-value sentinel.</summary>
    internal static byte VarNonEmpty(RowSortField field) => field.Descending ? (byte)0xFD : (byte)0x02;

    /// <summary>
    /// The single byte a child contributes when its parent struct or fixed-size list row is null.
    /// Chosen by the child's dtype but with the parent's field, since a nested field uses its root
    /// column's <see cref="RowSortField"/> unchanged.
    /// </summary>
    internal static byte ChildCanonicalNull(DType childDType, RowSortField field) =>
        childDType.Kind is DTypeKind.Utf8 or DTypeKind.Binary
            ? VarNull(field)
            : FixedNull(field);
}
