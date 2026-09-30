// Every .NET type a record maps, written under every encoding the writer offers, read back exactly.
//
// THE TABLE IS THE SPECIFICATION. `Expected` says, for each column, shape and hint, which scheme
// the file must hold: a hint the writer silently ignored would still read back, so a round trip
// alone proves nothing about the encoding. Where a scheme cannot describe a column -- ALP over
// text, a progression over runs -- the table says nothing and only the values are held.
//
// OUR READER AND OUR WRITER CAN AGREE ON A MISTAKE, so every file is also exported for the Rust
// reference when VORTICITY_WRITE_MATRIX names a directory: the canonical twin under `reference/`,
// each hinted file under the hint's name, paired by relative path, which is what
// `bench/crosscheck.sh` hands `verify_written`.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Matrix;

public sealed class TypeEncodingMatrixTests
{
    private const int BatchRows = 3_000;

    /// <summary>Every shape under every hint, <see cref="EncodingHint.Auto"/> included.</summary>
    public static TheoryData<Shape, EncodingHint> Cases()
    {
        TheoryData<Shape, EncodingHint> cases = [];
        foreach (Shape shape in Enum.GetValues<Shape>())
        {
            foreach (EncodingHint hint in Enum.GetValues<EncodingHint>())
            {
                cases.Add(shape, hint);
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryTypeRoundTripsUnderEveryEncoding(Shape shape, EncodingHint hint)
    {
        Decoders.EnsureRegistered();
        CancellationToken ct = TestContext.Current.CancellationToken;
        AllTypes[] rows = MatrixRows.Build(shape);
        string path = Temp();
        string twin = Temp();
        try
        {
            WriteReport report = await WriteAsync(path, rows, hint, ct);
            await WriteAsync(twin, rows, EncodingHint.Canonical, ct);

            // The rows, member by member, as the record reads them.
            AllTypes[] read = await ReadRecordsAsync(path, ct);
            Assert.Equal(rows.Length, read.Length);
            for (int i = 0; i < rows.Length; i++)
            {
                string expected = Render(rows[i]);
                string actual = Render(read[i]);
                if (expected != actual)
                {
                    Assert.Fail($"{shape} under {hint}, row {i}:\n  wrote {expected}\n  read  {actual}");
                }
            }

            // The values, column by column, as the file stores them: the same as the canonical twin's.
            Assert.Equal(await DescribeAsync(twin, ct), await DescribeAsync(path, ct));

            // The lists a builder filled are contiguous, and go out as `vortex.list`, one offset a
            // row, as the reference writes them; only the entries of a map stay a list view.
            Assert.Contains("vortex.list", await ArrayIdsAsync(path, ct));

            // The scheme the table says the hint writes, on every chunk of every column it names.
            List<string> misses = [];
            foreach (ColumnWriteReport column in report.Columns)
            {
                if (Expected(column.Path, shape, hint) is not string scheme)
                {
                    continue;
                }

                foreach (string written in column.Encodings)
                {
                    if (!Matches(written, scheme))
                    {
                        misses.Add($"{column.Path}: {written}, expected {scheme}");
                        break;
                    }
                }
            }

            Assert.True(misses.Count == 0, $"{shape} under {hint}:\n  " + string.Join("\n  ", misses));

            await ExportAsync(shape, hint, rows, ct);
        }
        finally
        {
            System.IO.File.Delete(path);
            System.IO.File.Delete(twin);
        }
    }

    // ------------------------------------------------------------------------------ the table

    /// <summary>What a column's physical leaves are, as far as the schemes are concerned.</summary>
    private enum Leaf
    {
        Bool,
        Integer,
        Float,
        Decimal,
        Text,
        Uuid,
        List,
        Struct,
    }

    private static Leaf LeafOf(string column) => column switch
    {
        "Bool" or "BoolN" => Leaf.Bool,
        "F16" or "F16N" or "F32" or "F32N" or "F64" or "F64N" => Leaf.Float,
        "Dec8" or "Dec16" or "Dec32" or "Dec64" or "Dec128" or "Dec128N" or "Wide128" or "Wide256" or "Wide256N"
            or "I128" or "I128N" or "U128" or "U128N" or "I128Wide" or "U128Wide" or "Big" or "BigN" => Leaf.Decimal,
        "Text" or "TextN" or "Bytes" or "BytesN" => Leaf.Text,
        "Uuid" or "UuidN" => Leaf.Uuid,
        "Ints" or "Texts" or "LongsN" or "IntArray" or "IntArrayN" or "TextList" or "TextListN" or "LongArray" or "Doubles"
            or "Jagged" or "ListOfArrays" or "Shorts" or "Guids" or "Items" or "ItemsN" or "ItemSequence"
            or "Scores" or "Weights" or "Statuses" => Leaf.List,
        "Blob" or "BlobN" or "BlobMemory" => Leaf.Text,
        "Nested" or "NestedN" => Leaf.Struct,
        _ => Leaf.Integer,
    };

    /// <summary>The storage width of an integer leaf in bytes, a date, a time or a timestamp's included.</summary>
    private static int WidthOf(string column) => column switch
    {
        "I8" or "I8N" or "U8" or "U8N" or "State" or "StateN" => 1,
        "I16" or "I16N" or "U16" or "U16N" or "Char" or "CharN" => 2,
        "I32" or "I32N" or "U32" or "U32N" or "Date" or "DateN" or "Dec32" => 4,
        "Dec8" => 1,
        "Dec16" => 2,
        "Dec128" or "Dec128N" or "Wide128" or "I128" or "I128N" or "U128" or "U128N" => 16,
        "Wide256" or "Wide256N" or "I128Wide" or "U128Wide" or "Big" or "BigN" => 32,
        _ => 8,
    };

    /// <summary>
    /// The scheme <paramref name="hint"/> must leave on <paramref name="column"/> for rows of
    /// <paramref name="shape"/>, or null where the table promises nothing but the values.
    /// </summary>
    private static string? Expected(string column, Shape shape, EncodingHint hint)
    {
        Leaf leaf = LeafOf(column);

        // One value on every row, or a null on every row, is a constant under every hint: nothing
        // costs less, and a reader keeps it one value through a scan.
        if (ConstantIn(column, shape))
        {
            return nameof(EncodingHint.Constant);
        }

        // A progression is written before any hint, since it costs nothing a row either.
        if (leaf == Leaf.Integer && shape == Shape.Progression && hint != EncodingHint.Auto)
        {
            return nameof(EncodingHint.Sequence);
        }

        // A number of sixteen bits or more pinned to pco is pco wherever it is not one value or a
        // progression: a pin comes before the shapes that would otherwise take it, a timestamp's
        // parts and the nulls of the scarce rows included.
        if (hint == EncodingHint.Pco && (leaf == Leaf.Float || (leaf == Leaf.Integer && WidthOf(column) > 1)))
        {
            return nameof(EncodingHint.Pco);
        }

        // A timestamp is its days, seconds and subseconds when pinned so, and wherever the instants
        // are coarser than their unit and no hint that applies to integers holds the column: the
        // spread instants are to the second, which in seconds leaves too little to split.
        if (Temporal(column))
        {
            if (hint == EncodingHint.DateTimeParts && shape is Shape.Runs or Shape.Dominant or Shape.Spread)
            {
                return nameof(EncodingHint.DateTimeParts);
            }

            if (shape == Shape.Spread && column != "ZonedN"
                && hint is not (EncodingHint.Canonical or EncodingHint.BitPacked or EncodingHint.Dictionary or EncodingHint.Zstd)
                && !(hint == EncodingHint.Sparse && Nullable(column)))
            {
                return nameof(EncodingHint.DateTimeParts);
            }
        }

        // Text pinned to OnPair is OnPair wherever it is not one value: a pin comes before the
        // shapes that would otherwise take it, the nulls of the scarce rows included.
        if (hint == EncodingHint.OnPair && column is "Text" or "TextN" && !ConstantIn(column, shape))
        {
            return nameof(EncodingHint.OnPair);
        }

        // Nulls on nineteen rows in twenty are the valid rows at their positions, under every hint
        // that never applies to such a column; a hint that does -- runs, a dictionary, a packing,
        // a trial -- holds it, as the caller asked.
        if (shape == Shape.Scarce && Nullable(column) && leaf is not (Leaf.List or Leaf.Struct or Leaf.Uuid) && column != "Weights"
            && hint is EncodingHint.Auto or EncodingHint.Sparse or EncodingHint.Sequence or EncodingHint.Constant)
        {
            return nameof(EncodingHint.Sparse);
        }

        // A decimal whose values fit 64 bits is written as those integers, under Auto and under any
        // hint that does not apply to it; one that pins a decimal scheme of its own keeps it.
        if (leaf == Leaf.Decimal && FitsInt64(column, shape)
            && (hint is EncodingHint.Auto or EncodingHint.DecimalByteParts or EncodingHint.BitPacked or EncodingHint.Alp or EncodingHint.AlpRd
                or EncodingHint.Fsst or EncodingHint.Sequence or EncodingHint.Constant or EncodingHint.Pco))
        {
            return nameof(EncodingHint.DecimalByteParts);
        }

        switch (hint)
        {
            case EncodingHint.Canonical:
                return leaf is Leaf.List or Leaf.Struct ? null : WrittenAsCanonical;
            case EncodingHint.RunEnd when shape == Shape.Runs:
                return leaf is Leaf.Bool or Leaf.Integer or Leaf.Float or Leaf.Decimal or Leaf.Text ? nameof(EncodingHint.RunEnd) : null;
            case EncodingHint.Dictionary when shape is Shape.Runs:
                // Sixteen entries take a byte a row, which a one-byte column already is.
                return leaf is Leaf.Float or Leaf.Text || (leaf is Leaf.Integer or Leaf.Decimal && WidthOf(column) > 1)
                    ? nameof(EncodingHint.Dictionary)
                    : null;
            case EncodingHint.BitPacked when shape is Shape.Runs:
                // The pools hold each type's minimum and maximum, which only a wide enough type packs past as patches.
                return leaf == Leaf.Integer && column is not ("I8" or "I8N" or "I16" or "I16N" or "U8" or "U8N" or "U16" or "U16N" or "Char" or "CharN" or "StampNs")
                    ? nameof(EncodingHint.BitPacked)
                    : null;
            case EncodingHint.BitPacked when shape is Shape.Spread:
                // A byte of 200 values, or of 256, and 50 000 values in sixteen bits, have no bit to spare.
                return leaf == Leaf.Integer && column is not ("I8" or "I8N" or "U8" or "U8N" or "U16" or "U16N" or "Char" or "CharN")
                    ? nameof(EncodingHint.BitPacked)
                    : null;
            case EncodingHint.Fsst when shape is Shape.Spread:
                return column is "Text" or "TextN" ? nameof(EncodingHint.Fsst) : null;
            case EncodingHint.Alp when shape is Shape.Spread:
                // ALP has no half, and a float's seven digits do not give prices to the cent back exactly.
                return column is "F64" or "F64N" ? nameof(EncodingHint.Alp) : null;
            default:
                return null;
        }
    }

    private const string WrittenAsCanonical = nameof(EncodingHint.Canonical);

    /// <summary>
    /// Whether a column holds one value, or only nulls, on every row of <paramref name="shape"/>: a
    /// list's elements differ within a row and a UUID is sixteen bytes, neither of them one value,
    /// and a map has no null a reader can fill, so an empty one stays a map.
    /// </summary>
    private static bool ConstantIn(string column, Shape shape) => shape switch
    {
        // A byte climbing twenty thousand rows would wrap, so the progression holds those still.
        Shape.Progression => column is "I8" or "I8N" or "U8" or "U8N" or "State" or "StateN",
        Shape.Constant => LeafOf(column) is not (Leaf.List or Leaf.Uuid),
        Shape.Absent when Nullable(column) => column != "Weights",
        Shape.Absent => LeafOf(column) is not (Leaf.List or Leaf.Uuid),
        _ => false,
    };

    /// <summary>Whether a member is a timestamp, a zoned one included.</summary>
    private static bool Temporal(string column) =>
        column is "Stamp" or "StampN" or "StampNs" or "StampMsUtc" or "Zoned" or "ZonedN";

    /// <summary>Whether a member is nullable, which the table's names say by their last letter.</summary>
    private static bool Nullable(string column) => column.EndsWith('N') || column == "Weights";

    /// <summary>Whether every unscaled value a decimal column holds for rows of <paramref name="shape"/> fits 64 bits.</summary>
    private static bool FitsInt64(string column, Shape shape) => column switch
    {
        "Dec8" or "Dec16" or "Dec32" or "Dec64" => true,
        "Dec128" or "Dec128N" or "Wide128" or "Wide256" or "Wide256N" => shape is Shape.Spread or Shape.Progression,
        _ => shape == Shape.Progression,
    };

    /// <summary>Whether a chunk's label is the scheme: a struct's fields, a list's elements and an extension's storage each carry it.</summary>
    private static bool Matches(string written, string scheme) =>
        written == scheme || (written.StartsWith('{') && written.Split([", ", "{", "}"], StringSplitOptions.RemoveEmptyEntries)
            .All(field => field.EndsWith(": " + scheme, StringComparison.Ordinal) || field.EndsWith(':' + scheme, StringComparison.Ordinal)));

    // ------------------------------------------------------------------------------ plumbing

    /// <summary>The hint on every leaf the record has: its members, and the fields of its nested records.</summary>
    private static Dictionary<string, EncodingHint> HintsFor(EncodingHint hint)
    {
        Dictionary<string, EncodingHint> hints = new Dictionary<string, EncodingHint>(StringComparer.Ordinal);
        if (hint == EncodingHint.Auto)
        {
            return hints;
        }

        foreach (VortexField field in AllTypes.Schema)
        {
            if (field.Type.NonNullable.Kind == VortexTypeKind.Struct)
            {
                foreach (VortexField nested in field.Type.NonNullable.Fields)
                {
                    hints[field.Name + "." + nested.Name] = hint;
                }

                continue;
            }

            hints[field.Name] = hint;
        }

        return hints;
    }

    internal static async Task<WriteReport> WriteAsync(string path, AllTypes[] rows, EncodingHint hint, CancellationToken ct, int? blockRows = null)
    {
        VortexWriteOptions options = new VortexWriteOptions
        {
            Hints = HintsFor(hint).ToImmutableDictionary(StringComparer.Ordinal),
            Identity = new Guid("11111111-2222-4333-8444-555555555555"),
        };
        if (blockRows is int block)
        {
            options = options with { BlockRows = block };
        }

        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<AllTypes>(path, options);
        for (int start = 0; start < rows.Length; start += BatchRows)
        {
            await writer.WriteAsync<AllTypes>(rows.AsSpan(start, Math.Min(BatchRows, rows.Length - start)).ToArray(), ct);
        }

        return await writer.CompleteAsync(ct);
    }

    internal static async Task<AllTypes[]> ReadRecordsAsync(string path, CancellationToken ct)
    {
        List<AllTypes> rows = [];
        await using VortexFile file = await VortexFile.OpenAsync(path, ct);
        await foreach (AllTypes row in file.Scan<AllTypes>().ToRecordsAsync(ct))
        {
            rows.Add(row);
        }

        return [.. rows];
    }

    private static async Task<List<string>> ArrayIdsAsync(string path, CancellationToken ct)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, ct);
        List<string> ids = [];
        for (int i = 0; i < file.ArrayEncodingCount; i++)
        {
            ids.Add(file.GetArrayEncodingId(i));
        }

        return ids;
    }

    private static async Task<List<string>> DescribeAsync(string path, CancellationToken ct)
    {
        List<string> rows = [];
        await using VortexFile file = await VortexFile.OpenAsync(path, ct);
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync().WithCancellation(ct))
        {
            Values.DescribeRows(batch, rows);
        }

        return rows;
    }

