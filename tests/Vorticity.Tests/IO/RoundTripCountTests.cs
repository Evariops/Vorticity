// How many round trips an open costs, which is the format's headline claim and was unmeasured.
//
// The format promises one or two round trips to first data, and no timing can show it: A ROUND
// TRIP IS FREE ON A LOCAL FILE. Every benchmark here memory-maps the corpus, where a read is a
// page fault and an extra one costs microseconds, so the number that matters over object storage
// - how many separate fetches the reader issues before it can hand over a row - never appears in
// any timing.
//
// The fix is not to time it but to COUNT it. The count is deterministic, which makes it the same
// kind of quantity as an allocation: a hard barrier rather than a barrier with margin, and one that
// means the same thing on a laptop and on an object store. The latency multiplier is then
// arithmetic - a source with a 50 ms first-byte time costs round-trips x 50 ms, and no benchmark
// needs to simulate it to know that.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.IO;

/// <summary>Counts the source operations an open and a first batch issue.</summary>
public sealed class RoundTripCountTests
{
    /// <summary>
    /// What an open to the first batch may cost, in round trips.
    /// </summary>
    /// <remarks>
    /// A RATCHET, like every other number in this repository that can be counted rather than timed,
    /// and set AT the count rather than above it: the count is exact, so there is no machine
    /// variance for a margin to absorb and a margin would only hide the first regression.
    ///
    /// The ceilings of 1 and 2 are exactly the format's promise of one or two round trips to first
    /// data. Raising either number means the format's central promise got worse: say in the commit
    /// message which extra fetch was added and why it cannot be coalesced into the set
    /// `ReadManyAsync` already receives.
    /// </remarks>
    private static readonly (string Entry, int OpenCeiling, int FirstBatchCeiling)[] Files =
    [
        ("containers/zoned_many_zones_nulls", 1, 2),
        ("distributions/high_cardinality_i64_r8193", 1, 2),
        ("types/utf8_nullable_r1025", 1, 2),
    ];

    [Fact]
    public async Task OpeningAFileAndTakingOneBatchStaysWithinItsRoundTrips()
    {
        Decoders.EnsureRegistered();

        List<string> over = [];
        System.Text.StringBuilder report = new System.Text.StringBuilder("ROUND TRIPS\n");
        foreach ((string entry, int openCeiling, int batchCeiling) in Files)
        {
            CountingSource open = await Measure(entry, readBatch: false);
            CountingSource batch = await Measure(entry, readBatch: true);

            report.Append("    ")
                .Append(entry.PadRight(42))
                .Append("open ")
                .Append(open.Trips.ToString(CultureInfo.InvariantCulture))
                .Append(" (ceiling ")
                .Append(openCeiling.ToString(CultureInfo.InvariantCulture))
                .Append(")   open+batch ")
                .Append(batch.Trips.ToString(CultureInfo.InvariantCulture))
                .Append(" (ceiling ")
                .Append(batchCeiling.ToString(CultureInfo.InvariantCulture))
                .Append(")\n");

            if (open.Trips > openCeiling)
            {
                over.Add($"{entry}: open took {open.Trips} round trips, ceiling {openCeiling}");
            }

            if (batch.Trips > batchCeiling)
            {
                over.Add($"{entry}: open+batch took {batch.Trips} round trips, ceiling {batchCeiling}");
            }
        }

        Console.Out.Write(report.ToString());
        Assert.True(over.Count == 0, string.Join("\n", over) + "\n" + report);
    }

    private static async Task<CountingSource> Measure(string entry, bool readBatch)
    {
        CountingSource source = new CountingSource(
            MemoryMappedSegmentSource.Open(Corpus.Path(entry)));

        await using (VortexFile file = await VortexFile.OpenAsync(
            source, new VortexOpenOptions { LeaveSourceOpen = true }, CancellationToken.None))
        {
            if (readBatch)
            {
                await foreach (RecordBatch b in file.Scan().ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    _ = b.RowCount;
                    break;
                }
            }
        }

        return source;
    }

    /// <summary>
    /// A source that counts fetches, which is what an object store charges for.
    /// </summary>
    /// <remarks>
    /// <c>ReadManyAsync</c> counts as ONE, because that is the whole point of it: the reader hands
    /// the source a set of ranges and the source coalesces them into as few requests as it can. A
    /// decorator that counted the ranges instead would report the reader as profligate when it is
    /// being exactly the opposite.
    /// </remarks>
    private sealed class CountingSource : ISegmentSource
    {
        private readonly ISegmentSource _inner;

        internal CountingSource(ISegmentSource inner) => _inner = inner;

        internal int Trips { get; private set; }

        public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken) =>
            _inner.GetLengthAsync(cancellationToken);

        public ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
        {
            Trips++;
            return _inner.ReadAsync(spec, cancellationToken);
        }

        public ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
        {
            Trips++;
            return _inner.ReadManyAsync(requests, cancellationToken);
        }

        public ValueTask<SegmentOwner> ReadRangeAsync(
            long offset, int length, int alignment, CancellationToken cancellationToken)
        {
            Trips++;
            return _inner.ReadRangeAsync(offset, length, alignment, cancellationToken);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
