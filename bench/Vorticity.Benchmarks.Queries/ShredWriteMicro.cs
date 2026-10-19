using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Types.Variants;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What shredding costs a job in time (<c>--micro shredwrite</c>): an object of attributes, its rows
/// decoded as a job reads them, counted path by path, the shredding chosen and the path directory
/// built, then written whole and written shredded. What a job pays for shredding is the count and the
/// difference of the two writes.
/// </summary>
/// <remarks>Run it with <c>DOTNET_TieredCompilation=0</c>.</remarks>
internal static class ShredWriteMicro
{
    internal static async Task<int> Run(int rows, int rounds)
    {
        Random random = new Random(11);
        ShredEvent[] events = new ShredEvent[rows];
        for (int i = 0; i < rows; i++)
        {
            events[i] = new ShredEvent(i, Attributes(random, i));
        }

        string source = Path.Combine(Path.GetTempPath(), $"vortex-shredwrite-{Guid.NewGuid():N}.vortex");
        string target = Path.Combine(Path.GetTempPath(), $"vortex-shredwrite-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<ShredEvent>(source))
            {
                await writer.WriteAsync<ShredEvent>(events);
                await writer.CompleteAsync();
            }

            await using VortexFile file = await VortexFile.OpenAsync(source);
            List<RecordBatch> batches = [];
            await foreach (RecordBatch batch in file.Scan<ShredEvent>().ToBatchesAsync())
            {
                batches.Add(batch);
            }

            int field = file.DType.IndexOfField(nameof(ShredEvent.Attributes));
            double census = double.MaxValue;
            double whole = double.MaxValue;
            double shredded = double.MaxValue;
            VariantShredding? plan = null;
            Vorticity.Indexes.VariantDirectory? directory = null;
            int chunk = 0;
            long wholeBytes = 0;
            long shreddedBytes = 0;
            for (int round = 0; round < rounds; round++)
            {
                // As a job counts: text bounds cut as a text column's zones cut theirs, and the path
                // directory built beside the choice.
                long begin = Stopwatch.GetTimestamp();
                PathCensus counted = new PathCensus(VortexWriteOptions.Default.StringBoundBytes);
                foreach (RecordBatch batch in batches)
                {
                    counted.Add(batch, field);
                }

                plan = ShreddingChoice.Choose(counted);
                chunk = ShreddingChoice.ChunkTargetBytes(counted);
                directory = Vorticity.Indexes.VariantDirectory.From(counted, plan);
                census = Math.Min(census, Stopwatch.GetElapsedTime(begin).TotalMilliseconds);

                (double time, long bytes) = await WriteAsync(batches, target, null, 0);
                whole = Math.Min(whole, time);
                wholeBytes = bytes;
                (time, bytes) = await WriteAsync(batches, target, plan, chunk);
                shredded = Math.Min(shredded, time);
                shreddedBytes = bytes;
            }

            Console.WriteLine(
                $"{rows} rows, {plan?.Keys.Count ?? 0} keys shredded, chunks of {chunk} bytes, a path directory of "
                + $"{directory?.Entries.Count ?? 0} entries in {directory?.ToBytes().Length ?? 0} bytes; best of {rounds}:");
            Console.WriteLine($"  count and choose  {census,8:F1} ms  {census * 1e6 / rows,6:F1} ns/row");
            Console.WriteLine($"  write whole       {whole,8:F1} ms  {whole * 1e6 / rows,6:F1} ns/row  {wholeBytes,11} B");
            Console.WriteLine($"  write shredded    {shredded,8:F1} ms  {shredded * 1e6 / rows,6:F1} ns/row  {shreddedBytes,11} B");
            Console.WriteLine($"  shredding's cost  {census + shredded - whole,8:F1} ms  {(census + shredded - whole) * 1e6 / rows,6:F1} ns/row");
            foreach (RecordBatch batch in batches)
            {
                batch.Dispose();
            }
        }
        finally
        {
            System.IO.File.Delete(source);
            System.IO.File.Delete(target);
        }

        return 0;
    }

    /// <summary>
    /// Attributes as a service's spans hold them: a route, a status now and then text, a duration, a
    /// flag, a user among 10 000, a region, retries on a tenth of the rows, and a key of its own on one
    /// row in a thousand.
    /// </summary>
    private static Variant Attributes(Random random, int i)
    {
        List<(string, Variant)> fields =
        [
            ("route", Variant.From($"/api/v1/r{random.Next(40)}")),
            ("status", i % 500 == 0 ? Variant.From("timeout") : Variant.From(200 + (random.Next(4) * 100))),
            ("duration", Variant.From(random.NextDouble() * 250)),
            ("ok", Variant.From(random.Next(10) != 0)),
            ("user", Variant.From($"user-{random.Next(10_000)}")),
            ("region", Variant.From($"eu-{random.Next(3)}")),
        ];
        if (i % 10 == 0)
        {
            fields.Add(("retries", Variant.From(random.Next(5))));
        }

        if (i % 1_000 == 0)
        {
            fields.Add(($"once-{i}", Variant.From(i)));
        }

        return Variant.Object([.. fields]);
    }

    /// <summary>The batches written into one file, whole or shredded: the time, and the file's bytes.</summary>
    private static async Task<(double Milliseconds, long Bytes)> WriteAsync(List<RecordBatch> batches, string path, VariantShredding? plan, int chunk)
    {
        VortexWriteOptions options = plan is null ? VortexWriteOptions.Default : VortexWriteOptions.Default with
        {
            ShreddedVariants = new Dictionary<string, VariantShredding> { [nameof(ShredEvent.Attributes)] = plan },
            ColumnChunkTargetBytes = ImmutableDictionary<string, int>.Empty.Add(nameof(ShredEvent.Attributes), chunk),
        };
        long begin = Stopwatch.GetTimestamp();
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<ShredEvent>(path, options))
        {
            foreach (RecordBatch batch in batches)
            {
                await writer.WriteBatchAsync(batch);
            }

            await writer.CompleteAsync();
        }

        double time = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
        return (time, new FileInfo(path).Length);
    }
}

[VortexRecord]
public partial record struct ShredEvent(long Id, Variant Attributes);
