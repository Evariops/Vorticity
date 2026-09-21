using System;
using System.Numerics;

namespace Vorticity;

/// <summary>Rows being assembled column by column, in buffers from the session's pool, before a writer encodes them.</summary>
/// <remarks>
/// Reusable: a writer's <c>WriteAsync</c> encodes the rows and clears the builder. Not thread-safe.
/// Every column must hold the same number of rows when the builder is written.
/// </remarks>
public class ColumnsBuilder
{
    private protected ColumnsBuilder()
    {
    }

    /// <summary>The columns the builder holds.</summary>
    public VortexSchema Schema => throw ColumnsBuilderPending.Error();

    /// <summary>The rows the builder holds; the writer refuses a builder whose columns disagree.</summary>
    public int RowCount => throw ColumnsBuilderPending.Error();

    /// <summary>The builder of column <paramref name="index"/>.</summary>
    /// <typeparam name="T">A .NET type the column's dtype maps to; <c>T?</c> for a nullable column that receives nulls.</typeparam>
    /// <param name="index">The column's position in <see cref="Schema"/>.</param>
    /// <returns>The column builder.</returns>
    /// <exception cref="VortexSchemaException"><typeparamref name="T"/> does not fit the column, or is nullable over a non-nullable column.</exception>
    public ColumnBuilder<T> Column<T>(int index) => throw ColumnsBuilderPending.Error();

    /// <summary>The builder of the column named <paramref name="name"/>.</summary>
    /// <typeparam name="T">A .NET type the column's dtype maps to.</typeparam>
    /// <param name="name">The column's name.</param>
    /// <returns>The column builder.</returns>
    public ColumnBuilder<T> Column<T>(string name) => throw ColumnsBuilderPending.Error();

    /// <summary>The builder of the struct column <paramref name="index"/>, whose fields are columns in turn.</summary>
    /// <param name="index">The column's position in <see cref="Schema"/>.</param>
    /// <returns>The nested builder.</returns>
    public ColumnsBuilder Struct(int index) => throw ColumnsBuilderPending.Error();

    /// <summary>Appends one null row to a nested struct builder: every field gets a default value and the struct row is null.</summary>
    /// <exception cref="VortexSchemaException">The builder is not a nullable struct column.</exception>
    public void AppendNull() => throw ColumnsBuilderPending.Error();

    /// <summary>Empties every column, keeping the buffers for the next rows.</summary>
    public void Clear() => throw ColumnsBuilderPending.Error();
}

/// <summary>Rows being assembled as the columns of <typeparamref name="TRecord"/>; the generator adds a property per member.</summary>
/// <typeparam name="TRecord">The record the writer is typed by.</typeparam>
public sealed class ColumnsBuilder<TRecord> : ColumnsBuilder
    where TRecord : IVortexRecord<TRecord>
{
    internal ColumnsBuilder()
    {
    }

    /// <summary>The builder of the nested record held by member <paramref name="index"/>.</summary>
    /// <typeparam name="TNested">The nested record type.</typeparam>
    /// <param name="index">The member's position in the record.</param>
    /// <returns>The nested builder.</returns>
    public ColumnsBuilder<TNested> Struct<TNested>(int index)
        where TNested : IVortexRecord<TNested> => throw ColumnsBuilderPending.Error();
}

/// <summary>One column of a <see cref="ColumnsBuilder"/>; the appends come from <see cref="ColumnBuilderExtensions"/>.</summary>
/// <typeparam name="T">The column's .NET type.</typeparam>
public readonly struct ColumnBuilder<T>
{
    /// <summary>The values appended so far.</summary>
    public int Count => throw ColumnsBuilderPending.Error();
}

/// <summary>The appends of <see cref="ColumnBuilder{T}"/>, one family per .NET type the dtypes map to.</summary>
public static class ColumnBuilderExtensions
{
    extension<T>(ColumnBuilder<T> b)
        where T : unmanaged, IBinaryNumber<T>
    {
        /// <summary>A span to fill in place, of at least <paramref name="sizeHint"/> values; <c>Advance</c> commits what was written.</summary>
        public Span<T> GetSpan(int sizeHint = 0) => throw ColumnsBuilderPending.Error();

        /// <summary>Commits <paramref name="count"/> values written into the last span.</summary>
        public void Advance(int count) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends one value.</summary>
        public void Append(T value) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends values.</summary>
        public void Append(ReadOnlySpan<T> values) => throw ColumnsBuilderPending.Error();
    }

