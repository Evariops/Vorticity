using System;
using System.Collections.Generic;
using Vorticity.Compute;
using Vorticity.Types;
using Vorticity.Types.Variants;

namespace Vorticity.Dataset;

/// <summary>
/// A layout value resolved against the dataset's schema: the top-level column its source lies in, the source's type, the
/// function it applies, and the type of the value it makes, which the key's encoding orders.
/// </summary>
internal sealed class LayoutColumn
{
    private LayoutColumn(LayoutValue value, int field, DType source, VortexType type, ValueFunction? function, VariantFieldExpr? read)
    {
        Value = value;
        Field = field;
        Source = source;
        Type = type;
        Function = function;
        Read = read;
    }

    /// <summary>The value declared.</summary>
    internal LayoutValue Value { get; }

    /// <summary>The top-level column the source lies in, by its place in the schema.</summary>
    internal int Field { get; }

    /// <summary>The source's type in the schema: a column's, or the variant column's for a key of one.</summary>
    internal DType Source { get; }

    /// <summary>The type of the value: the source's, or the class's a variant key is read in, nullable.</summary>
    internal VortexType Type { get; }

    /// <summary>The function the value applies to its source; null for the source itself.</summary>
    internal ValueFunction? Function { get; }

    /// <summary>The read of a variant key; null for a column's own values.</summary>
    internal VariantFieldExpr? Read { get; }

    /// <summary>Whether the value is a column's own values: no function, no variant key.</summary>
    internal bool IsColumn => Function is null && Read is null;

    /// <summary>
    /// <paramref name="value"/> resolved against <paramref name="schema"/>, as the layout's <paramref name="role"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The source is no column of the schema, a variant key of no variant column, a column of no order, or the function
    /// does not apply to it.
    /// </exception>
    internal static LayoutColumn Of(LayoutValue value, DType schema, string role)
    {
        if (value.Key is { } key)
        {
            int variant = schema.IndexOfField(value.SourcePath);
            if (variant < 0 || schema.GetField(variant).Kind != DTypeKind.Variant)
            {
                throw new ArgumentException(
                    $"The {role} {value} reads a key of '{value.SourcePath}', which is no variant column of the dataset's schema.", nameof(value));
            }

            VariantRead read = LayoutValue.ReadOf(value.Class);
            VariantFieldExpr path = new VariantFieldExpr(new Expressions.ColumnFieldExpr(value.SourcePath), [VariantStep.Of(key)], read);
            VortexType type = VariantFieldExpr.TypeOf(read);
            return new LayoutColumn(value, variant, schema.GetField(variant), type, FunctionOf(value, type, role), path);
        }

        DType source = Resolve(schema, value.SourcePath);
        if (!Orderable(source))
        {
            throw new ArgumentException(
                $"The {role} {value} reads '{value.SourcePath}', a column of {source.Kind}, which has no order a key keeps: a list, a map, a variant, a union or a decimal past 38 digits.",
                nameof(value));
        }

        VortexType columnType = VortexTypes.FromDType(source);
        return new LayoutColumn(value, ColumnPath.TopOf(schema, value.SourcePath), source, columnType, FunctionOf(value, columnType, role), null);
    }

    /// <summary>The function a value applies to a source of <paramref name="type"/>; null for the source itself.</summary>
    /// <exception cref="ArgumentException">The function does not apply to the source's type.</exception>
    private static ValueFunction? FunctionOf(LayoutValue value, VortexType type, string role)
    {
        try
        {
            return value.Function switch
            {
                LayoutFunction.Identity => null,
                LayoutFunction.Truncate => SymFunctions.Truncation(type, value.SourcePath, value.Unit, zone: null, offsets: false),
                LayoutFunction.InstantBucket => SymFunctions.Buckets(type, value.SourcePath, TimeSpan.FromTicks(value.Width)),
                LayoutFunction.IntegerBucket => SymFunctions.Buckets(type, value.SourcePath, value.Width),
                _ => SymFunctions.Buckets(type, value.SourcePath, value.FloatWidth),
            };
        }
        catch (Exception cause) when (cause is NotSupportedException or ArgumentException)
        {
            throw new ArgumentException($"The {role} {value} does not apply to its source, of {type}: {cause.Message}", nameof(value), cause);
        }
    }

    /// <summary>Whether a column of <paramref name="type"/> has an order a key's encoding keeps.</summary>
    private static bool Orderable(DType type) => type.Kind switch
    {
        DTypeKind.Null or DTypeKind.Bool or DTypeKind.Primitive or DTypeKind.Utf8 or DTypeKind.Binary => true,
        DTypeKind.Decimal => type.Precision <= 38,
        DTypeKind.Extension => Orderable(type.StorageType),
        DTypeKind.FixedSizeList => Orderable(type.ElementType),
        DTypeKind.Struct => Fields(type),
        _ => false,
    };

