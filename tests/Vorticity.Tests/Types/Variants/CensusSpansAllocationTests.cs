// The census of an object being written keeps, for each path, the spans of rows its values lie in, in arrays from the
// process's shelf that it gives back once its directory is declared: the next object's census takes them again.
using System;
using Vorticity.Tests.Scan;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed class CensusSpansAllocationTests
{
    /// <summary>
    /// A census of 30 000 rows whose key <c>k</c> lies in one row of three, groups of a row: 10 000 spans of the key, an
    /// array of 160 KB grown twice over as they came. Past the first, a census released before the next keeps its spans
    /// in the arrays the shelf holds, and allocates its tree alone, 16 KB at most, the same spans read back.
    /// </summary>
    [Fact]
    public void ACensusTakesTheSpansTheCensusBeforeItGaveBack()
    {
        Variant other = Variant.Object(("z", Variant.From(1L)));
        Variant keyed = Variant.Object(("k", Variant.From(2L)), ("z", Variant.From(1L)));
        for (int warm = 0; warm < 3; warm++)
        {
            Count().Release();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        PathCensus census = Count();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        ReadOnlySpan<long> spans = census.Root.Keys["k"].SpansOf(PathClass.Integer);
        Assert.Equal(2 * 10_000, spans.Length);
        Assert.Equal(29_998, spans[^1]);
        census.Release();
        Assert.True(census.Root.Keys["k"].SpansOf(PathClass.Integer).IsEmpty);
        Assert.True(allocated <= 16_384, $"{allocated} bytes allocated by a census of 10 000 spans");

        PathCensus Count()
        {
            PathCensus counting = new PathCensus(groupRows: 1);
            for (int row = 0; row < 30_000; row++)
            {
                Variant value = row % 3 == 1 ? keyed : other;
                counting.Add(value.MetadataSpan, value.ValueSpan);
            }

            return counting;
        }
    }
}
