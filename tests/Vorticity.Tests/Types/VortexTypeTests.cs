using System;
using System.Text;
using Xunit;

namespace Vorticity.Tests.Types;

/// <summary>What a type's factories refuse, and how a type and a schema format and find their fields.</summary>
public sealed class VortexTypeTests
{
    /// <summary>
    /// A timestamp in days has no instant, and the reference refuses one as it reads its type: a file written with one
    /// would not be read there. A timestamp in any other unit is made.
    /// </summary>
    [Fact]
    public void ATimestampInDaysIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VortexType.Timestamp(TimeUnit.Days));
        Assert.Throws<ArgumentOutOfRangeException>(() => VortexType.Timestamp((TimeUnit)42));
        foreach (TimeUnit unit in new[] { TimeUnit.Seconds, TimeUnit.Milliseconds, TimeUnit.Microseconds, TimeUnit.Nanoseconds })
        {
            Assert.Equal(unit, VortexType.Timestamp(unit, "UTC").Unit);
        }
    }

    /// <summary>A type or a schema formatted into UTF-8 bytes too short for it says false and no byte written, as the base types do.</summary>
    [Fact]
    public void AUtf8FormatThatDoesNotFitSaysNoByteWritten()
    {
        VortexType type = VortexType.Struct([new VortexField("first", VortexType.Int64), new VortexField("second", VortexType.Utf8)]);
        VortexSchema schema = VortexSchema.Create([new VortexField("first", VortexType.Int64), new VortexField("second", VortexType.Utf8)]);
        byte[] small = new byte[4];

        Assert.False(((IUtf8SpanFormattable)type).TryFormat(small, out int typeBytes, default, null));
        Assert.Equal(0, typeBytes);
        Assert.False(((IUtf8SpanFormattable)schema).TryFormat(small, out int schemaBytes, default, null));
        Assert.Equal(0, schemaBytes);

        byte[] room = new byte[256];
        Assert.True(((IUtf8SpanFormattable)type).TryFormat(room, out typeBytes, default, null));
        Assert.Equal(type.ToString(), Encoding.UTF8.GetString(room, 0, typeBytes));
    }

    /// <summary>A path longer than the stack's room is found by its UTF-8 bytes as a short one is.</summary>
    [Fact]
    public void ALongPathIsFoundByItsBytes()
    {
        string name = new string('a', 300);
        VortexSchema schema = VortexSchema.Create([new VortexField("first", VortexType.Int64), new VortexField(name, VortexType.Utf8)]);

        Assert.Equal(1, schema.IndexOf(Encoding.UTF8.GetBytes(name)));
        Assert.Equal(0, schema.IndexOf("first"u8));
        Assert.Equal(-1, schema.IndexOf(Encoding.UTF8.GetBytes(new string('b', 300))));
    }
}
