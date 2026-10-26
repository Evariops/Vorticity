// A timestamp column in a time zone the host cannot resolve, as a host without time-zone data meets one: a write of a
// DateTimeOffset needs no zone, its instant stored as it is and the zone kept as the column's metadata; a read as
// DateTimeOffset needs the zone for the offset it gives, and refuses, the column still reading as its storage.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed partial class UnresolvedZoneTests
{
    private const string Zone = "Nowhere/Atlantis";

    private static readonly DateTimeOffset At = new DateTimeOffset(2026, 10, 26, 17, 0, 0, TimeSpan.FromHours(1));

    [Fact]
    public async Task AnInstantIsWrittenWhateverItsZoneAndReadAsItsStorage()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.False(TimeZoneInfo.TryFindSystemTimeZoneById(Zone, out _));
        string path = Path.Combine(Path.GetTempPath(), $"unresolved-zone-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Somewhere>(path))
            {
                await writer.WriteAsync<Somewhere>([new Somewhere(1, At), new Somewhere(2, At.AddHours(1))], ct);
                await writer.CompleteAsync(ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            Assert.Equal(Zone, file.Schema[1].Type.TimeZone);

            VortexSchemaException refused = await Assert.ThrowsAsync<VortexSchemaException>(
                async () => await file.Scan<Somewhere>().ToRecordsAsync(ct).ToListAsync(ct));
            Assert.Contains("whose zone this host resolves; read another as long", refused.Message, StringComparison.Ordinal);

            List<long> stored = [];
            await foreach (BatchView batch in file.Scan("At").WithCancellation(ct))
            {
                stored.AddRange(batch.Column<long>("At").Values.ToArray());
            }

            Assert.Equal([Micros(At), Micros(At.AddHours(1))], stored);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void AWriteStillRefusesANaiveColumn()
    {
        // The instant needs a zoned column to go to: a naive one takes a DateTime.
        VortexSchemaException refused = Assert.Throws<VortexSchemaException>(
            () => ClrFit.Require<DateTimeOffset>(VortexType.Timestamp(TimeUnit.Microseconds), "The column", null, written: true));
        Assert.Contains("write a naive or UTC one as DateTime", refused.Message, StringComparison.Ordinal);
    }

    private static long Micros(DateTimeOffset at) => (at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;

    [VortexRecord]
    public partial record struct Somewhere(long Id, [VortexColumn(TimeZone = Zone)] DateTimeOffset At);
}
