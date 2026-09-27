using System;
using System.Numerics;
using Vorticity.Writing;

namespace Vorticity;

/// <summary>Rows being assembled column by column, in buffers from the session's pool, before a writer encodes them.</summary>
/// <remarks>
/// Reusable: a writer's <c>WriteAsync</c> takes the rows and clears the builder, keeping its buffers.
/// Not thread-safe. Every column must hold the same number of rows when the builder is written.
/// The builders a writer hands out share the writer's rows: <c>Builder()</c> and
/// <c>Builder&lt;TRecord&gt;()</c> are two views of the same columns.
/// </remarks>
public class ColumnsBuilder
{
    private readonly int[]? _map;

    internal ColumnsBuilder(StructStore store, VortexSchema schema, int[]? map, VortexFileWriter? writer)
    {
        Store = store;
        Schema = schema;
        _map = map;
        Writer = writer;
    }

    /// <summary>The struct whose fields this builder fills: the writer's root, or a nested struct column.</summary>
    internal StructStore Store { get; }

    /// <summary>The writer whose rows this builder holds; null for a nested struct's builder.</summary>
    internal VortexFileWriter? Writer { get; }

    /// <summary>The columns the builder holds.</summary>
    public VortexSchema Schema { get; }

    /// <summary>The rows the builder holds; the writer refuses a builder whose columns disagree.</summary>
    public int RowCount => Store.Rows - Store.Committed;

    /// <summary>The builder of column <paramref name="index"/>.</summary>
    /// <typeparam name="T">A .NET type the column's dtype maps to; <c>T?</c> for a nullable column that receives nulls.</typeparam>
    /// <param name="index">The column's position in <see cref="Schema"/>.</param>
    /// <returns>The column builder.</returns>
    /// <exception cref="VortexSchemaException"><typeparamref name="T"/> does not fit the column, or is nullable over a non-nullable column.</exception>
    public ColumnBuilder<T> Column<T>(int index)
    {
        ColumnStore column = Child(index);
        return ReferenceEquals(column.Checked, typeof(T))
            ? new ColumnBuilder<T>(column.Leaf)
            : Checked<T>(column, $"Column '{Schema[index].Name}'");
    }

    /// <summary>The builder of the column named <paramref name="name"/>.</summary>
    /// <typeparam name="T">A .NET type the column's dtype maps to.</typeparam>
    /// <param name="name">The column's name.</param>
    /// <returns>The column builder.</returns>
    /// <exception cref="VortexSchemaException">No column has that name, or <typeparamref name="T"/> does not fit it.</exception>
    public ColumnBuilder<T> Column<T>(string name) => Column<T>(IndexOf(name));

    /// <summary>The builder of the struct column <paramref name="index"/>, whose fields are columns in turn.</summary>
    /// <param name="index">The column's position in <see cref="Schema"/>.</param>
    /// <returns>The nested builder.</returns>
    /// <exception cref="VortexSchemaException">The column is not a struct.</exception>
    public ColumnsBuilder Struct(int index)
    {
        StructStore nested = StructOf(index);
        return nested.Facade ??= new ColumnsBuilder(nested, VortexSchema.Create(nested.Type.Fields), null, null);
    }

    /// <summary>Appends one null row to a nested struct builder: every field gets a default value and the struct row is null.</summary>
    /// <exception cref="VortexSchemaException">The builder is not a nullable struct column.</exception>
    public void AppendNull() => Store.AppendNull();

    /// <summary>Empties every column, keeping the buffers for the next rows.</summary>
    public void Clear() => Store.Truncate(Store.Committed);

    /// <summary>The nested struct column <paramref name="index"/>.</summary>
    private protected StructStore StructOf(int index)
    {
        ColumnStore column = Child(index);
        while (column is ExtensionStore extension)
        {
            column = extension.Storage;
        }

        return column as StructStore
            ?? throw new VortexSchemaException($"Column '{Schema[index].Name}' is {Child(index).Type}, not a struct.");
    }

    private ColumnStore Child(int index)
    {
        if ((uint)index >= (uint)Schema.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The builder has {Schema.Count} columns.");
        }

        return Store.Children[_map is null ? index : _map[index]];
    }

