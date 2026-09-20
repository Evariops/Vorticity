using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Samples;

internal static class WriteAFile
{
    internal static async Task RunAsync()
    {
        string path = Demo.Path("cities.vortex");
        const int rows = 200_000;

        // The schema, the batch and the write: Demo.CitiesSchema and Demo.CitiesBatch hold the
        // column shapes this page is about -- a text column, and a column with nulls.
        WriteReport report = await Demo.WriteCitiesAsync(path, VortexWriteOptions.Default, rows);

        Console.WriteLine($"{report.RowCount} rows in blocks of {report.BlockRows}, " +
            $"{report.ChunkRows.Count} chunks, {report.Bytes.Total} bytes");
        Console.WriteLine($"  data {report.Bytes.Data}, statistics {report.Bytes.Statistics}, " +
            $"zone maps {report.Bytes.ZoneMaps}, indexes {report.Bytes.Indexes}, footer {report.Bytes.Footer}");
        foreach (ColumnWriteReport column in report.Columns)
        {
            Console.WriteLine($"  {column.Path}: {string.Join(", ", column.Encodings)}");
        }

        foreach (IndexWriteReport index in report.Indexes)
        {
            Console.WriteLine($"  index {index.Path} {index.Kind}: {index.Outcome}");
        }

        Console.WriteLine($"  {rows * (8 + 6)} bytes of values became {new FileInfo(path).Length}");

        await using (VortexFile file = await VortexFile.OpenAsync(path))
        {
            await foreach (RecordBatch batch in file.Scan().Rows(new RowRange(0, 2)).ExecuteAsync())
            {
                using (batch)
                {
                    Console.WriteLine(Describe(batch));
                }
            }
        }

        await using (VortexFileWriter appender = await VortexFileWriter.AppendAsync(path))
        {
            // The rows already sealed into whole blocks; the tail block is rewritten by the append.
            Console.WriteLine($"appending after {appender.RowCount} rows");
            DTypeArena types = new DTypeArena();
            DType schema = Demo.CitiesSchema(types);
            using RecordBatch batch = Demo.CitiesBatch(
                types, schema, 10_000, appender.RowCount, everyThousandthIsNull: false);
            await appender.WriteAsync(batch);
            WriteReport appended = await appender.CompleteAsync();
            Console.WriteLine($"{appended.RowCount} rows once the append completed");
        }
    }

    private static string Describe(RecordBatch batch)
    {
        BinaryColumn city = batch.Column("city"u8).AsBinary();
        VortexColumn celsius = batch.Column("celsius"u8);
        string first = celsius.IsValid(0)
            ? celsius.AsPrimitive<double>()[0].ToString(CultureInfo.InvariantCulture)
            : "no reading";
        return $"{city.GetString(0)}: {first}";
    }
}
