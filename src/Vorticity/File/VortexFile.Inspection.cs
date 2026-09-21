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
    /// The layout tree, with each flat node's array encodings. It reads each flat node's segment to
    /// name its encodings, decompressing nothing; a node that cannot be read says why in its place.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The root node.</returns>
    public async ValueTask<VortexLayout> GetLayoutAsync(CancellationToken cancellationToken = default)
    {
        LayoutTree tree = LayoutTree;
        ArrayEncodingId[] encodings = new ArrayEncodingId[ArrayEncodingCount];
        for (int i = 0; i < encodings.Length; i++)
        {
            encodings[i] = GetArrayEncoding(i);
        }

        ArrayNodeArena arena = new ArrayNodeArena();
        return await DescribeAsync(tree.Root, arena, encodings, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<VortexLayout> DescribeAsync(LayoutNode node, ArrayNodeArena arena, ArrayEncodingId[] encodings, CancellationToken cancellationToken)
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
            array = await ArrayEncodingOfAsync(node, arena, encodings, cancellationToken).ConfigureAwait(false);
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
            described.Add(await DescribeAsync(child, arena, encodings, cancellationToken).ConfigureAwait(false));
        }

        return new VortexLayout(encoding, rows, type, segments.MoveToImmutable(), zoneCount, zoneLength, usable, array, described.MoveToImmutable());
    }

    private async ValueTask<string> ArrayEncodingOfAsync(LayoutNode node, ArrayNodeArena arena, ArrayEncodingId[] encodings, CancellationToken cancellationToken)
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

        SegmentSpec spec = SegmentSpecs[(int)index];

        // Copied before the await: a flat node may inline its array tree in its metadata, and the
        // metadata is a span over the layout buffer.
        byte[]? inlined = null;
        FlatLayoutMetadata metadata = FlatLayoutMetadata.Read(node.Metadata);
        if (metadata.HasArrayEncodingTree)
        {
            inlined = metadata.ArrayEncodingTree.ToArray();
        }

        try
        {
            using SegmentOwner owner = await Segments.ReadAsync(spec, cancellationToken).ConfigureAwait(false);
            if (inlined is null)
            {
                ArrayBlobReader.Load(arena, owner.Buffer, encodings);
            }
            else
            {
                ArrayBlobReader.Load(arena, inlined, owner.Buffer, encodings);
            }

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
