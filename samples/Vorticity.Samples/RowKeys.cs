using System;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.RowEncoding;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Samples;

internal static class RowKeys
{
    internal static async Task RunAsync()
    {
        Console.WriteLine($"the encoding follows Vortex {RowEncoder.VortexVersion}");

        DTypeArena types = new DTypeArena();
        DType schema = Demo.CitiesSchema(types);
        using RecordBatch batch = Demo.CitiesBatch(types, schema, 8, 0, everyThousandthIsNull: true);

        // One key per row, over both columns, ascending.
        ReadOnlySpan<RowSortField> ascending = [RowSortField.Ascending, RowSortField.Ascending];
        using RowEncoding.RowKeys keys = RowEncoder.Encode(batch, ascending);
        Console.WriteLine($"{keys.RowCount} keys, {keys.TotalBytes} bytes in all");
        for (int row = 0; row < 4; row++)
        {
            Console.WriteLine($"  row {row}: {Convert.ToHexString(keys.Row(row))}");
        }

        // The point of the encoding: memcmp order is tuple order.
        Console.WriteLine($"row 0 against row 1: keys {Sign(keys.Compare(0, 1))}, " +
            $"bytes {Sign(keys.Row(0).SequenceCompareTo(keys.Row(1)))}");

        int[] order = [7, 6, 5, 4, 3, 2, 1, 0];
        keys.SortIndices(order);
        Console.WriteLine($"sorted by key: {string.Join(", ", order)}");

        // Descending, and where nulls go.
        Compare("ascending, nulls last", [RowSortField.Ascending, RowSortField.Ascending.WithNullsLast()]);
        Compare("ascending, nulls first", [RowSortField.Ascending, RowSortField.Ascending.WithNullsFirst()]);
        Compare("descending", [RowSortField.Ascending.WithDescending(true), RowSortField.Ascending]);

        void Compare(string what, RowSortField[] fields)
        {
            using RowEncoding.RowKeys encoded = RowEncoder.Encode(batch, fields);
            Console.WriteLine($"  {what}: row 0 {Convert.ToHexString(encoded.Row(0))}");
        }

        // A key for a tuple you hold, to compare against the encoded rows.
        byte[] key = RowEncoder.EncodeKey(
            [FilterLiteral.From("Lyon"), FilterLiteral.From(12.5)],
            [types.Utf8(Nullability.NonNullable), types.Primitive(PType.F64, Nullability.Nullable)],
            ascending);
        Console.WriteLine($"the key of (\"Lyon\", 12.5): {Convert.ToHexString(key)}");

        // The same encoder, handed to the writer, so an index is built over the tuple.
        string path = Demo.Path("row-keys.vortex");
        VortexWriteOptions options = VortexWriteOptions.Default
            .WithKeyEncoder(new RowKeyEncoder([RowSortField.Ascending, RowSortField.Ascending]));
        await Demo.WriteCitiesAsync(path, options, rows: 20_000);
        Console.WriteLine($"written with a row-key encoder: {new System.IO.FileInfo(path).Length} bytes");
    }

    private static string Sign(int comparison) => comparison < 0 ? "<" : comparison > 0 ? ">" : "=";
}
