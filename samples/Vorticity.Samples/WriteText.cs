using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static partial class WriteText
{
    private const int Values = 1_000_000;

    /// <summary>When something happened, where, and a note that may be missing.</summary>
    [VortexRecord]
    public partial record struct Entry(DateTime StartedAt, string City, string? Note);

    internal static async Task RunAsync()
    {
        VortexSession session = VortexSession.Default;
        CancellationToken ct = CancellationToken.None;
        string path = Demo.Path("write-text.vortex");
        DateTime now = new DateTime(2026, 9, 22, 8, 30, 0, DateTimeKind.Utc);
        string cityString = "Lyon";
        string longText = new string('x', 20_000) + " the end";

        await using (VortexFileWriter writer = session.CreateWriter<Entry>(path))
        {
            ColumnsBuilder<Entry> b = writer.Builder<Entry>();

            b.StartedAt.Append(now);
            b.City.Append("Paris"u8);                                  // UTF-8 bytes, no transcoding
            b.Note.AppendNull();

            b.StartedAt.Append(now.AddMinutes(1));
            b.City.Append(cityString);                                 // a string, transcoded by Utf8.FromUtf16
            b.Note.Append(DateOnly.FromDateTime(now));                 // an IUtf8SpanFormattable, formatted in place

            b.StartedAt.Append(now.AddMinutes(2));
            b.City.Append("Nice"u8);
            b.Note.Append(Guid.Parse("0199f0c4-7d2a-7c3e-9a51-3f6b2c1d4e5f"));

            b.StartedAt.Append(now.AddMinutes(3));
            b.City.Append("Lille"u8);
            Span<byte> dst = b.Note.GetSpan(Encoding.UTF8.GetMaxByteCount(longText.Length));   // one value, written in place
            int written = Encoding.UTF8.GetBytes(longText, dst);
            b.Note.Commit(written);

            b.StartedAt.Append(now.AddMinutes(4));
            b.City.Append("Nantes"u8);
            Span<byte> iso = b.Note.GetSpan(10);
            DateOnly.FromDateTime(now).TryFormat(iso, out int length, "O");   // a format of your choosing, in place
            b.Note.Commit(length);

            try
            {
                b.City.Append([0xC3, 0x28]);
            }
            catch (VortexSchemaException e)
            {
                Console.WriteLine($"not UTF-8: {e.Message}");
            }

            await writer.WriteAsync(b, ct);
            await writer.CompleteAsync(ct);
        }

        await using (VortexFile file = await VortexFile.OpenAsync(path))
        {
            await foreach (Entry entry in file.Scan<Entry>().ToRecordsAsync(ct))
            {
                string note = entry.Note is null ? "null" : entry.Note.Length > 40 ? $"{entry.Note.Length} characters ending in '{entry.Note[^7..]}'" : $"\"{entry.Note}\"";
                Console.WriteLine($"  {entry.StartedAt:HH:mm} {entry.City}: {note}");
            }
        }

        await AppendCost(session);
    }

    private enum Form
    {
        Utf8,
        String,
        Formatted,
    }

    /// <summary>What each form of append costs per value, on a column of city names and numbers.</summary>
    private static async Task AppendCost(VortexSession session)
    {
        for (int round = 0; round < 2; round++)
        {
            double bytes = await Time(session, Form.Utf8);
            double text = await Time(session, Form.String);
            double number = await Time(session, Form.Formatted);
            if (round == 1)
            {
                Console.WriteLine($"per value, {Values} appends: UTF-8 bytes {bytes:F1} ns, a string {text:F1} ns, an int formatted {number:F1} ns");
            }
        }
    }

    /// <summary>The time spent in the appends alone, per value; the writes and the encoding are not counted.</summary>
    private static async Task<double> Time(VortexSession session, Form form)
    {
        byte[][] utf8 = Array.ConvertAll(Demo.Cities, Encoding.UTF8.GetBytes);
        string[] strings = Demo.Cities;
        VortexSchema schema = [("text", VortexType.Utf8)];
        string path = Demo.Path("write-text-cost.vortex");

        await using VortexFileWriter writer = session.CreateWriter(path, schema);
        ColumnsBuilder b = writer.Builder();
        long ticks = 0;
        for (int start = 0; start < Values; start += writer.BlockRows)
        {
            ticks += Fill(b.Column<string>(0), form, start, Math.Min(Values, start + writer.BlockRows), utf8, strings);
            await writer.WriteAsync(b);
        }

        await writer.CompleteAsync();
        return ticks * 1e9 / Stopwatch.Frequency / Values;
    }

    private static long Fill(ColumnBuilder<string> column, Form form, int start, int end, byte[][] utf8, string[] strings)
    {
        long before = Stopwatch.GetTimestamp();
        switch (form)
        {
            case Form.Utf8:
                for (int i = start; i < end; i++) column.Append(utf8[i % utf8.Length]);
                break;
            case Form.String:
                for (int i = start; i < end; i++) column.Append(strings[i % strings.Length]);
                break;
            default:
                for (int i = start; i < end; i++) column.Append(i);
                break;
        }

        return Stopwatch.GetTimestamp() - before;
    }
}