    private int IndexOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int index = Schema.IndexOfName(name);
        return index >= 0 ? index : throw new VortexSchemaException($"The builder has no column '{name}'; its schema is {Schema}.");
    }

    /// <summary>The builder of <paramref name="column"/> as <typeparamref name="T"/>, once the type is found to fit it.</summary>
    internal static ColumnBuilder<T> Checked<T>(ColumnStore column, string what)
    {
        ClrShape shape = ClrShape.For<T>.Value;
        ClrFit.Require<T>(column.Type, what, column.Extensions);
        RequireNullability(shape, column.Type, what);
        RequireLeaf(shape, column.Leaf, what);
        column.Checked = typeof(T);
        return new ColumnBuilder<T>(column.Leaf);
    }

    private static void RequireNullability(ClrShape shape, VortexType type, string what)
    {
        if (shape.IsNullableValue && !type.IsNullable)
        {
            throw new VortexSchemaException(
                $"{what} is {type}, which is not nullable; ask for a builder of {ClrFit.Name(Nullable.GetUnderlyingType(shape.Type)!)} instead of {ClrFit.Name(shape.Type)}.");
        }

        if (shape.Kind == ClrKind.List && shape.Element is { } element && type.ElementType is { } elements)
        {
            RequireNullability(element, elements, what + "'s elements");
        }
    }

    private static void RequireLeaf(ClrShape shape, ColumnStore leaf, string what)
    {
        bool fits = shape.Kind switch
        {
            ClrKind.Bool => leaf is BoolStore,
            ClrKind.Signed or ClrKind.Unsigned or ClrKind.Float => leaf is FixedStore { IsDecimal: false },
            ClrKind.String or ClrKind.Binary => leaf is VarBinStore,
            ClrKind.Decimal or ClrKind.VortexDecimal => leaf is FixedStore { IsDecimal: true },
            ClrKind.DateOnly or ClrKind.TimeOnly or ClrKind.DateTime or ClrKind.DateTimeOffset => leaf is FixedStore { IsDecimal: false },
            ClrKind.Guid => leaf is FixedListStore { Size: 16, Elements: FixedStore { Width: 1 } },
            ClrKind.List => leaf is ListStore or FixedListStore,
            _ => leaf is FixedStore or FixedListStore { Elements: FixedStore },
        };

        if (!fits)
        {
            throw new VortexSchemaException($"{what} is stored as {leaf.Type}, which a {shape.Type.Name} is not written to.");
        }
    }
}

/// <summary>Rows being assembled as the columns of <typeparamref name="TRecord"/>; the generator adds a property per member.</summary>
/// <typeparam name="TRecord">The record the writer is typed by.</typeparam>
/// <remarks>Column <c>i</c> is the record's member <c>i</c>, bound to the writer's column of the same name.</remarks>
public sealed class ColumnsBuilder<TRecord> : ColumnsBuilder
    where TRecord : IVortexRecord<TRecord>
{
    internal ColumnsBuilder(StructStore store, int[]? map, VortexFileWriter? writer)
        : base(store, TRecord.Schema, map, writer)
    {
    }

    /// <summary>The builder of the nested record held by member <paramref name="index"/>.</summary>
    /// <typeparam name="TNested">The nested record type.</typeparam>
    /// <param name="index">The member's position in the record.</param>
    /// <returns>The nested builder.</returns>
    /// <exception cref="VortexSchemaException">The member is not a struct column, or a field of <typeparamref name="TNested"/> has no column in it.</exception>
    public ColumnsBuilder<TNested> Struct<TNested>(int index)
        where TNested : IVortexRecord<TNested>
    {
        StructStore nested = StructOf(index);
        if (nested.TypedFacade is ColumnsBuilder<TNested> typed)
        {
            return typed;
        }

        typed = new ColumnsBuilder<TNested>(nested, WriteBinding.Map(typeof(TNested), TNested.Schema, nested.Type.FieldArray), null);
        nested.TypedFacade = typed;
        return typed;
    }
}

