using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetGranuleReadsTests
{
    /// <summary>
    /// The ten largest of a sequence that grows with the arrival, over a day sorted by the service then the instant: the ten
    /// lie in the last granules of as many services, nine here, and no plan reads fewer granules than hold them. The first
    /// pass reads the granules whose bounds reach the floor their statistics set, those that hold the ten and one more; the
    /// rows at the places kept are then read from the granules that hold them: 19 blocks of 74, where the model of ~3 assumed
    /// the ten in two or three granules. The answer is LINQ's.
    /// </summary>
    [Fact]
    public async Task ATopOfAnOrderCorrelatedWithTheKeyReadsTheGranulesThatHoldIt()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        (VortexDataset dataset, Row[] rows) = await DayAsync(memory, identifiers: null, IdentifierLocation.Granule);
        await dataset.DisposeAsync();
        await using VortexSession session = VortexSession.Create(static options => options.MaxDegreeOfParallelism = 1);
        await using VortexDataset cold = await VortexDataset.OpenAsync(memory, new DatasetOptions { Session = session }, Ct);
        Assert.Equal(1, await cold.ScanBuilder().ObjectsAsync(Ct).CountAsync(Ct));

        Projection<long> asked = cold.Scan<Row>().OrderByDescending(r => r.Sequence).Select(r => r.Sequence).Take(10);
        List<long> top = await asked.ToListAsync(Ct);

        // The object's rows in its order, the service then the instant: the granules of 8,192 rows the ten best lie in.
        Row[] ordered = [.. rows.OrderBy(static r => r.Service, StringComparer.Ordinal).ThenBy(static r => r.At)];
        long floor = rows.Select(static r => r.Sequence).OrderDescending().ElementAt(9);
        int holding = ordered.Select(static (r, i) => (r, i)).Where(p => p.r.Sequence >= floor).Select(static p => p.i / 8_192).Distinct().Count();
        int granules = (rows.Length + 8_191) / 8_192;

        Console.WriteLine($"CORRELATED TOP: {asked.Metrics.BlocksDecoded} blocks decoded of {granules} granules, the ten in {holding}");
        Assert.Equal(rows.Select(static r => r.Sequence).OrderDescending().Take(10), top);
        Assert.InRange(asked.Metrics.BlocksDecoded, holding, (2 * holding) + 2);
    }
}
