// A slice's groups read off the words it was sorted by: where its keys' codes pack into a word, the groups of every
// depth start where the word's bits for the keys down to it change, and the tables are the bytes the codes give.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SynopsisTableTests
{
    /// <summary>
    /// Four slices over two hours of hits by region, status and host, a byte count and a weight measured: counted once
    /// from the slices' codes and once from the words they were sorted by, they make the same object, byte for byte.
    /// </summary>
    [Fact]
    public async Task ASlicesGroupsReadOffItsSortedWordsAreTheGroupsOfItsCodes()
    {
        string scratch = Path.Combine(Path.GetTempPath(), $"vortex-packed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            const int SliceRows = 3_000;
            long[] starts = [0, TimeSpan.FromMinutes(30).Ticks / 10, TimeSpan.FromMinutes(65).Ticks / 10, TimeSpan.FromMinutes(95).Ticks / 10];
            Random random = new Random(17);
            List<Hit> rows = [];
            for (int s = 0; s < starts.Length; s++)
            {
                for (int i = 0; i < SliceRows; i++)
                {
                    int host = random.Next(40);
                    rows.Add(new Hit(
                        Noon.AddTicks((starts[s] + i) * 10), $"region-{host % 3}", (short)(random.Next(4) == 0 ? 500 : 200), $"host-{host}",
                        random.NextInt64(10_000), random.NextDouble()));
                }
            }

            string plain = Path.Combine(scratch, "plain.vortex");
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Hit>(plain))
            {
                await writer.WriteAsync<Hit>(rows.ToArray(), Ct);
                await writer.CompleteAsync(Ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(plain, cancellationToken: Ct);
            DType schema = file.DType;
            int instant = schema.IndexOfField(nameof(Hit.At));
            long micros = Noon.ToUnixTimeMilliseconds() * 1000;
            SliceTable table = new SliceTable(
                TimeUnit.Microseconds,
                [.. Enumerable.Range(0, starts.Length).Select(s => new TimeSlice(micros + starts[s], micros + starts[s] + 60_000_000, (long)s * SliceRows, SliceRows))],
                [schema.IndexOfField(nameof(Hit.Region)), schema.IndexOfField(nameof(Hit.Status)), schema.IndexOfField(nameof(Hit.Host))]);
            Guid identity = Guid.NewGuid();
            byte[][] written = new byte[2][];
            int packed = 0;
            for (int pass = 0; pass < written.Length; pass++)
            {
                bool words = pass == 1;
                string path = Path.Combine(scratch, $"sliced-{pass}.vortex");
                await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Hit>(path, new VortexWriteOptions
                {
                    Indexes = IndexPolicy.None.WithBudgetPerMille(100_000),
                    SynopsisColumns = [nameof(Hit.Region), nameof(Hit.Status), nameof(Hit.Host)],
                    ScratchMemoryBytes = 0,
                    ScratchDirectory = scratch,
                    Identity = identity,
                }))
                {
                    using SortedSlices slices = new SortedSlices(table, instant, schema);
                    using SynopsisTables tables = new SynopsisTables(
                        schema, table.Unit, SynopsisTables.Measures(schema, instant), writer, slices: table.Slices);
                    int next = 0;
                    await foreach (RecordBatch batch in file.Scan<Hit>().ToBatchesAsync(Ct))
                    {
                        using (batch)
                        {
                            await slices.AppendAsync(batch, async (sorted, token) =>
                            {
                                packed += words && sorted.Packed is not null ? 1 : 0;
                                await tables.AddSliceAsync(
                                    sorted.Batch, table.Slices[next++].Start, slices.Order, sorted.Codes, packed: words ? sorted.Packed : null, cancellationToken: token);
                                await writer.WriteBatchAsync(sorted.Batch, token);
                            }, Ct);
                        }
                    }

                    await slices.DrainAsync();
                    writer.DeclareSlices(nameof(Hit.At), slices.Table);
                    writer.DeclareSynopsisTables(await tables.FinishAsync(Ct));
                    await writer.CompleteAsync(Ct);
                }

                written[pass] = await System.IO.File.ReadAllBytesAsync(path, Ct);
            }

            Assert.Equal(starts.Length, packed);
            await using VortexFile sliced = await VortexFile.OpenAsync(Path.Combine(scratch, "sliced-1.vortex"), cancellationToken: Ct);
            Assert.Contains(await TablesAsync(sliced), t => t.Info.Keys.Length == 3 && t.Rows.Count > 0);
            Assert.Equal(written[0], written[1]);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [VortexRecord]
    public partial record struct Hit(DateTimeOffset At, string Region, short Status, string Host, long Bytes, double Weight);
}
