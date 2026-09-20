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
        await WriteWideAsync(path, VortexWriteOptions.Default);
        Wide = path;
        return path;
    }

    /// <summary>Writes the six-column file with the options given, and says how many chunks it took.</summary>
    internal static async Task<int> WriteWideAsync(string path, VortexWriteOptions options)
    {
        DTypeArena types = new DTypeArena();
        string[] names = ["a", "b", "c", "d", "e", "f"];
        DType[] fields = new DType[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            fields[i] = types.Primitive(PType.F64, Nullability.NonNullable);
        }

        DType schema = types.Struct(names, fields, Nullability.NonNullable);
        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
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
        WriteReport report = await writer.CompleteAsync();
        return report.ChunkRows.Count;
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

    /// <summary>The city names the text column of the cities file cycles through.</summary>
    internal static readonly string[] Cities = ["Paris", "Lyon", "Marseille", "Lille", "Bordeaux"];

    /// <summary>Two columns: a city name, and a temperature that is sometimes missing.</summary>
    internal static DType CitiesSchema(DTypeArena types) =>
        types.Struct(
            ["city", "celsius"],
            [types.Utf8(Nullability.NonNullable),
             types.Primitive(PType.F64, Nullability.Nullable)],
            Nullability.NonNullable);

    /// <summary>
    /// A batch of city names and temperatures. The text column is a view per row: four bytes of
    /// length, then the bytes themselves when they are twelve or fewer, otherwise a prefix and the
    /// offset of the rest in a data buffer. The temperature carries a bit per row saying whether it
    /// is there.
    /// </summary>
    internal static RecordBatch CitiesBatch(
        DTypeArena types, DType schema, int rows, long startRow, bool everyThousandthIsNull,
        bool clusteredByCity = false)
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
            // Cycling, the five names are in every block and no block's bounds can exclude one.
            // Clustered, each name holds a stretch of rows and the bounds can.
            string name = clusteredByCity
                ? Cities[(int)(i * (long)Cities.Length / rows)]
                : Cities[(int)((startRow + i) % Cities.Length)];
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            int length = Encoding.UTF8.GetBytes(name, view[4..]);
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

    /// <summary>
    /// A log of requests: a session identifier, and the row's own number. An identifier is the
    /// column an equality is asked of and the column a Bloom filter is for -- no order, no repeats,
    /// and nothing to compress, because that is what an identifier is.
    /// </summary>
    internal static async Task<WriteReport> WriteSessionsAsync(
        string path, VortexWriteOptions options, int rows = 200_000)
    {
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(
            ["session", "n"],
            [types.Utf8(Nullability.NonNullable),
             types.Primitive(PType.I64, Nullability.NonNullable)],
            Nullability.NonNullable);

        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
        CanonicalArena arena = new CanonicalArena();

        VortexBuffer counters = arena.Allocate(rows * sizeof(long), 8, out Span<byte> counterBytes);
        Span<long> n = MemoryMarshal.Cast<byte, long>(counterBytes);
        VortexBuffer views = arena.Allocate(rows * 16, 8, out Span<byte> viewBytes);
        for (int i = 0; i < rows; i++)
        {
            n[i] = i;
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            int length = Encoding.UTF8.GetBytes(Session(i), view[4..]);
            BinaryPrimitives.WriteInt32LittleEndian(view, length);
        }

        int session = arena.AddVarBinView(schema.GetField(0), rows, Validity.NonNullable, views, [VortexBuffer.Empty]);
        int counter = arena.AddPrimitive(schema.GetField(1), rows, Validity.NonNullable, PType.I64, counters);
        int root = arena.AddStruct(schema, rows, Validity.NonNullable, [session, counter]);
        using RecordBatch batch = new RecordBatch(arena, root, 0);
        await writer.WriteAsync(batch);
        return await writer.CompleteAsync();
    }

    /// <summary>
    /// The session identifier of row <paramref name="row"/>: twelve characters, so it lives inside
    /// its view rather than in a data buffer. Seven of them come from a hash of the row and five
    /// from the row itself, which is what makes every identifier distinct and the column
    /// incompressible -- the two properties a real identifier has.
    /// </summary>
    internal static string Session(int row)
    {
        const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
        Span<char> token = stackalloc char[12];
        uint scrambled = (uint)row * 2654435761u;
        for (int i = 0; i < 7; i++)
        {
            token[i] = Alphabet[(int)(scrambled % 36)];
            scrambled /= 36;
        }

        int value = row;
        for (int i = 11; i >= 7; i--)
        {
            token[i] = Alphabet[value % 36];
            value /= 36;
        }

        return new string(token);
    }

    /// <summary>Writes the cities file with the options given, and reports what the writer chose.</summary>
    internal static async Task<WriteReport> WriteCitiesAsync(
        string path, VortexWriteOptions options, int rows = 200_000, bool clusteredByCity = false)
    {
        DTypeArena types = new DTypeArena();
        DType schema = CitiesSchema(types);
        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
        using RecordBatch batch = CitiesBatch(
            types, schema, rows, 0, everyThousandthIsNull: true, clusteredByCity);
        await writer.WriteAsync(batch);
        return await writer.CompleteAsync();
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
