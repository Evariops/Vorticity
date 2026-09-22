using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Types;

/// <summary>
/// PType is a <c>uint8</c> on the wire, so 245 of its 256 possible
/// values are undefined and every one of them is reachable from a file.
/// </summary>
public sealed class PTypeTests
{
    [Theory]
    [InlineData(PType.U8, 1)]
    [InlineData(PType.U16, 2)]
    [InlineData(PType.U32, 4)]
    [InlineData(PType.U64, 8)]
    [InlineData(PType.I8, 1)]
    [InlineData(PType.I16, 2)]
    [InlineData(PType.I32, 4)]
    [InlineData(PType.I64, 8)]
    [InlineData(PType.F16, 2)]
    [InlineData(PType.F32, 4)]
    [InlineData(PType.F64, 8)]
    internal void ByteWidthCoversEveryDefinedTag(PType ptype, int expected) =>
        Assert.Equal(expected, ptype.ByteWidth());

    [Theory]
    [InlineData(PType.U8, "u8")]
    [InlineData(PType.U16, "u16")]
    [InlineData(PType.U32, "u32")]
    [InlineData(PType.U64, "u64")]
    [InlineData(PType.I8, "i8")]
    [InlineData(PType.I16, "i16")]
    [InlineData(PType.I32, "i32")]
    [InlineData(PType.I64, "i64")]
    [InlineData(PType.F16, "f16")]
    [InlineData(PType.F32, "f32")]
    [InlineData(PType.F64, "f64")]
    internal void NameCoversEveryDefinedTag(PType ptype, string expected) =>
        Assert.Equal(expected, ptype.Name());

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void IsDefinedAcceptsTheElevenDefinedTags(byte tag) =>
        Assert.True(PTypeExtensions.IsDefined((PType)tag));

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(255)]
    public void IsDefinedRejectsEveryTagAboveF64(byte tag) =>
        Assert.False(PTypeExtensions.IsDefined((PType)tag));

    [Theory]
    [InlineData(11)]
    [InlineData(255)]
    public void ByteWidthRejectsAnUndefinedTag(byte tag) =>
        Assert.Throws<VortexFormatException>(() => { _ = ((PType)tag).ByteWidth(); });

    [Theory]
    [InlineData(11)]
    [InlineData(255)]
    public void NameRejectsAnUndefinedTag(byte tag) =>
        Assert.Throws<VortexFormatException>(() => { _ = ((PType)tag).Name(); });

    [Fact]
    public void ClassificationPartitionsTheElevenTags()
    {
        for (byte tag = 0; tag <= 10; tag++)
        {
            PType p = (PType)tag;
            bool signed = p.IsSignedInteger();
            bool unsigned = p.IsUnsignedInteger();
            bool integer = p.IsInteger();
            bool floating = p.IsFloat();

            // Exactly one family, and IsInteger is exactly the union of the two integer families.
            Assert.False(signed && unsigned);
            Assert.False(integer && floating);
            Assert.Equal(signed || unsigned, integer);
            Assert.True(integer || floating);
        }
    }

    [Fact]
    public void SignedAndUnsignedFamiliesMatchTheSchemaOrder()
    {
        // The schema orders the tags U8..U64, then I8..I64, then F16..F64.
        Assert.True(PType.U8.IsUnsignedInteger());
        Assert.True(PType.U64.IsUnsignedInteger());
        Assert.False(PType.I8.IsUnsignedInteger());
        Assert.True(PType.I8.IsSignedInteger());
        Assert.True(PType.I64.IsSignedInteger());
        Assert.False(PType.F16.IsSignedInteger());
        Assert.True(PType.F16.IsFloat());
        Assert.True(PType.F64.IsFloat());
        Assert.False(PType.U8.IsFloat());
    }

    [Fact]
    public void ClassificationDoesNotClaimUndefinedTags()
    {
        // A tag past F64 must not be silently classified as a float just because it is "large".
        PType bogus = (PType)255;
        Assert.False(bogus.IsFloat());
        Assert.False(bogus.IsInteger());
        Assert.False(bogus.IsSignedInteger());
        Assert.False(bogus.IsUnsignedInteger());
    }
}
