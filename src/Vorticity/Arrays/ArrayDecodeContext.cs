using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Vorticity.Buffers;
using Vorticity.File;
using Vorticity.Types;

namespace Vorticity.Arrays;

/// <summary>
/// The per-batch decode facade: arenas, options, the encoding tables, and the two shared rules
/// (child recursion and validity) every decoder is required to route through.
/// </summary>
/// <remarks>
/// A class rather than a ref struct, because the scan holds it across the whole batch and hands it
/// to every decoder. Two invariants live here and nowhere else: the array depth cap, which is
/// charged only on the child-recursion path, and the validity rule, which every decoder that can
/// carry validity calls instead of re-implementing it.
/// </remarks>
internal sealed class ArrayDecodeContext
{
    private readonly ScanContext _scan;
    private int _depth;

    internal ArrayDecodeContext(ScanContext scan)
    {
        _scan = scan;
    }

    /// <summary>The scan this context belongs to.</summary>
    public ScanContext Scan => _scan;

    /// <summary>The batch's serialized-node arena.</summary>
    public ArrayNodeArena Nodes => _scan.Nodes;

    /// <summary>The batch's canonical arena.</summary>
    public CanonicalArena Canonical => _scan.Canonical;

    /// <summary>The scan's dtype arena. Derive every DType here; see <see cref="ScanContext.Types"/>.</summary>
    public DTypeArena Types => _scan.Types;

    /// <summary>The batch's scalar store.</summary>
    public ScalarStore Scalars => _scan.Scalars;

    /// <summary>Read-time policy, copied from the file.</summary>
    public VortexReadOptions Options => _scan.Options;

    /// <summary>Maps a wire <c>u16</c> encoding spec index to a resolved id.</summary>
    public ReadOnlySpan<ArrayEncodingId> ArrayEncodings => _scan.ArrayEncodings;

    /// <summary>
    /// Loads an array blob into <see cref="Nodes"/>, using this scan's encoding table.
    /// Convenience for the layout readers, which would otherwise repeat the same three arguments.
    /// </summary>
    /// <param name="segment">The whole array-blob segment.</param>
    /// <exception cref="VortexFormatException">The blob is malformed.</exception>
    public void LoadBlob(VortexBuffer segment) =>
        ArrayBlobReader.Load(_scan.Nodes, segment, _scan.ArrayEncodings);

    /// <summary>
    /// Loads an array blob whose <c>Array</c> FlatBuffer was inlined in the layout's
    /// <c>array_encoding_tree</c> metadata.
    /// </summary>
    /// <param name="arrayTree">The inlined <c>Array</c> FlatBuffer.</param>
    /// <param name="segment">The segment holding the data buffers.</param>
    /// <exception cref="VortexFormatException">The blob is malformed.</exception>
    public void LoadBlob(ReadOnlySpan<byte> arrayTree, VortexBuffer segment) =>
        ArrayBlobReader.Load(_scan.Nodes, arrayTree, segment, _scan.ArrayEncodings);

    /// <summary>
    /// Decodes an array node at the top of a traversal - the root of a blob, or a chunk of one.
    /// Resets the depth budget, so it is not the way to reach a child.
    /// </summary>
    /// <param name="node">The serialized node.</param>
    /// <param name="dtype">The DType this node must produce.</param>
    /// <param name="length">The row count this node must produce.</param>
    /// <returns>The canonical node's index in <see cref="Canonical"/>.</returns>
    /// <exception cref="VortexFormatException">The node violates its encoding's contract.</exception>
    /// <exception cref="VortexUnsupportedException">This build does not decode that encoding.</exception>
    public int Decode(in ArrayNode node, DType dtype, int length)
    {
        _depth = 0;
        return DecodeNode(in node, dtype, length);
    }

    /// <summary>Alias for <see cref="Decode"/>, for call sites that read better naming the root.</summary>
    /// <param name="node">The serialized root node.</param>
    /// <param name="dtype">The DType this node must produce.</param>
    /// <param name="length">The row count this node must produce.</param>
    /// <returns>The canonical node's index in <see cref="Canonical"/>.</returns>
    public int DecodeRoot(in ArrayNode node, DType dtype, int length) => Decode(in node, dtype, length);

    /// <summary>
    /// Decodes only <paramref name="wanted"/> rows of a root node. Resets the depth budget.
    /// </summary>
    /// <param name="node">The serialized root node.</param>
    /// <param name="dtype">The DType this node must produce.</param>
    /// <param name="length">The row count the node would produce, which bounds the indices.</param>
    /// <param name="wanted">Row indices, strictly ascending, all in <c>[0, length)</c>.</param>
    /// <returns>The canonical node's index, holding <c>wanted.Length</c> rows.</returns>
    public int DecodeRootSelected(
        in ArrayNode node, DType dtype, int length, ReadOnlySpan<int> wanted)
    {
        _depth = 0;
        return DecodeNodeSelected(in node, dtype, length, wanted);
    }

