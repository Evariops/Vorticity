using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Vorticity.Parquet.Metadata;
using Vorticity.Types;
using Vorticity.Types.Serialization;

namespace Vorticity.Parquet.Schema;

/// <summary>What a leaf's values become in the arena, decided once per column when the file opens.</summary>
internal enum LeafForm : byte
{
    /// <summary>BOOLEAN, a bitmap.</summary>
    Bool,

    /// <summary>A fixed-width value the page stores as the arena wants it: INT32, INT64, FLOAT, DOUBLE, and every annotation over them that keeps their bytes.</summary>
    Fixed,

    /// <summary>INT32 narrowed to <c>i8</c>, <c>i16</c>, <c>u8</c> or <c>u16</c>, each value checked.</summary>
    Narrowed,

    /// <summary>FIXED_LEN_BYTE_ARRAY(2) annotated <c>FLOAT16</c>: the same little-endian bytes as an <c>f16</c>.</summary>
    Float16,

    /// <summary>FIXED_LEN_BYTE_ARRAY, INT96 among them: the bytes as a fixed-size list of bytes.</summary>
    FixedBytes,

    /// <summary>BYTE_ARRAY: views over the page.</summary>
    Binary,

    /// <summary>BYTE_ARRAY annotated as text: views over the page, validated as UTF-8.</summary>
    Utf8,

    /// <summary>A DECIMAL on FIXED_LEN_BYTE_ARRAY or BYTE_ARRAY: big-endian bytes reversed and sign-extended.</summary>
    BigEndianDecimal,

    /// <summary><c>UNKNOWN</c>: every value null.</summary>
    Null,
}

/// <summary>The shape of a field once mapped to a Vortex type.</summary>
internal enum FieldShape : byte
{
    Leaf,
    Struct,
    List,
    Map,

    /// <summary>A VARIANT-annotated group, assembled as the struct of its fields and read as the variant they hold.</summary>
    Variant,
}

/// <summary>A leaf of the schema: a column, with its levels and the form its values take.</summary>
internal sealed class ParquetColumn
{
    internal int Ordinal { get; init; }

    /// <summary>The leaf's index in the footer's schema list.</summary>
    internal int Element { get; init; }

    /// <summary>The names from the root's child down to the leaf.</summary>
    internal required string[] Path { get; init; }

    /// <summary>The path's names, each as UTF-8, to check a chunk's <c>path_in_schema</c> against.</summary>
    internal required byte[][] PathUtf8 { get; init; }

    internal PhysicalType Physical { get; init; }

    /// <summary>A FIXED_LEN_BYTE_ARRAY's length; 12 for INT96; 0 otherwise.</summary>
    internal int TypeLength { get; init; }

    internal int MaxDefinitionLevel { get; init; }

    internal int MaxRepetitionLevel { get; init; }

    /// <summary>The annotation in force: the logical type, or the converted type's equivalent, or none.</summary>
    internal LogicalTypeInfo Logical { get; init; }

    /// <summary>Whether the leaf carries the <c>INTERVAL</c> converted type, which has no logical type.</summary>
    internal bool IsInterval { get; init; }

    internal ColumnOrderKind Order { get; init; }

    internal LeafForm Form { get; init; }

    /// <summary>The physical type of the arena's values: what a fixed or narrowed form is stored as.</summary>
    internal PType Storage { get; init; }

    /// <summary>The leaf's Vortex type, nullable when the leaf is optional.</summary>
    internal required VortexType Type { get; set; }

    internal string DottedPath => string.Join('.', Path);
}

/// <summary>A field of the Vortex schema a Parquet schema maps to, and the levels that define it.</summary>
internal sealed class ParquetField
{
    internal required string Name { get; init; }

    internal required VortexType Type { get; set; }

    internal FieldShape Shape { get; init; }

    /// <summary>A struct's fields; a list's element; a map's key and value.</summary>
    internal ParquetField[] Children { get; init; } = [];

    /// <summary>A leaf's column, or -1.</summary>
    internal int Column { get; init; } = -1;

    /// <summary>The definition level from which the field is present: not null.</summary>
    internal int DefinedAt { get; init; }

    /// <summary>A list's or a map's repetition level: where a new element starts within one value.</summary>
    internal int RepeatedAt { get; init; }

    /// <summary>The definition level from which a list or a map holds an element: not empty.</summary>
    internal int ElementsAt { get; init; }

    /// <summary>The first leaf under the field, and how many there are.</summary>
    internal int FirstLeaf { get; init; }

    internal int LeafCount { get; init; }

    /// <summary>
    /// Whether the field reads as not null though its file lets it be: a map's key some writers made
    /// optional, whose null the read refuses where it meets one.
    /// </summary>
    internal bool RefusesNull { get; set; }

    /// <summary>A variant's group: the struct of its metadata, its value and its typed value, as the file holds them.</summary>
    internal VortexType? Storage { get; init; }

    /// <summary>Why a variant cannot be read, which a read of it says and an open does not; null when it can.</summary>
    internal string? Refusal { get; init; }

    internal bool IsNullable => Type.IsNullable;

    /// <summary>The field's type with every variant under it its group's, the type of the columns the file holds.</summary>
    internal VortexType StorageType => Shape switch
    {
        FieldShape.Variant => Storage!,
        FieldShape.Struct when HasVariant(this) => Typed(VortexType.Struct([.. Children.Select(c => new VortexField(c.Name, c.StorageType))])),
        FieldShape.List when HasVariant(this) => Typed(VortexType.List(Children[0].StorageType)),
        FieldShape.Map when HasVariant(this) => Typed(VortexType.Map(Children[0].StorageType, Children[1].StorageType)),
        _ => Type,
    };

    private VortexType Typed(VortexType type) => IsNullable ? type.Nullable : type;

