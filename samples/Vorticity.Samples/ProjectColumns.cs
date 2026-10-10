using System;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class ProjectColumns
{
    internal static async Task RunAsync()
    {
        string path = await Demo.VisitsAsync();
        await using VortexFile file = await VortexFile.OpenAsync(path);

        int longest = 0;
        DateTime last = default;
        await foreach (var (startedAt, durationMs) in file.Scan<VisitTiming>())
        {
            foreach (int duration in durationMs.Values)
            {
                longest = Math.Max(longest, duration);
            }

            last = startedAt[startedAt.Length - 1];
        }

        Console.WriteLine($"longest visit {longest} ms, the last one started at {last:u}");

        Console.WriteLine($"{"record",-12} {"columns",8} {"segments",9} {"to read",10} {"requests",9} {"read",10}");
        await MeasureAsync<Visit>(file, "Visit");
        await MeasureAsync<VisitTiming>(file, "VisitTiming");
        await MeasureAsync<VisitOrigin>(file, "VisitOrigin");
        await MeasureAsync<VisitId>(file, "VisitId");

        try
        {
            await file.Scan<VisitLength>().CountAsync();
        }
        catch (VortexSchemaException error)
        {
            Console.WriteLine($"VisitLength: {error.Message}");
        }
    }

    private static async Task MeasureAsync<TRecord>(VortexFile file, string name)
        where TRecord : IVortexRecord<TRecord>
    {
        ScanPlan plan = await file.Scan<TRecord>().ExplainAsync();

        Scan<TRecord> scan = file.Scan<TRecord>();
        long rows = 0;
        await foreach (Columns<TRecord> columns in scan)
        {
            rows += columns.RowCount;
        }

        ScanMetrics stats = scan.Metrics;
        Console.WriteLine($"{name,-12} {Leaves(TRecord.Schema),8} {plan.Segments,9} {plan.BytesToRead,10} {stats.Requests,9} {stats.BytesRequested,10}  ({rows} rows)");
    }

    private static int Leaves(VortexSchema schema)
    {
        int leaves = 0;
        foreach (VortexField field in schema)
        {
            leaves += field.Type.Kind == VortexTypeKind.Struct ? field.Type.Fields.Length : 1;
        }

        return leaves;
    }
}

/// <summary>Two columns of the visits file.</summary>
[VortexRecord]
public partial record struct VisitTiming(DateTime StartedAt, int DurationMs);

/// <summary>One field of the nested origin record.</summary>
[VortexRecord]
public partial record struct OriginCountry(string Country);

/// <summary>The origin of a visit, reduced to its country.</summary>
[VortexRecord]
public partial record struct VisitOrigin(OriginCountry Origin);

/// <summary>The identifier of a visit.</summary>
[VortexRecord]
public partial record struct VisitId(Guid Id);

/// <summary>A member the visits file has no column for.</summary>
[VortexRecord]
public partial record struct VisitLength(long Duration);
