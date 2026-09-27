using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Layouts;
using Vorticity.Serialization.Schemas;

namespace Vorticity;

/// <summary>A component id a file declares, and whether this library reads it.</summary>
/// <param name="Id">The id as the file writes it, such as <c>fastlanes.bitpacked</c>.</param>
/// <param name="Supported">Whether this library decodes it.</param>
public sealed record VortexComponent(string Id, bool Supported);

/// <summary>One segment of a file: where it is, how long, and the alignment it declares.</summary>
/// <param name="Offset">The first byte.</param>
/// <param name="Length">The number of bytes.</param>
/// <param name="Alignment">The alignment the file promises for its first byte.</param>
public sealed record VortexSegment(long Offset, int Length, int Alignment);

/// <summary>One node of a file's layout tree, with the array encoding a flat node holds.</summary>
/// <param name="Encoding">The layout id, such as <c>vortex.chunked</c> or <c>vortex.flat</c>.</param>
/// <param name="RowCount">The rows under the node.</param>
/// <param name="Type">The node's type, when the layout records one.</param>
/// <param name="Segments">The indices of the segments the node references in <see cref="VortexFile.SegmentMap"/>.</param>
/// <param name="ZoneCount">The zones of the node's zone map, or 0.</param>
/// <param name="ZoneLength">The rows per zone, or 0.</param>
/// <param name="ZonesUsable">Whether the zone map can prune.</param>
/// <param name="ArrayEncoding">For a flat node, its array encodings as <c>id(child,child)</c> with <c>?</c> after an id this library does not decode; a reason in parentheses when the node cannot be read; null for other nodes.</param>
/// <param name="Children">The child nodes.</param>
public sealed record VortexLayout(
    string Encoding, long RowCount, VortexType? Type, ImmutableArray<int> Segments, int ZoneCount, long ZoneLength,
    bool ZonesUsable, string? ArrayEncoding, ImmutableArray<VortexLayout> Children);

public sealed partial class VortexFile
{
    private ImmutableArray<VortexComponent> _arrayComponents;
    private ImmutableArray<VortexComponent> _layoutComponents;

    /// <summary>The array encodings the file's footer declares, in dictionary order.</summary>
    public ImmutableArray<VortexComponent> ArrayEncodings
    {
        get
        {
            if (_arrayComponents.IsDefault)
            {
                ImmutableArray<VortexComponent>.Builder ids = ImmutableArray.CreateBuilder<VortexComponent>(ArrayEncodingCount);
                for (int i = 0; i < ArrayEncodingCount; i++)
                {
                    ids.Add(new VortexComponent(GetArrayEncodingId(i), GetArrayEncoding(i) != ArrayEncodingId.Unknown));
                }

                _arrayComponents = ids.MoveToImmutable();
            }

            return _arrayComponents;
        }
    }

    /// <summary>The layout encodings the file's footer declares, in dictionary order.</summary>
    public ImmutableArray<VortexComponent> LayoutEncodings
    {
        get
        {
            if (_layoutComponents.IsDefault)
            {
                ImmutableArray<VortexComponent>.Builder ids = ImmutableArray.CreateBuilder<VortexComponent>(LayoutEncodingCount);
                for (int i = 0; i < LayoutEncodingCount; i++)
                {
                    ids.Add(new VortexComponent(GetLayoutEncodingId(i), GetLayoutEncoding(i) != LayoutEncodingId.Unknown));
                }

                _layoutComponents = ids.MoveToImmutable();
            }

            return _layoutComponents;
        }
    }

    /// <summary>Every segment the footer maps, in file order.</summary>
    public ImmutableArray<VortexSegment> SegmentMap
    {
        get
        {
            ReadOnlySpan<SegmentSpec> specs = SegmentSpecs;
            ImmutableArray<VortexSegment>.Builder segments = ImmutableArray.CreateBuilder<VortexSegment>(specs.Length);
            foreach (ref readonly SegmentSpec spec in specs)
            {
                segments.Add(new VortexSegment((long)spec.Offset, (int)spec.Length, 1 << spec.AlignmentExponent));
            }

            return segments.MoveToImmutable();
        }
    }

