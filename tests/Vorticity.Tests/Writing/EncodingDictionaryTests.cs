using System;

using Vorticity.Editions;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class EncodingDictionaryTests
{
    [Fact]
    public void AKnownIdIsInternedFromItsBytesWithoutAString()
    {
        EncodingDictionary arrays = new EncodingDictionary(ComponentKind.Array, EditionRegistry.Newest);
        arrays.Intern("vortex.struct"u8);

        long before = GC.GetAllocatedBytesForCurrentThread();
        ushort primitive = arrays.Intern("vortex.primitive"u8);
        ushort again = arrays.Intern("vortex.primitive"u8);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(1, primitive);
        Assert.Equal(primitive, again);
        Assert.Equal("vortex.primitive", arrays.Ids[primitive]);
        Assert.Equal(primitive, arrays.Intern("vortex.primitive"));
    }

    [Fact]
    public void AnIdAFileNamesTwiceIsFoundAtItsFirstIndex()
    {
        EncodingDictionary layouts = new EncodingDictionary(ComponentKind.Layout, EditionRegistry.Newest);
        layouts.Seed(["vortex.flat", "vortex.chunked", "vortex.flat"]);

        Assert.Equal(0, layouts.Intern("vortex.flat"));
        Assert.Equal(0, layouts.Intern("vortex.flat"u8));
        Assert.Equal(1, layouts.Intern("vortex.chunked"u8));
        Assert.Equal(3, layouts.Intern("vortex.struct"));
        Assert.Equal(4, layouts.Count);
    }

    [Fact]
    public void AnIdTheTargetLacksIsRefusedWhateverItsForm()
    {
        EncodingDictionary arrays = new EncodingDictionary(ComponentKind.Array, VortexEdition.Core20250500);

        Assert.Throws<VortexUnsupportedException>(() => arrays.Intern("vortex.zstd"u8));
        Assert.Throws<VortexUnsupportedException>(() => arrays.Intern("vortex.zstd"));
        Assert.Throws<VortexUnsupportedException>(() => arrays.Intern("vortex.not_an_id"u8));
        Assert.Equal(0, arrays.Count);
    }
}
