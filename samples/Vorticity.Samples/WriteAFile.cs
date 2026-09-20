using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Samples;

internal static class WriteAFile
{
    private static readonly string[] Cities = ["Paris", "Lyon", "Marseille", "Lille", "Bordeaux"];

    internal static async Task RunAsync()
    {
        string path = Demo.Path("cities.vortex");
        const int rows = 200_000;

        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(
            ["city", "celsius"],
            [types.Utf8(Nullability.NonNullable),
             types.Primitive(PType.F64, Nullability.Nullable)],
            Nullability.NonNullable);

        WriteReport report;
        await using (VortexFileWriter writer = VortexFileWriter.Create(path, schema))
        {
            using RecordBatch batch = Batch(types, schema, rows, startRow: 0, everyThousandthIsNull: true);
            await writer.WriteAsync(batch);
            report = await writer.CompleteAsync();
        }

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
            using RecordBatch batch = Batch(types, schema, 10_000, appender.RowCount, everyThousandthIsNull: false);
            await appender.WriteAsync(batch);
            WriteReport appended = await appender.CompleteAsync();
            Console.WriteLine($"{appended.RowCount} rows once the append completed");
        }
    }

    /// <summary>
    /// A batch of city names and temperatures. The text column is a view per row: four bytes of
    /// length, then the bytes themselves when they are twelve or fewer, otherwise a prefix and the
    /// offset of the rest. The temperature carries a bit per row saying whether it is there.
    /// </summary>
    private static RecordBatch Batch(
        DTypeArena types, DType schema, int rows, long startRow, bool everyThousandthIsNull)
    {
        CanonicalArena arena = new CanonicalArena();

        VortexBuffer degrees = arena.Allocate(rows * sizeof(double), 8, out Span<byte> degreeBytes);
        Span<double> celsius = MemoryMarshal.Cast<byte, double>(degreeBytes);
        for (int i = 0; i < rows; i++)
        {
            celsius[i] = 10.0 + (i * 7919L % 3001) / 100.0;
        }

        VortexBuffer views = arena.Allocate(rows * 16, 8, out Span<byte> viewBytes);
        for (int i = 0; i < rows; i++)
        {
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            int length = Encoding.UTF8.GetBytes(Cities[(int)((startRow + i) % Cities.Length)], view[4..]);
            BinaryPrimitives.WriteInt32LittleEndian(view, length);
        }

        Validity validity = Validity.AllValid;
        if (everyThousandthIsNull)
        {
            VortexBuffer bitmap = arena.Allocate((rows + 7) / 8, 8, out Span<byte> bits);
            bits.Fill(0xFF);
            for (int i = 0; i < rows; i += 1_000)
            {
                bits[i >> 3] &= (byte)~(1 << (i & 7));
            }

            validity = Validity.Bitmap(
                arena.AddBool(types.Bool(Nullability.NonNullable), rows, Validity.NonNullable, bitmap, 0));
        }

        int city = arena.AddVarBinView(schema.GetField(0), rows, Validity.NonNullable, views, [VortexBuffer.Empty]);
        int temperature = arena.AddPrimitive(schema.GetField(1), rows, validity, PType.F64, degrees);
        int root = arena.AddStruct(schema, rows, Validity.NonNullable, [city, temperature]);
        return new RecordBatch(arena, root, startRow);
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
