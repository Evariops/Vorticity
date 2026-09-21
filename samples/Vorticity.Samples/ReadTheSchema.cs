using System;
using System.Threading.Tasks;
using Vorticity.File;
using Vorticity.Types;

namespace Vorticity.Samples;

internal static class ReadTheSchema
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();

        await using VortexFile file = await VortexFile.OpenAsync(path);
        DType schema = file.DType;
        Console.WriteLine($"{schema.FieldCount} columns, {file.RowCount} rows, tabular: {file.IsTabular}");
        for (int i = 0; i < schema.FieldCount; i++)
        {
            DType field = schema.GetField(i);
            Console.WriteLine($"  {schema.GetFieldName(i)}: {DTypeFormatter.Format(field)}" +
                (field.IsNullable ? ", may be null" : string.Empty));
        }

        Console.WriteLine($"the whole schema in one line: {DTypeFormatter.Format(schema)}");

        int index = schema.IndexOfField("celsius");
        Console.WriteLine(index < 0 ? "no celsius column" : $"celsius is column {index}");

        if (file.HasFileStatistics)
        {
            FileStatistics statistics = file.FileStatistics;
            for (int i = 0; i < statistics.FieldCount; i++)
            {
                FieldStatistics field = statistics.GetField(i);
                string nulls = field.TryGetStoredNullCount(out ulong count) ? count.ToString() : "unknown";
                string sorted = field.TryGetIsSorted(out bool isSorted) ? isSorted.ToString() : "unknown";
                Console.WriteLine($"  {schema.GetFieldName(i)}: nulls {nulls}, sorted {sorted}, " +
                    $"bounds known: {field.HasMin && field.HasMax}");
            }
        }
    }
}
