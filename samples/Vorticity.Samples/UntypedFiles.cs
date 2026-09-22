using System;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class UntypedFiles
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();
        await using VortexFile file = await VortexFile.OpenAsync(path);
        Console.WriteLine($"{file.Schema}, {file.RowCount} rows");

        int threshold = 900;
        long rows = 0;
        double total = 0;
        await foreach (BatchView batch in file.Scan("Celsius", "Day").Where($"Day >= {threshold}"))
        {
            Column<int> day = batch.Column<int>("Day");
            Column<double?> celsius = batch.Column<double?>("Celsius");
            if (rows == 0)
            {
                Console.WriteLine($"asked for Celsius, Day; the batch holds {batch.Schema}, first day {day[0]}");
            }

            for (int i = 0; i < celsius.Length; i++)
            {
                total += celsius[i] ?? 0;
            }

            rows += batch.RowCount;
        }

        Console.WriteLine($"Day >= {threshold}: {rows} rows, {total:F1} degrees in all");

        VortexExpr parsed = VortexExpr.Parse("Day >= 900 and City = 'Paris' and Celsius is not null");
        Console.WriteLine($"parsed: {parsed}, {await file.Scan().Where(parsed).CountAsync()} rows");
        VortexExpr combined = VortexExpr.Parse("City in ('Lyon', 'Nice')") & !VortexExpr.Parse("Celsius < 40");
        Console.WriteLine($"combined: {combined}, {await file.Scan().Where(combined).CountAsync()} rows");

        try
        {
            string text = "900";
            file.Scan("Day").Where($"Day >= {text}");
        }
        catch (VortexSchemaException e)
        {
            Console.WriteLine($"a string hole against an integer column: {e.Message}");
        }

        try
        {
            file.Scan("Day").Where(VortexExpr.Parse("Month = 3"));
        }
        catch (VortexSchemaException e)
        {
            Console.WriteLine($"a column the file does not have: {e.Message}");
        }

        await foreach (BatchView batch in file.Scan("Celsius"))
        {
            try
            {
                Column<double> raw = batch.Column<double>("Celsius");
                Console.WriteLine($"Column<double> over a nullable column: accepted, {raw.NullCount} nulls in the first batch, row 0 reads {raw[0]}");
            }
            catch (VortexSchemaException e)
            {
                Console.WriteLine($"a non-nullable type over a nullable column: {e.Message}");
            }

            break;
        }

        string visits = await Demo.VisitsAsync();
        await using VortexFile nested = await VortexFile.OpenAsync(visits);
        VortexSchema schema = nested.Schema;
        foreach (VortexField field in schema)
        {
            VortexType type = field.Type;
            string detail = type.Kind switch
            {
                VortexTypeKind.List => $", elements {type.ElementType}",
                VortexTypeKind.Struct => $", {type.Fields.Length} fields",
                VortexTypeKind.Extension => $", extension {type.ExtensionId} stored as {type.StorageType}",
                _ => string.Empty,
            };
            Console.WriteLine($"  {field.Name}: {type} ({type.Kind}{(type.IsNullable ? ", nullable" : string.Empty)}{detail})");
        }

        Console.WriteLine($"IndexOf(\"Origin.City\") = {schema.IndexOf("Origin.City")}, IndexOf(\"Nope\") = {schema.IndexOf("Nope")}");

        await foreach (BatchView batch in nested.Scan("Origin.City", "Id").Where($"Origin.Country = {"DE"}"))
        {
            Console.WriteLine($"a nested path by name: the batch holds {batch.Schema}, {batch.RowCount} rows in the first batch");
            break;
        }

        VortexSchema declared = [("Day", VortexType.Int32), ("Celsius", VortexType.Float64.Nullable), ("City", VortexType.Utf8)];
        Console.WriteLine($"a schema written by hand equals the file's: {declared == file.Schema}");
    }
}
