// Per-row validity, resolved ONCE per column instead of per row.
//
// The reference does the same thing for the same reason (vortex-row/src/codec.rs,
// `resolve_validity`): the all-valid case is the common one, and hoisting the decision out of the
// loop is what lets the encoder's inner loop be a straight-line write rather than a walk through
// four validity representations.
using System;
using Vorticity.Arrays;
using Vorticity.Buffers;

namespace Vorticity.RowEncoding;

/// <summary>One column's validity, flattened to a span and a predicate.</summary>
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

    /// <summary>
    /// <see langword="true"/> when no row is null, so the caller can take a loop with no per-row
    /// branch at all.
    /// </summary>
    internal bool AllValid => _state == State.AllValid;

    /// <summary>Resolves a node's validity.</summary>
    /// <param name="arena">The arena the node belongs to.</param>
    /// <param name="node">The node whose validity to read.</param>
    /// <returns>The flattened validity.</returns>
    /// <exception cref="VortexFormatException">A bitmap validity does not name a Bool node.</exception>
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

    /// <summary>Whether row <paramref name="row"/> holds a value.</summary>
    /// <param name="row">0-based row index.</param>
    /// <returns><see langword="true"/> when the row is not null.</returns>
    /// <exception cref="VortexFormatException">The bitmap is shorter than the column.</exception>
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
