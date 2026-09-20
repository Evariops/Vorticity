using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scan;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Samples;

/// <summary>
/// The files the samples read, and the two measurements several pages quote. Every file is written
/// under one temporary directory and deleted when the run ends.
/// </summary>
internal static class Demo
{
    /// <summary>A million rows of two columns: a day counter in order, and a temperature.</summary>
    internal const int ReadingRows = 1_000_000;

    /// <summary>Half a million rows of six columns of the same width, to price a projection.</summary>
    internal const int WideRows = 500_000;

    internal static readonly string Directory =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vorticity-samples");

    internal static string Readings { get; private set; } = string.Empty;

    internal static string Wide { get; private set; } = string.Empty;

    internal static string Path(string name) =>
        System.IO.Path.Combine(Ensure(), name);

    /// <summary>Writes the readings file once and returns its path.</summary>
    internal static async ValueTask<string> ReadingsAsync()
    {
        if (Readings.Length != 0)
        {
            return Readings;
        }

        string path = Path("readings.vortex");
        await WriteReadingsAsync(path, VortexWriteOptions.Default);
        Readings = path;
        return path;
    }

    /// <summary>Writes the six-column file once and returns its path.</summary>
    internal static async ValueTask<string> WideAsync()
    {
        if (Wide.Length != 0)
        {
            return Wide;
        }

        string path = Path("wide.vortex");
        DTypeArena types = new DTypeArena();
        string[] names = ["a", "b", "c", "d", "e", "f"];
        DType[] fields = new DType[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            fields[i] = types.Primitive(PType.F64, Nullability.NonNullable);
        }

        DType schema = types.Struct(names, fields, Nullability.NonNullable);
        await using (VortexFileWriter writer = VortexFileWriter.Create(path, schema))
        {
            CanonicalArena arena = new CanonicalArena();
            int[] nodes = new int[names.Length];
            for (int f = 0; f < names.Length; f++)
            {
                VortexBuffer buffer = arena.Allocate(WideRows * sizeof(double), 8, out Span<byte> bytes);
                Span<double> values = MemoryMarshal.Cast<byte, double>(bytes);
                for (int i = 0; i < WideRows; i++)
                {
                    values[i] = (i * (7919L + f) % 100_003) / 7.0;
                }

                nodes[f] = arena.AddPrimitive(schema.GetField(f), WideRows, Validity.NonNullable, PType.F64, buffer);
            }

            int root = arena.AddStruct(schema, WideRows, Validity.NonNullable, nodes);
            using RecordBatch batch = new RecordBatch(arena, root, 0);
            await writer.WriteAsync(batch);
            await writer.CompleteAsync();
        }

        Wide = path;
        return path;
    }

    /// <summary>
    /// Writes the readings file with the options given: a day counter that rises one per thousand
    /// rows, and a temperature that walks the same range over and over.
    /// </summary>
    internal static async Task WriteReadingsAsync(string path, VortexWriteOptions options)
    {
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(
            ["day", "celsius"],
            [types.Primitive(PType.I32, Nullability.NonNullable),
             types.Primitive(PType.F64, Nullability.NonNullable)],
            Nullability.NonNullable);

        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer days = arena.Allocate(ReadingRows * sizeof(int), 8, out Span<byte> dayBytes);
        VortexBuffer degrees = arena.Allocate(ReadingRows * sizeof(double), 8, out Span<byte> degreeBytes);
        Span<int> day = MemoryMarshal.Cast<byte, int>(dayBytes);
        Span<double> celsius = MemoryMarshal.Cast<byte, double>(degreeBytes);
        for (int i = 0; i < ReadingRows; i++)
        {
            day[i] = i / 1_000;
            celsius[i] = 10.0 + (i * 7919L % 4001) / 100.0;
        }

        int dayNode = arena.AddPrimitive(schema.GetField(0), ReadingRows, Validity.NonNullable, PType.I32, days);
        int celsiusNode = arena.AddPrimitive(schema.GetField(1), ReadingRows, Validity.NonNullable, PType.F64, degrees);
        int root = arena.AddStruct(schema, ReadingRows, Validity.NonNullable, [dayNode, celsiusNode]);
        using RecordBatch batch = new RecordBatch(arena, root, 0);
        await writer.WriteAsync(batch);
        await writer.CompleteAsync();
    }

    /// <summary>
    /// What a scan asks of the file underneath it: the rounds of reading, the bytes in them, and
    /// the rows delivered. A segment is fetched again for every block that needs it, so the bytes
    /// counted here are well above the file's length on a scan that reads everything.
    /// </summary>
    internal static async Task<(long Requests, long Bytes, long Rows)> MeasureAsync(
        string path, Func<VortexFile, ScanBuilder> scan)
    {
        CountingSegmentSource counting = new CountingSegmentSource(RandomAccessSegmentSource.Open(path));
        long rows = 0;
        await using (VortexFile file = await VortexFile.OpenAsync(counting, VortexOpenOptions.Default))
        {
            await foreach (RecordBatch batch in scan(file).ExecuteAsync())
            {
                using (batch)
                {
                    rows += batch.RowCount;
                }
            }
        }

        return (counting.Requests, counting.Bytes, rows);
    }

    internal static void Clean()
    {
        Readings = string.Empty;
        Wide = string.Empty;
        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private static string Ensure()
    {
        System.IO.Directory.CreateDirectory(Directory);
        return Directory;
    }
}
