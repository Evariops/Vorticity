// The parts of a variant's chunk one question reads through a shredded key: the children every range of the key reads whole
// decoded once for the chunk, not once a part.
//
// THE ORACLE IS LINQ AND THE CHILDREN DECODED: the answer is LINQ's, and the shared children decoded are no more than the
// chunks hold keys.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Dataset;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Dataset;

/// <remarks>In the collection that runs alone, because the count it reads is the process's.</remarks>
[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed class VariantKeySharedChildrenTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The calls of <see cref="DatasetVariantGranulesTests.AKeysStatisticsAreThoseOfItsValuesAsAColumn"/> under a status code that
    /// holds for ten calls of a service in a row, sorted by the service, asked for one service's calls under a status code of 500 or more through the rows: each chunk read in several
    /// parts, each part its rows of the code alone; the children each part reads whole decoded once a chunk for all its parts,
    /// where each part decoded them again.
    /// </summary>
    [Fact]
    public async Task APartsKeyReadsTheChildrenItsChunkSharesOnce()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        // A code that holds for ten calls of a service in a row, of 503 one run in ten: the key's typed column runs.
        DatasetVariantGranulesTests.Call[] calls = await DatasetVariantGranulesTests.WrittenAsync(store, static i => (i / 60) % 10 == 0 ? 503 : 200);
        string[] expected =
        [
            .. calls.Where(static c => c.Service == "svc-3" && c.Code >= 500).GroupBy(static c => c.Service).Select(static g => $"{g.Key}:{g.Count()}"),
        ];
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { ContentsObjectBytes = 0, SheetObjectBytes = 0 }, Ct);
        Scan<DatasetVariantGranulesTests.Call> scan = dataset.Scan<DatasetVariantGranulesTests.Call>().With(new ScanOptions { UseSynopses = false });
        long before = ArrayDecodeContext.SharedChildrenDecoded;
        List<DatasetVariantGranulesTests.ServiceCount> answers = await scan.Where(c => c.Service == "svc-3" && c.Attributes["code"].AsInt64() >= 500)
            .GroupBy(c => c.Service).Select(g => (g.Key, g.Count())).As<DatasetVariantGranulesTests.ServiceCount>().ToRecordsAsync(Ct).ToListAsync(Ct);
        long shared = ArrayDecodeContext.SharedChildrenDecoded - before;
        Assert.Equal(expected, answers.Select(static a => $"{a.Service}:{a.Count}"));
        Console.WriteLine($"SHARED CHILDREN: {shared} decoded for a service's calls");
        Assert.True(shared <= 8, $"{shared} shared children decoded");
    }
}
