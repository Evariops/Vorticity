using System;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// One entry of a zone map's aggregate list:
/// <c>message AggregateSpecProto { string id = 1; bytes options = 2; }</c>.
/// </summary>
/// <remarks>
/// A <c>ref struct</c> rather than the plain <c>readonly struct</c> the other codecs use, because
/// both fields are spans borrowed from the metadata buffer. Copying them would allocate per zone
/// map; <see cref="AggregateSpecList"/> is the durable form.
/// </remarks>
public readonly ref struct AggregateSpec
{
    private const string MessageName = "AggregateSpecProto";

    private readonly ReadOnlySpan<byte> _idUtf8;
    private readonly ReadOnlySpan<byte> _options;

    /// <summary>Creates an aggregate spec over caller-owned spans.</summary>
    /// <param name="idUtf8">The aggregate id as UTF-8 (tag 1).</param>
    /// <param name="options">The aggregate's opaque options payload (tag 2).</param>
    public AggregateSpec(ReadOnlySpan<byte> idUtf8, ReadOnlySpan<byte> options)
    {
        _idUtf8 = idUtf8;
        _options = options;
    }

    /// <summary>The aggregate id as UTF-8 (tag 1), e.g. <c>vortex.bounded_min</c>.</summary>
    public ReadOnlySpan<byte> IdUtf8 => _idUtf8;

    /// <summary>
    /// The aggregate's options (tag 2). Opaque here: each aggregate defines its own encoding, and
    /// it is not always Protobuf — see <see cref="AggregateRegistry.TryGetBoundLength"/>.
    /// </summary>
    public ReadOnlySpan<byte> Options => _options;

    /// <summary>
    /// Reads the nested <c>AggregateSpecProto</c> that the tag just returned by
    /// <see cref="ProtoReader.TryReadTag"/> introduces, consuming its length prefix and body.
    /// </summary>
    /// <param name="reader">Reader positioned immediately after a length-delimited field tag.</param>
    /// <exception cref="VortexFormatException">The body is malformed.</exception>
    public static AggregateSpec Read(scoped ref ProtoReader reader)
    {
        ProtoReader body = reader.ReadMessage();
        ReadOnlySpan<byte> id = default;
        ReadOnlySpan<byte> options = default;
        while (body.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    id = MetadataProto.ReadBytes(ref body, wire, MessageName, "id");
                    break;
                case 2:
                    options = MetadataProto.ReadBytes(ref body, wire, MessageName, "options");
                    break;
                default:
                    body.SkipField(wire);
                    break;
            }
        }

        // An absent id is not rejected: a string field with implicit presence puts absent and empty
        // on the same bytes, and an id nobody recognizes only disables that aggregate's pruning.
        // AggregateRegistry.Resolve maps it to AggregateId.Unknown.
        return new AggregateSpec(id, options);
    }

    /// <summary>Writes this spec as a nested message under <paramref name="fieldNumber"/>.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="fieldNumber">The enclosing message's field number for this spec.</param>
    /// <param name="value">The spec.</param>
    public static void Write(ref ProtoWriter writer, int fieldNumber, in AggregateSpec value)
    {
        ProtoWriter.MessageScope scope = writer.BeginMessage(fieldNumber);
        writer.WriteStringUtf8(1, value._idUtf8);
        writer.WriteBytes(2, value._options);
        scope.End();
    }
}