/// <summary>One column of a <see cref="ColumnsBuilder"/>; the appends come from <see cref="ColumnBuilderExtensions"/>.</summary>
/// <typeparam name="T">The column's .NET type.</typeparam>
public readonly struct ColumnBuilder<T>
{
    internal ColumnBuilder(ColumnStore store) => Store = store;

    internal ColumnStore Store { get; }

    /// <summary>The values appended since the builder was last written or cleared.</summary>
    public int Count => Store.Count - Store.Committed;
}

/// <summary>The appends of <see cref="ColumnBuilder{T}"/>, one family per .NET type the dtypes map to.</summary>
public static class ColumnBuilderExtensions
{
    extension<T>(ColumnBuilder<T> b)
        where T : unmanaged, IBinaryNumber<T>
    {
        /// <summary>A span to fill in place, of exactly <paramref name="sizeHint"/> values when it is positive; <c>Advance</c> commits what was written.</summary>
        public Span<T> GetSpan(int sizeHint = 0) => Fixed(b.Store).GetSpan<T>(sizeHint);

        /// <summary>Commits <paramref name="count"/> values written into the last span.</summary>
        public void Advance(int count) => Fixed(b.Store).Advance(count);

        /// <summary>Appends one value.</summary>
        public void Append(T value) => Fixed(b.Store).Append(value);

        /// <summary>Appends values.</summary>
        public void Append(ReadOnlySpan<T> values) => Fixed(b.Store).Append(values);
    }

    extension<T>(ColumnBuilder<T?> b)
        where T : unmanaged, IBinaryNumber<T>
    {
        /// <summary>A span to fill in place with valid values; <c>Advance</c> commits them.</summary>
        public Span<T> GetSpan(int sizeHint = 0) => Fixed(b.Store).GetSpan<T>(sizeHint);

        /// <summary>Commits <paramref name="count"/> valid values written into the last span.</summary>
        public void Advance(int count) => Fixed(b.Store).Advance(count);

        /// <summary>Appends one valid value.</summary>
        public void Append(T value) => Fixed(b.Store).Append(value);

        /// <summary>Appends one value or a null.</summary>
        public void Append(T? value)
        {
            if (value is { } present)
            {
                Fixed(b.Store).Append(present);
            }
            else
            {
                Fixed(b.Store).AppendNulls(1);
            }
        }

        /// <summary>Appends valid values.</summary>
        public void Append(ReadOnlySpan<T> values) => Fixed(b.Store).Append(values);

        /// <summary>Appends values with their validity: bit <c>i % 64</c> of word <c>i / 64</c> for value <c>i</c>.</summary>
        public void Append(ReadOnlySpan<T> values, ReadOnlySpan<ulong> validity) => Fixed(b.Store).Append(values, validity);

        /// <summary>Appends a null.</summary>
        public void AppendNull() => Fixed(b.Store).AppendNulls(1);

        /// <summary>Appends <paramref name="count"/> nulls.</summary>
        public void AppendNulls(int count) => Fixed(b.Store).AppendNulls(count);
    }

    extension(ColumnBuilder<bool> b)
    {
        /// <summary>Appends one value.</summary>
        public void Append(bool value) => Bits(b.Store).Append(value);

        /// <summary>Appends values.</summary>
        public void Append(ReadOnlySpan<bool> values) => Bits(b.Store).Append(values);

        /// <summary>Appends <paramref name="count"/> values packed as bits, least significant first.</summary>
        public void AppendBits(ReadOnlySpan<ulong> bits, int count) => Bits(b.Store).AppendBits(bits, count);
    }

    extension(ColumnBuilder<bool?> b)
    {
        /// <summary>Appends one value or a null.</summary>
        public void Append(bool? value)
        {
            if (value is { } present)
            {
                Bits(b.Store).Append(present);
            }
            else
            {
                Bits(b.Store).AppendNull();
            }
        }

        /// <summary>Appends a null.</summary>
        public void AppendNull() => Bits(b.Store).AppendNull();
    }

    extension(ColumnBuilder<ReadOnlyMemory<byte>> b)
    {
        /// <summary>Appends one value's bytes.</summary>
        public void Append(ReadOnlySpan<byte> bytes) => Text(b.Store).Append(bytes);

        /// <summary>A span to write one value into, of exactly <paramref name="sizeHint"/> bytes when it is positive; <c>Commit</c> appends it.</summary>
        public Span<byte> GetSpan(int sizeHint = 0) => Text(b.Store).GetSpan(sizeHint);

