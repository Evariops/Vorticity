using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class OwnedBatches
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();
        await using VortexFile file = await VortexFile.OpenAsync(path);
        CancellationToken ct = CancellationToken.None;

        Channel<RecordBatch> channel = Channel.CreateBounded<RecordBatch>(new BoundedChannelOptions(4) { SingleWriter = true, SingleReader = true });

        Task producer = Task.Run(async () =>
        {
            try
            {
                await foreach (RecordBatch batch in file.Scan<Reading>().ToBatchesAsync(ct))
                {
                    try
                    {
                        await channel.Writer.WriteAsync(batch, ct);
                    }
                    catch
                    {
                        batch.Dispose();
                        throw;
                    }
                }

                channel.Writer.Complete();
            }
            catch (Exception e)
            {
                channel.Writer.Complete(e);
            }
        });

        long rows = 0, valid = 0;
        int batches = 0;
        double total = 0;
        await foreach (RecordBatch batch in channel.Reader.ReadAllAsync(ct))
        {
            using (batch)
            {
                Columns<Reading> cols = batch.As<Reading>();
                total += Sum(cols.Celsius);
                valid += cols.Celsius.Length - cols.Celsius.NullCount;
                rows += cols.RowCount;
                batches++;
            }
        }

        await producer;
        Console.WriteLine($"through a channel: {batches} batches, {rows} rows, mean {total / valid:F2} degrees");

        RecordBatch? kept = null;
        await foreach (Columns<Reading> cols in file.Scan<Reading>())
        {
            if (cols.StartRow <= 500_000 && 500_000 < cols.StartRow + cols.RowCount)
            {
                kept = cols.ToOwned();
            }
        }

        using (kept)
        {
            BatchView view = kept!.View;
            Column<int> day = view.Column<int>("Day");
            Console.WriteLine($"kept past the loop: {kept.RowCount} rows from row {kept.StartRow}, days {day[0]} to {day[day.Length - 1]}, schema {kept.Schema}");
        }

        try
        {
            _ = kept.View;
        }
        catch (ObjectDisposedException e)
        {
            Console.WriteLine($"after Dispose: {e.GetType().Name}");
        }

        await foreach (Columns<Reading> cols in file.Scan<Reading>())
        {
            using RecordBatch copy = cols.ToOwned();
            Columns<Reading> o = copy.As<Reading>();
            Console.WriteLine($"a borrowed batch holds day {cols.Day.Encoding}, celsius {cols.Celsius.Encoding}, city {cols.City.Encoding}; its ToOwned copy the same: {o.Day.Encoding}, {o.Celsius.Encoding}, {o.City.Encoding}");
            break;
        }

        await foreach (RecordBatch batch in file.Scan<Reading>().ToBatchesAsync())
        {
            using (batch)
            {
                Columns<Reading> o = batch.As<Reading>();
                Console.WriteLine($"a batch of ToBatchesAsync holds day {o.Day.Encoding}, celsius {o.Celsius.Encoding}, city {o.City.Encoding}");
            }

            break;
        }

        // A tool scan's borrowed batch is canonical, like an owned one: the pair isolates the copy.
        // The four run in turn, round after round, so that a busy machine slows them alike.
        Func<Task<long>>[] variants = [() => BorrowedAsync(file), () => CanonicalAsync(file), () => OwnedAsync(file), () => CopiedAsync(file)];
        (TimeSpan Time, long Bytes)[] best = await BestOfAsync(variants);
        (TimeSpan borrowed, long borrowedBytes) = best[0];
        (TimeSpan canonical, long canonicalBytes) = best[1];
        (TimeSpan owned, long ownedBytes) = best[2];
        (TimeSpan copied, long copiedBytes) = best[3];
        Console.WriteLine($"every value, borrowed and encoded:   {Ms(borrowed)}, {borrowedBytes / 1024} KiB allocated");
        Console.WriteLine($"every value, borrowed and canonical: {Ms(canonical)}, {canonicalBytes / 1024} KiB allocated");
        Console.WriteLine($"every value, ToBatchesAsync:         {Ms(owned)}, {ownedBytes / 1024} KiB allocated");
        Console.WriteLine($"every value, ToOwned per batch:      {Ms(copied)}, {copiedBytes / 1024} KiB allocated");
        Console.WriteLine($"the copy of ToBatchesAsync: {Ms(owned - canonical)}, {(owned - canonical) / canonical:P0} of the canonical scan");

        long selected = await file.Scan<Reading>().Where(r => r.Celsius > 45.0).CountAsync();
        long ownedRows = 0;
        await foreach (RecordBatch batch in file.Scan<Reading>().Where(r => r.Celsius > 45.0).With(new ScanOptions { Compact = false }).ToBatchesAsync())
        {
            using (batch)
            {
                Selection selection = batch.As<Reading>().Selection;
                ownedRows += selection.Count;
            }
        }

        Console.WriteLine($"Compact = false: {selected} rows pass, the owned batches select {ownedRows}");

        long takenRows = 0;
        await foreach (RecordBatch batch in file.Scan<Reading>().Rows(4, 900_000).ToBatchesAsync())
        {
            using (batch)
            {
                takenRows += batch.As<Reading>().Selection.Count;
            }
        }

        Console.WriteLine($"Rows(4, 900_000): the owned batches select {takenRows}");
    }

    private static string Ms(TimeSpan elapsed) => $"{elapsed.TotalMilliseconds:F1} ms";

    private static async Task<long> BorrowedAsync(VortexFile file)
    {
        long checksum = 0;
        await foreach (Columns<Reading> cols in file.Scan<Reading>())
        {
            checksum += Touch(cols);
        }

        return checksum;
    }

    private static async Task<long> CanonicalAsync(VortexFile file)
    {
        long checksum = 0;
        await foreach (BatchView batch in file.Scan())
        {
            checksum += Touch(batch.Column<int>("Day"), batch.Column<double?>("Celsius"), batch.Column<string>("City"));
        }

        return checksum;
    }

    private static async Task<long> OwnedAsync(VortexFile file)
    {
        long checksum = 0;
        await foreach (RecordBatch batch in file.Scan<Reading>().ToBatchesAsync())
        {
            using (batch)
            {
                checksum += Touch(batch.As<Reading>());
            }
        }

        return checksum;
    }

    private static async Task<long> CopiedAsync(VortexFile file)
    {
        long checksum = 0;
        await foreach (Columns<Reading> cols in file.Scan<Reading>())
        {
            using RecordBatch copy = cols.ToOwned();
            checksum += Touch(copy.As<Reading>());
        }

        return checksum;
    }

    private static long Touch(Columns<Reading> cols) => Touch(cols.Day, cols.Celsius, cols.City);

    private static long Touch(Column<int> days, Column<double?> celsius, Column<string> city)
    {
        long checksum = 0;
        foreach (int day in days.Values)
        {
            checksum += day;
        }

        checksum += (long)Sum(celsius);
        for (int i = 0; i < city.Length; i++)
        {
            checksum += city.GetLength(i);
        }

        return checksum;
    }

    private static double Sum(Column<double?> celsius)
    {
        double total = 0;
        ReadOnlySpan<double> values = celsius.Values;
        for (int i = 0; i < values.Length; i++)
        {
            if (celsius.IsValid(i))
            {
                total += values[i];
            }
        }

        return total;
    }

    private static async Task<(TimeSpan Time, long Bytes)[]> BestOfAsync(Func<Task<long>>[] variants)
    {
        (TimeSpan Time, long Bytes)[] best = new (TimeSpan, long)[variants.Length];
        best.AsSpan().Fill((TimeSpan.MaxValue, long.MaxValue));
        for (int round = 0; round < 40; round++)
        {
            for (int v = 0; v < variants.Length; v++)
            {
                long before = GC.GetTotalAllocatedBytes(precise: true);
                Stopwatch watch = Stopwatch.StartNew();
                await variants[v]();
                TimeSpan elapsed = watch.Elapsed;
                long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

                // The first rounds warm the JIT and are not kept.
                if (round >= 20)
                {
                    best[v] = (elapsed < best[v].Time ? elapsed : best[v].Time, Math.Min(allocated, best[v].Bytes));
                }
            }
        }

        return best;
    }
}
