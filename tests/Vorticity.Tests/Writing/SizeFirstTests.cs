using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>
/// What <see cref="CompressionProfile.Smallest"/> promises: no column written larger than the
/// default profile writes it, and every value read back as it went in.
/// </summary>
/// <remarks>
/// Size first offers a column to zstd, pco, FSST and ALP under what the best exact plan writes,
/// and that counts the plan's own layer -- a run-end's ends and values, a dictionary's codes at a
/// whole byte -- before its children take schemes of their own. A trial that comes in under it
/// can still write more than the plan does, so the shapes here are the ones where one did.
/// </remarks>
public sealed class SizeFirstTests
{
    private const int Rows = 262_144;
    private const int Batch = 65_536;

    public static TheoryData<string> Shapes => ["sorted runs", "nullable ids", "jittered timestamps", "small integers"];

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task NoColumnIsWrittenLargerThanTheDefaultWritesIt(string shape)
    {
        (long auto, string byDefault) = await WriteAsync(shape, CompressionProfile.Auto);
        (long smallest, string sizeFirst) = await WriteAsync(shape, CompressionProfile.Smallest);

        Assert.True(
            smallest <= auto,
            $"{shape}: {smallest} bytes ({sizeFirst}) under size first against {auto} ({byDefault}) by default");
    }

    [Fact]
    public async Task ARunEndThatWritesLessThanAFrameIsKept()
    {
        (_, string encodings) = await WriteAsync("sorted runs", CompressionProfile.Smallest);

        Assert.DoesNotContain("Zstd", encodings, StringComparison.Ordinal);
        Assert.Contains("RunEnd", encodings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATrialThatWritesLessIsStillTaken()
    {
        // Jittered timestamps bit-pack at the 27 bits a chunk's span needs over its frame of
        // reference; zstd gets them under 17, and pco, whose first-order delta leaves the jitter
        // for its bins to entropy-code, under 11: the one shape here where a trial is smaller.
        (_, string encodings) = await WriteAsync("jittered timestamps", CompressionProfile.Smallest);

        Assert.Contains("Pco", encodings, StringComparison.Ordinal);
    }

    /// <summary>Writes the shape under <paramref name="profile"/>, reads every value back, and returns the file's bytes and its column's encodings.</summary>
    private static async Task<(long Bytes, string Encodings)> WriteAsync(string shape, CompressionProfile profile)
    {
        Decoders.EnsureRegistered();
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-size-first-{Guid.NewGuid():N}.vortex");
        try
        {
            DTypeArena types = new DTypeArena();
            DType column = Text(shape)
                ? types.Utf8(Nullability.Nullable)
                : types.Primitive(PType.I64, Nullability.NonNullable);
            DType schema = types.Struct(["v"], [column], Nullability.NonNullable);
            WriteReport report;
            await using (VortexFileWriter writer = VortexFileWriter.Create(path, schema, new VortexWriteOptions { Compression = profile }))
            {
                for (int start = 0; start < Rows; start += Batch)
                {
                    CanonicalArena arena = new CanonicalArena();
                    int values = Text(shape) ? Ids(arena, types, column, start) : Longs(arena, column, shape, start);
                    int root = arena.AddStruct(schema, Batch, Validity.NonNullable, [values]);
                    using RecordBatch batch = new RecordBatch(arena, root, start);
                    await writer.WriteAsync(batch, CancellationToken.None);
                }

                report = await writer.CompleteAsync(CancellationToken.None);
            }

            await ReadBackAsync(path, shape);
            return (new FileInfo(path).Length, string.Join(",", report.Columns[0].Encodings));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static async Task ReadBackAsync(string path, string shape)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long row = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync())
        {
            VortexColumn column = batch.Column(0);
            for (int i = 0; i < batch.RowCount; i++, row++)
            {
                if (Text(shape))
                {
                    Assert.Equal(!IsNull(row), column.IsValid(i));
                    if (!IsNull(row))
                    {
                        Assert.Equal(Id(row), Encoding.ASCII.GetString(column.AsBinary().GetSpan(i)));
                    }
                }
                else
                {
                    Assert.Equal(Long(shape, row), column.AsPrimitive<long>()[i]);
                }
            }
        }

        Assert.Equal(Rows, row);
    }

    private static bool Text(string shape) => shape == "nullable ids";

    private static long Long(string shape, long row) => shape switch
    {
        "sorted runs" => row / 1000,
        "jittered timestamps" => 1_700_000_000_000L + (row * 1000) + (long)(Mix(row) % 1000),
        _ => (long)(Mix(row) % 1000),
    };

    /// <summary>A row's id; a null row keeps one under its cleared bit, as a column cut from a larger one does.</summary>
    private static string Id(long row) => string.Create(CultureInfo.InvariantCulture, $"customer-{Mix(row) % 10_000:D5}");

    private static bool IsNull(long row) => Mix(row) % 10 == 0;

    private static int Longs(CanonicalArena arena, DType dtype, string shape, int start)
    {
        VortexBuffer buffer = arena.Allocate(Batch * sizeof(long), 64, out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < Batch; i++)
        {
            values[i] = Long(shape, start + i);
        }

        return arena.AddPrimitive(dtype, Batch, Validity.NonNullable, PType.I64, buffer);
    }

    private static int Ids(CanonicalArena arena, DTypeArena types, DType dtype, int start)
    {
        VortexBuffer heapBuffer = arena.Allocate(Batch * 16, 64, out Span<byte> heap);
        VortexBuffer viewBuffer = arena.Allocate(Batch * 16, 64, out Span<byte> views);
        VortexBuffer bitBuffer = arena.Allocate(Batch / 8, 64, out Span<byte> bits);
        bits.Clear();
        views.Clear();
        int used = 0;
        for (int i = 0; i < Batch; i++)
        {
            if (!IsNull(start + i))
            {
                bits[i >> 3] |= (byte)(1 << (i & 7));
            }

            int length = Encoding.ASCII.GetBytes(Id(start + i), heap[used..]);
            Span<byte> view = views.Slice(i * 16, 16);
            MemoryMarshal.Write(view, length);
            heap.Slice(used, 4).CopyTo(view[4..]);
            MemoryMarshal.Write(view[12..], used);
            used += length;
        }

        int validity = arena.AddBool(types.Bool(Nullability.NonNullable), Batch, Validity.NonNullable, bitBuffer, 0);
        return arena.AddVarBinView(dtype, Batch, Validity.Bitmap(validity), viewBuffer, [heapBuffer.Slice(0, used)]);
    }

    private static ulong Mix(long row)
    {
        ulong z = (ulong)row + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
