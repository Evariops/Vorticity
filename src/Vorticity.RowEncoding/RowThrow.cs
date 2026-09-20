using System;
using Vorticity.Arrays;

namespace Vorticity.RowEncoding;

/// <summary>
/// The exceptions the row encoder raises, kept out of line so the hot loops stay inlineable.
/// </summary>
internal static class RowThrow
{
    /// <summary>Always throws: a canonical form the row format defines no ordering for.</summary>
    internal static VortexUnsupportedException UnsupportedCanonical(CanonicalNode node) =>
        throw new VortexUnsupportedException(
            node.DType.Kind.ToString().ToLowerInvariant(),
            VortexComponentKind.DType,
            $"Row encoding does not support canonical {node.Kind} arrays.");

    /// <summary>
    /// Always throws: a decimal value too wide for the key width its declared precision implies.
    /// </summary>
    internal static VortexFormatException DecimalDoesNotFit(int row, byte precision, int keyWidth) =>
        throw new VortexFormatException(
            $"Row {row} holds a decimal value that does not fit the {keyWidth}-byte key width " +
            $"implied by precision {precision}. Encoding it would produce a key that compares wrong.");
}