    private static bool HasVariant(ParquetField field) => field.Shape == FieldShape.Variant || field.Children.Any(HasVariant);
}

/// <summary>
/// A file's schema, compiled once at the open: the tree of the footer's depth-first list, each
/// node's definition and repetition levels, the leaves as columns, and the Vortex schema the file
/// reads as.
/// </summary>
/// <remarks>
/// Lists and maps are read by the standard's three-level structure and, for files written before it
/// was required, by its backward-compatibility rules: a repeated field outside a list or a map is a
/// required list of required elements, and a two-level list's element is found by the five rules of
/// <c>LogicalTypes.md</c>. An annotation this build does not know, or one on a physical type it does
/// not allow, is dropped with the column's order, and the column reads as its physical type.
/// </remarks>
internal sealed class ParquetSchema
{
    /// <summary>How deep the schema may nest.</summary>
    internal const int MaxDepth = 64;

    /// <summary>The extension id an INT96 reads as: its twelve bytes, since the standard gives them no epoch.</summary>
    internal const string Int96ExtensionId = "parquet.int96";

    /// <summary>The extension id an INTERVAL reads as: its three little-endian unsigned counts of months, days and milliseconds.</summary>
    internal const string IntervalExtensionId = "parquet.interval";

    /// <summary>The extension id a GEOMETRY reads as: its WKB as binary, its metadata the CRS's UTF-8, empty when the file sets none.</summary>
    internal const string GeometryExtensionId = "parquet.geometry";

    /// <summary>
    /// The extension id a GEOGRAPHY reads as: its WKB as binary, its metadata the edge algorithm's
    /// value in a byte, SPHERICAL's 0 where the file sets none, then the CRS's UTF-8.
    /// </summary>
    internal const string GeographyExtensionId = "parquet.geography";

    /// <summary>
    /// The key-value pair under which this package's writer keeps the Vortex schema a file was written
    /// from: its dtype's FlatBuffers bytes in base64.
    /// </summary>
    internal const string VortexSchemaKey = "vorticity.schema";

    private ParquetSchema(ParquetColumn[] columns, ParquetField[] fields, VortexSchema vortex)
    {
        Columns = columns;
        Fields = fields;
        Vortex = vortex;
    }

    /// <summary>The leaves, in the order a row group's column chunks follow.</summary>
    internal ParquetColumn[] Columns { get; }

    /// <summary>The root's fields, mapped.</summary>
    internal ParquetField[] Fields { get; }

    /// <summary>The Vortex schema the file reads as.</summary>
    internal VortexSchema Vortex { get; }

    /// <summary>
    /// Compiles a footer's schema list, with the footer's column orders and, where a CRS names one,
    /// the value of its key-value pair <paramref name="keys"/> gives.
    /// </summary>
    internal static ParquetSchema Compile(SchemaElement[] elements, ColumnOrderKind[] orders, Func<string, string?>? keys = null)
    {
        Node[] nodes = BuildTree(elements);

        // A column's order is found by its ordinal as its leaf is collected: the list is held to the
        // leaves before, since a list shorter than them would be read past its end.
        if (orders.Length != 0 && orders.Length != nodes[0].LeafCount)
        {
            ParquetThrow.Format($"The footer declares {orders.Length} column orders for {nodes[0].LeafCount} columns.");
        }

        List<ParquetColumn> columns = [];
        Collect(elements, nodes, 0, [], columns, orders, keys);

        ParquetColumn[] leaves = columns.ToArray();
        Mapper mapper = new(elements, nodes, leaves);
        ParquetField[] fields = mapper.Root();
        VortexField[] vortexFields = new VortexField[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            vortexFields[i] = new VortexField(fields[i].Name, fields[i].Type);
        }

        return new ParquetSchema(leaves, fields, VortexSchema.Create(vortexFields));
    }

    /// <summary>
    /// This schema with what the Vortex schema a file was written from says and Parquet cannot
    /// restored where the two agree, a top-level column at a time: a timestamp's zone, which Parquet
    /// keeps as UTC, and an extension Parquet has no annotation for, over the type the column reads
    /// as. A column that disagrees reads as Parquet says it is; a schema this build cannot read, or one
    /// of other fields, is ignored whole.
    /// </summary>
    /// <param name="written">The <see cref="VortexSchemaKey"/> pair's value, or null.</param>
    /// <remarks>It changes the schema in place: one compiled for the open that publishes it.</remarks>
    internal ParquetSchema Restored(string? written)
    {
        if (written is null)
        {
            return this;
        }

        VortexSchema stored;
        try
        {
            stored = VortexTypes.SchemaOf(DTypeFlatBuffers.Read(Convert.FromBase64String(written), new DTypeArena()));
        }
        catch (Exception e) when (e is FormatException or VortexException)
        {
            return this;
        }

        if (stored.Count != Fields.Length)
        {
            return this;
        }

        bool restored = false;
        for (int i = 0; i < Fields.Length; i++)
        {
            ParquetField field = Fields[i];
            if (field.Column < 0 || !string.Equals(stored[i].Name, field.Name, StringComparison.Ordinal) || Restore(stored[i].Type, field.Type) is not { } type)
            {
                continue;
            }

            field.Type = type;
            Columns[field.Column].Type = type;
            restored = true;
        }

        if (!restored)
        {
            return this;
        }

        VortexField[] vortexFields = new VortexField[Fields.Length];
        for (int i = 0; i < Fields.Length; i++)
        {
            vortexFields[i] = new VortexField(Fields[i].Name, Fields[i].Type);
        }

        return new ParquetSchema(Columns, Fields, VortexSchema.Create(vortexFields));
    }

