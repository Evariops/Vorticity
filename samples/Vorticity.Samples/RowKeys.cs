using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Vorticity.RowEncoding;

namespace Vorticity.Samples;

[VortexRecord]
public partial record struct CityCelsius(string City, double? Celsius);

[VortexRecord]
public partial record struct CityKey(string City);

internal static class RowKeys
{
    internal static async Task RunAsync()
    {
        Console.WriteLine($"the encoding follows Vortex {RowEncoder.VortexVersion}");
        string path = await Demo.ReadingsAsync();
        await using VortexFile file = await VortexFile.OpenAsync(path);

        await foreach (Columns<CityCelsius> batch in file.Scan<CityCelsius>().With(new ScanOptions { BatchRows = 8 }))
        {
            using RowEncoding.RowKeys keys = RowEncoder.Encode(batch);
            Console.WriteLine($"{keys.RowCount} keys, {keys.TotalBytes} bytes in all");
            for (int row = 0; row < 4; row++)
            {
                Console.WriteLine($"  row {row}: {batch.City.GetString(row)}, {batch.Celsius[row]?.ToString() ?? "null"} -> {Convert.ToHexString(keys.Row(row))}");
            }

            Console.WriteLine($"row 6 against row 7: keys {Sign(keys.Compare(6, 7))}, bytes {Sign(keys.Row(6).SequenceCompareTo(keys.Row(7)))}");

            int[] order = [0, 1, 2, 3, 4, 5, 6, 7];
            keys.SortIndices(order);
            Console.WriteLine($"sorted by key: {string.Join(", ", order)}");

            Show("descending, nulls first", batch, [RowSortField.Ascending, RowSortField.Ascending.WithDescending(true)]);
            Show("ascending, nulls last", batch, [RowSortField.Ascending, RowSortField.Ascending.WithNullsLast()]);

            byte[] probe = RowEncoder.EncodeKey(new CityCelsius("Paris", 10.5));
            Console.WriteLine($"the key of (Paris, 10.5): {Convert.ToHexString(probe)}; against row 1: {Sign(((ReadOnlySpan<byte>)probe).SequenceCompareTo(keys.Row(1)))}");

            byte[] prefix = RowEncoder.EncodeKey(new CityKey("Paris"));
            Console.WriteLine($"the key of (Paris) is a prefix of row 1: {keys.Row(1).StartsWith(prefix)}");
            break;
        }

        await foreach (BatchView batch in file.Scan("City", "Celsius").With(new ScanOptions { BatchRows = 8 }))
        {
            using RowEncoding.RowKeys keys = RowEncoder.Encode(batch);
            Console.WriteLine($"the tool path, {batch.RowCount} rows: row 1 {Convert.ToHexString(keys.Row(1))}");
            break;
        }

        string indexed = Demo.Path("row-keys.vortex");
        VortexWriteOptions options = new VortexWriteOptions
        {
            Indexes = IndexPolicy.None.ForKey([Reading.ColumnNames.City, Reading.ColumnNames.Day], IndexKind.SortedRuns, new RowKeyEncoder(), required: true),
        };

        WriteReport report;
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(indexed, options))
        {
            Reading[] rows = new Reading[200_000];
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new Reading(i / 1_000, i % 50 == 0 ? null : i % 400 / 10.0, Demo.Cities[i / 7 % Demo.Cities.Length]);
            }

            await writer.WriteAsync<Reading>(rows);
            report = await writer.CompleteAsync();
        }

        foreach (IndexWriteReport index in report.Indexes)
        {
            Console.WriteLine($"index on {index.Column}: {index.Kind}, {index.Outcome}, {index.Bytes} bytes {index.Reason}");
        }

        await using VortexFile written = await VortexFile.OpenAsync(indexed);
        ImmutableArray<VortexIndexInfo> listed = await written.GetIndexesAsync();
        foreach (VortexIndexInfo info in listed)
        {
            Console.WriteLine($"in the file: {info.Kind} on {info.Column}, {info.Runs} runs, {info.Entries} entries, {info.ListedBytes} bytes");
        }

        Console.WriteLine($"the format it names: {new RowKeyEncoder().Format}; descending: {new RowKeyEncoder(RowSortField.Ascending, RowSortField.Ascending.WithDescending(true)).Format}");
    }

    private static void Show(string what, Columns<CityCelsius> batch, RowSortField[] fields)
    {
        using RowEncoding.RowKeys keys = RowEncoder.Encode(batch, fields);
        Console.WriteLine($"  {what}: row 0 {Convert.ToHexString(keys.Row(0))}, row 1 {Convert.ToHexString(keys.Row(1))}");
    }

    private static string Sign(int comparison) => comparison < 0 ? "<" : comparison > 0 ? ">" : "=";
}
