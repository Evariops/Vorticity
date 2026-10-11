using System;
using System.Buffers.Binary;
using Vorticity.Serialization.Protobuf;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.flat</c> <b>layout</b> metadata:
/// <c>message FlatLayoutMetadata { optional bytes array_encoding_tree = 1; }</c>.
/// </summary>
/// <remarks>
/// A <c>ref struct</c>: <see cref="ArrayEncodingTree"/> is a slice of the caller's metadata buffer,
/// never a copy. When the field is present the Array FlatBuffer is inlined here and the buffers are
/// read from the start of the segment, whose tail a reader then never reaches: the reference's
/// writer still puts the whole blob there, and so does this library's, which inlines only a tree
/// that declares no buffer (<see cref="BufferlessTree"/>). A reader that ignores the field reads the
/// segment as it always did.
/// </remarks>
internal readonly ref struct FlatLayoutMetadata
{
    private const string MessageName = "FlatLayoutMetadata";

    private readonly ReadOnlySpan<byte> _arrayEncodingTree;

    /// <summary>Creates flat-layout metadata with an inlined array encoding tree.</summary>
    /// <param name="arrayEncodingTree">The inlined Array FlatBuffer; may be empty but is present.</param>
    public FlatLayoutMetadata(ReadOnlySpan<byte> arrayEncodingTree)
    {
        _arrayEncodingTree = arrayEncodingTree;
        HasArrayEncodingTree = true;
    }

    /// <summary>True when tag 1 was present, even if its payload is zero bytes.</summary>
    public bool HasArrayEncodingTree { get; }

    /// <summary>
    /// The inlined Array FlatBuffer (tag 1), borrowed from the metadata buffer. Empty when
    /// <see cref="HasArrayEncodingTree"/> is false.
    /// </summary>
    public ReadOnlySpan<byte> ArrayEncodingTree => _arrayEncodingTree;

    /// <summary>Reads a <c>vortex.flat</c> layout metadata payload.</summary>
    /// <param name="layoutMetadata">The raw layout metadata bytes; empty means the field is absent.</param>
    /// <exception cref="VortexFormatException">The payload is malformed.</exception>
    public static FlatLayoutMetadata Read(ReadOnlySpan<byte> layoutMetadata)
    {
        ProtoReader reader = new ProtoReader(layoutMetadata);
        FlatLayoutMetadata result = default;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            if (field == 1)
            {
                result = new FlatLayoutMetadata(
                    MetadataProto.ReadBytes(ref reader, wire, MessageName, "array_encoding_tree"));
            }
            else
            {
                reader.SkipField(wire);
            }
        }

        return result;
    }

    /// <summary>Whether an Array FlatBuffer declares no buffer, so that it is its array whole.</summary>
    /// <param name="arrayTree">The Array FlatBuffer.</param>
    /// <exception cref="VortexFormatException">The tree is not a well-formed Array FlatBuffer.</exception>
    public static bool DeclaresNoBuffer(ReadOnlySpan<byte> arrayTree)
    {
        int tableBudget = VortexLimits.MaxFlatBufferTables;
        return ArrayView.Root(arrayTree, ref tableBudget).Buffers.IsEmpty;
    }

    /// <summary>
    /// The array tree of <paramref name="blob"/> when it declares no buffer, the one a flat layout
    /// over the blob inlines so that a read of the array needs nothing of the segment; empty
    /// otherwise. The blob stays whole in the segment, as the reference's writer leaves it.
    /// </summary>
    /// <param name="blob">An array blob as a writer assembled it, its FlatBuffer and length at the end.</param>
    public static ReadOnlySpan<byte> BufferlessTree(ReadOnlySpan<byte> blob)
    {
        int treeLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[^4..]);
        ReadOnlySpan<byte> tree = blob.Slice(blob.Length - 4 - treeLength, treeLength);
        return DeclaresNoBuffer(tree) ? tree : default;
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    public static void Write(ref ProtoWriter writer, in FlatLayoutMetadata value)
    {
        if (value.HasArrayEncodingTree)
        {
            // Always: the field has explicit presence, so a zero-length tree must still reach the
            // wire as an empty payload rather than vanishing into absence.
            writer.WriteBytesAlways(1, value._arrayEncodingTree);
        }
    }
}
