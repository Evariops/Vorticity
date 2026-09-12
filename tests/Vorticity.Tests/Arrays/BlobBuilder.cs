// Forges array blobs byte by byte so the adversarial cases can be written as data rather than as
// prose. It uses fb-schemas' ArrayWriter for the well-formed parts and patches the result where a
// well-formed writer would refuse to go.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Vorticity.Serialization.FlatBuffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Tests.Arrays;

/// <summary>A node in a forged array tree.</summary>
internal sealed class ForgedNode
{
    internal ForgedNode(ushort encoding)
    {
        Encoding = encoding;
    }

    internal ushort Encoding { get; }

    internal byte[] Metadata { get; init; } = [];

    internal ushort[] BufferIndices { get; init; } = [];

    internal List<ForgedNode> Children { get; } = [];

    internal ForgedNode With(ForgedNode child)
    {
        Children.Add(child);
        return this;
    }
}

internal static class BlobBuilder
{
    /// <summary>
    /// Builds a complete blob: <c>[buffer region][Array flatbuffer][u32 fb length]</c>.
    /// </summary>
    /// <param name="root">The array tree.</param>
    /// <param name="buffers">Descriptors, in the flat global order the blob declares.</param>
    /// <param name="regionLength">
    /// How many bytes to reserve for the buffer region. -1 computes the exact
    /// <c>sum(padding + length)</c>.
    /// </param>
    internal static byte[] Build(ForgedNode root, ReadOnlySpan<BufferSpec> buffers, int regionLength = -1)
    {
        byte[] flatBuffer = BuildFlatBuffer(root, buffers);

        if (regionLength < 0)
        {
            long needed = 0;
            for (int i = 0; i < buffers.Length; i++)
            {
                needed += buffers[i].Padding + buffers[i].Length;
            }

            regionLength = checked((int)needed);
        }

        byte[] blob = new byte[regionLength + flatBuffer.Length + 4];
        flatBuffer.CopyTo(blob.AsSpan(regionLength));
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(blob.Length - 4), (uint)flatBuffer.Length);
        return blob;
    }

    /// <summary>Just the <c>Array</c> FlatBuffer, for the inlined variant and for length patching.</summary>
    internal static byte[] BuildFlatBuffer(ForgedNode root, ReadOnlySpan<BufferSpec> buffers)
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();
        int rootOffset = WriteNode(builder, root);
        int arrayOffset = ArrayWriter.Write(builder, rootOffset, buffers);
        return builder.FinishToArray(arrayOffset);
    }

    /// <summary>A chain of <paramref name="depth"/> nodes, each the single child of the last.</summary>
    internal static ForgedNode Chain(int depth, ushort encoding = 0)
    {
        ForgedNode root = new ForgedNode(encoding);
        ForgedNode current = root;
        for (int i = 1; i < depth; i++)
        {
            ForgedNode child = new ForgedNode(encoding);
            current.Children.Add(child);
            current = child;
        }

        return root;
    }

    private static int WriteNode(FlatBufferBuilder builder, ForgedNode node)
    {
        // Children must be written before the parent's table is started.
        Span<int> childOffsets = node.Children.Count == 0 ? default : new int[node.Children.Count];
        for (int i = 0; i < node.Children.Count; i++)
        {
            childOffsets[i] = WriteNode(builder, node.Children[i]);
        }

        return ArrayWriter.WriteNode(
            builder,
            node.Encoding,
            node.Metadata,
            childOffsets,
            node.BufferIndices,
            0);
    }
}