    /// <summary>
    /// The layout tree, with each flat node's array encodings. It reads each flat node's array
    /// tree, from the layout or the tail of its segment, to name its encodings, and none of its
    /// values; a node whose tree cannot be read says why in its place.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The root node.</returns>
    public async ValueTask<VortexLayout> GetLayoutAsync(CancellationToken cancellationToken = default)
    {
        ArrayNodeArena arena = new ArrayNodeArena();
        return await DescribeAsync(LayoutTree.Root, arena, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<VortexLayout> DescribeAsync(LayoutNode node, ArrayNodeArena arena, CancellationToken cancellationToken)
    {
        ReadOnlySpan<uint> segmentIndices = node.Segments;
        ImmutableArray<int>.Builder segments = ImmutableArray.CreateBuilder<int>(segmentIndices.Length);
        foreach (uint index in segmentIndices)
        {
            segments.Add((int)index);
        }

        int zoneCount = 0;
        long zoneLength = 0;
        bool usable = false;
        if (node.TryGetZoneMap(out ZoneMap zones))
        {
            zoneCount = zones.ZoneCount;
            zoneLength = zones.ZoneLength;
            usable = zones.IsPruningAvailable;
        }

        string encoding = node.EncodingIdText;
        VortexType? type = node.DType.IsDefault ? null : VortexTypes.FromDType(node.DType);
        long rows = node.RowCount;
        string? array = null;
        if (node.Encoding == LayoutEncodingId.Flat)
        {
            array = await ArrayEncodingOfAsync(node, arena, cancellationToken).ConfigureAwait(false);
        }

        int childCount = node.ChildCount;
        List<LayoutNode> children = new List<LayoutNode>(childCount);
        for (int i = 0; i < childCount; i++)
        {
            children.Add(node.GetChild(i));
        }

        ImmutableArray<VortexLayout>.Builder described = ImmutableArray.CreateBuilder<VortexLayout>(childCount);
        foreach (LayoutNode child in children)
        {
            described.Add(await DescribeAsync(child, arena, cancellationToken).ConfigureAwait(false));
        }

        return new VortexLayout(encoding, rows, type, segments.MoveToImmutable(), zoneCount, zoneLength, usable, array, described.MoveToImmutable());
    }

    /// <summary>The bytes read off the end of a segment for its array tree, which a tree longer than this is read whole after.</summary>
    private const int ArrayTreeTailBytes = 1024;

    /// <summary>
    /// Loads the array tree of a flat chunk into <paramref name="arena"/>, its nodes and no buffer,
    /// for a caller that asks what the chunk was written as: from the tree the layout inlines, or
    /// else from the tail of the segment, which is all of it that is read.
    /// </summary>
    /// <exception cref="VortexFormatException">The layout is not one flat segment, or its tree is malformed.</exception>
    internal async ValueTask LoadArrayTreeAsync(LayoutNode flat, ArrayNodeArena arena, CancellationToken cancellationToken)
    {
        FlatLayoutMetadata metadata = FlatLayoutMetadata.Read(flat.Metadata);
        if (metadata.HasArrayEncodingTree)
        {
            ArrayBlobReader.LoadTree(arena, metadata.ArrayEncodingTree, ResolvedArrayEncodings);
            return;
        }

        if (flat.Segments.Length != 1)
        {
            throw new VortexFormatException($"A flat layout of {flat.Segments.Length} segments holds no one array tree.");
        }

        SegmentSpec spec = SegmentSpecs[(int)flat.Segments[0]];
        long end = (long)spec.Offset + spec.Length;
        int window = (int)Math.Min(spec.Length, ArrayTreeTailBytes);
        int length;
        using (SegmentOwner tail = await Segments.ReadRangeAsync(end - window, window, 1, cancellationToken).ConfigureAwait(false))
        {
            ReadOnlySpan<byte> bytes = tail.Buffer.Span;
            uint declared = window < 4 ? uint.MaxValue : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[^4..]);
            if (declared + 4L > spec.Length)
            {
                throw new VortexFormatException(
                    $"An array blob of {spec.Length} bytes declares a {declared}-byte FlatBuffer before its length.");
            }

            length = (int)declared;
            if (length + 4 <= window)
            {
                ArrayBlobReader.LoadTree(arena, bytes.Slice(window - 4 - length, length), ResolvedArrayEncodings);
                return;
            }
        }

        using SegmentOwner tree = await Segments.ReadRangeAsync(end - 4 - length, length, 1, cancellationToken).ConfigureAwait(false);
        ArrayBlobReader.LoadTree(arena, tree.Buffer.Span, ResolvedArrayEncodings);
    }

    private async ValueTask<string> ArrayEncodingOfAsync(LayoutNode node, ArrayNodeArena arena, CancellationToken cancellationToken)
    {
        ReadOnlySpan<uint> segments = node.Segments;
        if (segments.Length != 1)
        {
            return $"(flat with {segments.Length} segments)";
        }

        uint index = segments[0];
        if (index >= (uint)SegmentSpecs.Length)
        {
            return $"(segment {index} is out of range)";
        }

        try
        {
            await LoadArrayTreeAsync(node, arena, cancellationToken).ConfigureAwait(false);
            StringBuilder tree = new StringBuilder();
            AppendArray(tree, arena.Root);
            return tree.ToString();
        }
        catch (VortexUnsupportedException error)
        {
            return $"(unsupported: {error.Message})";
        }
        catch (VortexFormatException error)
        {
            return $"(malformed: {error.Message})";
        }
    }

    private void AppendArray(StringBuilder output, ArrayNode node)
    {
        output.Append(GetArrayEncodingId(node.EncodingSpecIndex)).Append(node.Encoding == ArrayEncodingId.Unknown ? "?" : string.Empty);
        if (node.ChildCount == 0)
        {
            return;
        }

        output.Append('(');
        for (int i = 0; i < node.ChildCount; i++)
        {
            if (i != 0)
            {
                output.Append(',');
            }

            AppendArray(output, node.GetChild(i));
        }

        output.Append(')');
    }
}
