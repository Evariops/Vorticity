using System;
using Vorticity.Arrays;
using Vorticity.Buffers;

namespace Vorticity.RowEncoding;

/// <summary>
/// One column's validity, flattened once per column to a span and a predicate so the encoder's
/// inner loop is a straight-line write rather than a walk through four representations.
/// </summary>
internal readonly ref struct RowValidity
{
    private readonly ReadOnlySpan<byte> _bits;
    private readonly int _bitOffset;
    private readonly State _state;

    private RowValidity(State state, ReadOnlySpan<byte> bits, int bitOffset)
    {
        _state = state;
        _bits = bits;
        _bitOffset = bitOffset;
    }

    private enum State : byte
    {
        AllValid = 0,
        AllInvalid = 1,
        Bitmap = 2,
    }

    /// <summary>Lets the caller take a loop with no per-row branch at all.</summary>
    internal bool AllValid => _state == State.AllValid;

    internal static RowValidity Resolve(CanonicalArena arena, CanonicalNode node)
    {
        Validity validity = node.Validity;
        switch (validity.Kind)
        {
            case ValidityKind.NonNullable:
            case ValidityKind.AllValid:
                return new RowValidity(State.AllValid, default, 0);

            case ValidityKind.AllInvalid:
                return new RowValidity(State.AllInvalid, default, 0);

            default:
                CanonicalNode bits = arena.GetNode(validity.CanonicalNodeIndex);
                VortexBuffer buffer = bits.Bits;
                return new RowValidity(State.Bitmap, buffer.Span, bits.BitOffset);
        }
    }

    /// <summary>Whether the 0-based row <paramref name="row"/> holds a value.</summary>
    internal bool IsValid(int row)
    {
        switch (_state)
        {
            case State.AllValid:
                return true;
            case State.AllInvalid:
                return false;
            default:
            {
                int bitIndex = _bitOffset + row;
                int byteIndex = bitIndex >> 3;
                if ((uint)byteIndex >= (uint)_bits.Length)
                {
                    throw new VortexFormatException(
                        $"Validity bit {bitIndex} is outside a {_bits.Length}-byte bitmap.");
                }

                return (_bits[byteIndex] & (1 << (bitIndex & 7))) != 0;
            }
        }
    }
}