    /// <summary>The type <paramref name="written"/> restores of a column Parquet reads as <paramref name="read"/>; null when it restores nothing.</summary>
    private static VortexType? Restore(VortexType written, VortexType read)
    {
        if (written.Equals(read) || written.Kind != VortexTypeKind.Extension || written.IsNullable != read.IsNullable)
        {
            return null;
        }

        // A timestamp in a zone of its own, which Parquet keeps as adjusted to UTC.
        if (written.ExtensionId == ExtensionIds.Timestamp)
        {
            return read.ExtensionId == ExtensionIds.Timestamp && read.TimeZone == "UTC" && written.TimeZone is not null
                && written.Unit == read.Unit && Equals(written.StorageType, read.StorageType)
                ? written
                : null;
        }

        // An extension Parquet has no annotation for, written as its storage.
        return written.ExtensionId is not (ExtensionIds.Date or ExtensionIds.Time or ExtensionIds.Uuid or IntervalExtensionId or Int96ExtensionId
                or GeometryExtensionId or GeographyExtensionId)
            && Equals(written.StorageType, read)
            ? written
            : null;
    }

    private struct Node
    {
        internal int FirstChild;
        internal int ChildCount;
        internal int DefinitionLevel;
        internal int RepetitionLevel;
        internal int FirstLeaf;
        internal int LeafCount;
    }

    /// <summary>
    /// Rebuilds the tree of the depth-first list: a group's children follow it, each with its own
    /// subtree, and the root's count must take the whole list. Levels accumulate down the tree.
    /// </summary>
    private static Node[] BuildTree(SchemaElement[] elements)
    {
        Node[] nodes = new Node[elements.Length];
        ref readonly SchemaElement root = ref elements[0];
        if (root.ChildCount < 0 || root.HasType)
        {
            ParquetThrow.Format("The schema's root is not a group.");
        }

        Span<(int Node, int Remaining)> stack = stackalloc (int, int)[MaxDepth + 1];
        int top = 0;
        stack[0] = (0, root.ChildCount);
        nodes[0].FirstChild = root.ChildCount > 0 ? 1 : -1;
        nodes[0].ChildCount = root.ChildCount;
        int next = 1;
        int leaves = 0;
        while (top >= 0)
        {
            if (stack[top].Remaining == 0)
            {
                int done = stack[top].Node;
                nodes[done].LeafCount = leaves - nodes[done].FirstLeaf;
                top--;
                continue;
            }

            stack[top].Remaining--;
            if (next >= elements.Length)
            {
                ParquetThrow.Format("The schema's groups declare more children than the list holds.");
            }

            int index = next++;
            ref readonly SchemaElement element = ref elements[index];
            int parent = stack[top].Node;
            if (!element.HasRepetition)
            {
                ParquetThrow.Format($"The schema element '{element.Name}' has no repetition.");
            }

            if (element.Repetition is < FieldRepetition.Required or > FieldRepetition.Repeated)
            {
                ParquetThrow.Format($"The schema element '{element.Name}' has an unknown repetition.");
            }

            ref Node node = ref nodes[index];
            node.DefinitionLevel = nodes[parent].DefinitionLevel + (element.Repetition == FieldRepetition.Required ? 0 : 1);
            node.RepetitionLevel = nodes[parent].RepetitionLevel + (element.Repetition == FieldRepetition.Repeated ? 1 : 0);
            node.FirstLeaf = leaves;
            if (element.ChildCount >= 0)
            {
                if (element.HasType)
                {
                    ParquetThrow.Format($"The schema element '{element.Name}' is both a group and a leaf.");
                }

                if (element.ChildCount == 0)
                {
                    ParquetThrow.Unsupported(element.Name, ParquetComponentKind.Feature, "A group with no field has no column to carry its rows.");
                }

                if (top + 1 > MaxDepth)
                {
                    ParquetThrow.Format($"The schema nests deeper than {MaxDepth} levels.");
                }

                node.FirstChild = next;
                node.ChildCount = element.ChildCount;
                stack[++top] = (index, element.ChildCount);
            }
            else
            {
                if (!element.HasType)
                {
                    ParquetThrow.Format($"The schema leaf '{element.Name}' has no type.");
                }

                node.FirstChild = -1;
                node.LeafCount = 1;
                leaves++;
            }
        }

        if (next != elements.Length)
        {
            ParquetThrow.Format("The schema list holds elements no group claims.");
        }

        nodes[0].FirstLeaf = 0;
        nodes[0].LeafCount = leaves;
        return nodes;
    }

    private static void Collect(SchemaElement[] elements, Node[] nodes, int index, List<string> path, List<ParquetColumn> columns, ColumnOrderKind[] orders, Func<string, string?>? keys)
    {
        ref readonly Node node = ref nodes[index];
        if (node.FirstChild < 0)
        {
            int ordinal = columns.Count;
            columns.Add(Leaf(elements[index], index, ordinal, [.. path], node, orders.Length == 0 ? ColumnOrderKind.Undefined : orders[ordinal], keys));
            return;
        }

        int child = node.FirstChild;
        for (int i = 0; i < node.ChildCount; i++)
        {
            path.Add(elements[child].Name);
            Collect(elements, nodes, child, path, columns, orders, keys);
            path.RemoveAt(path.Count - 1);
            child = Next(nodes, child);
        }
    }

    /// <summary>The sibling after <paramref name="index"/>: past its whole subtree.</summary>
    private static int Next(Node[] nodes, int index)
    {
        // A subtree of n elements: the node, then its children's subtrees, depth first.
        int end = index + 1;
        Span<int> pending = stackalloc int[MaxDepth + 1];
        int top = 0;
        pending[0] = nodes[index].FirstChild < 0 ? 0 : nodes[index].ChildCount;
        while (top >= 0)
        {
            if (pending[top] == 0)
            {
                top--;
                continue;
            }

            pending[top]--;
            int child = end++;
            if (nodes[child].FirstChild >= 0)
            {
                pending[++top] = nodes[child].ChildCount;
            }
        }

        return end;
    }

