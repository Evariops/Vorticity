using System.Globalization;
using System.Text;
using Vorticity.Types;

namespace Vorticity;

/// <summary>Renders a <see cref="VortexType"/> the way the format's tools print a dtype.</summary>
internal static class VortexTypeFormatter
{
    private static readonly string[] UnitNames = ["ns", "µs", "ms", "s", "days"];

    internal static string Format(VortexType type)
    {
        StringBuilder text = new StringBuilder();
        Append(text, type);
        return text.ToString();
    }

    internal static string Format(VortexSchema schema)
    {
        StringBuilder text = new StringBuilder();
        if (!schema.RootIsStruct)
        {
            Append(text, schema[0].Type);
            return text.ToString();
        }

        AppendFields(text, "struct{", schema.FieldArray);
        return text.ToString();
    }

    private static void Append(StringBuilder text, VortexType type)
    {
        switch (type.Kind)
        {
            case VortexTypeKind.Null:
                text.Append("null");
                return;
            case VortexTypeKind.Bool:
                text.Append("bool");
                break;
            case VortexTypeKind.Primitive:
                text.Append(type.PrimitiveType.Name());
                break;
            case VortexTypeKind.Decimal:
                text.Append("decimal(").Append(type.Precision.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(type.Scale.ToString(CultureInfo.InvariantCulture)).Append(')');
                break;
            case VortexTypeKind.Utf8:
                text.Append("utf8");
                break;
            case VortexTypeKind.Binary:
                text.Append("binary");
                break;
            case VortexTypeKind.Struct:
                AppendFields(text, "struct{", type.Fields.ToArray());
                break;
            case VortexTypeKind.List:
                text.Append("list(");
                Append(text, type.ElementType!);
                text.Append(')');
                break;
            case VortexTypeKind.FixedSizeList:
                text.Append("fsl(");
                Append(text, type.ElementType!);
                text.Append(", ").Append(type.FixedSize.ToString(CultureInfo.InvariantCulture)).Append(')');
                break;
            case VortexTypeKind.Extension:
                AppendExtension(text, type);
                break;
            case VortexTypeKind.Map:
                text.Append("map(");
                Append(text, type.Fields[0].Type);
                text.Append(", ");
                Append(text, type.Fields[1].Type);
                text.Append(')');
                break;
            case VortexTypeKind.Union:
                AppendFields(text, "union{", type.Fields.ToArray());
                break;
            case VortexTypeKind.Variant:
                text.Append("variant");
                break;
            default:
                text.Append("unknown");
                break;
        }

        if (type.IsNullable)
        {
            text.Append('?');
        }
    }

    private static void AppendExtension(StringBuilder text, VortexType type)
    {
        TimeUnit? unit = type.Unit;
        switch (type.ExtensionId)
        {
            case ExtensionIds.Date:
                text.Append(unit == TimeUnit.Days ? "date" : "date(" + UnitName(unit) + ")");
                return;
            case ExtensionIds.Time:
                text.Append("time(").Append(UnitName(unit)).Append(')');
                return;
            case ExtensionIds.Timestamp:
                text.Append("timestamp(").Append(UnitName(unit));
                if (type.TimeZone is { } zone)
                {
                    text.Append(", ").Append(zone);
                }

                text.Append(')');
                return;
            case ExtensionIds.Uuid:
                text.Append("uuid");
                return;
            default:
                text.Append("ext(").Append(type.ExtensionId).Append(", ");
                Append(text, type.StorageType!.NonNullable);
                text.Append(')');
                return;
        }
    }

    private static string UnitName(TimeUnit? unit) =>
        unit is { } u && (uint)u < (uint)UnitNames.Length ? UnitNames[(int)u] : "?";

    private static void AppendFields(StringBuilder text, string opening, VortexField[] fields)
    {
        text.Append(opening);
        for (int i = 0; i < fields.Length; i++)
        {
            if (i != 0)
            {
                text.Append(", ");
            }

            text.Append(fields[i].Name).Append(": ");
            Append(text, fields[i].Type);
        }

        text.Append('}');
    }
}
