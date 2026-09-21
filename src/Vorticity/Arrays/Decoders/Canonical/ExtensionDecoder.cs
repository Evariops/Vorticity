using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>
/// Decodes <c>vortex.ext</c>: a logical type wrapped around a canonical storage child, its only
/// child. There is no validity child, because an extension's validity is its storage's, which is
/// why the canonical extension node carries no validity of its own.
/// </summary>
internal sealed class ExtensionDecoder : ArrayDecoder
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
    /// Resolves the extension id and parses its metadata against the storage dtype. An unsupported
    /// extension component fails here, when the field is first used, rather than when the dtype was
    /// parsed: this is the only site in a decode that may raise
    /// <see cref="VortexUnsupportedException"/> with kind <c>"dtype"</c>.
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
