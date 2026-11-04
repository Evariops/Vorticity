// The zstd decompressors the scan contexts and the index regions rent, kept from one to the next: as many as the processors
// rent at once come back at the next rents, none made anew. The pool is the process's, so the test runs alone.
using System;
using System.Collections.Generic;
using System.Linq;
using ManagedCompressions.Zstd;
using Vorticity.Arrays;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Arrays;

[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed class ZstdDecodersTests
{
    /// <summary>
    /// A decompressor for each processor, rented at once then given back: the next as many rents take those same ones, where
    /// a pool of sixteen made the rest anew on a machine of more processors.
    /// </summary>
    [Fact]
    public void AsManyDecompressorsAsProcessorsRentAtOnceAreKept()
    {
        int count = Environment.ProcessorCount;
        Assert.True(ZstdDecoders.Capacity >= count, $"{ZstdDecoders.Capacity} kept for {count} processors");
        ZstdDecompressor[] first = [.. Enumerable.Range(0, count).Select(_ => ZstdDecoders.Rent())];
        foreach (ZstdDecompressor decoder in first)
        {
            ZstdDecoders.Return(decoder);
        }

        HashSet<ZstdDecompressor> kept = new HashSet<ZstdDecompressor>(first, ReferenceEqualityComparer.Instance);
        ZstdDecompressor[] second = [.. Enumerable.Range(0, count).Select(_ => ZstdDecoders.Rent())];
        foreach (ZstdDecompressor decoder in second)
        {
            ZstdDecoders.Return(decoder);
        }

        Assert.Equal(count, second.Count(kept.Contains));
    }
}
