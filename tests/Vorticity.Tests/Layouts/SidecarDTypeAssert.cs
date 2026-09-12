// Compares a DType against the sidecar's parsed dtype tree, structurally.
//
// Structurally rather than by rendering: our DType.ToString() is a diagnostic renderer that elides
// after 32 fields and truncates at 2048 characters, and the sidecar's `dtype_display` is Rust's
// `impl Display for DType`. Comparing the trees needs no second formatter and catches a wrong
// nullability or a wrong ptype just as sharply.
using System;
using System.Globalization;
using System.Text;

using Vorticity.Types;

using Xunit;

namespace Vorticity.Tests.Layouts;

internal static class SidecarDTypeAssert
{
    internal static void Equal(SidecarDType expected, DType actual, string where)
    {
        Assert.False(actual.IsDefault, $"{where}: the node carries no dtype.");

        DTypeKind kind = KindOf(expected.Kind, where);
        Assert.True(
            kind == actual.Kind,
            $"{where}: expected dtype kind {expected.Kind}, got {actual.Kind}.");

        if (kind != DTypeKind.Null)
        {
            Nullability nullability = expected.Nullable ? Nullability.Nullable : Nullability.NonNullable;
            Assert.True(
                nullability == actual.Nullability,
                $"{where}: expected nullability {nullability}, got {actual.Nullability}.");
        }

        switch (kind)
        {
            case DTypeKind.Primitive:
                Assert.True(
                    string.Equals(expected.PType, actual.PType.Name(), StringComparison.Ordinal),
                    $"{where}: expected ptype {expected.PType}, got {actual.PType.Name()}.");
                break;

            case DTypeKind.Decimal:
                Assert.True(
                    expected.Precision == actual.Precision && expected.Scale == actual.Scale,
                    $"{where}: expected decimal({expected.Precision},{expected.Scale}), " +
                    $"got decimal({actual.Precision},{actual.Scale}).");
                break;

            case DTypeKind.List:
                Equal(expected.Element!, actual.ElementType, where + ".element");
                break;

            case DTypeKind.FixedSizeList:
                Assert.True(
                    expected.Size == actual.FixedSize,
                    $"{where}: expected size {expected.Size}, got {actual.FixedSize}.");
                Equal(expected.Element!, actual.ElementType, where + ".element");
                break;

            case DTypeKind.Map:
                Equal(expected.Key!, actual.KeyType, where + ".key");
                Equal(expected.Value!, actual.ValueType, where + ".value");
                break;

            case DTypeKind.Extension:
                Assert.True(
                    string.Equals(expected.ExtensionId, Utf8(actual.ExtensionIdUtf8), StringComparison.Ordinal),
                    $"{where}: expected extension id {expected.ExtensionId}, got {Utf8(actual.ExtensionIdUtf8)}.");
                Equal(expected.Storage!, actual.StorageType, where + ".storage");
                break;

            case DTypeKind.Struct:
            {
                Assert.True(
                    expected.Fields.Length == actual.FieldCount,
                    $"{where}: expected {expected.Fields.Length.ToString(CultureInfo.InvariantCulture)} fields, " +
                    $"got {actual.FieldCount.ToString(CultureInfo.InvariantCulture)}.");

                for (int i = 0; i < expected.Fields.Length; i++)
                {
                    string name = Utf8(actual.GetFieldNameUtf8(i));
                    Assert.True(
                        string.Equals(expected.FieldNames[i], name, StringComparison.Ordinal),
                        $"{where}: field {i.ToString(CultureInfo.InvariantCulture)} is named " +
                        $"'{name}', expected '{expected.FieldNames[i]}'.");
                    Equal(expected.Fields[i], actual.GetField(i), where + "." + expected.FieldNames[i]);
                }

                break;
            }

            default:
                break;
        }
    }

    private static string Utf8(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    private static DTypeKind KindOf(string kind, string where) => kind switch
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
        "map" => DTypeKind.Map,
        _ => throw new InvalidOperationException($"{where}: unknown sidecar dtype kind '{kind}'."),
    };
}