    private static bool Fields(DType type)
    {
        for (int i = 0; i < type.FieldCount; i++)
        {
            if (!Orderable(type.GetField(i)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The dtype of a <c>.</c>-separated path against a struct schema.</summary>
    /// <exception cref="ArgumentException">The path descends into no struct, or names no column.</exception>
    internal static DType Resolve(DType schema, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        DType at = schema;
        ReadOnlySpan<char> rest = path;
        foreach (Range segment in rest.Split('.'))
        {
            if (at.Kind != DTypeKind.Struct)
            {
                throw new ArgumentException($"'{path}' descends into a {at.Kind}, which has no fields.", nameof(path));
            }

            int index = ColumnPath.IndexOf(at, rest[segment]);
            if (index < 0)
            {
                throw new ArgumentException($"'{path}' names no column of the schema.", nameof(path));
            }

            at = at.GetField(index);
        }

        return at;
    }
}

/// <summary>
/// A dataset's layout resolved against its schema (<see cref="DatasetLayout"/>): its partition and the components of its
/// key, each where its source lies and what it makes, and the prefix and the organization it keeps.
/// </summary>
internal sealed class LayoutPlan
{
    private LayoutPlan(DatasetLayout layout, LayoutColumn? partition, LayoutColumn[] components)
    {
        Layout = layout;
        Partition = partition;
        Components = components;
    }

    /// <summary>The layout declared, as the header records it.</summary>
    internal DatasetLayout Layout { get; }

    /// <summary>The partition; null for one.</summary>
    internal LayoutColumn? Partition { get; }

    /// <summary>The key's components, the first first; empty for the order the rows arrive in.</summary>
    internal IReadOnlyList<LayoutColumn> Components { get; }

    /// <summary>The components each object indexes.</summary>
    internal int Prefix => Layout.Prefix;

    /// <summary>The organization the dataset keeps.</summary>
    internal LayoutOrganization Organization => Layout.Organized;

    /// <summary>Whether the layout orders the rows by a key.</summary>
    internal bool Keyed => Components.Count > 0;

    /// <summary>
    /// <paramref name="layout"/> resolved against <paramref name="schema"/>; null for no layout, or one that states neither a
    /// partition nor a key.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A value is not one the schema can make, the key holds more than <see cref="DatasetLayout.MostComponents"/>
    /// components or one twice, or the prefix is longer than the key.
    /// </exception>
    internal static LayoutPlan? Of(DatasetLayout? layout, DType schema)
    {
        if (layout is null || (layout.Partition is null && layout.SortKey.Count == 0))
        {
            return null;
        }

        if (schema.Kind != DTypeKind.Struct)
        {
            throw new ArgumentException("A dataset's layout reads the columns of a schema that is a struct.", nameof(schema));
        }

        if (layout.SortKey.Count > DatasetLayout.MostComponents)
        {
            throw new ArgumentException(
                $"The key holds {layout.SortKey.Count} components, past the {DatasetLayout.MostComponents} a key holds.", nameof(layout));
        }

        if (layout.IndexedPrefix is { } prefix && prefix > layout.SortKey.Count)
        {
            throw new ArgumentException(
                $"The indexed prefix holds {prefix} components, and the key {layout.SortKey.Count}.", nameof(layout));
        }

        LayoutColumn? partition = layout.Partition is { } declared ? LayoutColumn.Of(declared, schema, "partition") : null;
        LayoutColumn[] components = new LayoutColumn[layout.SortKey.Count];
        HashSet<LayoutValue> seen = [];
        for (int i = 0; i < components.Length; i++)
        {
            LayoutValue component = layout.SortKey[i] ?? throw new ArgumentException($"Component {i} of the key is null.", nameof(layout));
            if (!seen.Add(component))
            {
                throw new ArgumentException($"The key names {component} twice; the second orders nothing the first did not.", nameof(layout));
            }

            components[i] = LayoutColumn.Of(component, schema, "key component");
        }

        return new LayoutPlan(layout, partition, components);
    }

    /// <summary>Whether the top-level column <paramref name="column"/> is the source of the partition or of a component.</summary>
    internal bool Reads(string column)
    {
        if (Partition is { } partition && Top(partition.Value) == column)
        {
            return true;
        }

        foreach (LayoutColumn component in Components)
        {
            if (Top(component.Value) == column)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The top-level column a value's source lies in, by its name.</summary>
    private static string Top(LayoutValue value)
    {
        int dot = value.Key is null ? value.SourcePath.IndexOf('.', StringComparison.Ordinal) : -1;
        return dot < 0 ? value.SourcePath : value.SourcePath[..dot];
    }

    /// <summary>
    /// The paths of the key's components when each is a column's own values, as the sort key over columns takes
    /// them; null when one applies a function or reads a variant key.
    /// </summary>
    internal string[]? ColumnPaths()
    {
        string[] paths = new string[Components.Count];
        for (int i = 0; i < paths.Length; i++)
        {
            if (!Components[i].IsColumn)
            {
                return null;
            }

            paths[i] = Components[i].Value.SourcePath;
        }

        return paths.Length == 0 ? null : paths;
    }
}