    /// <summary>
    /// Decodes only <paramref name="wanted"/> rows of child <paramref name="childIndex"/>. The
    /// selective counterpart of <see cref="DecodeChild"/>, and the only way a specialized decoder
    /// may push a selection into a child.
    /// </summary>
    /// <param name="node">The parent node.</param>
    /// <param name="childIndex">Which child.</param>
    /// <param name="childDType">The DType the child must produce.</param>
    /// <param name="childLength">The row count the child would produce.</param>
    /// <param name="wanted">Row indices into the child, strictly ascending.</param>
    /// <returns>The canonical node's index, holding <c>wanted.Length</c> rows.</returns>
    public int DecodeChildSelected(
        in ArrayNode node, int childIndex, DType childDType, int childLength,
        ReadOnlySpan<int> wanted)
    {
        ArrayNode child = node.GetChild(childIndex);
        return DecodeNodeSelected(in child, childDType, childLength, wanted);
    }

    /// <summary>
    /// Decodes child <paramref name="childIndex"/> of <paramref name="node"/>. Charges
    /// <see cref="VortexLimits.MaxArrayDepth"/>, bounds-checks the index, resolves the encoding and
    /// dispatches. <b>Every decoder recurses through this</b>, never through
    /// <see cref="ArrayDecoder.Decode"/>.
    /// </summary>
    /// <param name="node">The parent node.</param>
    /// <param name="childIndex">0-based child position.</param>
    /// <param name="childDType">The child's DType, which only the parent can derive.</param>
    /// <param name="childLength">The child's row count, also derived by the parent.</param>
    /// <returns>The child's canonical node index.</returns>
    /// <exception cref="VortexFormatException">The index is out of range or the child is malformed.</exception>
    /// <exception cref="VortexUnsupportedException">This build does not decode the child's encoding.</exception>
    public int DecodeChild(in ArrayNode node, int childIndex, DType childDType, int childLength)
    {
        ArrayNode child = node.GetChild(childIndex);
        return DecodeNode(in child, childDType, childLength);
    }

    /// <summary>
    /// Decodes child <paramref name="childIndex"/> of <paramref name="node"/> once for the scan
    /// rather than once per batch, and hands the caller a window onto it.
    /// </summary>
    /// <param name="node">The parent node.</param>
    /// <param name="childIndex">0-based child position.</param>
    /// <param name="childDType">The child's DType.</param>
    /// <param name="childLength">The child's row count.</param>
    /// <returns>The child's index in the batch's arena, holding every row.</returns>
    /// <remarks>
    /// <para>
    /// For a child <b>every row of the parent shares</b>: a dictionary's values, where a batch that
    /// wants one row still needs the entry that row points at, so the selection never narrows it and
    /// each batch decodes the whole of it again. On a chunk larger than the batch that is the same
    /// waste the flat reader's retention exists for -- but the reader does not reach here, because
    /// it retains only for an encoding with no specialized selective decode, and a dictionary has
    /// one for its codes.
    /// </para>
    /// <para>
    /// Retained only inside a reader-opened scope, which is what says the node outlives the batch,
    /// and never inside another retained decode, whose arena is already the one being filled.
    /// Outside either, this is <see cref="DecodeChild"/> and nothing more. The window costs a
    /// handful of records: a slice's buffers are views onto the retained arena, whose entry cannot
    /// be evicted while the batch that touched it is alive.
    /// </para>
    /// <para>
    /// Internal where <see cref="DecodeChild"/> is public, because what it promises is a property
    /// of this scan's retention and not of the decoding contract: a decoder outside this assembly
    /// would be given a lifetime rule to honour in exchange for a gain only some encodings can
    /// take.
    /// </para>
    /// </remarks>
    internal int DecodeChildShared(in ArrayNode node, int childIndex, DType childDType, int childLength)
    {
        ArrayNode child = node.GetChild(childIndex);
        if (_scan.NodeCheckScope is not uint segment || _scan.IsRetaining ||
            ScanContext.ChildKey(segment, child.Index) is not long key)
        {
            return DecodeNode(in child, childDType, childLength);
        }

        if (!_scan.TryGetRetained(key, out CanonicalArena held, out int retained))
        {
            held = _scan.BeginRetainedDecode();
            retained = -1;
            try
            {
                retained = DecodeNode(in child, childDType, childLength);
            }
            finally
            {
                _scan.EndRetainedDecode(key, retained);
            }
        }

        return Layouts.CanonicalSlice.SliceAcross(held, Canonical, retained, 0, childLength);
    }

