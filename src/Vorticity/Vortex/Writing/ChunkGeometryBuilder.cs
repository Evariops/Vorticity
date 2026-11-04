using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// The geometry of each chunk a writer writes (<see cref="ColumnMarks"/>): its tree, copied from its blob's tail, and the marks
/// of its buffers of variable length, read back from the blob just written.
/// </summary>
/// <remarks>
/// <para>
/// A mark is where each granule's first row starts in the bytes FSST compressed or a binary column holds, which only the
/// offsets the encoder wrote say: the offsets child is decoded from the blob, a cheap pass over integers, and sampled at
/// each granule's first row. Only the nodes that hold the chunk's rows are marked: the root, through an extension's
/// storage, and a variant's parts and a struct's fields, a shredded key's text among them, which a read of the key by its
/// granules finds its own in; a dictionary's values hold other rows, and are read whole.
/// </para>
/// <para>
/// One decode context serves every chunk of the writer, made again only when the file names more encodings than it knew.
/// A file that names neither FSST nor a binary encoding decodes nothing.
/// </para>
/// </remarks>
internal sealed class ChunkGeometryBuilder : IDisposable
{
    private ScanContext? _context;
    private int _encodings = -1;
    private readonly List<ChunkMark> _marks = [];
    private readonly ArrayNodeArena _nodes = new ArrayNodeArena();
    private readonly GranuleBytes _planner = new GranuleBytes { Variants = true };
    private ArrayEncodingId[] _resolved = [];

    /// <summary>The geometry of a chunk of <paramref name="rows"/> rows of <paramref name="dtype"/>, its blob <paramref name="blob"/>.</summary>
    internal unsafe ChunkGeometry Of(ReadOnlySpan<byte> blob, IReadOnlyList<string> encodings, DType dtype, int rows, int granuleRows)
    {
        uint treeLength = BinaryPrimitives.ReadUInt32LittleEndian(blob[^4..]);
        byte[] tree = blob.Slice(blob.Length - 4 - (int)treeLength, (int)treeLength).ToArray();
        if (!Variable(encodings) || rows <= 0)
        {
            return new ChunkGeometry(tree, []);
        }

        ScanContext context = ContextFor(encodings);
        _marks.Clear();
        fixed (byte* pinned = blob)
        {
            try
            {
                context.Decode.LoadBlob(VortexBuffer.FromPointer(pinned, blob.Length, 0));
                ArrayNode root = context.Nodes.Root;
                Mark(context, in root, dtype, rows, granuleRows);
            }
            finally
            {
                context.ResetBatch();
            }
        }

        return new ChunkGeometry(tree, _marks.Count == 0 ? [] : [.. _marks]);
    }

    /// <summary>Marks the nodes that hold the chunk's rows, through an extension's storage, a variant's parts and a struct's fields.</summary>
    private void Mark(ScanContext context, in ArrayNode node, DType dtype, int rows, int granuleRows)
    {
        switch (node.Encoding)
        {
            case ArrayEncodingId.Extension when dtype.Kind == DTypeKind.Extension:
                ArrayNode storage = node.GetChild(0);
                Mark(context, in storage, dtype.StorageType, rows, granuleRows);
                break;

            case ArrayEncodingId.Variant or ArrayEncodingId.ParquetVariant or ArrayEncodingId.Struct:
                for (int c = 0; c < node.ChildCount; c++)
                {
                    ArrayNode part = node.GetChild(c);
                    Mark(context, in part, default, rows, granuleRows);
                }

                break;

            case ArrayEncodingId.Fsst when node.BufferCount == 3 && node.ChildCount >= 2:
                Sample(context, in node, 1, FsstMetadata.Read(node.Metadata).CodesOffsetsPType, 2, rows, granuleRows);
                break;

            case ArrayEncodingId.VarBin when node.BufferCount == 1 && node.ChildCount >= 1:
                Sample(context, in node, 0, VarBinMetadata.Read(node.Metadata).OffsetsPType, 0, rows, granuleRows);
                break;
        }
    }