        /// <summary>Appends the value of <paramref name="length"/> bytes written into the last span.</summary>
        public void Commit(int length) => Text(b.Store).Commit(length);
    }

    extension(ColumnBuilder<ReadOnlyMemory<byte>?> b)
    {
        /// <summary>Appends one value's bytes.</summary>
        public void Append(ReadOnlySpan<byte> bytes) => Text(b.Store).Append(bytes);

        /// <summary>A span to write one value into, of exactly <paramref name="sizeHint"/> bytes when it is positive; <c>Commit</c> appends it.</summary>
        public Span<byte> GetSpan(int sizeHint = 0) => Text(b.Store).GetSpan(sizeHint);

        /// <summary>Appends the value of <paramref name="length"/> bytes written into the last span.</summary>
        public void Commit(int length) => Text(b.Store).Commit(length);

        /// <summary>Appends a null.</summary>
        public void AppendNull() => Text(b.Store).AppendNulls(1);

        /// <summary>Appends <paramref name="count"/> nulls.</summary>
        public void AppendNulls(int count) => Text(b.Store).AppendNulls(count);
    }

    extension(ColumnBuilder<decimal> b)
    {
        /// <summary>Appends one value, which must fit the column's precision and scale exactly.</summary>
        public void Append(decimal value) => BuilderValues.AppendDecimal(Fixed(b.Store), value);

        /// <summary>Appends values.</summary>
        public void Append(ReadOnlySpan<decimal> values)
        {
            FixedStore store = Fixed(b.Store);
            foreach (decimal value in values)
            {
                BuilderValues.AppendDecimal(store, value);
            }
        }
    }

    extension(ColumnBuilder<decimal?> b)
    {
        /// <summary>Appends one value or a null.</summary>
        public void Append(decimal? value)
        {
            if (value is { } present)
            {
                BuilderValues.AppendDecimal(Fixed(b.Store), present);
            }
            else
            {
                Fixed(b.Store).AppendNulls(1);
            }
        }

        /// <summary>Appends a null.</summary>
        public void AppendNull() => Fixed(b.Store).AppendNulls(1);
    }

    extension(ColumnBuilder<VortexDecimal> b)
    {
        /// <summary>Appends one value, rescaled to the column's scale when that is exact.</summary>
        public void Append(VortexDecimal value) => BuilderValues.AppendDecimal(Fixed(b.Store), value);

        /// <summary>Appends values.</summary>
        public void Append(ReadOnlySpan<VortexDecimal> values)
        {
            FixedStore store = Fixed(b.Store);
            foreach (VortexDecimal value in values)
            {
                BuilderValues.AppendDecimal(store, value);
            }
        }
    }

    extension(ColumnBuilder<VortexDecimal?> b)
    {
        /// <summary>Appends one value or a null.</summary>
        public void Append(VortexDecimal? value)
        {
            if (value is { } present)
            {
                BuilderValues.AppendDecimal(Fixed(b.Store), present);
            }
            else
            {
                Fixed(b.Store).AppendNulls(1);
            }
        }

        /// <summary>Appends a null.</summary>
        public void AppendNull() => Fixed(b.Store).AppendNulls(1);
    }

    extension(ColumnBuilder<DateOnly> b)
    {
        /// <summary>Appends one date.</summary>
        public void Append(DateOnly value) => BuilderValues.AppendDate(Fixed(b.Store), value);

        /// <summary>Appends dates.</summary>
        public void Append(ReadOnlySpan<DateOnly> values)
        {
            FixedStore store = Fixed(b.Store);
            foreach (DateOnly value in values)
            {
                BuilderValues.AppendDate(store, value);
            }
        }
    }

    extension(ColumnBuilder<DateOnly?> b)
    {
        /// <summary>Appends one date or a null.</summary>
        public void Append(DateOnly? value)
        {
            if (value is { } present)
            {
                BuilderValues.AppendDate(Fixed(b.Store), present);
            }
            else
            {
                Fixed(b.Store).AppendNulls(1);
            }
        }

        /// <summary>Appends a null.</summary>
        public void AppendNull() => Fixed(b.Store).AppendNulls(1);
    }

