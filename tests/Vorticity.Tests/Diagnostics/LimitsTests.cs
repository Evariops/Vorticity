using System;
using Vorticity;
using Xunit;

namespace Vorticity.Tests.Diagnostics;

public sealed class LimitsTests
{
    [Theory]
    [InlineData((byte)0, 1)]
    [InlineData((byte)4, 16)]
    [InlineData((byte)6, 64)]
    public void CheckAlignmentExponent_accepts_legal_exponents(byte exponent, int expected) =>
        Assert.Equal(expected, VortexLimits.CheckAlignmentExponent(exponent));

    [Theory]
    [InlineData((byte)7)]
    [InlineData((byte)255)]
    public void CheckAlignmentExponent_rejects_oversized_exponents(byte exponent) =>
        Assert.Throws<VortexFormatException>(() => VortexLimits.CheckAlignmentExponent(exponent));

    [Fact]
    public void UnsupportedException_names_id_and_kind()
    {
        var ex = new VortexUnsupportedException("vortex.pco", VortexComponentKind.Array);
        Assert.Contains("vortex.pco", ex.Message, StringComparison.Ordinal);
        Assert.Contains("array", ex.Message, StringComparison.Ordinal);
        Assert.Equal("vortex.pco", ex.ComponentId);
        Assert.Equal("array", ex.Kind);
    }
}