    /// <summary>The offsets child decoded, each granule's first row's offset into buffer <paramref name="buffer"/> kept.</summary>
    private void Sample(ScanContext context, in ArrayNode node, int child, PType offsetsPType, int buffer, int rows, int granuleRows)
    {
        DType offsetsType = context.Types.Primitive(offsetsPType, Nullability.NonNullable);
        int offsetsIndex = context.Decode.DecodeChild(in node, child, offsetsType, rows + 1);
        CanonicalNode offsets = context.Canonical.GetNode(CanonicalSupport.ExpandIfConstant(context.Decode, offsetsIndex));
        if (offsets.Kind != CanonicalKind.Primitive || offsets.Length != rows + 1)
        {
            return;
        }

        ReadOnlySpan<byte> values = offsets.Values.Span;
        int width = offsetsPType.ByteWidth();
        long[] starts = new long[(rows + granuleRows - 1) / granuleRows];
        for (int g = 0; g < starts.Length; g++)
        {
            ReadOnlySpan<byte> at = values.Slice(g * granuleRows * width, width);
            starts[g] = width switch
            {
                1 => at[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(at),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(at),
                _ => BinaryPrimitives.ReadInt64LittleEndian(at),
            };
        }

        _marks.Add(new ChunkMark(node.Arena.GlobalBufferIndex(node.Record.FirstBuffer + buffer), starts));
    }

    /// <summary>
    /// The bytes of a chunk of <paramref name="rows"/> rows of <paramref name="dtype"/>, whose tree <paramref name="geometry"/>
    /// holds, that a read of any part of it reads whole, as <see cref="GranuleBytes"/> plans the read of its first granule:
    /// a dictionary's values, patches, runs, the whole of a chunk a read of a part does not slice. A part of
    /// <c>g</c> of its <c>k</c> granules then reads about <c>W + g·(R − W)/k</c> of its <c>R</c> bytes, so a first granule
    /// that reads <c>P</c> of them makes <c>W = (k·P − R) / (k − 1)</c>, between none and all.
    /// </summary>
    internal long WholeOf(ChunkGeometry geometry, IReadOnlyList<string> encodings, DType dtype, int rows, int granuleRows)
    {
        (long Start, long Length)[] layout = ArrayBlobReader.BufferLayout(geometry.Tree.Span, out long regionLength);
        int granules = granuleRows <= 0 ? 1 : (rows + granuleRows - 1) / granuleRows;
        if (granules <= 1 || regionLength <= 0)
        {
            return Math.Max(regionLength, 0);
        }

        ArrayBlobReader.LoadLayout(_nodes, geometry.Tree.Span, Resolved(encodings));
        ArrayNode root = _nodes.Root;
        if (!_planner.TryPlan(in root, GranulePlans.WidthOf(dtype), rows, 0, granuleRows, layout, geometry, granuleRows))
        {
            return regionLength;
        }

        return Math.Clamp((granules * _planner.Bytes - regionLength) / (granules - 1), 0, regionLength);
    }

    /// <summary>The file's encodings resolved, made again when the file names more encodings than it knew.</summary>
    private ArrayEncodingId[] Resolved(IReadOnlyList<string> encodings)
    {
        if (_resolved.Length != encodings.Count)
        {
            _resolved = new ArrayEncodingId[encodings.Count];
            for (int i = 0; i < _resolved.Length; i++)
            {
                _resolved[i] = EncodingRegistry.ResolveArray(System.Text.Encoding.UTF8.GetBytes(encodings[i]));
            }
        }

        return _resolved;
    }

    /// <summary>Whether the file names an encoding whose rows take bytes of their own length.</summary>
    private static bool Variable(IReadOnlyList<string> encodings)
    {
        foreach (string encoding in encodings)
        {
            if (encoding is "vortex.fsst" or "vortex.varbin")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The decode context, made again when the file names more encodings than it knew.</summary>
    private ScanContext ContextFor(IReadOnlyList<string> encodings)
    {
        if (_context is null || _encodings != encodings.Count)
        {
            _context?.Dispose();
            string[] ids = new string[encodings.Count];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = encodings[i];
            }

            _context = new ScanContext(ids);
            _encodings = encodings.Count;
        }

        return _context;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _context?.Dispose();
        _context = null;
    }
}
