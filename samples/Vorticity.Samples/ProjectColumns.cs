using System;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Samples;

internal static class ProjectColumns
{
    internal static async Task RunAsync()
    {
        string path = await Demo.WideAsync();

        await using VortexFile file = await VortexFile.OpenAsync(path);
        await foreach (RecordBatch batch in file.ScanBuilder().Project(["c", "e"]).ExecuteAsync())
        {
            // The batch holds the columns asked for, and its schema is the projected one.
            using (batch)
            {
                Console.WriteLine($"{batch.FieldCount} columns: {DTypeFormatter.Format(batch.DType)}");
                break;
            }
        }

        ScanPlan whole = await file.ScanBuilder().ExplainAsync();
        ScanPlan projected = await file.ScanBuilder().Project(["c"]).ExplainAsync();
        Console.WriteLine($"six columns: {whole.SegmentsToRead} segments, {whole.BytesToRead} bytes");
        Console.WriteLine($"one column:  {projected.SegmentsToRead} segments, {projected.BytesToRead} bytes");

        (long allRequests, long allBytes, long rows) = await Demo.MeasureAsync(path, f => f.ScanBuilder());
        (long oneRequest, long oneBytes, long _) = await Demo.MeasureAsync(path, f => f.ScanBuilder().Project(["c"]));
        Console.WriteLine($"measured over {rows} rows: six columns {allRequests} rounds and {allBytes} bytes, " +
            $"one column {oneRequest} rounds and {oneBytes} bytes");

        await Names("asked for e then c", f => f.ScanBuilder().Project(["e", "c"]));
        await Names("asked for fields 2 and 4", f => f.ScanBuilder().ProjectFields([2, 4]));

        async Task Names(string what, Func<VortexFile, ScanBuilder> scan)
        {
            await foreach (RecordBatch batch in scan(file).ExecuteAsync())
            {
                using (batch)
                {
                    Console.WriteLine($"{what}: {batch.GetFieldName(0)}, {batch.GetFieldName(1)}");
                    break;
                }
            }
        }
    }
}
