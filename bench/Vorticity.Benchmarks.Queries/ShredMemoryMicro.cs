using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Types.Variants;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What a job's writer holds while it shreds a variant column (<c>--micro shredmemory --store DIR</c>): the first
/// <c>--rows</c> rows (3 000 000 by default) of the dataset directory's largest object, every column, written as a job
/// writes them, the variant column shredded as the census of the object's every row chooses, as a job's census of a
/// partition's rows does, in chunks of a million rows as a job writes them,
/// then of a quarter and of a sixteenth of that. For each: the most private bytes the process held above its start, the
/// native blocks among them, the collector's committed memory, the seconds, and the bytes written. With <c>--out DIR</c>,
/// each file stays there, named after its chunks' rows, for the files of two builds compared byte for byte.
/// </summary>
internal static class ShredMemoryMicro
{
    internal static async Task<int> Run(string directory, int rows, string? keep)
    {
        List<string> files = await InputMemoryMicro.LargestAsync(directory, 1);
        string temporary = Path.Combine(Path.GetTempPath(), $"vortex-shredmemory-{Guid.NewGuid():N}.vortex");
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(files[0]);
            DType schema = file.LayoutTree.Root.DType;
            int field = Enumerable.Range(0, schema.FieldCount).First(i => schema.GetField(i).Kind == DTypeKind.Variant);
            string name = schema.GetFieldName(field);
            RowRange first = new RowRange(0, Math.Min(rows, file.LayoutTree.Root.RowCount));

            // Every row's paths counted, as a job counts a partition's, their text bounds cut as a column's zones cut theirs.
            PathCensus census = new PathCensus(VortexWriteOptions.Default.StringBoundBytes, VortexWriteOptions.Default.BlockRows);
            await foreach (RecordBatch batch in file.ScanBuilder().ProjectMask(FieldMask.Single(field)).ExecuteAsync())
            {
                census.Add(batch, 0);
            }

            VariantShredding plan = ShreddingChoice.Choose(census) ?? throw new InvalidOperationException($"No path of {name} qualifies for shredding.");
            int chunk = ShreddingChoice.ChunkTargetBytes(census);
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{census.Rows:N0} rows of {name}, {plan.Keys.Count} keys shredded at its first level, chunks of {chunk / 1e6:F0} MB of values for {ShreddingChoice.ChunkRows:N0} rows"));
            Console.WriteLine("chunk rows   seconds   private MB    native MB   managed MB   written MB");
            foreach (int share in new[] { 1, 4, 16 })
            {
                // Kept, the files of two builds compare byte for byte.
                long chunkRows = ShreddingChoice.ChunkRows / share;
                string target = keep is null ? temporary : Path.Combine(keep, $"shredded-{chunkRows}.vortex");
                await MeasureAsync(file, first, name, plan, chunk / share, chunkRows, target);
            }
        }
        finally
        {
            System.IO.File.Delete(temporary);
        }

        return 0;
    }

    private static async Task MeasureAsync(VortexFile file, RowRange rows, string name, VariantShredding plan, int chunk, long chunkRows, string target)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        AlignedBufferPool.Shared.Trim();
        long nativeStart = NativeSegmentOwner.NativeBytes;
        VortexWriteOptions options = VortexWriteOptions.Default with
        {
            ShreddedVariants = new Dictionary<string, VariantShredding> { [name] = plan },
            ColumnChunkTargetBytes = ImmutableDictionary<string, int>.Empty.Add(name, chunk),
        };
        long begin = Stopwatch.GetTimestamp();
        PeakMemory memory = new PeakMemory();
        try
        {
            await using VortexFileWriter writer = VortexSession.Default.CreateWriter(target, file.Schema, options);
            await foreach (RecordBatch batch in file.ScanBuilder().Rows(rows).ExecuteAsync())
            {
                await writer.WriteBatchAsync(batch);
            }

            await writer.CompleteAsync();
        }
        finally
        {
            memory.Dispose();
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{chunkRows,10:N0} {Stopwatch.GetElapsedTime(begin).TotalSeconds,9:F1} {(memory.Peak - memory.First) / 1e6,12:F0} {(memory.PeakNative - nativeStart) / 1e6,12:F0} {memory.PeakCommitted / 1e6,12:F0} {new FileInfo(target).Length / 1e6,12:F0}"));
    }
}
