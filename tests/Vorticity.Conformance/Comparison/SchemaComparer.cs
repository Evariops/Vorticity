// The `dtype` line, compared against the parsed schema as a TREE rather than as a rendered string.
//
// The header also carries a Display rendering, and comparing that is easy and weak: two dtypes that
// print the same can differ in a field name's bytes, and a renderer bug hides a parser bug. So this
// walks both trees together - kind, ptype, nullability, precision/scale, element type, fixed size,
// extension id and extension metadata bytes, field names in order - and reports the first
// disagreement at the path where it happened.
using System;
using System.Globalization;
using System.Text;
using Vorticity.Conformance.Sidecar;
using Vorticity.Types;

namespace Vorticity.Conformance.Comparison;

/// <summary>Compares the sidecar's dtype tree against a parsed <see cref="DType"/>.</summary>
internal static class SchemaComparer
{
    /// <summary>Walks both trees and logs every disagreement.</summary>
    /// <param name="expected">The sidecar's `dtype` line tree, or a subtree of it.</param>
    /// <param name="actual">The parsed dtype at the same position.</param>
    /// <param name="path">The dotted path, empty at the root.</param>
    /// <param name="log">Where mismatches go.</param>
    internal static void Compare(JsonValue expected, DType actual, string path, MismatchLog log)
    {
        if (expected.Kind != JsonKind.Object)
        {
            log.Add(path, -1, "a dtype node that is not an object", expected.Summary(), actual.Kind.ToString());
            return;
        }

        string kind = expected.Find("kind")?.Text ?? "?";
        DTypeKind wanted = KindOf(kind);
        if (wanted != actual.Kind)
        {
            log.Add(path, -1, "dtype kind", kind, actual.Kind.ToString());
            return;
        }

        // `null` carries no nullability: the type IS the null type.
        if (wanted != DTypeKind.Null)
        {
            JsonValue? nullable = expected.Find("nullable");
            if (nullable is not null && nullable.Kind == JsonKind.Boolean && nullable.Boolean != actual.IsNullable)
            {
                log.Add(path, -1, "dtype nullability",
                    nullable.Boolean ? "nullable" : "non-nullable",
                    actual.IsNullable ? "nullable" : "non-nullable");
            }
        }

        switch (wanted)
        {
            case DTypeKind.Primitive:
            {
                string ptype = expected.Find("ptype")?.Text ?? "?";
                string mine = actual.PType.Name();
                if (!string.Equals(ptype, mine, StringComparison.Ordinal))
                {
                    log.Add(path, -1, "primitive ptype", ptype, mine);
                }

                return;
            }

            case DTypeKind.Decimal:
            {
                CompareNumber(expected, "precision", actual.Precision, path, log);
                CompareNumber(expected, "scale", actual.Scale, path, log);
                return;
            }

            case DTypeKind.Struct:
            {
                JsonValue fields = expected.Find("fields") ?? JsonValue.Null;
                if (fields.Kind != JsonKind.Array)
                {
                    log.Add(path, -1, "a struct dtype with no fields array", expected.Summary(), "a struct");
                    return;
                }

                if (fields.Items.Length != actual.FieldCount)
                {
                    log.Add(path, -1, "struct field count",
                        fields.Items.Length.ToString(CultureInfo.InvariantCulture),
                        actual.FieldCount.ToString(CultureInfo.InvariantCulture));
                    return;
                }

                for (int i = 0; i < fields.Items.Length; i++)
                {
                    JsonValue field = fields.Items[i];
                    string name = field.Find("name")?.Text ?? string.Empty;
                    string mine = actual.GetFieldName(i);
                    string child = path.Length == 0 ? name : path + "." + name;

                    if (!string.Equals(name, mine, StringComparison.Ordinal))
                    {
                        log.Add(child, -1, $"struct field {i.ToString(CultureInfo.InvariantCulture)} name",
                            "\"" + name + "\"", "\"" + mine + "\"");
                        continue;
                    }

                    Compare(field.Find("dtype") ?? JsonValue.Null, actual.GetField(i), child, log);
                }

                return;
            }

            case DTypeKind.List:
                Compare(expected.Find("element") ?? JsonValue.Null, actual.ElementType, path + "[]", log);
                return;

            case DTypeKind.FixedSizeList:
                CompareNumber(expected, "size", actual.FixedSize, path, log);
                Compare(expected.Find("element") ?? JsonValue.Null, actual.ElementType, path + "[]", log);
                return;

            case DTypeKind.Extension:
            {
                string id = expected.Find("id")?.Text ?? "?";
                string mine = actual.ExtensionId;
                if (!string.Equals(id, mine, StringComparison.Ordinal))
                {
                    log.Add(path, -1, "extension id", id, mine);
                }

                string? hex = expected.Find("metadata_hex")?.Text;
                if (hex is not null)
                {
                    string actualHex = Convert.ToHexStringLower(actual.ExtensionMetadata);
                    if (!string.Equals(Normalize(hex), actualHex, StringComparison.OrdinalIgnoreCase))
                    {
                        log.Add(path, -1, "extension metadata bytes", hex, actualHex);
                    }
                }

                Compare(expected.Find("storage") ?? JsonValue.Null, actual.StorageType, path + ".<storage>", log);
                return;
            }

            case DTypeKind.Map:
            {
                if (expected.Find("keys_sorted") is JsonValue sorted &&
                    sorted.Kind == JsonKind.Boolean && sorted.Boolean != actual.KeysSorted)
                {
                    log.Add(path, -1, "map keys_sorted",
                        sorted.Boolean ? "true" : "false", actual.KeysSorted ? "true" : "false");
                }

                Compare(expected.Find("key") ?? JsonValue.Null, actual.KeyType, path + ".<key>", log);
                Compare(expected.Find("value") ?? JsonValue.Null, actual.ValueType, path + ".<value>", log);
                return;
            }

            default:
                return;
        }
    }

    private static void CompareNumber(JsonValue owner, string key, long actual, string path, MismatchLog log)
    {
        JsonValue? value = owner.Find(key);
        if (value is null || value.Kind != JsonKind.Number ||
            !long.TryParse(value.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long wanted))
        {
            log.Add(path, -1, $"a dtype with no numeric '{key}'", owner.Summary(),
                actual.ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (wanted != actual)
        {
            log.Add(path, -1, $"dtype {key}",
                wanted.ToString(CultureInfo.InvariantCulture), actual.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string Normalize(string hex)
    {
        StringBuilder builder = new StringBuilder(hex.Length);
        foreach (char c in hex)
        {
            if (c is ' ' or '_' or '-')
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    private static DTypeKind KindOf(string kind) => kind switch
    {
        "null" => DTypeKind.Null,
        "bool" => DTypeKind.Bool,
        "primitive" => DTypeKind.Primitive,
        "decimal" => DTypeKind.Decimal,
        "utf8" => DTypeKind.Utf8,
        "binary" => DTypeKind.Binary,
        "struct" => DTypeKind.Struct,
        "list" => DTypeKind.List,
        "extension" => DTypeKind.Extension,
        "fixed_size_list" => DTypeKind.FixedSizeList,
        "variant" => DTypeKind.Variant,
        "union" => DTypeKind.Union,
        "map" => DTypeKind.Map,
        _ => (DTypeKind)0,
    };
}
