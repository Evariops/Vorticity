using System;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetBlockAnswerTests
{
    /// <summary>
    /// Errors by route and hour, read as rows from objects keyed by the service, the hour and the instant: a granule within
    /// an hour takes the hour from the zones of its instants, which are then not read; the granules that straddle an hour,
    /// and a question that wants the instants themselves, read them. The answer is the blocks' (<see cref="AQuestionAnswersAsItsRowsDo"/>).
    /// </summary>
    [Fact]
    public async Task AnHoursGranulesTakeTheHourFromTheirZones()
    {
        MemoryObjectStore shared = _fixture.Hourly;
        await ForgetLearnedAsync(shared);
        long hours = await RowBytesAsync(shared, "5xx by route and hour");
        long instants = await RowBytesAsync(shared, "5xx by route and hour, the last instant");
        Console.WriteLine($"ZONE KEYS 5xx by route and hour: {hours:N0} bytes through the rows, {instants:N0} with the instants");
        Assert.True(hours < instants, $"{hours} bytes for the hours, {instants} with the instants");
    }

    /// <summary>The bytes <paramref name="question"/> reads of <paramref name="shared"/> through its objects' rows, by a session of its own.</summary>
    private static async Task<long> RowBytesAsync(MemoryObjectStore shared, string question)
    {
        await using CountingObjectStore store = new CountingObjectStore(shared);
        await using VortexSession session = VortexSession.Create(_ => { });
        await using VortexDataset dataset = await VortexDataset.OpenAsync(
            store, new DatasetOptions { Session = session, CostProfile = Vorticity.IO.CostProfile.Memory }, Ct);
        store.Reset();
        await AskAsync(dataset, question, synopses: false);
        return store.BytesRead;
    }
}
