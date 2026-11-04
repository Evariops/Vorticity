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
        ValidateExtensionDType(dtype, storageDType, context.Scan.HasFile ? context.Scan.File.Session.Options.Extensions : null);

        // The storage is the column itself under a label, so it may stay encoded when this node
        // may: a dictionary of dates is read by code like a dictionary of integers.
        context.KeepEncodingInChild();
        int storageIndex = context.DecodeChild(in node, 0, storageDType, length);
        return context.Canonical.AddExtension(dtype, length, storageIndex);
    }

    /// <inheritdoc/>
    public override bool DecodesRange(ArrayDecodeContext context, in ArrayNode node)
    {
        ArgumentNullException.ThrowIfNull(context);
        return node.ChildCount == 1 && context.ChildDecodesRange(in node, 0);
    }

    /// <summary>The storage's range under the label: a range of timestamps is a range of their integers.</summary>
    /// <inheritdoc/>
    public override int DecodeRange(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        EncodingMetadata.RequireEmpty(node.Metadata, Id);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 1, Id);
        CanonicalSupport.RequireKind(dtype, DTypeKind.Extension, Id);

        DType storageDType = dtype.StorageType;
        ValidateExtensionDType(dtype, storageDType, context.Scan.HasFile ? context.Scan.File.Session.Options.Extensions : null);
        context.KeepEncodingInChild();
        int storageIndex = context.DecodeChildRange(in node, 0, storageDType, length, start, count);
        return context.Canonical.AddExtension(dtype, count, storageIndex);
    }

    /// <inheritdoc/>
    public override bool EvaluatesWithoutFullDecode => true;

    /// <summary>
    /// Asks the storage: a filter on an extension compares its storage, the column under a label,
    /// so a timestamp's comparison is its integers', answered by their encoding.
    /// </summary>
    /// <inheritdoc/>
    public override bool TryEvaluate(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, Compute.EncodedPredicate predicate,
        ref Compute.PredicateBits bits)
    {
        ArgumentNullException.ThrowIfNull(context);

        EncodingMetadata.RequireEmpty(node.Metadata, Id);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 1, Id);
        CanonicalSupport.RequireKind(dtype, DTypeKind.Extension, Id);

        DType storageDType = dtype.StorageType;
        ValidateExtensionDType(dtype, storageDType, context.Scan.HasFile ? context.Scan.File.Session.Options.Extensions : null);
        return context.TryEvaluateChild(in node, 0, storageDType, length, predicate, ref bits);
    }

    /// <summary>
    /// Resolves the extension id and parses its metadata against the storage dtype. An extension
    /// neither built in nor registered on the session fails here, when the field is first used,
    /// rather than when the dtype was parsed: this is the only site in a decode that may raise
    /// <see cref="VortexUnsupportedException"/> for a dtype. A registered one decodes as its storage,
    /// which its registration reads.
    /// </summary>
    internal static void ValidateExtensionDType(DType dtype, DType storage, VortexExtensionRegistry? extensions = null)
    {
        ReadOnlySpan<byte> id = dtype.ExtensionIdUtf8;
        ExtensionKind kind = ExtensionDTypeRegistry.Resolve(id);
        if (kind == ExtensionKind.Unknown && extensions is not null && extensions.IsRegistered(id))
        {
            return;
        }

        ExtensionDTypeRegistry.RequireSupported(id);

        ReadOnlySpan<byte> metadata = dtype.ExtensionMetadata;
        switch (kind)
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
