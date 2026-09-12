// Hand-built `Layout` FlatBuffers, for the shapes no writer produces: a flat layout with two
// segments, a chunked layout whose children do not add up, a 65-deep tree, a segment id one past
// the end.
//
// The detached LayoutTree.Parse overload takes exactly what this produces - the bytes, a schema,
// the footer's layout_specs and the segment count - so an adversarial structural test never has to
// forge a whole container.
using System;
using System.Collections.Generic;

using Vorticity.Serialization.FlatBuffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Tests.Layouts;

/// <summary>One node of a layout tree to be serialized.</summary>
internal sealed class SyntheticLayout
{
    internal SyntheticLayout(string encodingId, ulong rowCount)
    {
        EncodingId = encodingId;
        RowCount = rowCount;
    }

    internal string EncodingId { get; }

    internal ulong RowCount { get; }

    internal byte[] Metadata { get; set; } = [];

    internal uint[] Segments { get; set; } = [];

    internal List<SyntheticLayout> Children { get; } = new List<SyntheticLayout>();

    internal SyntheticLayout With(params SyntheticLayout[] children)
    {
        Children.AddRange(children);
        return this;
    }

    internal SyntheticLayout WithMetadata(byte[] metadata)
    {
        Metadata = metadata;
        return this;
    }

    internal SyntheticLayout WithSegments(params uint[] segments)
    {
        Segments = segments;
        return this;
    }

    /// <summary>A <c>vortex.flat</c> node over one segment.</summary>
    internal static SyntheticLayout Flat(ulong rowCount, uint segment) =>
        new SyntheticLayout("vortex.flat", rowCount).WithSegments(segment);
}

internal static class SyntheticLayoutWriter
{
    /// <summary>Serializes a tree and reports the <c>layout_specs</c> dictionary it implies.</summary>
    /// <param name="root">The tree.</param>
    /// <param name="specIds">The distinct encoding ids, in first-seen order; a node's spec index is its position.</param>
    /// <returns>The <c>Layout</c> FlatBuffer bytes.</returns>
    internal static byte[] Write(SyntheticLayout root, out string[] specIds)
    {
        List<string> ids = new List<string>();
        Collect(root, ids);
        specIds = ids.ToArray();

        FlatBufferBuilder builder = new FlatBufferBuilder();
        try
        {
            int offset = WriteNode(builder, root, ids);
            return builder.FinishToArray(offset);
        }
        finally
        {
            builder.Dispose();
        }
    }

    /// <summary>
    /// Serializes a chain of <paramref name="depth"/> chunked nodes ending in one flat leaf, for the
    /// depth-cap test.
    /// </summary>
    internal static byte[] Chain(int depth, out string[] specIds)
    {
        SyntheticLayout node = SyntheticLayout.Flat(1, 0);
        for (int i = 1; i < depth; i++)
        {
            node = new SyntheticLayout("vortex.chunked", 1).With(node);
        }

        return Write(node, out specIds);
    }

    /// <summary>
    /// Serializes a tree whose nodes SHARE children: <paramref name="levels"/> chunked layouts, each
    /// pointing twice at the next, so the materialized tree is 2^levels nodes from a few hundred
    /// bytes. FlatBuffers' forward-only uoffsets exclude cycles but not sharing
    /// (docs/03-architecture.md §6).
    /// </summary>
    internal static byte[] SharedChildren(int levels, out string[] specIds)
    {
        specIds = ["vortex.chunked", "vortex.flat"];

        FlatBufferBuilder builder = new FlatBufferBuilder();
        try
        {
            // The leaf is written once; every level above points at the SAME offset twice.
            int child = LayoutWriter.Write(builder, 1, 1, default, default, [0u]);
            ulong rows = 1;
            int[] children = new int[2];
            for (int i = 0; i < levels; i++)
            {
                rows *= 2;
                children[0] = child;
                children[1] = child;
                child = LayoutWriter.Write(builder, 0, rows, default, children, default);
            }

            return builder.FinishToArray(child);
        }
        finally
        {
            builder.Dispose();
        }
    }

    /// <summary>
    /// Serializes a chunked root with <paramref name="siblings"/> empty chunked children that all
    /// point at ONE metadata vector of <paramref name="metadataBytes"/> bytes. FlatBuffers lets a
    /// vector be shared, so the bytes a naive parser would copy or re-parse grow as
    /// siblings x metadata while the buffer itself grows only as siblings + metadata.
    /// </summary>
    /// <remarks>
    /// The children are chunked rather than flat so the payload can be arbitrary: a chunked
    /// layout's metadata is one flag byte and trailing bytes are ignored, whereas a flat layout's
    /// is a protobuf message that all-zero bytes do not parse as.
    /// </remarks>
    internal static byte[] SharedMetadata(int siblings, int metadataBytes, out string[] specIds)
    {
        specIds = ["vortex.chunked"];

        FlatBufferBuilder builder = new FlatBufferBuilder();
        try
        {
            // Byte 0 is the legacy "child 0 is a statistics table" flag; leave it clear.
            byte[] metadata = new byte[metadataBytes];
            int sharedVector = builder.CreateByteVector(metadata);

            int[] children = new int[siblings];
            for (int i = 0; i < siblings; i++)
            {
                builder.StartTable();
                builder.AddUInt16(SchemaFieldIds.LayoutEncoding, 0);
                builder.AddUInt64(SchemaFieldIds.LayoutRowCount, 0);
                builder.AddOffset(SchemaFieldIds.LayoutMetadata, sharedVector);
                children[i] = builder.EndTable();
            }

            int root = LayoutWriter.Write(builder, 0, 0, default, children, default);
            return builder.FinishToArray(root);
        }
        finally
        {
            builder.Dispose();
        }
    }

    private static void Collect(SyntheticLayout node, List<string> ids)
    {
        if (!ids.Contains(node.EncodingId))
        {
            ids.Add(node.EncodingId);
        }

        foreach (SyntheticLayout child in node.Children)
        {
            Collect(child, ids);
        }
    }

    private static int WriteNode(FlatBufferBuilder builder, SyntheticLayout node, List<string> ids)
    {
        int count = node.Children.Count;
        int[] childOffsets = count == 0 ? [] : new int[count];
        for (int i = 0; i < count; i++)
        {
            childOffsets[i] = WriteNode(builder, node.Children[i], ids);
        }

        return LayoutWriter.Write(
            builder,
            (ushort)ids.IndexOf(node.EncodingId),
            node.RowCount,
            node.Metadata,
            childOffsets,
            node.Segments);
    }
}