    /// <summary>
    /// Writes the file and its canonical twin where the Rust cross-check reads them, when asked to:
    /// the first rows only, in blocks of 1 024, so that the reference's scalar-by-scalar comparison
    /// still crosses blocks and chunks without spending minutes on every list of every row.
    /// </summary>
    private static async Task ExportAsync(Shape shape, EncodingHint hint, AllTypes[] rows, CancellationToken ct)
    {
        string? root = Environment.GetEnvironmentVariable("VORTICITY_WRITE_MATRIX");
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        const int Exported = 4_096;
        AllTypes[] head = rows.AsSpan(0, Math.Min(Exported, rows.Length)).ToArray();
        string name = Path.Combine("matrix", shape.ToString().ToLowerInvariant() + ".vortex");
        string reference = Path.Combine(root, "reference", name);
        string hinted = Path.Combine(root, hint.ToString(), name);
        Directory.CreateDirectory(Path.GetDirectoryName(reference)!);
        Directory.CreateDirectory(Path.GetDirectoryName(hinted)!);
        await WriteAsync(reference, head, EncodingHint.Canonical, ct, blockRows: 1_024);
        await WriteAsync(hinted, head, hint, ct, blockRows: 1_024);
    }

    internal static string Temp() =>
        Path.Combine(Path.GetTempPath(), $"vorticity-matrix-{Guid.NewGuid():N}.vortex");