    /// <summary>
    /// Opens a scope in which one node's validation walk may be remembered across the batches that
    /// visit it.
    /// </summary>
    /// <param name="segmentId">The segment the blob being decoded came from.</param>
    /// <returns>What was in force, to be restored by <see cref="EndNodeCheckScope"/>.</returns>
    /// <remarks>
    /// Opened by the reader and never by a decoder, because only the reader knows the two facts
    /// that make remembering sound: which segment the blob came from - a decoder sees nodes, not
    /// segments - and whether the node is bigger than the batch, below which it is visited once and
    /// there is nothing to remember.
    /// </remarks>
    internal uint? BeginNodeCheckScope(uint segmentId) => _scan.BeginNodeCheckScope(segmentId);

    /// <summary>Restores what <see cref="BeginNodeCheckScope"/> displaced.</summary>
    /// <param name="previous">Its return value.</param>
    internal void EndNodeCheckScope(uint? previous) => _scan.EndNodeCheckScope(previous);

    /// <summary>
    /// Whether an O(n) validation of <paramref name="node"/> has already run and passed in this
    /// scan, so this decode may skip it.
    /// </summary>
    /// <param name="node">The node about to be walked.</param>
    /// <returns><see langword="false"/> whenever there is any doubt, so the walk runs.</returns>
    /// <remarks>
    /// <para>
    /// A check that is a property of the node may be asked once. That the run ends of a run-end
    /// array ascend, or that patch indices ascend, are facts about bytes that do not change between
    /// batches, and re-establishing them can dominate a selective take of a node that many batches
    /// visit.
    /// </para>
    /// <para>
    /// It is not a way to skip validation. Outside a reader-opened scope this returns false, so a
    /// full scan, a filter and every first visit walk in full; only a node already walked and
    /// passed in this same scan is spared, and only from the second visit onward. A malformed file
    /// still throws on its first batch, which is the batch that would have thrown anyway.
    /// </para>
    /// </remarks>
    public bool IsNodeChecked(in ArrayNode node) =>
        _scan.NodeCheckScope is uint segment &&
        ScanContext.NodeCheckKey(segment, node.Index) is long key &&
        _scan.IsNodeChecked(key);

    /// <summary>Records that <paramref name="node"/>'s validation walk ran and passed.</summary>
    /// <param name="node">The node just walked.</param>
    public void MarkNodeChecked(in ArrayNode node)
    {
        if (_scan.NodeCheckScope is uint segment &&
            ScanContext.NodeCheckKey(segment, node.Index) is long key)
        {
            _scan.MarkNodeChecked(key);
        }
    }

    /// <summary>
    /// The shared validity rule: resolve a node's optional validity child into a
    /// <see cref="Validity"/>, collapsing a constant bitmap.
    /// </summary>
    /// <param name="node">The parent node.</param>
    /// <param name="firstValidityChildIndex">
    /// The serialized index the validity child would occupy: the number of non-validity children
    /// that precede it.
    /// </param>
    /// <param name="nullability">The inherited DType's nullability, which disambiguates an absent child.</param>
    /// <param name="length">The parent's row count; the validity child must match it.</param>
    /// <returns>
    /// <see cref="Validity.NonNullable"/> or <see cref="Validity.AllValid"/> when the child is
    /// absent; <see cref="Validity.AllInvalid"/> or <see cref="Validity.AllValid"/> when the
    /// decoded bitmap turns out to be constant; otherwise <see cref="Validity.Bitmap"/>.
    /// </returns>
    /// <remarks>
    /// The validity array may itself be encoded - constant, dict, runend, bitpacked over bool - so
    /// this goes through the normal decode dispatch and never a bitmap fast path. The collapse is
    /// required, not an optimization: <c>AllInvalid</c> reaches the wire as a
    /// <c>vortex.constant(false)</c> child, and an all-null column must report
    /// <see cref="ValidityKind.AllInvalid"/> so that a caller can skip the per-row check instead of
    /// asking every row.
    /// </remarks>
    /// <exception cref="VortexFormatException">
    /// The child count is neither <paramref name="firstValidityChildIndex"/> nor one more, or the
    /// decoded child is not a Bool of exactly <paramref name="length"/> rows.
    /// </exception>
    public Validity DecodeValidity(
        in ArrayNode node,
        int firstValidityChildIndex,
        Nullability nullability,
        int length)
    {
        int childCount = node.ChildCount;

        if (childCount == firstValidityChildIndex)
        {
            return Validity.FromNullability(nullability);
        }

        if (childCount != firstValidityChildIndex + 1)
        {
            ArraysThrow.Format(
                $"An array with validity has {firstValidityChildIndex} or " +
                $"{firstValidityChildIndex + 1} children; this one has {childCount}.");
        }

        // A validity child is always a non-nullable Bool of exactly the parent's length.
        DType boolType = Types.Bool(Nullability.NonNullable);
        int decoded = DecodeChild(in node, firstValidityChildIndex, boolType, length);

        CanonicalNode bits = Canonical.GetNode(decoded);
        if (bits.Kind != CanonicalKind.Bool)
        {
            ArraysThrow.Format($"A validity child decoded to {bits.Kind}, not Bool.");
        }

        if (bits.Length != length)
        {
            ArraysThrow.Format(
                $"A validity child of {bits.Length} rows cannot describe an array of {length}.");
        }

        if (length == 0)
        {
            return Validity.AllValid;
        }

        return ClassifyValidityBits(bits.Bits.Span, bits.BitOffset, length) switch
        {
            ValidityBitmapShape.AllClear => Validity.AllInvalid,
            ValidityBitmapShape.AllSet => Validity.AllValid,
            _ => Validity.Bitmap(decoded),
        };
    }