    /// <summary>A leaf's annotation, form and Vortex type, from its physical type and its annotations.</summary>
    private static ParquetColumn Leaf(in SchemaElement element, int index, int ordinal, string[] path, in Node node, ColumnOrderKind order, Func<string, string?>? keys)
    {
        PhysicalType physical = element.Type;
        if (physical is < PhysicalType.Boolean or > PhysicalType.FixedLenByteArray)
        {
            ParquetThrow.Unsupported(((int)physical).ToString(CultureInfo.InvariantCulture), ParquetComponentKind.PhysicalType);
        }

        int typeLength = 0;
        if (physical == PhysicalType.FixedLenByteArray)
        {
            if (element.TypeLength < 0)
            {
                ParquetThrow.Format($"The FIXED_LEN_BYTE_ARRAY column '{string.Join('.', path)}' has no length.");
            }

            typeLength = element.TypeLength;
        }
        else if (physical == PhysicalType.Int96)
        {
            typeLength = 12;
        }

        bool interval = false;
        LogicalTypeInfo logical = element.LogicalType;
        if (logical.Kind == LogicalTypeKind.None && element.ConvertedType >= 0)
        {
            logical = FromConverted(element, out interval);
        }

        bool nullable = element.Repetition == FieldRepetition.Optional;
        (LeafForm form, PType storage, VortexType? type) = Map(physical, typeLength, ref logical, interval, string.Join('.', path));
        if (type is not null && logical.Kind is LogicalTypeKind.Geometry or LogicalTypeKind.Geography
            && logical.Crs is { } crs && crs.StartsWith(ProjjsonPrefix, StringComparison.Ordinal) && keys?.Invoke(crs[ProjjsonPrefix.Length..]) is { } projjson)
        {
            // A CRS the file keeps in its key-value metadata: its PROJJSON itself, so that the type
            // stands without the file, as one written elsewhere does.
            LogicalTypeInfo embedded = logical;
            embedded.Crs = projjson;
            type = Geospatial(embedded);
        }

        if (type is null)
        {
            // An annotation on a physical type it does not allow: dropped, with the column's order,
            // as the standard asks of a reader that does not recognize it.
            logical = default;
            logical.Kind = LogicalTypeKind.Unrecognized;
            interval = false;
            order = ColumnOrderKind.Unrecognized;
            LogicalTypeInfo none = default;
            (form, storage, type) = Map(physical, typeLength, ref none, false, string.Join('.', path));
        }

        if (logical.Kind == LogicalTypeKind.Unrecognized)
        {
            order = ColumnOrderKind.Unrecognized;
        }

        byte[][] utf8 = new byte[path.Length][];
        for (int i = 0; i < path.Length; i++)
        {
            utf8[i] = Encoding.UTF8.GetBytes(path[i]);
        }

        return new ParquetColumn
        {
            Ordinal = ordinal,
            Element = index,
            Path = path,
            PathUtf8 = utf8,
            Physical = physical,
            TypeLength = typeLength,
            MaxDefinitionLevel = node.DefinitionLevel,
            MaxRepetitionLevel = node.RepetitionLevel,
            Logical = logical,
            IsInterval = interval,
            Order = order,
            Form = form,
            Storage = storage,
            Type = nullable || type!.Kind == VortexTypeKind.Null ? type!.Nullable : type!,
        };
    }

    /// <summary>The logical type a converted type stands for, by the standard's backward-compatibility tables.</summary>
    private static LogicalTypeInfo FromConverted(in SchemaElement element, out bool interval)
    {
        interval = false;
        LogicalTypeInfo info = default;
        info.VariantVersion = -1;
        info.EdgeAlgorithm = -1;
        switch ((ConvertedType)element.ConvertedType)
        {
            case ConvertedType.Utf8:
                info.Kind = LogicalTypeKind.String;
                break;
            case ConvertedType.Enum:
                info.Kind = LogicalTypeKind.Enum;
                break;
            case ConvertedType.Json:
                info.Kind = LogicalTypeKind.Json;
                break;
            case ConvertedType.Bson:
                info.Kind = LogicalTypeKind.Bson;
                break;
            case ConvertedType.Decimal:
                info.Kind = LogicalTypeKind.Decimal;
                info.Precision = element.Precision;
                info.Scale = element.Scale < 0 ? 0 : element.Scale;
                break;
            case ConvertedType.Date:
                info.Kind = LogicalTypeKind.Date;
                break;
            case ConvertedType.TimeMillis:
            case ConvertedType.TimeMicros:
                info.Kind = LogicalTypeKind.Time;
                info.IsAdjustedToUtc = true;
                info.Unit = element.ConvertedType == (int)ConvertedType.TimeMillis ? ParquetTimeUnit.Millis : ParquetTimeUnit.Micros;
                break;
            case ConvertedType.TimestampMillis:
            case ConvertedType.TimestampMicros:
                info.Kind = LogicalTypeKind.Timestamp;
                info.IsAdjustedToUtc = true;
                info.Unit = element.ConvertedType == (int)ConvertedType.TimestampMillis ? ParquetTimeUnit.Millis : ParquetTimeUnit.Micros;
                break;
            case ConvertedType.UInt8:
            case ConvertedType.UInt16:
            case ConvertedType.UInt32:
            case ConvertedType.UInt64:
                info.Kind = LogicalTypeKind.Integer;
                info.BitWidth = (byte)(8 << (element.ConvertedType - (int)ConvertedType.UInt8));
                info.IsSigned = false;
                break;
            case ConvertedType.Int8:
            case ConvertedType.Int16:
            case ConvertedType.Int32:
            case ConvertedType.Int64:
                info.Kind = LogicalTypeKind.Integer;
                info.BitWidth = (byte)(8 << (element.ConvertedType - (int)ConvertedType.Int8));
                info.IsSigned = true;
                break;
            case ConvertedType.Interval:
                interval = true;
                break;
            default:
                // LIST, MAP and MAP_KEY_VALUE annotate groups; a leaf ignores them, as it does a
                // number the enum does not define.
                break;
        }

        return info;
    }

