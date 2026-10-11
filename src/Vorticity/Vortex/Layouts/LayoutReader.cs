using System;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Layouts;

/// <summary>One reader per layout encoding. Stateless and thread-safe; a single instance is shared.</summary>
/// <remarks>
/// Reading is split in two so that input and decoding stay apart:
/// <see cref="RegisterSegments"/> registers exactly the segments <see cref="Execute"/> will read,
/// and <see cref="Execute"/> reads only registered ones. That pair is what lets a batch issue a
/// single coalesced read; break it and the scan either fails on an unpopulated segment or silently
/// issues a second read.
/// </remarks>
internal abstract class LayoutReader
{
    /// <summary>The registry slot this reader occupies.</summary>
    public abstract LayoutEncodingId EncodingId { get; }

    /// <summary>The wire id, UTF-8. A <c>u8</c> literal, never allocated per call.</summary>
    public abstract ReadOnlySpan<byte> IdUtf8 { get; }

    /// <summary>
    /// Phase 1 of 2: registers every segment this node and its selected descendants need for
    /// <paramref name="rows"/>. No I/O, no decoding, no managed allocation.
    /// </summary>
    /// <param name="node">The layout node, whose <see cref="LayoutNode.Encoding"/> is <see cref="EncodingId"/>.</param>
    /// <param name="rows">The wanted rows, <b>local to this node</b>: 0-based within its own rows.</param>
    /// <param name="fields">Which fields of a struct subtree the caller wants.</param>
    /// <param name="segments">The caller's request set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="segments"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rows"/> escapes the node.</exception>
    /// <exception cref="VortexFormatException">The node is malformed.</exception>
    /// <exception cref="VortexUnsupportedException">A selected descendant uses a layout this build does not read.</exception>
    public abstract void RegisterSegments(
        in LayoutNode node, RowRange rows, in FieldMask fields, SegmentRequestSet segments);

    /// <summary>
    /// Phase 2 of 2: decodes, synchronously, from segments the caller has already materialized.
    /// </summary>
    /// <param name="node">The layout node.</param>
    /// <param name="rows">The wanted rows, local to this node.</param>
    /// <param name="fields">Which fields of a struct subtree the caller wants.</param>
    /// <param name="context">The scan's arenas, buffers, options and decoder table.</param>
    /// <returns>The canonical node's index in <see cref="ScanContext.Canonical"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rows"/> escapes the node.</exception>
    /// <exception cref="VortexFormatException">The node or the data it points at is malformed.</exception>
    /// <exception cref="VortexUnsupportedException">A selected descendant uses a component this build does not read.</exception>
    public abstract int Execute(
        in LayoutNode node, RowRange rows, in FieldMask fields, ScanContext context);

    /// <summary>Rejects a row range that escapes <paramref name="node"/>. A caller error, not a file one.</summary>
    /// <param name="node">The node the range is local to.</param>
    /// <param name="rows">The range.</param>
    protected static void CheckRange(in LayoutNode node, RowRange rows)
    {
        if (rows.End > node.RowCount)
        {
            LayoutsThrow.RangeOutsideNode(rows, node.RowCount);
        }
    }

    /// <summary>Narrows a batch's row count to an <see cref="int"/>.</summary>
    /// <param name="rows">The range.</param>
    /// <returns>Its length.</returns>
    /// <remarks>
    /// A batch never exceeds <see cref="int.MaxValue"/> rows; a caller asking for more gets an
    /// <see cref="ArgumentOutOfRangeException"/>, because the file is not at fault.
    /// </remarks>
    protected static int BatchLength(RowRange rows)
    {
        long length = rows.Length;
        if (length > int.MaxValue)
        {
            LayoutsThrow.BatchTooLarge(length);
        }

        return (int)length;
    }

    /// <summary>Narrows a whole node's row count to an <see cref="int"/>.</summary>
    /// <param name="node">The node.</param>
    /// <returns>Its row count.</returns>
    /// <exception cref="VortexFormatException">
    /// The node claims more rows than one array can hold. Unlike <see cref="BatchLength"/> this is
    /// the file's fault: a single serialized array cannot carry more than <see cref="int.MaxValue"/>
    /// rows, whatever the layout claims.
    /// </exception>
    protected static int NodeLength(in LayoutNode node)
    {
        long rows = node.RowCount;
        if (rows > int.MaxValue)
        {
            LayoutsThrow.Format($"A single layout node cannot carry {rows} rows.");
        }

        return (int)rows;
    }

    /// <summary>Registers segment <paramref name="which"/> of <paramref name="node"/>.</summary>
    /// <param name="node">The node owning the segment id.</param>
    /// <param name="which">Index into <see cref="LayoutNode.Segments"/>.</param>
    /// <param name="segments">The request set.</param>
    /// <returns>The slot the set assigned.</returns>
    protected static int RegisterSegment(in LayoutNode node, int which, SegmentRequestSet segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return segments.Add(SpecOf(in node, which));
    }