    /// <summary>
    /// Allocates a canonical node of <paramref name="kind"/> with only the four common fields set.
    /// Prefer the typed <c>CanonicalArena.AddXxx</c> builders, which validate their buffers.
    /// </summary>
    /// <param name="kind">The canonical form.</param>
    /// <param name="dtype">The DType the node produces.</param>
    /// <param name="length">Row count.</param>
    /// <param name="validity">Per-row validity.</param>
    /// <returns>The new node's index.</returns>
    public int NewCanonical(CanonicalKind kind, DType dtype, int length, Validity validity) =>
        Canonical.AddBare(kind, dtype, length, validity);

    /// <summary>The id text of encoding spec <paramref name="specIndex"/>, for an exception message.</summary>
    /// <param name="specIndex">The wire <c>u16</c> from <c>ArrayNode.encoding</c>.</param>
    public string GetEncodingIdText(int specIndex) => _scan.GetArrayEncodingIdText(specIndex);

    // ---------------------------------------------------------------- shared validation helpers

    /// <summary>Requires an exact child count.</summary>
    /// <param name="actual">The node's child count.</param>
    /// <param name="expected">What the encoding's contract requires.</param>
    /// <param name="encodingId">The encoding id, for the message.</param>
    /// <exception cref="VortexFormatException">The counts differ.</exception>
    public static void RequireChildCount(int actual, int expected, string encodingId)
    {
        if (actual != expected)
        {
            ThrowChildCount(actual, expected, expected, encodingId);
        }
    }

    /// <summary>Requires a child count in an inclusive range.</summary>
    /// <param name="actual">The node's child count.</param>
    /// <param name="min">Smallest legal count.</param>
    /// <param name="max">Largest legal count.</param>
    /// <param name="encodingId">The encoding id, for the message.</param>
    /// <exception cref="VortexFormatException">The count is outside the range.</exception>
    public static void RequireChildCount(int actual, int min, int max, string encodingId)
    {
        if (actual < min || actual > max)
        {
            ThrowChildCount(actual, min, max, encodingId);
        }
    }

    /// <summary>Requires an exact buffer count.</summary>
    /// <param name="actual">The node's buffer count.</param>
    /// <param name="expected">What the encoding's contract requires.</param>
    /// <param name="encodingId">The encoding id, for the message.</param>
    /// <exception cref="VortexFormatException">The counts differ.</exception>
    public static void RequireBufferCount(int actual, int expected, string encodingId)
    {
        if (actual != expected)
        {
            ThrowBufferCount(actual, expected, encodingId);
        }
    }

    /// <summary>
    /// Narrows a wire <c>u64</c> length to <see cref="int"/>. Every wire length goes through this:
    /// a <c>u64</c> row count is attacker-controlled and must never size an allocation unchecked.
    /// </summary>
    /// <param name="value">The wire value.</param>
    /// <param name="what">What the value counts, for the message.</param>
    /// <returns>The narrowed length.</returns>
    /// <exception cref="VortexFormatException">The value does not fit an <see cref="int"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CheckedLength(ulong value, string what)
    {
        if (value > int.MaxValue)
        {
            ThrowLength(value, what);
        }

        return (int)value;
    }

