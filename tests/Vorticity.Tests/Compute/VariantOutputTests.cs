using System;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Compute;

/// <summary>
/// The column a variant key's values are written into tells its rows present by the bits set, not by the writes made: an
/// object that holds the key twice writes its row twice, and a row that does not hold it stays absent, its slot cleared.
/// </summary>
public sealed class VariantOutputTests
{
    [Fact]
    public void ARowWrittenTwiceLeavesARowAbsentAbsent()
    {
        CanonicalArena arena = new(16, TestAccount.Shared);
        VariantFieldExpr.Output output = new(arena, VariantRead.AsInt64, 2);
        byte[] five = VariantEncoding.Integer(5);
        output.Write(0, five, default, default);
        output.Write(0, five, default, default);
        int node = output.Finish(arena, ReadOnlySpan<VortexBuffer>.Empty);

        ValidityMask mask = ValidityMask.From(arena, arena.GetNode(node).Validity);
        Assert.True(mask.IsValid(0));
        Assert.False(mask.IsValid(1));
        Assert.Equal(0L, arena.GetNode(node).Values.Span.Slice(sizeof(long), sizeof(long)).ToArray() is { } bytes ? BitConverter.ToInt64(bytes) : -1);
    }
}
