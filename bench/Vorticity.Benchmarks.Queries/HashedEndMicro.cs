using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Vorticity.Indexes;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What the end of a file's hashed postings holds when its writer knows no bound on its rows (<c>--micro hashedend</c>):
/// <c>--rows</c> random identifiers, a million at a time, written to a file of one column that hashed postings index, then
/// completed, where the one bucket every pair went to is cut again by the next bits of the hashes and sorted part by part.
/// The private bytes the process held at most while it wrote and while it completed, each above what it held as it began,
/// and what the completion allocated. The file and the scratch lie in a directory of their own under <c>--store</c>,
/// removed after.
/// </summary>
internal static class HashedEndMicro
{
    internal static async Task<int> Run(string root, long rows)
    {
        string directory = Path.Combine(root, $"hashedend-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            VortexWriteOptions options = new VortexWriteOptions
            {
                IndexBudgetPerMille = 1_000_000,
                WritePolicy = WritePolicy.None.For(nameof(HashedId.Id), IndexSpec.HashedPostings.AsRequired()),
                ScratchDirectory = directory,
            };

            Random random = new Random(17);
            HashedId[] batch = new HashedId[1 << 20];
            long begin = Stopwatch.GetTimestamp();
            (long First, long Peak, long Native, long Committed) writing;
            (long First, long Peak, long Native, long Committed) ending;
            double written;
            long allocated;
            WriteReport report;
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<HashedId>(Path.Combine(directory, "ids.vortex"), options))
            {
                using (PeakMemory memory = new PeakMemory())
                {
                    for (long at = 0; at < rows; at += batch.Length)
                    {
                        int count = (int)Math.Min(batch.Length, rows - at);
                        for (int i = 0; i < count; i++)
                        {
                            batch[i] = new HashedId(random.NextInt64());
                        }

                        await writer.WriteAsync<HashedId>(batch.AsSpan(0, count));

                        // The bytes the writer holds go to the file past 64 MiB, as a stream of rows flushes them.
                        if (writer.UnflushedBytes >= 64L << 20)
                        {
                            await writer.FlushAsync();
                        }
                    }

                    writing = (memory.First, memory.Peak, memory.PeakNative, memory.PeakCommitted);
                }

                written = Stopwatch.GetElapsedTime(begin).TotalSeconds;
                long before = GC.GetTotalAllocatedBytes(precise: true);
                using (PeakMemory memory = new PeakMemory())
                {
                    report = await writer.CompleteAsync();
                    ending = (memory.First, memory.Peak, memory.PeakNative, memory.PeakCommitted);
                }

                allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            }

            double seconds = Stopwatch.GetElapsedTime(begin).TotalSeconds;
            long index = report.Index(nameof(HashedId.Id), IndexKinds.PostingsHashed)?.Bytes ?? 0;
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{rows:N0} random ids, hashed postings of {index / 1e6:F1} MB, written in {written:F1} s and completed in {seconds - written:F1} s"));
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  writing:    {writing.First / 1e9:F2} GB private at the start, {writing.Peak / 1e9:F2} GB at most (+{(writing.Peak - writing.First) / 1e9:F2}), of which {writing.Native / 1e9:F2} GB native blocks and {writing.Committed / 1e9:F2} GB the collector's"));
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  completing: {ending.First / 1e9:F2} GB private at the start, {ending.Peak / 1e9:F2} GB at most (+{(ending.Peak - ending.First) / 1e9:F2}), of which {ending.Native / 1e9:F2} GB native blocks and {ending.Committed / 1e9:F2} GB the collector's, {allocated / 1e6:F0} MB allocated"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return 0;
    }
}

/// <summary>A row of one random identifier, as the hashed postings' end micro writes it.</summary>
[VortexRecord]
public partial record struct HashedId(long Id);