    /// <summary>
    /// Multiplies two non-negative lengths. Every <c>len * width</c> goes through this: upstream
    /// leaves several of them unchecked and a wrap produces a short buffer that reads out of bounds.
    /// </summary>
    /// <param name="a">First factor.</param>
    /// <param name="b">Second factor.</param>
    /// <param name="what">What the product counts, for the message.</param>
    /// <returns>The product.</returns>
    /// <exception cref="VortexFormatException">Either factor is negative, or the product overflows.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CheckedMultiply(int a, int b, string what)
    {
        long product = (long)a * b;
        if (a < 0 || b < 0 || product > int.MaxValue)
        {
            ThrowMultiply(a, b, what);
        }

        return (int)product;
    }

    // ------------------------------------------------------------------------------ internals

    internal void ResetBatch() => _depth = 0;

    private int DecodeNode(in ArrayNode node, DType dtype, int length)
    {
        // Semantic array depth. The FlatBuffers table budget charged at load counts tables, which
        // is a different quantity: only this one bounds how deep a decode recurses.
        VortexLimits.CheckDepth(++_depth, VortexLimits.MaxArrayDepth, "Array");

        if (length < 0)
        {
            ArraysThrow.Format($"An array node cannot produce {length} rows.");
        }

        if (dtype.IsDefault)
        {
            throw new ArgumentException("A node cannot be decoded without a DType.", nameof(dtype));
        }

        // One bounds-checked array index plus one virtual call, per node. Require rather than Get:
        // the id text is read out of the file's footer on demand and allocates, so only the
        // failing branch may ask for it.
        ArrayDecoder decoder = ArrayDecoderTable.Require(
            _scan, node.Encoding, node.EncodingSpecIndex);

        int result = decoder.Decode(this, in node, dtype, length);
        _depth--;
        return result;
    }

    private int DecodeNodeSelected(
        in ArrayNode node, DType dtype, int length, ReadOnlySpan<int> wanted)
    {
        VortexLimits.CheckDepth(++_depth, VortexLimits.MaxArrayDepth, "Array");

        if (length < 0)
        {
            ArraysThrow.Format($"An array node cannot produce {length} rows.");
        }

        if (dtype.IsDefault)
        {
            throw new ArgumentException("A node cannot be decoded without a DType.", nameof(dtype));
        }

        ArrayDecoder decoder = ArrayDecoderTable.Require(
            _scan, node.Encoding, node.EncodingSpecIndex);

        int result = decoder.DecodeSelected(this, in node, dtype, length, wanted);
        _depth--;
        return result;
    }

    /// <summary>Whether a bit range is all zero, all one, or mixed. O(bits/8), no allocation.</summary>
    internal static ValidityBitmapShape ClassifyValidityBits(ReadOnlySpan<byte> bits, int bitOffset, int length)
    {
        int start = bitOffset;
        long endExclusive = (long)bitOffset + length;
        int firstByte = start >> 3;
        int lastByte = (int)((endExclusive - 1) >> 3);

        if (lastByte >= bits.Length)
        {
            ArraysThrow.Format(
                $"A validity bitmap of {length} bits at offset {bitOffset} needs {lastByte + 1} " +
                $"bytes; the buffer holds {bits.Length}.");
        }

        Decoders.Canonical.BitmapKernels.Classify(
            bits, start, length, out bool anySet, out bool anyClear);
        return (anySet, anyClear) switch
        {
            (true, true) => ValidityBitmapShape.Mixed,
            (true, false) => ValidityBitmapShape.AllSet,
            _ => ValidityBitmapShape.AllClear,
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowChildCount(int actual, int min, int max, string encodingId) =>
        throw new VortexFormatException(
            min == max
                ? $"{encodingId} requires exactly {min} children; this node has {actual}."
                : $"{encodingId} requires {min} to {max} children; this node has {actual}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowBufferCount(int actual, int expected, string encodingId) =>
        throw new VortexFormatException(
            $"{encodingId} requires exactly {expected} buffers; this node has {actual}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowLength(ulong value, string what) =>
        throw new VortexFormatException(
            $"{what} is {value}, which does not fit a 32-bit length.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowMultiply(int a, int b, string what) =>
        throw new VortexFormatException(
            $"{what} would need {(long)a * b} bytes ({a} x {b}), which does not fit a 32-bit length.");
}

/// <summary>The three shapes a validity bitmap can collapse to.</summary>
internal enum ValidityBitmapShape : byte
{
    /// <summary>Every bit in range is zero: the array is all null.</summary>
    AllClear = 0,

    /// <summary>Every bit in range is one: the array has no nulls.</summary>
    AllSet = 1,

    /// <summary>Both values occur; the bitmap must be kept.</summary>
    Mixed = 2,
}
