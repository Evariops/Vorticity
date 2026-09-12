// Layout vortex.flat — vortex-layout-0.86.1/src/layouts/flat/mod.rs. spec/METADATA.md.
using System;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.flat</c> <b>layout</b> metadata:
/// <c>message FlatLayoutMetadata { optional bytes array_encoding_tree = 1; }</c>.
/// </summary>
/// <remarks>
/// A <c>ref struct</c>: <see cref="ArrayEncodingTree"/> is a slice of the caller's metadata buffer,
/// never a copy. When the field is present the Array FlatBuffer is inlined here and the segment
/// holds buffers only — a different offset-reconstruction path, exercised by the corpus file
/// <c>containers/flat_inline_array_node.vortex</c> and by nothing a default writer produces
/// (docs/04-conformance.md §3).
/// </remarks>
public readonly ref struct FlatLayoutMetadata
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
