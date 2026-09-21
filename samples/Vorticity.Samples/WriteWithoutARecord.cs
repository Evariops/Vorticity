using System;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class WriteWithoutARecord
{
    private const int Rows = 100_000;

    internal static async Task RunAsync()
    {
        VortexSession session = VortexSession.Default;
        CancellationToken ct = CancellationToken.None;
        string path = Demo.Path("write-without-a-record.vortex");
        long[] ids = new long[Rows];
        for (int i = 0; i < ids.Length; i++) ids[i] = 1_000_000 + i;
        byte[] payload = new byte[64];
        new Random(42).NextBytes(payload);

        VortexSchema schema = [("id", VortexType.Int64), ("payload", VortexType.Binary.Nullable), ("origin", VortexType.Struct([("country", VortexType.Utf8)]))];
        WriteReport report;
        await using (VortexFileWriter writer = session.CreateWriter(path, schema))
        {
            ColumnsBuilder b = writer.Builder();
            b.Column<long>(0).Append(ids);                                           // by position
            ColumnBuilder<ReadOnlyMemory<byte>?> payloads = b.Column<ReadOnlyMemory<byte>?>("payload");   // by name
            ColumnBuilder<string> country = b.Struct(2).Column<string>("country");  // a nested field, through its struct
            for (int i = 0; i < Rows; i++)
            {
                BitConverter.TryWriteBytes(payload, i);
                if (i % 3 == 0) payloads.AppendNull();
                else payloads.Append(payload.AsSpan(0, 16 + (i % 49)));
                country.Append(i % 2 == 0 ? "FR"u8 : "DE"u8);
            }

            try
            {
                b.Column<int>("id");
            }
            catch (VortexSchemaException e)
            {
                Console.WriteLine($"Column<int> over an i64: {e.Message}");
            }

            try
            {
                b.Column<long>("identifier");
            }
            catch (VortexSchemaException e)
            {
                Console.WriteLine($"a name the schema does not have: {e.Message}");
            }

            await writer.WriteAsync(b, ct);
            report = await writer.CompleteAsync(ct);
        }

        Console.WriteLine($"{report.RowCount} rows, {report.Bytes.Total} bytes; {string.Join("; ", Array.ConvertAll([.. report.Columns], c => $"{c.Path} {WriteReportText.Encodings(c.Encodings)}"))}");

        await using VortexFile file = await VortexFile.OpenAsync(path);
        Console.WriteLine($"schema {file.Schema}");
        long sum = 0;
        long nulls = 0;
        long bytes = 0;
        await foreach (BatchView batch in file.Scan("id", "payload"))
        {
            Column<long> id = batch.Column<long>("id");
            Column<ReadOnlyMemory<byte>?> blobs = batch.Column<ReadOnlyMemory<byte>?>("payload");
            foreach (long value in id.Values) sum += value;
            nulls += blobs.NullCount;
            for (int i = 0; i < blobs.Length; i++)
            {
                if (blobs.IsValid(i)) bytes += blobs[i].Length;
            }
        }

        Console.WriteLine($"read back by name: ids summing to {sum}, {nulls} null payloads, {bytes} payload bytes");
    }
}