    /// <summary>The bytes of segment <paramref name="which"/>, from an already-populated set.</summary>
    /// <param name="node">The node owning the segment id.</param>
    /// <param name="which">Index into <see cref="LayoutNode.Segments"/>.</param>
    /// <param name="context">The scan holding the populated request set.</param>
    /// <returns>The segment's view, valid until the batch is released.</returns>
    /// <remarks>
    /// Re-registering an already-registered spec is a lookup, not a second read: the set is keyed
    /// by <c>(Offset, Length)</c> and returns the existing slot. That is how a stateless reader
    /// finds, in <c>Execute</c>, the slot it created in <c>RegisterSegments</c>.
    /// </remarks>
    protected static VortexBuffer SegmentBuffer(in LayoutNode node, int which, ScanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        SegmentRequestSet segments = context.Segments;
        int slot = segments.Add(SpecOf(in node, which));
        VortexBuffer buffer = segments.GetBuffer(slot);

        // What is decoded out of these bytes can be views onto them, and a decode retained past
        // this batch has to keep the segment alive: while one runs, the context notes whose bytes
        // these are.
        if (context.IsRetaining)
        {
            context.NoteSegment(segments.GetOwner(slot));
        }

        return buffer;
    }

    /// <summary>Registers one child's segments.</summary>
    /// <param name="child">The child node.</param>
    /// <param name="rows">The child-local row range.</param>
    /// <param name="fields">The child's field mask.</param>
    /// <param name="segments">The request set.</param>
    protected static void RegisterChild(
        in LayoutNode child, RowRange rows, in FieldMask fields, SegmentRequestSet segments) =>
        LayoutReaderTable.Require(in child).RegisterSegments(in child, rows, in fields, segments);

    /// <summary>Executes one child that is not the predicate's column, whatever the parent is.</summary>
    /// <param name="child">The child node.</param>
    /// <param name="rows">The child-local row range.</param>
    /// <param name="fields">The child's field mask.</param>
    /// <param name="context">The scan context.</param>
    /// <returns>The child's canonical node index.</returns>
    /// <remarks>
    /// The default is to clear the pushed comparison, and it is the safe default rather than the
    /// convenient one. A layout's children are not always its rows: a dictionary layout's are its
    /// codes and its values, a list layout's are its offsets and its elements. A leaf under one of
    /// those answering a predicate would hand its parent a boolean column where the format promises
    /// integers. A reader whose children carry the same rows as itself says so with
    /// <see cref="ExecuteRowChild"/>.
    /// <para>
    /// Encoded delivery is cleared for the same reason: the parent reads such a child's values,
    /// and a codes child left as a dictionary would be refused where a primitive is required.
    /// </para>
    /// </remarks>
    protected static int ExecuteChild(
        in LayoutNode child, RowRange rows, in FieldMask fields, ScanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        bool outer = context.PredicateAtNode;
        bool encoded = context.KeepEncodings;
        context.PredicateAtNode = false;
        context.KeepEncodings = false;
        try
        {
            return LayoutReaderTable.Require(in child).Execute(in child, rows, in fields, context);
        }
        finally
        {
            context.PredicateAtNode = outer;
            context.KeepEncodings = encoded;
        }
    }

    /// <summary>Executes one child that carries the same rows as its parent.</summary>
    /// <param name="child">The child node.</param>
    /// <param name="rows">The child-local row range.</param>
    /// <param name="fields">The child's field mask.</param>
    /// <param name="context">The scan context.</param>
    /// <returns>The child's canonical node index.</returns>
    /// <remarks>
    /// For the two readers that re-partition or wrap a column without changing what it holds: a
    /// chunked layout's children are ranges of its rows and a zoned layout's data child is the
    /// column itself, so a predicate pushed at the parent is still about what the child produces.
    /// </remarks>
    protected static int ExecuteRowChild(
        in LayoutNode child, RowRange rows, in FieldMask fields, ScanContext context) =>
        LayoutReaderTable.Require(in child).Execute(in child, rows, in fields, context);

    private static SegmentSpec SpecOf(in LayoutNode node, int which)
    {
        ReadOnlySpan<uint> ids = node.Segments;
        if ((uint)which >= (uint)ids.Length)
        {
            LayoutsThrow.Format($"A layout node has {ids.Length} segments; segment {which} was asked for.");
        }

        // Bounds-checked once already, at parse time: a segment id can only name a spec the footer
        // declares.
        ReadOnlySpan<SegmentSpec> specs = node.Tree.File.SegmentSpecs;
        uint id = ids[which];
        if (id >= (uint)specs.Length)
        {
            LayoutsThrow.MissingSegment(id, specs.Length);
        }

        return specs[(int)id];
    }
}
