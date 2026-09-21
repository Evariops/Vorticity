// Renders a DType in the grammar the corpus manifest uses, which is the Rust reference's
// `impl Display for DType` and not our own DType.ToString().
//
// The two differ deliberately: DTypeFormatter prints `struct{...}`, elides after 32 fields and
// truncates at 2048 characters, all of which are right for a diagnostic and wrong for a
// byte-for-byte comparison against every manifest string. This renderer exists so the corpus test
// can compare the whole schema exactly - including the extension dtypes, whose rendering needs
// their metadata decoded (`id[metadata](storage)`).
//
// It is test-only. The library's public rendering stays DType.ToString().
using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Vorticity.Types;

namespace Vorticity.Tests.File;

/// <summary>Renders a <see cref="DType"/> the way <c>corpus/manifest.json</c> spells it.</summary>
internal static class ManifestDTypeFormatter
{
    /// <summary>Renders <paramref name="dtype"/>.</summary>
    /// <param name="dtype">The dtype to render.</param>
    /// <returns>The rendered dtype.</returns>
    internal static string Format(DType dtype)
    {
        StringBuilder builder = new StringBuilder(64);
        Append(builder, dtype);
        return builder.ToString();
    }

    private static void Append(StringBuilder b, DType d)
    {
        switch (d.Kind)
        {
            case DTypeKind.Null:
                // `Null => write!(f, "null")` - no nullability suffix, the type carries none.
                b.Append("null");
                return;

            case DTypeKind.Bool:
                b.Append("bool");
                break;

            case DTypeKind.Primitive:
                b.Append(d.PType.Name());
                break;

            case DTypeKind.Decimal:
                b.Append("decimal(")
                 .Append(d.Precision.ToString(CultureInfo.InvariantCulture))
                 .Append(',')
                 .Append(d.Scale.ToString(CultureInfo.InvariantCulture))
                 .Append(')');
                break;

            case DTypeKind.Utf8:
                b.Append("utf8");
                break;

            case DTypeKind.Binary:
                b.Append("binary");
                break;

            case DTypeKind.Variant:
                b.Append("variant");
                break;

            case DTypeKind.List:
                b.Append("list(");
                Append(b, d.ElementType);
                b.Append(')');
                break;

            case DTypeKind.FixedSizeList:
                b.Append("fixed_size_list(");
                Append(b, d.ElementType);
                b.Append(")[").Append(d.FixedSize.ToString(CultureInfo.InvariantCulture)).Append(']');
                break;

            case DTypeKind.Map:
                b.Append("map(");
                Append(b, d.KeyType);
                b.Append(", ");
                Append(b, d.ValueType);
                b.Append(", keys_sorted=").Append(d.KeysSorted ? "true" : "false").Append(')');
                break;

            case DTypeKind.Struct:
                b.Append('{');
                for (int i = 0; i < d.FieldCount; i++)
                {
                    if (i > 0)
                    {
                        b.Append(", ");
                    }

                    b.Append(Encoding.UTF8.GetString(d.GetFieldNameUtf8(i))).Append('=');
                    Append(b, d.GetField(i));
                }

                b.Append('}');
                break;

            case DTypeKind.Union:
                b.Append("union(");
                AppendUnionVariants(b, d);
                b.Append(')');
                break;

            case DTypeKind.Extension:
                // `ExtDTypeRef::fmt`: "{id}[{metadata}]({storage})", with the brackets dropped when
                // the metadata renders empty. The storage carries the nullability, so an extension
                // never appends one of its own.
                b.Append(Encoding.UTF8.GetString(d.ExtensionIdUtf8));
                string options = FormatExtensionMetadata(d);
                if (options.Length != 0)
                {
                    b.Append('[').Append(options).Append(']');
                }

                b.Append('(');
                Append(b, d.StorageType);
                b.Append(')');
                return;

            default:
                throw new InvalidOperationException($"Unhandled dtype kind {d.Kind}.");
        }

        if (d.Nullability == Nullability.Nullable)
        {
            b.Append('?');
        }
    }

    private static void AppendUnionVariants(StringBuilder b, DType d)
    {
        bool consecutive = true;
        for (int i = 0; i < d.FieldCount; i++)
        {
            if (d.GetTypeId(i) != i)
            {
                consecutive = false;
                break;
            }
        }

        for (int i = 0; i < d.FieldCount; i++)
        {
            if (i > 0)
            {
                b.Append(", ");
            }

            b.Append(Encoding.UTF8.GetString(d.GetFieldNameUtf8(i)));
            if (!consecutive)
            {
                b.Append('@').Append(d.GetTypeId(i).ToString(CultureInfo.InvariantCulture));
            }

            b.Append('=');
            Append(b, d.GetField(i));
        }
    }

    private static string FormatExtensionMetadata(DType d)
    {
        ReadOnlySpan<byte> id = d.ExtensionIdUtf8;
        ReadOnlySpan<byte> metadata = d.ExtensionMetadata;

        if (id.SequenceEqual("vortex.date"u8) || id.SequenceEqual("vortex.time"u8))
        {
            // 1 byte, the TimeUnit discriminant; trailing bytes ignored.
            return metadata.IsEmpty
                ? throw new InvalidOperationException("A date/time extension dtype needs a unit byte.")
                : TimeUnitName(metadata[0]);
        }

        if (id.SequenceEqual("vortex.timestamp"u8))
        {
            // [unit][u16 LE tz length][tz], minimum 3 bytes: the length prefix is always written,
            // so "no timezone" is [unit, 0, 0] and not a bare unit byte.
            if (metadata.Length < 3)
            {
                throw new InvalidOperationException("A timestamp extension dtype needs at least 3 metadata bytes.");
            }

            string unit = TimeUnitName(metadata[0]);
            int tzLength = BinaryPrimitives.ReadUInt16LittleEndian(metadata.Slice(1, 2));
            if (tzLength == 0)
            {
                return unit;
            }

            if (metadata.Length < 3 + tzLength)
            {
                throw new InvalidOperationException("Truncated timestamp timezone.");
            }

            return unit + ", tz=" + Encoding.UTF8.GetString(metadata.Slice(3, tzLength));
        }

        if (id.SequenceEqual("vortex.uuid"u8))
        {
            // 0 or 1 byte. `UuidMetadata::fmt` renders "" for no version and "v{n}" otherwise.
            return metadata.IsEmpty
                ? string.Empty
                : "v" + metadata[0].ToString(CultureInfo.InvariantCulture);
        }

        // ForeignExtMetadata renders as "{len}B".
        return metadata.Length.ToString(CultureInfo.InvariantCulture) + "B";
    }

    private static string TimeUnitName(byte unit) => unit switch
    {
        0 => "ns",

        // U+00B5 MICRO SIGN, not U+03BC GREEK SMALL LETTER MU. Nineteen corpus files compare
        // against this exact character.
        1 => "µs",
        2 => "ms",
        3 => "s",
        4 => "days",
        _ => throw new InvalidOperationException($"Undefined Vortex TimeUnit {unit}."),
    };
}
