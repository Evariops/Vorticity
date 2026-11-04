using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Types.Variants;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What the census of a consolidation holds while it counts its inputs' paths (<c>--micro census --store DIR</c>): the
/// variant column of the largest <c>--objects</c> data objects (12 by default) of the dataset directory <c>DIR</c>, counted at
/// 1, 4 and 16 lanes side by side, each lane an object at a time, as a job's census counts its inputs. For each: the most
/// private bytes the process held above its start, the native blocks among them and those parked in the pool, the
/// collector's committed memory, and the rows counted.
/// </summary>
internal static class CensusMemoryMicro
{
    internal static async Task<int> Run(string directory, int objects)
    {
        List<string> files = await InputMemoryMicro.LargestAsync(directory, objects);
        Console.WriteLine("lanes          rows   seconds   private MB    native MB    parked MB   managed MB");
        foreach (int lanes in new[] { 1, 4, 16 })
        {
            await MeasureAsync([.. files], lanes);
        }

        return 0;
    }

    private static async Task MeasureAsync(string[] files, int lanes)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        AlignedBufferPool.Shared.Trim();
        long rows = 0;
        long begin = Stopwatch.GetTimestamp();
        long nativeStart = NativeSegmentOwner.NativeBytes;
        long parkedStart = AlignedBufferPool.Shared.ParkedBytes;
        int textBound = new VortexWriteOptions().StringBoundBytes;
        PeakMemory memory = new PeakMemory();
        try
        {
            await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = lanes }, async (path, token) =>
            {
                await using VortexFile file = await VortexFile.OpenAsync(path, token);
                DType schema = file.LayoutTree.Root.DType;
                int field = Enumerable.Range(0, schema.FieldCount).First(i => schema.GetField(i).Kind == DTypeKind.Variant);
                PathCensus census = new PathCensus(textBound);
                await foreach (RecordBatch batch in file.ScanBuilder().ProjectMask(FieldMask.Single(field)).ExecuteAsync().WithCancellation(token))
                {
                    census.Add(batch, 0);
                    Interlocked.Add(ref rows, batch.RowCount);
                }
            });
        }
        finally
        {
            memory.Dispose();
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{lanes,5} {rows,13:N0} {Stopwatch.GetElapsedTime(begin).TotalSeconds,9:F1} {(memory.Peak - memory.First) / 1e6,12:F0} {(memory.PeakNative - nativeStart) / 1e6,12:F0} {(memory.PeakParked - parkedStart) / 1e6,12:F0} {memory.PeakCommitted / 1e6,12:F0}"));
    }
}