    // ------------------------------------------------------------------------------ rendering

    private static readonly PropertyInfo[] Members = typeof(AllTypes).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    /// <summary>Every member of a row as exact text: a float by its bits, a decimal by its value, an instant by its ticks.</summary>
    internal static string Render(AllTypes row)
    {
        StringBuilder text = new StringBuilder();
        foreach (PropertyInfo member in Members)
        {
            text.Append(member.Name).Append('=');
            RenderValue(member.GetValue(row), text);
            text.Append(' ');
        }

        return text.ToString();
    }

    private static void RenderValue(object? value, StringBuilder text)
    {
        switch (value)
        {
            case null:
                text.Append("null");
                return;
            case Half half:
                text.Append(BitConverter.HalfToUInt16Bits(half).ToString("X4", CultureInfo.InvariantCulture));
                return;
            case float single:
                text.Append(BitConverter.SingleToUInt32Bits(single).ToString("X8", CultureInfo.InvariantCulture));
                return;
            case double number:
                text.Append(BitConverter.DoubleToUInt64Bits(number).ToString("X16", CultureInfo.InvariantCulture));
                return;
            case decimal number:
                text.Append(number.ToString("G29", CultureInfo.InvariantCulture));
                return;
            case VortexDecimal wide:
                text.Append(Normalized(wide.ToString()));
                return;
            case DateTime instant:
                text.Append(instant.Ticks.ToString(CultureInfo.InvariantCulture));
                return;
            case DateTimeOffset instant:
                text.Append(instant.UtcTicks.ToString(CultureInfo.InvariantCulture));
                return;
            case string s:
                text.Append('"').Append(s).Append('"');
                return;
            case char c:
                text.Append("u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                return;
            case TimeSpan span:
                text.Append(span.Ticks.ToString(CultureInfo.InvariantCulture));
                return;
            case ReadOnlyMemory<byte> bytes:
                text.Append("0x").Append(Convert.ToHexString(bytes.Span));
                return;
            case Memory<byte> bytes:
                text.Append("0x").Append(Convert.ToHexString(bytes.Span));
                return;
            case byte[] bytes:
                text.Append("0x").Append(Convert.ToHexString(bytes));
                return;
            case Enum e:
                text.Append(Convert.ToInt64(e, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                return;
            case Inner inner:
                text.Append('{');
                RenderValue(inner.A, text);
                text.Append(',');
                RenderValue(inner.B, text);
                text.Append('}');
                return;
            case IFormattable formattable:
                text.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
        }

        Type type = value.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            text.Append('(');
            RenderValue(type.GetProperty("Key")!.GetValue(value), text);
            text.Append(':');
            RenderValue(type.GetProperty("Value")!.GetValue(value), text);
            text.Append(')');
            return;
        }

        if (value is System.Collections.IEnumerable sequence)
        {
            text.Append('[');
            bool head = true;
            foreach (object? element in sequence)
            {
                text.Append(head ? string.Empty : ",");
                RenderValue(element, text);
                head = false;
            }

            text.Append(']');
            return;
        }

        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(ReadOnlyMemory<>) || type.GetGenericTypeDefinition() == typeof(Memory<>)))
        {
            object array = type.GetMethod(nameof(ReadOnlyMemory<int>.ToArray))!.Invoke(value, null)!;
            text.Append('[');
            bool first = true;
            foreach (object? element in (System.Collections.IEnumerable)array)
            {
                text.Append(first ? string.Empty : ",");
                RenderValue(element, text);
                first = false;
            }

            text.Append(']');
            return;
        }

        text.Append(value);
    }

    /// <summary>A decimal's text without the zeros its scale adds after the point.</summary>
    private static string Normalized(string text)
    {
        if (text.Contains('.', StringComparison.Ordinal))
        {
            text = text.TrimEnd('0').TrimEnd('.');
        }

        return text is "-0" or "" ? "0" : text;
    }
}
