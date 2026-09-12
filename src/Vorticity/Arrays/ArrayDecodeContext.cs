// Phase 1 contract §2.4 and §8.3. A CLASS, not a ref struct: it is held by the ScanContext across
// the whole batch and handed to every decoder.
//
// Two invariants live here and nowhere else:
//   * the array depth cap. Every decoder recurses through DecodeChild and never by calling
//     ArrayDecoder.Decode on a child, because that path is the only one that charges the cap;
//   * the validity rule of contract §2.6, whose body is the reference's `deserialize_validity`
//     plus the required constant collapse. Every decoder that can carry validity calls
//     DecodeValidity, and none of them re-implements it.
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
public sealed class ArrayDecodeContext
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
    /// Decodes child <paramref name="childIndex"/> of <paramref name="node"/>. Charges
    /// <see cref="VortexLimits.MaxArrayDepth"/>, bounds-checks the index, resolves the encoding and
    /// dispatches. <b>Every decoder recurses through this</b>, never through
    /// <see cref="ArrayDecoder.Decode"/>.
    /// </summary>
    /// <param name="node">The parent node.</param>
    /// <param name="childIndex">0-based child position.</param>
    /// <param name="childDType">The child's DType, derived by the parent per contract §2.5.</param>
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
    /// The shared validity rule of contract §2.6, whose body is the reference's
    /// <c>deserialize_validity</c> (vortex-array-0.86.1/src/validity.rs) plus the required
    /// constant collapse.
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
    /// <c>vortex.constant(false)</c> child (upstream's <c>validity_to_child</c> is the exact
    /// inverse), and an all-null column must report <see cref="ValidityKind.AllInvalid"/> so a
    /// caller can skip the per-row check docs/07-dotnet-mapping.md §1 promises.
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

        // Validity::DTYPE is non-nullable Bool, always, and its length is always the parent's.
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
    /// <param name="what">What is being measured, for the message.</param>
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
    /// <param name="what">What is being measured, for the message.</param>
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
        // Semantic array depth. The FlatBuffers table budget charged at load counts tables; these
        // are separate budgets and only the budget stops a shared-children DAG (contract §1.5).
        VortexLimits.CheckDepth(++_depth, VortexLimits.MaxArrayDepth, "Array");

        if (length < 0)
        {
            ArraysThrow.Format($"An array node cannot produce {length} rows.");
        }

        if (dtype.IsDefault)
        {
            throw new ArgumentException("A node cannot be decoded without a DType.", nameof(dtype));
        }

        // One bounds-checked array index plus one virtual call, per NODE (contract §2.4). Require,
        // not Get: the id TEXT is read out of the file's footer on demand and allocates, so only
        // the failing branch may ask for it.
        ArrayDecoder decoder = ArrayDecoderTable.Require(
            _scan, node.Encoding, node.EncodingSpecIndex);

        int result = decoder.Decode(this, in node, dtype, length);
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

        bool anySet = false;
        bool anyClear = false;
        for (int b = firstByte; b <= lastByte; b++)
        {
            int lo = b == firstByte ? start & 7 : 0;
            int hi = b == lastByte ? (int)((endExclusive - 1) & 7) + 1 : 8;
            byte mask = (byte)(((1 << hi) - 1) & ~((1 << lo) - 1));
            byte masked = (byte)(bits[b] & mask);

            anySet |= masked != 0;
            anyClear |= masked != mask;
            if (anySet && anyClear)
            {
                return ValidityBitmapShape.Mixed;
            }
        }

        return anySet ? ValidityBitmapShape.AllSet : ValidityBitmapShape.AllClear;
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

/// <summary>The three shapes a validity bitmap can collapse to (contract §2.6 rule 3).</summary>
internal enum ValidityBitmapShape : byte
{
    /// <summary>Every bit in range is zero: the array is all null.</summary>
    AllClear = 0,

    /// <summary>Every bit in range is one: the array has no nulls.</summary>
    AllSet = 1,

    /// <summary>Both values occur; the bitmap must be kept.</summary>
    Mixed = 2,
}