    /// <summary>
    /// The form, the storage and the Vortex type of a leaf; a null type when the annotation does not
    /// fit the physical type, which the caller then drops.
    /// </summary>
    private static (LeafForm Form, PType Storage, VortexType? Type) Map(PhysicalType physical, int typeLength, ref LogicalTypeInfo logical, bool interval, string path)
    {
        if (logical.Kind == LogicalTypeKind.Unknown)
        {
            return (LeafForm.Null, default, VortexType.Null);
        }

        switch (physical)
        {
            case PhysicalType.Boolean:
                return logical.Kind is LogicalTypeKind.None or LogicalTypeKind.Unrecognized ? (LeafForm.Bool, default, VortexType.Bool) : default;

            case PhysicalType.Int32:
                switch (logical.Kind)
                {
                    case LogicalTypeKind.None:
                    case LogicalTypeKind.Unrecognized:
                        return (LeafForm.Fixed, PType.I32, VortexType.Int32);
                    case LogicalTypeKind.Integer:
                        return (logical.BitWidth, logical.IsSigned) switch
                        {
                            (8, true) => (LeafForm.Narrowed, PType.I8, VortexType.Int8),
                            (16, true) => (LeafForm.Narrowed, PType.I16, VortexType.Int16),
                            (32, true) => (LeafForm.Fixed, PType.I32, VortexType.Int32),
                            (8, false) => (LeafForm.Narrowed, PType.U8, VortexType.UInt8),
                            (16, false) => (LeafForm.Narrowed, PType.U16, VortexType.UInt16),
                            (32, false) => (LeafForm.Fixed, PType.U32, VortexType.UInt32),
                            _ => default,
                        };
                    case LogicalTypeKind.Decimal:
                        return Decimal(logical, 9, path, LeafForm.Fixed, PType.I32);
                    case LogicalTypeKind.Date:
                        return (LeafForm.Fixed, PType.I32, VortexType.Date);
                    case LogicalTypeKind.Time when logical.Unit == ParquetTimeUnit.Millis:
                        return (LeafForm.Fixed, PType.I32, VortexType.Time(TimeUnit.Milliseconds));
                    default:
                        return default;
                }

            case PhysicalType.Int64:
                switch (logical.Kind)
                {
                    case LogicalTypeKind.None:
                    case LogicalTypeKind.Unrecognized:
                        return (LeafForm.Fixed, PType.I64, VortexType.Int64);
                    case LogicalTypeKind.Integer when logical.BitWidth == 64:
                        return logical.IsSigned ? (LeafForm.Fixed, PType.I64, VortexType.Int64) : (LeafForm.Fixed, PType.U64, VortexType.UInt64);
                    case LogicalTypeKind.Decimal:
                        return Decimal(logical, 18, path, LeafForm.Fixed, PType.I64);
                    case LogicalTypeKind.Time when logical.Unit is ParquetTimeUnit.Micros or ParquetTimeUnit.Nanos:
                        return (LeafForm.Fixed, PType.I64, VortexType.Time(Unit(logical.Unit)));
                    case LogicalTypeKind.Timestamp:
                        return (LeafForm.Fixed, PType.I64, VortexType.Timestamp(Unit(logical.Unit), logical.IsAdjustedToUtc ? "UTC" : null));
                    default:
                        return default;
                }

            case PhysicalType.Int96:
                return logical.Kind is LogicalTypeKind.None or LogicalTypeKind.Unrecognized
                    ? (LeafForm.FixedBytes, PType.U8, VortexType.Extension(Int96ExtensionId, VortexType.FixedSizeList(VortexType.UInt8, 12)))
                    : default;

            case PhysicalType.Float:
                return logical.Kind is LogicalTypeKind.None or LogicalTypeKind.Unrecognized ? (LeafForm.Fixed, PType.F32, VortexType.Float32) : default;

            case PhysicalType.Double:
                return logical.Kind is LogicalTypeKind.None or LogicalTypeKind.Unrecognized ? (LeafForm.Fixed, PType.F64, VortexType.Float64) : default;

            case PhysicalType.ByteArray:
                switch (logical.Kind)
                {
                    case LogicalTypeKind.None:
                    case LogicalTypeKind.Unrecognized:
                    case LogicalTypeKind.Bson:
                        return (LeafForm.Binary, default, VortexType.Binary);
                    case LogicalTypeKind.Geometry:
                    case LogicalTypeKind.Geography:
                        return Geospatial(logical) is { } geospatial ? (LeafForm.Binary, default, geospatial) : default;
                    case LogicalTypeKind.String:
                    case LogicalTypeKind.Enum:
                    case LogicalTypeKind.Json:
                        return (LeafForm.Utf8, default, VortexType.Utf8);
                    case LogicalTypeKind.Decimal:
                        return Decimal(logical, VortexDecimalDigits, path, LeafForm.BigEndianDecimal, default);
                    default:
                        return default;
                }

            default:
                if (interval)
                {
                    return typeLength == 12
                        ? (LeafForm.FixedBytes, PType.U8, VortexType.Extension(IntervalExtensionId, VortexType.FixedSizeList(VortexType.UInt8, 12)))
                        : default;
                }

                switch (logical.Kind)
                {
                    case LogicalTypeKind.None:
                    case LogicalTypeKind.Unrecognized:
                        return (LeafForm.FixedBytes, PType.U8, VortexType.FixedSizeList(VortexType.UInt8, typeLength));
                    case LogicalTypeKind.Uuid when typeLength == 16:
                        return (LeafForm.FixedBytes, PType.U8, VortexType.Uuid);
                    case LogicalTypeKind.Float16 when typeLength == 2:
                        return (LeafForm.Float16, PType.F16, VortexType.Float16);
                    case LogicalTypeKind.Decimal:
                        return Decimal(logical, Math.Min(VortexDecimalDigits, FixedLengthDigits(typeLength)), path, LeafForm.BigEndianDecimal, default);
                    default:
                        return default;
                }
        }
    }

