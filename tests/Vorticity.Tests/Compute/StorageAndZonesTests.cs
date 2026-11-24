using System;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Compute;

/// <summary>What the kernels read under an extension, and the offsets a zone of an id is given.</summary>
public sealed class StorageAndZonesTests
{
    /// <summary>
    /// The kernels read an extension over an extension at its storage, as a column and the zone pruner do: one level
    /// alone, it reached them as an extension and a <see cref="NotSupportedException"/>.
    /// </summary>
    [Fact]
    public void AnExtensionOverAnExtensionIsReadAtItsStorage()
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        VortexBuffer values = arena.Allocate(8, 8, out Span<byte> bytes);
        bytes.Clear();
        int storage = arena.AddPrimitive(i64, 1, Validity.NonNullable, PType.I64, values);
        DType inner = types.Extension("test.inner", i64, default);
        DType outer = types.Extension("test.outer", inner, default);
        int once = arena.AddExtension(inner, 1, storage);
        int twice = arena.AddExtension(outer, 1, once);

        Assert.Equal(storage, ComparisonKernels.Unwrap(arena, twice));
        Assert.Equal(storage, ComparisonKernels.Unwrap(arena, once));
        Assert.Equal(storage, ComparisonKernels.Unwrap(arena, storage));
    }

    /// <summary>
    /// Two zones made under one id with other rules get offsets of their own, where the second read the first's; the same
    /// zone asked again gets the offsets kept for it.
    /// </summary>
    [Fact]
    public void TwoZonesOfOneIdAndOtherRulesKeepTheirOwnOffsets()
    {
        string id = "Test/Same-" + Guid.NewGuid().ToString("N");
        TimeZoneInfo two = TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(2), "Two", "Two");
        TimeZoneInfo five = TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(5), "Five", "Five");

        ZoneOffsets first = ZoneOffsets.For(two);
        Assert.Same(first, ZoneOffsets.For(two));
        Assert.NotSame(first, ZoneOffsets.For(five));
    }
}
