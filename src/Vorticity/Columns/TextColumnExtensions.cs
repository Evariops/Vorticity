using System;

namespace Vorticity;

/// <summary>The accessors of a text column, <c>Column&lt;string&gt;</c> and <c>Column&lt;string?&gt;</c> alike.</summary>
/// <remarks>
/// Generic over the text type, and so apart from <see cref="ColumnExtensions"/> whose numeric
/// indexer has the same signature: the nullability of a type argument is invariant, and a block
/// declared over <c>Column&lt;string&gt;</c> would warn on every nullable text column. Only
/// <see cref="string"/> satisfies the constraint among the types a column maps to.
/// </remarks>
public static class TextColumnExtensions
{
    extension<TText>(Column<TText> column)
        where TText : IComparable<string?>?
    {
        /// <summary>The UTF-8 bytes of row <paramref name="index"/>, borrowed; empty for a null.</summary>
        public ReadOnlySpan<byte> this[int index] => ColumnData.Bytes(column.Arena, column.Node, index);

        /// <summary>Row <paramref name="index"/> as a <see cref="string"/>, which allocates; null for a null.</summary>
        /// <param name="index">A row.</param>
        /// <returns>The text.</returns>
        public string? GetString(int index) => ColumnData.String(column.Arena, column.Node, index);

        /// <summary>The UTF-8 byte length of row <paramref name="index"/>; 0 for a null.</summary>
        /// <param name="index">A row.</param>
        /// <returns>The length.</returns>
        public int GetLength(int index) => ColumnData.ByteLength(column.Arena, column.Node, index);

        /// <summary>Copies every row into <paramref name="destination"/> as a <see cref="string"/>, which allocates one per row; null for a null.</summary>
        /// <param name="destination">At least <c>Length</c> elements.</param>
        public void CopyTo(Span<TText> destination) => ColumnData.CopyStrings(column.Arena, column.Node, destination);
    }
}
