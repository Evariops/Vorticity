// Throw helpers, kept out of line so the hot loops stay small enough to inline.
using System;
using Vorticity.Arrays;

namespace Vorticity.RowEncoding;

/// <summary>The exceptions the row encoder raises.</summary>
internal static class RowThrow
{
    /// <summary>A canonical form the row format defines no ordering for.</summary>
    /// <param name="node">The offending node.</param>
    /// <returns>Never; always throws.</returns>
    /// <exception cref="VortexUnsupportedException">Always.</exception>
    internal static VortexUnsupportedException UnsupportedCanonical(CanonicalNode node) =>
        throw new VortexUnsupportedException(
            node.DType.Kind.ToString().ToLowerInvariant(),
            VortexComponentKind.DType,
            $"Row encoding does not support canonical {node.Kind} arrays.");

    /// <summary>A decimal value too wide for the key width its declared precision implies.</summary>
    /// <param name="row">The offending row.</param>
    /// <param name="precision">The declared precision.</param>
    /// <param name="keyWidth">The key width in bytes.</param>
    /// <returns>Never; always throws.</returns>
    /// <exception cref="VortexFormatException">Always.</exception>
    internal static VortexFormatException DecimalDoesNotFit(int row, byte precision, int keyWidth) =>
        throw new VortexFormatException(
            $"Row {row} holds a decimal value that does not fit the {keyWidth}-byte key width " +
            $"implied by precision {precision}. Encoding it would produce a key that compares wrong.");
}
