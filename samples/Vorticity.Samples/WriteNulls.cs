using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static partial class WriteNulls
{
    private const int Rows = 1_000_000;

    /// <summary>A day, a temperature and a station, the last two of which may be missing.</summary>
    [VortexRecord]
    public partial record struct Sample(int Day, double? Celsius, string? Station);

    private enum Nulls
    {
        NoneAppended,
        NoneInABitmap,
        One,
        OneInFifty,
        All,
    }

    internal static async Task RunAsync()
    {
        VortexSession session = VortexSession.Default;
        CancellationToken ct = CancellationToken.None;
        string path = Demo.Path("write-nulls.vortex");
        double[] values = [18.5, 19.0, 19.5, 20.0, 20.5];
        ulong[] validity = [0b11011];

        await using (VortexFileWriter writer = session.CreateWriter<Sample>(path))
        {
            ColumnsBuilder<Sample> b = writer.Builder<Sample>();
            b.Day.Append([1, 1, 1, 2, 2, 2, 2, 2]);

            b.Celsius.Append(21.5);
            b.Celsius.AppendNull();
            b.Celsius.Append((double?)null);
            b.Celsius.Append(values, validity);                        // bulk: bit i of the bitmap for value i, 1 for a value

            b.Station.Append("Paris-Montsouris"u8);
            b.Station.AppendNulls(7);                                  // a nullable text column takes nulls the same way

            await writer.WriteAsync(b, ct);
            await writer.CompleteAsync(ct);
        }

        await using (VortexFile file = await VortexFile.OpenAsync(path))
        {
            await foreach (Sample sample in file.Scan<Sample>().ToRecordsAsync(ct))
            {
                Console.Write($"{(sample.Celsius is { } c ? c.ToString("F1") : "null")} ");
            }

            Console.WriteLine();
        }

        await using (VortexFileWriter writer = session.CreateWriter<Sample>(path))
        {
            try
            {
                writer.Builder().Column<int?>(0);
            }
            catch (VortexSchemaException e)
            {
                Console.WriteLine($"a nullable builder over Day: {e.Message}");
            }

            writer.Abandon();
        }

        foreach (Nulls nulls in Enum.GetValues<Nulls>())
        {
            await Measure(session, path, nulls, ct);
        }
    }

    private static async Task Measure(VortexSession session, string path, Nulls nulls, CancellationToken ct)
    {
        byte[][] stations = Array.ConvertAll(Demo.Cities, Encoding.UTF8.GetBytes);
        WriteReport report;
        await using (VortexFileWriter writer = session.CreateWriter<Sample>(path))
        {
            ColumnsBuilder<Sample> b = writer.Builder<Sample>();
            double[] celsius = new double[writer.BlockRows];
            ulong[] validity = new ulong[writer.BlockRows / 64];
            for (int start = 0; start < Rows; start += writer.BlockRows)
            {
                int count = Math.Min(writer.BlockRows, Rows - start);
                Span<int> day = b.Day.GetSpan(count);
                for (int i = 0; i < count; i++)
                {
                    day[i] = (start + i) / 1_000;
                    celsius[i] = 10.0 + ((start + i) % 400 / 10.0);
                }

                b.Day.Advance(count);
                validity.AsSpan().Fill(ulong.MaxValue);
                switch (nulls)
                {
                    case Nulls.NoneAppended:
                        b.Celsius.Append(celsius.AsSpan(0, count));
                        break;
                    case Nulls.NoneInABitmap:
                        b.Celsius.Append(celsius.AsSpan(0, count), validity);
                        break;
                    case Nulls.One:
                        if (start <= Rows / 2 && Rows / 2 < start + count)
                        {
                            int i = Rows / 2 - start;
                            validity[i >> 6] &= ~(1UL << i);
                        }

                        b.Celsius.Append(celsius.AsSpan(0, count), validity);
                        break;
                    case Nulls.OneInFifty:
                        for (int i = 0; i < count; i++)
                        {
                            if ((start + i) % 50 == 0) validity[i >> 6] &= ~(1UL << i);
                        }

                        b.Celsius.Append(celsius.AsSpan(0, count), validity);
                        break;
                    default:
                        b.Celsius.AppendNulls(count);
                        break;
                }

                for (int i = 0; i < count; i++) b.Station.Append(stations[(start + i) / 7 % stations.Length]);
                await writer.WriteAsync(b, ct);
            }

            report = await writer.CompleteAsync(ct);
        }

        await using VortexFile file = await VortexFile.OpenAsync(path);
        file.Statistics[1].TryGetNullCount(out long nullCount);
        int batches = 0;
        int withBitmap = 0;
        long counted = 0;
        await foreach (var (_, celsius, _) in file.Scan<Sample>())
        {
            batches++;
            withBitmap += celsius.IsAllValid ? 0 : 1;
            counted += celsius.NullCount;
        }

        Console.WriteLine($"{nulls}: {new FileInfo(path).Length} bytes, Celsius as {WriteReportText.Encodings(report.Columns[1].Encodings)}; " +
            $"null count {nullCount} in the statistics, {counted} counted; {withBitmap} of {batches} batches carry a bitmap");
    }
}
