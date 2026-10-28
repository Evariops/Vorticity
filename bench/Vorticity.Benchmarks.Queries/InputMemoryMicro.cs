using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Layouts;
using Vorticity.Scanning;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What the inputs of a merge hold while it reads them (<c>--micro inputs --store DIR</c>): the largest <c>--objects</c>
/// data objects (8 by default) of the dataset directory <c>DIR</c>, scanned as a job's merge reads its inputs, in their own
/// order: the largest one a column at a time, then every column of 1, 2, 4 and 8 of them in lockstep, each holding its
/// batch while the others step, as a merge holds an input's run. For each: the most private bytes the process held above
/// its start, the native blocks among them and those parked in the pool, the collector's committed memory, and the rows
/// and batches read.
/// </summary>
internal static class InputMemoryMicro
{
    /// <summary>Whether a scan of one input prints its native blocks every twentieth batch (<c>VORTICITY_INPUTS_TRACE=1</c>).</summary>
    private static readonly bool Trace = Environment.GetEnvironmentVariable("VORTICITY_INPUTS_TRACE") == "1";

    internal static async Task<int> Run(string directory, int objects)
    {
        List<string> files = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(directory, "data"), "*.vortex").OrderByDescending(static file => new FileInfo(file).Length))
        {
            // The partition indexes lie beside the data objects and are no Vortex files.
            try
            {
                await using VortexFile opened = await VortexFile.OpenAsync(file);
                files.Add(file);
            }
            catch (VortexFormatException)
            {
                continue;
            }

            if (files.Count == Math.Max(1, objects))
            {
                break;
            }
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{directory}: the {files.Count} largest data objects, {files.Sum(static file => new FileInfo(file).Length) / 1e6:F0} MB"));
        Console.WriteLine("what                          rows   batches   seconds   private MB    native MB    parked MB   managed MB");
        await using (VortexFile largest = await VortexFile.OpenAsync(files[0]))
        {
            Types.DType schema = largest.LayoutTree.Root.DType;
            for (int field = 0; field < schema.FieldCount; field++)
            {
                await MeasureAsync(schema.GetField(field) is var dtype ? $"{schema.GetFieldName(field)} ({dtype.Kind})" : string.Empty, [files[0]], FieldMask.Single(field));
            }
        }

        foreach (int count in new[] { 1, 2, 4, 8 })
        {
            if (count <= files.Count)
            {
                await MeasureAsync($"{count} inputs, every column", [.. files.Take(count)], FieldMask.All);
            }
        }

        return 0;
    }

    private static async Task MeasureAsync(string what, string[] files, FieldMask fields)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        AlignedBufferPool.Shared.Trim();
        List<VortexFile> opened = [];
        List<IAsyncEnumerator<RecordBatch>> scans = [];
        long rows = 0;
        long batches = 0;
        long begin = Stopwatch.GetTimestamp();
        long nativeStart = NativeSegmentOwner.NativeBytes;
        long parkedStart = AlignedBufferPool.Shared.ParkedBytes;
        PeakMemory memory = new PeakMemory();
        try
        {
            foreach (string path in files)
            {
                VortexFile file = await VortexFile.OpenAsync(path);
                opened.Add(file);
                scans.Add(file.ScanBuilder().ProjectMask(fields).ExecuteAsync().GetAsyncEnumerator());
            }

            // Each input steps in turn and holds its batch while the others step, as a merge holds an input's run.
            bool[] done = new bool[scans.Count];
            for (int left = scans.Count; left > 0;)
            {
                for (int i = 0; i < scans.Count; i++)
                {
                    if (done[i])
                    {
                        continue;
                    }

                    if (await scans[i].MoveNextAsync())
                    {
                        rows += scans[i].Current.RowCount;
                        batches++;
                        if (Trace && scans.Count == 1 && batches % 20 == 0)
                        {
                            Console.WriteLine(string.Create(
                                CultureInfo.InvariantCulture,
                                $"    batch {batches,5}: {rows,12:N0} rows, native {(NativeSegmentOwner.NativeBytes - nativeStart) / 1e6,8:F0} MB, parked {(AlignedBufferPool.Shared.ParkedBytes - parkedStart) / 1e6,6:F0} MB"));
                        }
                    }
                    else
                    {
                        done[i] = true;
                        left--;
                    }
                }
            }
        }
        finally
        {
            memory.Dispose();
            foreach (IAsyncEnumerator<RecordBatch> scan in scans)
            {
                await scan.DisposeAsync();
            }

            foreach (VortexFile file in opened)
            {
                await file.DisposeAsync();
            }
        }

        double seconds = Stopwatch.GetElapsedTime(begin).TotalSeconds;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{what,-26} {rows,11:N0} {batches,9:N0} {seconds,9:F1} {(memory.Peak - memory.First) / 1e6,12:F0} {(memory.PeakNative - nativeStart) / 1e6,12:F0} {(memory.PeakParked - parkedStart) / 1e6,12:F0} {memory.PeakCommitted / 1e6,12:F0}"));
    }
}
