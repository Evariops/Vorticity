// A spill writer's claims on its directory's free space: a claim that would leave less than a tenth of it is refused
// and counts nothing, the others count, a claim another budget refused is given back, and the writer's file gone gives
// back every byte it claimed.
using System.IO;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed class SpaceClaimsTests
{
    [Fact]
    public void AClaimPastTheFreeSpaceIsRefusedAndTheOthersAreCounted()
    {
        string directory = Path.GetTempPath();
        long? space = GroupCore.Free(directory);
        Assert.SkipWhen(space is null, $"The free space of {directory} cannot be read here.");
        long free = space!.Value;
        SpaceClaims claims = default;
        Assert.False(claims.TryClaim(directory, free));
        Assert.True(claims.TryClaim(directory, 4_096));
        Assert.True(claims.TryClaim(directory, 65_536));
        claims.Release(65_536);
        Assert.True(claims.TryClaim(directory, 8_192));
        Assert.Equal(12_288, claims.ReleaseAll());
        Assert.Equal(0, claims.ReleaseAll());
    }
}