    /// <summary>
    /// A GEOMETRY's or GEOGRAPHY's type: its WKB as binary, under an extension whose metadata keeps
    /// the CRS and a GEOGRAPHY's edge algorithm; null for an algorithm the standard does not name,
    /// which drops the annotation.
    /// </summary>
    private static VortexType? Geospatial(in LogicalTypeInfo logical)
    {
        byte[] crs = logical.Crs is { } text ? Encoding.UTF8.GetBytes(text) : [];
        if (logical.Kind == LogicalTypeKind.Geometry)
        {
            return VortexType.Extension(GeometryExtensionId, VortexType.Binary, crs);
        }

        int algorithm = Math.Max(logical.EdgeAlgorithm, 0);
        return algorithm > MaxEdgeAlgorithm ? null : VortexType.Extension(GeographyExtensionId, VortexType.Binary, (byte[])[(byte)algorithm, .. crs]);
    }

    /// <summary>
    /// The extension id a FILE reads as: the struct of its fields, a reference to bytes inline or at
    /// a URI, which the standard names.
    /// </summary>
    internal const string FileExtensionId = "parquet.file";

    /// <summary>The prefix of a CRS that names a key of the file's key-value metadata holding its PROJJSON.</summary>
    internal const string ProjjsonPrefix = "projjson:";

    /// <summary>The last edge algorithm the standard names: KARNEY.</summary>
    internal const int MaxEdgeAlgorithm = 4;

    /// <summary>The most digits a Vortex decimal holds.</summary>
    private const int VortexDecimalDigits = 76;

    private static (LeafForm, PType, VortexType?) Decimal(in LogicalTypeInfo logical, int maxDigits, string path, LeafForm form, PType storage)
    {
        if (logical.Precision < 1 || logical.Scale < 0 || logical.Scale > logical.Precision)
        {
            ParquetThrow.Format($"The DECIMAL column '{path}' declares precision {logical.Precision} and scale {logical.Scale}.");
        }

        if (logical.Precision > maxDigits)
        {
            if (maxDigits == VortexDecimalDigits)
            {
                ParquetThrow.Unsupported($"DECIMAL({logical.Precision}, {logical.Scale})", ParquetComponentKind.LogicalType,
                    $"The column '{path}' has more digits than a Vortex decimal's {VortexDecimalDigits}.");
            }

            ParquetThrow.Format($"The DECIMAL column '{path}' declares {logical.Precision} digits, more than its physical type holds.");
        }

        return (form, storage, VortexType.Decimal(logical.Precision, logical.Scale));
    }

    /// <summary><c>floor(log10(2^(8n - 1) - 1))</c>: the digits a fixed length of <paramref name="length"/> bytes holds.</summary>
    private static int FixedLengthDigits(int length)
    {
        if (length <= 0)
        {
            return 0;
        }

        if (length > 32)
        {
            return int.MaxValue;
        }

        System.Numerics.BigInteger largest = (System.Numerics.BigInteger.One << (8 * length - 1)) - 1;
        return largest.IsZero ? 0 : (int)Math.Floor(System.Numerics.BigInteger.Log10(largest) + 1e-9);
    }

    private static TimeUnit Unit(ParquetTimeUnit unit) => unit switch
    {
        ParquetTimeUnit.Millis => TimeUnit.Milliseconds,
        ParquetTimeUnit.Micros => TimeUnit.Microseconds,
        _ => TimeUnit.Nanoseconds,
    };

    /// <summary>Maps the tree to Vortex fields, lists and maps by the standard's structures and rules.</summary>
    private sealed class Mapper(SchemaElement[] elements, Node[] nodes, ParquetColumn[] columns)
    {
        internal ParquetField[] Root() => Children(0);

        private ParquetField[] Children(int index)
        {
            ref readonly Node node = ref nodes[index];
            ParquetField[] fields = new ParquetField[node.ChildCount];
            int child = node.FirstChild;
            for (int i = 0; i < fields.Length; i++)
            {
                fields[i] = Field(child);
                child = Next(nodes, child);
            }

            return fields;
        }

        /// <summary>A field in its parent: a repeated one, outside a list or a map, is a required list of required elements.</summary>
        private ParquetField Field(int index)
        {
            ref readonly SchemaElement element = ref elements[index];
            if (element.Repetition == FieldRepetition.Repeated)
            {
                ParquetField item = Required(index, element.Name);
                return List(element.Name, item, nullable: false, definedAt: nodes[index].DefinitionLevel - 1, index, index);
            }

            return Type(index, element.Name, element.Repetition == FieldRepetition.Optional);
        }