    extension(ColumnBuilder<TimeOnly> b)
    {
        /// <summary>Appends one time, in the column's unit.</summary>
        public void Append(TimeOnly value) => BuilderValues.AppendTime(Fixed(b.Store), value);

        /// <summary>Appends times.</summary>
        public void Append(ReadOnlySpan<TimeOnly> values)
        {
            FixedStore store = Fixed(b.Store);
            foreach (TimeOnly value in values)
            {
                BuilderValues.AppendTime(store, value);
            }
        }
    }

    extension(ColumnBuilder<TimeOnly?> b)
    {
        /// <summary>Appends one time or a null.</summary>
        public void Append(TimeOnly? value)
        {
            if (value is { } present)
            {
                BuilderValues.AppendTime(Fixed(b.Store), present);
            }
            else
            {
                Fixed(b.Store).AppendNulls(1);
            }
        }

        /// <summary>Appends a null.</summary>
        public void AppendNull() => Fixed(b.Store).AppendNulls(1);
    }

    extension(ColumnBuilder<DateTime> b)
    {
        /// <summary>Appends one instant, in the column's unit; a local time is converted for a UTC column.</summary>
        public void Append(DateTime value) => BuilderValues.AppendTimestamp(Fixed(b.Store), value);

        /// <summary>Appends instants.</summary>
        public void Append(ReadOnlySpan<DateTime> values)
        {
            FixedStore store = Fixed(b.Store);
            foreach (DateTime value in values)
            {
                BuilderValues.AppendTimestamp(store, value);
            }
        }
    }

    extension(ColumnBuilder<DateTime?> b)
    {
        /// <summary>Appends one instant or a null.</summary>
        public void Append(DateTime? value)
        {
            if (value is { } present)
            {
                BuilderValues.AppendTimestamp(Fixed(b.Store), present);
            }
            else
            {
                Fixed(b.Store).AppendNulls(1);
            }
        }

        /// <summary>Appends a null.</summary>
        public void AppendNull() => Fixed(b.Store).AppendNulls(1);
    }

    extension(ColumnBuilder<DateTimeOffset> b)
    {
        /// <summary>Appends one instant, stored in UTC.</summary>
        public void Append(DateTimeOffset value) => BuilderValues.AppendTimestamp(Fixed(b.Store), value);

        /// <summary>Appends instants.</summary>
        public void Append(ReadOnlySpan<DateTimeOffset> values)
        {
            FixedStore store = Fixed(b.Store);
            foreach (DateTimeOffset value in values)
            {
                BuilderValues.AppendTimestamp(store, value);
            }
        }
    }

    extension(ColumnBuilder<DateTimeOffset?> b)
    {
        /// <summary>Appends one instant or a null.</summary>
        public void Append(DateTimeOffset? value)
        {
            if (value is { } present)
            {
                BuilderValues.AppendTimestamp(Fixed(b.Store), present);
            }
            else
            {
                Fixed(b.Store).AppendNulls(1);
            }
        }

        /// <summary>Appends a null.</summary>
        public void AppendNull() => Fixed(b.Store).AppendNulls(1);
    }

    extension(ColumnBuilder<Guid> b)
    {
        /// <summary>Appends one uuid, stored in network order.</summary>
        public void Append(Guid value) => Uuids(b.Store).AppendGuid(value);

        /// <summary>Appends uuids.</summary>
        public void Append(ReadOnlySpan<Guid> values)
        {
            FixedListStore store = Uuids(b.Store);
            foreach (Guid value in values)
            {
                store.AppendGuid(value);
            }
        }
    }

    extension(ColumnBuilder<Guid?> b)
    {
        /// <summary>Appends one uuid or a null.</summary>
        public void Append(Guid? value)
        {
            if (value is { } present)
            {
                Uuids(b.Store).AppendGuid(present);
            }
            else
            {
                Uuids(b.Store).AppendNull();
            }
        }

        /// <summary>Appends a null.</summary>
        public void AppendNull() => Uuids(b.Store).AppendNull();
    }

