// vortex.ext - vortex-array-0.86.1/src/arrays/extension/vtable/mod.rs `deserialize`.
//
// Exactly one child, the storage, at the extension dtype's storage dtype. There is NO validity
// child: `ValidityVTableFromChild` means an extension's validity IS its storage's, which is why
// CanonicalArena.AddExtension takes no Validity at all (Phase 1 contract §9.1).
//
// Upstream validates the extension dtype at dtype-parse time (`try_with_vtable` always calls
// `validate_dtype`). Phase 1 defers unsupported-component failures to first USE of the field
// (contract §2.3), so the validation runs here, from the decoder, and again from the column
// accessor. The metadata layouts are hand-rolled, not protobuf; they live in
// ExtensionDTypeRegistry (contract §8.7) because `columns` needs them too.
using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Decodes <c>vortex.ext</c>: a logical type wrapped around a canonical storage child.</summary>
public sealed class ExtensionDecoder : ArrayDecoder
{
    /// <summary>The wire id, UTF-8.</summary>
    public const string Id = "vortex.ext";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly ExtensionDecoder Instance = new ExtensionDecoder();

    private ExtensionDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.ext"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Extension;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        EncodingMetadata.RequireEmpty(node.Metadata, Id);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 1, Id);
        CanonicalSupport.RequireKind(dtype, DTypeKind.Extension, Id);

        DType storageDType = dtype.StorageType;
        ValidateExtensionDType(dtype, storageDType);

        int storageIndex = context.DecodeChild(in node, 0, storageDType, length);
        return context.Canonical.AddExtension(dtype, length, storageIndex);
    }

    /// <summary>
    /// Resolves the extension id and parses its metadata against the storage dtype. This is the
    /// only site in a decode that may raise <see cref="VortexUnsupportedException"/> with kind
    /// <c>"dtype"</c>, and it does so through
    /// <see cref="ExtensionDTypeRegistry.RequireSupported"/> (contract §2.3).
    /// </summary>
    internal static void ValidateExtensionDType(DType dtype, DType storage)
    {
        ReadOnlySpan<byte> id = dtype.ExtensionIdUtf8;
        ExtensionDTypeRegistry.RequireSupported(id);

        ReadOnlySpan<byte> metadata = dtype.ExtensionMetadata;
        switch (ExtensionDTypeRegistry.Resolve(id))
        {
            case ExtensionKind.Date:
                ExtensionDTypeRegistry.ReadDateUnit(metadata, storage);
                break;
            case ExtensionKind.Time:
                ExtensionDTypeRegistry.ReadTimeUnit(metadata, storage);
                break;
            case ExtensionKind.Timestamp:
                ExtensionDTypeRegistry.ReadTimestamp(metadata, storage);
                break;
            default:
                ExtensionDTypeRegistry.ReadUuid(metadata, storage);
                break;
        }
    }
}