        /// <summary>The node as a value of its own type, under <paramref name="name"/>.</summary>
        private ParquetField Type(int index, string name, bool nullable)
        {
            ref readonly SchemaElement element = ref elements[index];
            ref readonly Node node = ref nodes[index];
            if (node.FirstChild < 0)
            {
                ParquetColumn column = columns[node.FirstLeaf];
                return new ParquetField
                {
                    Name = name,
                    Type = nullable || column.Type.Kind == VortexTypeKind.Null ? column.Type.Nullable : column.Type.NonNullable,
                    Shape = FieldShape.Leaf,
                    Column = column.Ordinal,
                    DefinedAt = node.DefinitionLevel,
                    FirstLeaf = node.FirstLeaf,
                    LeafCount = 1,
                };
            }

            LogicalTypeKind annotation = element.LogicalType.Kind;
            if (annotation == LogicalTypeKind.None && element.ConvertedType >= 0)
            {
                annotation = (ConvertedType)element.ConvertedType switch
                {
                    ConvertedType.List => LogicalTypeKind.List,
                    ConvertedType.Map or ConvertedType.MapKeyValue => LogicalTypeKind.Map,
                    _ => LogicalTypeKind.None,
                };
            }

            return annotation switch
            {
                LogicalTypeKind.List => ListOf(index, name, nullable),
                LogicalTypeKind.Map => MapOf(index, name, nullable),
                LogicalTypeKind.Variant => VariantOf(index, name, nullable),
                LogicalTypeKind.File => FileOf(index, name, nullable),
                _ => Struct(index, name, nullable),
            };
        }

        /// <summary>
        /// A FILE-annotated group: the struct of its fields under <c>parquet.file</c> where each is one
        /// the standard names, of its type, and optional; else the struct alone, the annotation
        /// dropped as one on a type it does not fit.
        /// </summary>
        private ParquetField FileOf(int index, string name, bool nullable)
        {
            ParquetField field = Struct(index, name, nullable);
            foreach (ParquetField child in field.Children)
            {
                if (child.Shape != FieldShape.Leaf || !child.IsNullable || !IsFileField(child.Name, columns[child.Column]))
                {
                    return field;
                }
            }

            field.Type = VortexType.Extension(FileExtensionId, field.Type);
            return field;
        }

        /// <summary>Whether <paramref name="column"/> is the FILE field <paramref name="name"/>, of the type the standard gives it.</summary>
        private static bool IsFileField(string name, ParquetColumn column) => name switch
        {
            "uri" or "content_type" or "checksum" => column.Physical == PhysicalType.ByteArray && column.Logical.Kind == LogicalTypeKind.String,
            "offset" or "size" => column.Physical == PhysicalType.Int64 && column.Logical.Kind == LogicalTypeKind.None,
            "inline" => column.Physical == PhysicalType.ByteArray && column.Logical.Kind == LogicalTypeKind.None,
            _ => false,
        };

        /// <summary>
        /// A VARIANT-annotated group: its fields compile as a struct's, which a read assembles and
        /// then puts the variant back together from. A typed column of a type the standard does not
        /// shred to is refused when the field is read, not when the file opens.
        /// </summary>
        private ParquetField VariantOf(int index, string name, bool nullable)
        {
            ParquetField[] children = Children(index);
            VortexField[] fields = new VortexField[children.Length];
            string? refusal = null;
            for (int i = 0; i < children.Length; i++)
            {
                fields[i] = new VortexField(children[i].Name, children[i].Type);
                if (children[i].Name == "typed_value")
                {
                    refusal ??= Unshreddable(children[i]);
                }
            }

            VortexType storage = VortexType.Struct(fields);
            ref readonly Node node = ref nodes[index];
            return new ParquetField
            {
                Name = name,
                Type = VortexType.Variant(nullable),
                Shape = FieldShape.Variant,
                Children = children,
                Storage = nullable ? storage.Nullable : storage,
                Refusal = refusal,
                DefinedAt = node.DefinitionLevel,
                FirstLeaf = node.FirstLeaf,
                LeafCount = node.LeafCount,
            };
        }

        /// <summary>
        /// The first column under the typed value <paramref name="typed"/> of a type the standard's
        /// table of shredded types does not have, as a refusal; null when there is none. An object's
        /// fields and an array's element are each a group of a value and a typed value.
        /// </summary>
        private string? Unshreddable(ParquetField typed)
        {
            if (typed.Shape == FieldShape.Leaf)
            {
                ParquetColumn column = columns[typed.Column];
                return Shreddable(column) ? null
                    : $"The column '{column.DottedPath}' shreds a variant as {Describe(column)}, which the standard does not shred to.";
            }

            if (typed.Shape is not (FieldShape.Struct or FieldShape.List))
            {
                return null;
            }

            foreach (ParquetField group in typed.Children)
            {
                foreach (ParquetField part in group.Children)
                {
                    if (part.Name == "typed_value" && Unshreddable(part) is { } refusal)
                    {
                        return refusal;
                    }
                }
            }

            return null;
        }

        /// <summary>Whether the standard shreds a variant value to <paramref name="column"/>'s physical type and annotation.</summary>
        private static bool Shreddable(ParquetColumn column)
        {
            LogicalTypeInfo logical = column.Logical;
            if (column.IsInterval)
            {
                return false;
            }

            return column.Physical switch
            {
                PhysicalType.Boolean or PhysicalType.Float or PhysicalType.Double => logical.Kind == LogicalTypeKind.None,
                PhysicalType.Int32 => logical.Kind switch
                {
                    LogicalTypeKind.None or LogicalTypeKind.Decimal or LogicalTypeKind.Date => true,
                    LogicalTypeKind.Integer => logical.IsSigned && logical.BitWidth is 8 or 16 or 32,
                    _ => false,
                },
                PhysicalType.Int64 => logical.Kind switch
                {
                    LogicalTypeKind.None or LogicalTypeKind.Decimal => true,
                    LogicalTypeKind.Integer => logical.IsSigned && logical.BitWidth == 64,
                    LogicalTypeKind.Time => !logical.IsAdjustedToUtc && logical.Unit == ParquetTimeUnit.Micros,
                    LogicalTypeKind.Timestamp => logical.Unit is ParquetTimeUnit.Micros or ParquetTimeUnit.Nanos,
                    _ => false,
                },
                PhysicalType.ByteArray => logical.Kind is LogicalTypeKind.None or LogicalTypeKind.String or LogicalTypeKind.Decimal,
                PhysicalType.FixedLenByteArray => logical.Kind is LogicalTypeKind.Decimal or LogicalTypeKind.Uuid,
                _ => false,
            };
        }