    extension<T>(ColumnBuilder<ReadOnlyMemory<T>> b)
    {
        /// <summary>The builder of every list's elements; those appended between <c>BeginList</c> and <c>EndList</c> form one list.</summary>
        public ColumnBuilder<T> Elements
        {
            get
            {
                ColumnStore elements = ElementsOf(b.Store);
                return ReferenceEquals(elements.Checked, typeof(T))
                    ? new ColumnBuilder<T>(elements.Leaf)
                    : ColumnsBuilder.Checked<T>(elements, "The list's elements");
            }
        }

        /// <summary>Opens a list: the elements appended until <c>EndList</c> belong to it.</summary>
        public void BeginList()
        {
            if (b.Store is ListStore list)
            {
                list.BeginList();
            }
            else
            {
                ((FixedListStore)b.Store).BeginList();
            }
        }

        /// <summary>Closes the open list.</summary>
        public void EndList()
        {
            if (b.Store is ListStore list)
            {
                list.EndList();
            }
            else
            {
                ((FixedListStore)b.Store).EndList();
            }
        }

        /// <summary>Appends one list, in one call; a list refused part way leaves nothing behind.</summary>
        public void Append(ReadOnlySpan<T> list)
        {
            ColumnStore store = b.Store;
            if (store is ListStore variable)
            {
                variable.BeginList();
                try
                {
                    ElementWriter.Append(variable.Elements.Leaf, list);
                    variable.EndList();
                }
                catch
                {
                    variable.AbortList();
                    throw;
                }
            }
            else
            {
                FixedListStore fixedSize = (FixedListStore)store;
                fixedSize.BeginList();
                try
                {
                    ElementWriter.Append(fixedSize.Elements.Leaf, list);
                    fixedSize.EndList();
                }
                catch
                {
                    fixedSize.AbortList();
                    throw;
                }
            }
        }

        /// <summary>Appends a null list, to a nullable column.</summary>
        public void AppendNull() => b.Store.AppendNull();
    }

    private static FixedStore Fixed(ColumnStore store) => (FixedStore)store;

    private static BoolStore Bits(ColumnStore store) => (BoolStore)store;

    internal static VarBinStore Text(ColumnStore store) => (VarBinStore)store;

    private static FixedListStore Uuids(ColumnStore store) => (FixedListStore)store;

    private static ColumnStore ElementsOf(ColumnStore store) =>
        store is ListStore list ? list.Elements : ((FixedListStore)store).Elements;
}

/// <summary>The append of a column of a registered extension type.</summary>
public static class ExtensionColumnBuilderExtensions
{
    extension<T>(ColumnBuilder<T> b)
        where T : IVortexExtension<T>
    {
        /// <summary>Appends one value, written as its storage by the registered type.</summary>
        public void Append(T value) => BuilderValues.AppendExtension(b.Store, in value);

        /// <summary>Appends a null, to a nullable column.</summary>
        public void AppendNull() => b.Store.AppendNull();
    }
}

/// <summary>Which column of a struct each member of a record writes, by name.</summary>
internal static class WriteBinding
{
    /// <summary>Per member of <paramref name="record"/>, its column among <paramref name="fields"/>; null when they are the same, in order.</summary>
    internal static int[]? Map(Type recordType, VortexSchema record, VortexField[] fields)
    {
        int[] map = new int[record.Count];
        bool identity = record.Count == fields.Length;
        ColumnNames names = new ColumnNames(fields);
        for (int i = 0; i < record.Count; i++)
        {
            map[i] = Match(recordType, record[i].Name, fields, names);
            identity &= map[i] == i;
        }

        return identity ? null : map;
    }

    private static int Match(Type recordType, string name, VortexField[] fields, ColumnNames names)
    {
        int exact = names.Exact(name);
        if (exact >= 0)
        {
            return exact;
        }

        (int found, int second) = names.Loose(name);
        if (second >= 0)
        {
            throw new VortexSchemaException(
                $"Member '{name}' of {recordType.Name} matches both '{fields[found].Name}' and '{fields[second].Name}' when case is ignored; name the column with [VortexColumn].");
        }

        return found >= 0
            ? found
            : throw new VortexSchemaException($"Member '{name}' of {recordType.Name} has no column to write; the struct is {VortexType.Struct(fields)}.");
    }
}