    extension<T>(ColumnBuilder<T?> b)
        where T : unmanaged, IBinaryNumber<T>
    {
        /// <summary>A span to fill in place with valid values; <c>Advance</c> commits them.</summary>
        public Span<T> GetSpan(int sizeHint = 0) => throw ColumnsBuilderPending.Error();

        /// <summary>Commits <paramref name="count"/> valid values written into the last span.</summary>
        public void Advance(int count) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends one valid value.</summary>
        public void Append(T value) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends one value or a null.</summary>
        public void Append(T? value) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends valid values.</summary>
        public void Append(ReadOnlySpan<T> values) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends values with their validity: bit <c>i % 64</c> of word <c>i / 64</c> for value <c>i</c>.</summary>
        public void Append(ReadOnlySpan<T> values, ReadOnlySpan<ulong> validity) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends a null.</summary>
        public void AppendNull() => throw ColumnsBuilderPending.Error();

        /// <summary>Appends <paramref name="count"/> nulls.</summary>
        public void AppendNulls(int count) => throw ColumnsBuilderPending.Error();
    }

    extension(ColumnBuilder<bool> b)
    {
        /// <summary>Appends one value.</summary>
        public void Append(bool value) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends values.</summary>
        public void Append(ReadOnlySpan<bool> values) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends <paramref name="count"/> values packed as bits, least significant first.</summary>
        public void AppendBits(ReadOnlySpan<ulong> bits, int count) => throw ColumnsBuilderPending.Error();
    }

    extension(ColumnBuilder<bool?> b)
    {
        /// <summary>Appends one value or a null.</summary>
        public void Append(bool? value) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends a null.</summary>
        public void AppendNull() => throw ColumnsBuilderPending.Error();
    }

    extension(ColumnBuilder<string> b)
    {
        /// <summary>Appends UTF-8 bytes, without transcoding.</summary>
        public void Append(ReadOnlySpan<byte> utf8) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends text, transcoded to UTF-8.</summary>
        public void Append(ReadOnlySpan<char> text) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends a value formatted as UTF-8 straight into the column: a number, a date, a Guid.</summary>
        public void Append<TValue>(TValue value)
            where TValue : IUtf8SpanFormattable => throw ColumnsBuilderPending.Error();

        /// <summary>A span to write one value's UTF-8 bytes into; <c>Commit</c> appends it.</summary>
        public Span<byte> GetSpan(int sizeHint = 0) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends the value of <paramref name="length"/> bytes written into the last span.</summary>
        public void Commit(int length) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends a null, to a nullable column.</summary>
        public void AppendNull() => throw ColumnsBuilderPending.Error();

        /// <summary>Appends <paramref name="count"/> nulls, to a nullable column.</summary>
        public void AppendNulls(int count) => throw ColumnsBuilderPending.Error();
    }

    extension(ColumnBuilder<ReadOnlyMemory<byte>> b)
    {
        /// <summary>Appends one value's bytes.</summary>
        public void Append(ReadOnlySpan<byte> bytes) => throw ColumnsBuilderPending.Error();

        /// <summary>A span to write one value into; <c>Commit</c> appends it.</summary>
        public Span<byte> GetSpan(int sizeHint = 0) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends the value of <paramref name="length"/> bytes written into the last span.</summary>
        public void Commit(int length) => throw ColumnsBuilderPending.Error();
    }

    extension(ColumnBuilder<ReadOnlyMemory<byte>?> b)
    {
        /// <summary>Appends one value's bytes.</summary>
        public void Append(ReadOnlySpan<byte> bytes) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends a null.</summary>
        public void AppendNull() => throw ColumnsBuilderPending.Error();
    }

    extension(ColumnBuilder<decimal> b)
    {
        /// <summary>Appends one value, which must fit the column's precision and scale exactly.</summary>
        public void Append(decimal value) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends values.</summary>
        public void Append(ReadOnlySpan<decimal> values) => throw ColumnsBuilderPending.Error();
    }

    extension(ColumnBuilder<decimal?> b)
    {
        /// <summary>Appends one value or a null.</summary>
        public void Append(decimal? value) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends a null.</summary>
        public void AppendNull() => throw ColumnsBuilderPending.Error();
    }

    extension(ColumnBuilder<VortexDecimal> b)
    {
        /// <summary>Appends one value, rescaled to the column's scale when that is exact.</summary>
        public void Append(VortexDecimal value) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends values.</summary>
        public void Append(ReadOnlySpan<VortexDecimal> values) => throw ColumnsBuilderPending.Error();
    }

