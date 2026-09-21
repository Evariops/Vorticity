using System;
using System.Runtime.InteropServices;
using Vorticity.Arrays;

namespace Vorticity;

/// <summary>The encoded forms a decoded node can keep, and their decode into canonical form.</summary>
internal static class EncodedForms
{
    internal static ColumnEncoding EncodingOf(CanonicalArena arena, int node) => arena.RecordRef(node).Kind switch
    {
        CanonicalKind.Constant => ColumnEncoding.Constant,
        CanonicalKind.Dictionary => ColumnEncoding.Dictionary,
        CanonicalKind.RunEnd => ColumnEncoding.RunEnd,
        _ => ColumnEncoding.Canonical,
    };

    /// <summary>The node holding the values contiguously: the node, or its materialized twin.</summary>
    internal static int Canonical(CanonicalArena arena, int node) => arena.RecordRef(node).Kind switch
    {
        CanonicalKind.Constant => arena.MaterializeConstant(node),
        CanonicalKind.Dictionary or CanonicalKind.RunEnd => arena.MaterializeEncoded(node),
        _ => node,
    };

    /// <summary>The codes of a dictionary node, and the node of its distinct values.</summary>
    internal static int Dictionary(CanonicalArena arena, int node, out ReadOnlySpan<uint> codes)
    {
        if (arena.RecordRef(node).Kind != CanonicalKind.Dictionary)
        {
            throw new InvalidOperationException("The column is not dictionary-encoded.");
        }

        CanonicalNode dictionary = arena.GetNode(node);
        codes = MemoryMarshal.Cast<byte, uint>(dictionary.Codes.Span);
        return dictionary.EncodedValuesIndex;
    }

    /// <summary>The run ends of a run-end node, and the node of its run values.</summary>
    internal static int RunEnd(CanonicalArena arena, int node, out ReadOnlySpan<uint> ends)
    {
        if (arena.RecordRef(node).Kind != CanonicalKind.RunEnd)
        {
            throw new InvalidOperationException("The column is not run-end-encoded.");
        }

        CanonicalNode runs = arena.GetNode(node);
        ends = MemoryMarshal.Cast<byte, uint>(runs.RunEnds.Span);
        return runs.EncodedValuesIndex;
    }
}