        /// <summary>A column's physical type, its length when it has one, and its annotation, as the standard spells them.</summary>
        private static string Describe(ParquetColumn column)
        {
            string physical = ParquetMetadata.Physical(column.Physical);
            if (column.Physical == PhysicalType.FixedLenByteArray)
            {
                physical += string.Create(CultureInfo.InvariantCulture, $"({column.TypeLength})");
            }

            return ParquetMetadata.Logical(column.Logical) is { } logical ? $"{physical} {logical}" : physical;
        }

        /// <summary>The node as a required element: its own type, its repetition set aside.</summary>
        private ParquetField Required(int index, string name)
        {
            ParquetField field = Type(index, name, nullable: false);
            return field;
        }

        private ParquetField Struct(int index, string name, bool nullable)
        {
            ParquetField[] children = Children(index);
            VortexField[] fields = new VortexField[children.Length];
            for (int i = 0; i < children.Length; i++)
            {
                fields[i] = new VortexField(children[i].Name, children[i].Type);
            }

            VortexType type = VortexType.Struct(fields);
            ref readonly Node node = ref nodes[index];
            return new ParquetField
            {
                Name = name,
                Type = nullable ? type.Nullable : type,
                Shape = FieldShape.Struct,
                Children = children,
                DefinedAt = node.DefinitionLevel,
                FirstLeaf = node.FirstLeaf,
                LeafCount = node.LeafCount,
            };
        }

        /// <summary>A LIST-annotated group: its repeated child, and the element the five rules find in it.</summary>
        private ParquetField ListOf(int index, string name, bool nullable)
        {
            ref readonly Node node = ref nodes[index];
            if (node.ChildCount != 1)
            {
                ParquetThrow.Format($"The LIST '{name}' holds {node.ChildCount} fields, not one repeated field.");
            }

            int repeated = node.FirstChild;
            ref readonly SchemaElement repeatedElement = ref elements[repeated];
            if (repeatedElement.Repetition != FieldRepetition.Repeated)
            {
                ParquetThrow.Format($"The LIST '{name}' holds a field that does not repeat.");
            }

            ref readonly Node repeatedNode = ref nodes[repeated];
            ParquetField element;
            if (repeatedNode.FirstChild < 0)
            {
                // Rule 1: a repeated leaf is the element, required.
                element = Required(repeated, "element");
            }
            else if (repeatedNode.ChildCount > 1
                || elements[repeatedNode.FirstChild].Repetition == FieldRepetition.Repeated
                || repeatedElement.Name == "array"
                || repeatedElement.Name == elements[index].Name + "_tuple")
            {
                // Rules 2, 3 and 4: the repeated group is the element, required.
                element = Required(repeated, "element");
            }
            else
            {
                // Rule 5, the three-level list: the repeated group's one field is the element,
                // with its own repetition.
                int child = repeatedNode.FirstChild;
                element = Type(child, "element", elements[child].Repetition == FieldRepetition.Optional);
            }

            return List(name, element, nullable, node.DefinitionLevel, repeated, index);
        }

        private ParquetField List(string name, ParquetField element, bool nullable, int definedAt, int repeated, int outer)
        {
            VortexType type = VortexType.List(element.Type);
            return new ParquetField
            {
                Name = name,
                Type = nullable ? type.Nullable : type,
                Shape = FieldShape.List,
                Children = [element],
                DefinedAt = definedAt,
                RepeatedAt = nodes[repeated].RepetitionLevel,
                ElementsAt = nodes[repeated].DefinitionLevel,
                FirstLeaf = nodes[outer].FirstLeaf,
                LeafCount = nodes[outer].LeafCount,
            };
        }

        /// <summary>A MAP-annotated group: its repeated group of a key and, optionally, a value, found by position.</summary>
        private ParquetField MapOf(int index, string name, bool nullable)
        {
            ref readonly Node node = ref nodes[index];
            if (node.ChildCount != 1)
            {
                ParquetThrow.Format($"The MAP '{name}' holds {node.ChildCount} fields, not one repeated group.");
            }

            int entries = node.FirstChild;
            ref readonly Node entryNode = ref nodes[entries];
            if (elements[entries].Repetition != FieldRepetition.Repeated || entryNode.FirstChild < 0 || entryNode.ChildCount is < 1 or > 2)
            {
                ParquetThrow.Format($"The MAP '{name}' does not hold a repeated group of a key and a value.");
            }

            // The standard requires the key, and some writers made it optional: it reads as required,
            // and a null key is refused where the read meets one.
            int keyIndex = entryNode.FirstChild;
            ParquetField key = Type(keyIndex, "key", nullable: false);
            key.RefusesNull = elements[keyIndex].Repetition != FieldRepetition.Required;
            ParquetField value;
            if (entryNode.ChildCount == 2)
            {
                int valueIndex = Next(nodes, keyIndex);
                value = Type(valueIndex, "value", elements[valueIndex].Repetition == FieldRepetition.Optional);
            }
            else
            {
                value = new ParquetField { Name = "value", Type = VortexType.Null, Shape = FieldShape.Leaf, DefinedAt = int.MaxValue };
            }

            VortexType type = VortexType.Map(key.Type, value.Type);
            return new ParquetField
            {
                Name = name,
                Type = nullable ? type.Nullable : type,
                Shape = FieldShape.Map,
                Children = [key, value],
                DefinedAt = node.DefinitionLevel,
                RepeatedAt = entryNode.RepetitionLevel,
                ElementsAt = entryNode.DefinitionLevel,
                FirstLeaf = node.FirstLeaf,
                LeafCount = node.LeafCount,
            };
        }
    }
}
