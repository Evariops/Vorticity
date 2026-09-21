using System;

namespace Vorticity;

/// <summary>The appends of a text column, <c>ColumnBuilder&lt;string&gt;</c> and <c>ColumnBuilder&lt;string?&gt;</c> alike.</summary>
/// <remarks>
/// Generic over the text type, and so apart from <see cref="ColumnBuilderExtensions"/> whose
/// numeric appends have the same signatures: the nullability of a type argument is invariant, and a
/// block declared over <c>ColumnBuilder&lt;string&gt;</c> would warn on every nullable text column.
/// Only <see cref="string"/> satisfies the constraint among the types a column maps to.
/// </remarks>
public static class TextColumnBuilderExtensions
{
    extension<TText>(ColumnBuilder<TText> b)
        where TText : IComparable<string?>?
    {
        /// <summary>Appends UTF-8 bytes, without transcoding.</summary>
        public void Append(ReadOnlySpan<byte> utf8) => ColumnBuilderExtensions.Text(b.Store).Append(utf8);

        /// <summary>Appends text, transcoded to UTF-8.</summary>
        public void Append(ReadOnlySpan<char> text) => ColumnBuilderExtensions.Text(b.Store).Append(text);

        /// <summary>Appends a value formatted as UTF-8 straight into the column: a number, a date, a Guid.</summary>
        public void Append<TValue>(TValue value)
            where TValue : IUtf8SpanFormattable => ColumnBuilderExtensions.Text(b.Store).Append(value);

        /// <summary>A span to write one value's UTF-8 bytes into, of exactly <paramref name="sizeHint"/> bytes when it is positive; <c>Commit</c> appends it.</summary>
        public Span<byte> GetSpan(int sizeHint = 0) => ColumnBuilderExtensions.Text(b.Store).GetSpan(sizeHint);

        /// <summary>Appends the value of <paramref name="length"/> bytes written into the last span.</summary>
        public void Commit(int length) => ColumnBuilderExtensions.Text(b.Store).Commit(length);

        /// <summary>Appends a null, to a nullable column.</summary>
        public void AppendNull() => ColumnBuilderExtensions.Text(b.Store).AppendNulls(1);

        /// <summary>Appends <paramref name="count"/> nulls, to a nullable column.</summary>
        public void AppendNulls(int count) => ColumnBuilderExtensions.Text(b.Store).AppendNulls(count);
    }
}
