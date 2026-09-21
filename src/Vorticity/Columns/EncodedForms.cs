using System;
using Vorticity.Arrays;

namespace Vorticity;

/// <summary>The encoded forms a decoded node can keep, and their decode into canonical form.</summary>
internal static class EncodedForms
{
    internal static ColumnEncoding EncodingOf(CanonicalArena arena, int node) =>
        arena.RecordRef(node).Kind == CanonicalKind.Constant ? ColumnEncoding.Constant : ColumnEncoding.Canonical;

    /// <summary>The node holding the values contiguously: the node, or its materialized twin.</summary>
    internal static int Canonical(CanonicalArena arena, int node) =>
        arena.RecordRef(node).Kind == CanonicalKind.Constant ? arena.MaterializeConstant(node) : node;

    /// <summary>The codes of a dictionary node, and the node of its distinct values.</summary>
    internal static int Dictionary(CanonicalArena arena, int node, out ReadOnlySpan<uint> codes) =>
        throw new InvalidOperationException("The column is not dictionary-encoded.");

    /// <summary>The run ends of a run-end node, and the node of its run values.</summary>
    internal static int RunEnd(CanonicalArena arena, int node, out ReadOnlySpan<uint> ends) =>
        throw new InvalidOperationException("The column is not run-end-encoded.");
}