    extension(ColumnBuilder<VortexDecimal?> b)
    {
        /// <summary>Appends one value or a null.</summary>
        public void Append(VortexDecimal? value) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends a null.</summary>
        public void AppendNull() => throw ColumnsBuilderPending.Error();
    }

    extension(ColumnBuilder<DateOnly> b)
    {
        /// <summary>Appends one date.</summary>
        public void Append(DateOnly value) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends dates.</summary>
        public void Append(ReadOnlySpan<DateOnly> values) => throw ColumnsBuilderPending.Error();
    }

    extension(ColumnBuilder<DateOnly?> b)
    {
        /// <summary>Appends one date or a null.</summary>
        public void Append(DateOnly? value) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends a null.</summary>
        public void AppendNull() => throw ColumnsBuilderPending.Error();
    }

    extension(ColumnBuilder<TimeOnly> b)
    {
        /// <summary>Appends one time, in the column's unit.</summary>
        public void Append(TimeOnly value) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends times.</summary>
        public void Append(ReadOnlySpan<TimeOnly> values) => throw ColumnsBuilderPending.Error();
    }

    extension(ColumnBuilder<TimeOnly?> b)
    {
        /// <summary>Appends one time or a null.</summary>
        public void Append(TimeOnly? value) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends a null.</summary>
        public void AppendNull() => throw ColumnsBuilderPending.Error();
    }

    extension(ColumnBuilder<DateTime> b)
    {
        /// <summary>Appends one instant, in the column's unit; a local time is converted for a UTC column.</summary>
        public void Append(DateTime value) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends instants.</summary>
        public void Append(ReadOnlySpan<DateTime> values) => throw ColumnsBuilderPending.Error();
    }

    extension(ColumnBuilder<DateTime?> b)
    {
        /// <summary>Appends one instant or a null.</summary>
        public void Append(DateTime? value) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends a null.</summary>
        public void AppendNull() => throw ColumnsBuilderPending.Error();
    }

    extension(ColumnBuilder<DateTimeOffset> b)
    {
        /// <summary>Appends one instant, stored in UTC.</summary>
        public void Append(DateTimeOffset value) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends instants.</summary>
        public void Append(ReadOnlySpan<DateTimeOffset> values) => throw ColumnsBuilderPending.Error();
    }

    extension(ColumnBuilder<DateTimeOffset?> b)
    {
        /// <summary>Appends one instant or a null.</summary>
        public void Append(DateTimeOffset? value) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends a null.</summary>
        public void AppendNull() => throw ColumnsBuilderPending.Error();
    }

    extension(ColumnBuilder<Guid> b)
    {
        /// <summary>Appends one uuid, stored in network order.</summary>
        public void Append(Guid value) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends uuids.</summary>
        public void Append(ReadOnlySpan<Guid> values) => throw ColumnsBuilderPending.Error();
    }

    extension(ColumnBuilder<Guid?> b)
    {
        /// <summary>Appends one uuid or a null.</summary>
        public void Append(Guid? value) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends a null.</summary>
        public void AppendNull() => throw ColumnsBuilderPending.Error();
    }

    extension<T>(ColumnBuilder<ReadOnlyMemory<T>> b)
    {
        /// <summary>The builder of every list's elements; those appended between <c>BeginList</c> and <c>EndList</c> form one list.</summary>
        public ColumnBuilder<T> Elements => throw ColumnsBuilderPending.Error();

        /// <summary>Opens a list: the elements appended until <c>EndList</c> belong to it.</summary>
        public void BeginList() => throw ColumnsBuilderPending.Error();

        /// <summary>Closes the open list.</summary>
        public void EndList() => throw ColumnsBuilderPending.Error();

        /// <summary>Appends one list, in one call.</summary>
        public void Append(ReadOnlySpan<T> list) => throw ColumnsBuilderPending.Error();

        /// <summary>Appends a null list, to a nullable column.</summary>
        public void AppendNull() => throw ColumnsBuilderPending.Error();
    }
}

/// <summary>The append of a column of a registered extension type.</summary>
public static class ExtensionColumnBuilderExtensions
{
    extension<T>(ColumnBuilder<T> b)
        where T : IVortexExtension<T>
    {
        /// <summary>Appends one value, written as its storage by the registered type.</summary>
        public void Append(T value) => throw ColumnsBuilderPending.Error();
    }
}

internal static class ColumnsBuilderPending
{
    internal static NotSupportedException Error() => new NotSupportedException("The column builders are not implemented yet.");
}
