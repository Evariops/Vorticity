using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.File;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Vorticity.Types.Serialization;

namespace Vorticity.Indexes;

/// <summary>
/// Reads one shredded key of a variant column, chunk by chunk, from its own buffers: the path map
/// (<see cref="VariantPathMap"/>) gives each chunk's range and tree, the range is the one read, and the
/// tree decodes it as a column, as a flat layout decodes a segment whose tree is inlined.
/// </summary>
internal static class VariantPathReader
{
    /// <summary>
    /// The key's rows in <paramref name="chunk"/>, decoded in <paramref name="context"/>: its typed
    /// column, or, where the key kept values of its own, a variant of them and its typed column, which
    /// decodes as the variant <c>Struct{metadata, value}</c> of the key's values.
    /// </summary>
    /// <returns>The node, and the dtype it wears.</returns>
    /// <exception cref="VortexFormatException">The chunk's tree does not decode its buffers.</exception>
    internal static async ValueTask<(int Node, DType DType)> ReadAsync(
        ScanContext context, VortexFile file, PathChunk chunk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(chunk);
        DType dtype = DTypeProtobuf.Read(chunk.DType, context.Types);
        Buffers.VortexBuffer segment = default;
        if (chunk.Length > 0)
        {
            int slot = context.Segments.Add(new SegmentSpec(chunk.Offset, chunk.Length, chunk.AlignmentExponent, 0, 0));
            await file.Segments.ReadManyAsync(context.Segments, cancellationToken).ConfigureAwait(false);
            segment = context.Segments.GetBuffer(slot);
        }

        context.Decode.LoadBlob(chunk.Tree, segment);
        ArrayNode root = context.Nodes.Root;
        return (context.Decode.DecodeRoot(in root, dtype, checked((int)chunk.Rows)), dtype);
    }
}
