// A merge's runs gathered into a block in one pass over the runs (CanonicalRuns), where the block was made of slices of each
// input batch, their concatenation, a gather of the runs' rows by their indices and a copy that shares repeats.
//
// THE ORACLE IS THAT OTHER WAY: the same sources and the same runs through CanonicalSlice, CanonicalConcat, CanonicalFilter
// and CanonicalArena.CopyFrom give the node the gather by runs must give, node for node: the kinds, the validities, the
// values, every view's sixteen bytes and every data buffer's bytes. What reads a block tells a repeat by its view, so a view
// that differs is a block that differs.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Layouts;
using Vorticity.Tests.Arrays.Decoders.Canonical;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class CanonicalRunsTests
{
    /// <summary>
    /// Sources of every shape a merge's batches take, numbers, booleans, texts with nulls, a constant, a dictionary, an
    /// extension and a variant, and runs that interleave them a few rows at a time, some rows passed over: the gather by
    /// runs gives the node the slices, their concatenation, the gather by indices and the sharing copy give.
    /// </summary>
    [Theory]
    [InlineData(1, 3, 5)]
    [InlineData(2, 9, 5)]
    [InlineData(3, 1, 5)]
    [InlineData(4, 17, 5)]
    [InlineData(5, 40, 5)]
    [InlineData(6, 40, 60)]
    [InlineData(7, 200, 60)]
    [InlineData(8, 5, 60)]
    public void TheRunsGatheredAreTheSlicesConcatenatedGatheredAndCopied(int seed, int longestRun, int stretch)
    {
        Random random = new Random(seed);
        using ScanContext scan = new ScanContext(TestEncodings.Ids);
        CanonicalArena into = scan.Canonical;
        DTypeArena types = new DTypeArena();
        Shape shape = new Shape(types, stretch);

        // Six batches, each in an arena of its own, as a merge's inputs hand theirs.
        List<Source> sources = [];
        for (int s = 0; s < 6; s++)
        {
            CanonicalArena arena = new CanonicalArena();
            int rows = random.Next(60, 400);
            sources.Add(new Source(arena, shape.Batch(arena, random, rows, s), rows));
        }

        (Source[] taken, CanonicalRuns.Run[] runs, int total) = Interleave(random, sources, longestRun);
        Assert.True(CanonicalRuns.Takes(shape.Root));

        int expected = BySlices(scan, shape.Root, taken, runs, total);
        int actual = CanonicalRuns.Gather<Source>(scan.Decode, shape.Root, taken, runs, total);

        Assert.True(actual >= 0, "the gather by runs took the sources");
        AssertSame(into, expected, actual, "root");
    }

    /// <summary>A column every source holds as the same constant stays one, and a list is not taken.</summary>
    [Fact]
    public void AConstantEverySourceHoldsStaysAConstantAndAListIsNotTaken()
    {
        using ScanContext scan = new ScanContext(TestEncodings.Ids);
        DTypeArena types = new DTypeArena();
        DType text = types.Utf8(Nullability.NonNullable);
        byte[] element = System.Text.Encoding.UTF8.GetBytes("the one service of every batch");
        List<Source> sources = [];
        for (int s = 0; s < 3; s++)
        {
            CanonicalArena arena = new CanonicalArena();
            sources.Add(new Source(arena, arena.AddConstant(text, 50, Validity.NonNullable, element), 50));
        }

        (Source[] taken, CanonicalRuns.Run[] runs, int total) = Interleave(new Random(11), sources, 7);
        int expected = BySlices(scan, text, taken, runs, total);
        int actual = CanonicalRuns.Gather<Source>(scan.Decode, text, taken, runs, total);
        Assert.Equal(CanonicalKind.Constant, scan.Canonical.GetNode(actual).Kind);
        AssertSame(scan.Canonical, expected, actual, "constant");

        Assert.False(CanonicalRuns.Takes(types.List(types.Primitive(PType.I64, Nullability.NonNullable), Nullability.NonNullable)));
    }

    /// <summary>
    /// A column every source holds as a constant of its own, two of them the same text, one short: each source's rows view
    /// its constant, the same text copied once where the runs of the two follow each other.
    /// </summary>
    [Fact]
    public void ConstantsThatDifferAreGatheredAsTheirRows()
    {
        using ScanContext scan = new ScanContext(TestEncodings.Ids);
        DTypeArena types = new DTypeArena();
        DType text = types.Utf8(Nullability.NonNullable);
        string[] elements = ["a service whose name is long", "a service whose name is long", "short", "another service, long as well"];
        List<Source> sources = [];
        foreach (string element in elements)
        {
            CanonicalArena arena = new CanonicalArena();
            sources.Add(new Source(arena, arena.AddConstant(text, 80, Validity.NonNullable, System.Text.Encoding.UTF8.GetBytes(element)), 80));
        }

        (Source[] taken, CanonicalRuns.Run[] runs, int total) = Interleave(new Random(5), sources, 6);
        int expected = BySlices(scan, text, taken, runs, total);
        int actual = CanonicalRuns.Gather<Source>(scan.Decode, text, taken, runs, total);
        Assert.Equal(CanonicalKind.VarBinView, scan.Canonical.GetNode(actual).Kind);
        AssertSame(scan.Canonical, expected, actual, "constants");
    }

    /// <summary>The node the way before gives: each source sliced over the rows its runs span, the slices concatenated, the runs' rows gathered, the result copied with its repeats shared.</summary>
    private static int BySlices(ScanContext scan, DType dtype, Source[] sources, CanonicalRuns.Run[] runs, int total)
    {
        CanonicalArena into = scan.Canonical;
        int[] slices = new int[sources.Length];
        int[] starts = new int[sources.Length];
        int spanned = 0;
        for (int s = 0; s < sources.Length; s++)
        {
            Source source = sources[s];
            slices[s] = into.DecodedTree(CanonicalSlice.SliceAcross(source.Arena, into, source.Node, source.First, source.End - source.First));
            starts[s] = spanned;
            spanned += source.End - source.First;
        }

        int[] picks = new int[total];
        int picked = 0;
        foreach (CanonicalRuns.Run run in runs)
        {
            for (int j = 0; j < run.Rows; j++)
            {
                picks[picked++] = starts[run.Source] + run.Row - sources[run.Source].First + j;
            }
        }

        int all = CanonicalConcat.Concat(scan.Decode, dtype, spanned, slices);
        return into.CopyFrom(into, CanonicalFilter.Apply(into, all, picks), shareRepeats: true);
    }

    /// <summary>
    /// Runs of one to <paramref name="longest"/> rows taken of the sources in turn, each source's in its rows' order, a few
    /// rows passed over now and then; the sources a run takes, in the order their first run comes, with the rows their runs
    /// span.
    /// </summary>
    private static (Source[] Taken, CanonicalRuns.Run[] Runs, int Rows) Interleave(Random random, List<Source> sources, int longest)
    {
        int[] cursor = new int[sources.Count];
        int[] index = new int[sources.Count];
        Array.Fill(index, -1);
        List<Source> taken = [];
        List<CanonicalRuns.Run> runs = [];
        int total = 0;
        while (true)
        {
            List<int> open = [];
            for (int s = 0; s < sources.Count; s++)
            {
                if (cursor[s] < sources[s].Rows)
                {
                    open.Add(s);
                }
            }

            if (open.Count == 0)
            {
                break;
            }

            int chosen = open[random.Next(open.Count)];
            if (random.Next(8) == 0)
            {
                cursor[chosen] = Math.Min(sources[chosen].Rows, cursor[chosen] + random.Next(1, 5));
                continue;
            }

            int count = Math.Min(random.Next(1, longest + 1), sources[chosen].Rows - cursor[chosen]);
            if (index[chosen] < 0)
            {
                index[chosen] = taken.Count;
                taken.Add(sources[chosen] with { First = cursor[chosen], End = cursor[chosen] + count });
            }
            else
            {
                taken[index[chosen]] = taken[index[chosen]] with { End = cursor[chosen] + count };
            }

            runs.Add(new CanonicalRuns.Run(index[chosen], cursor[chosen], count));
            cursor[chosen] += count;
            total += count;
        }

        return ([.. taken], [.. runs], total);
    }

    private static void AssertSame(CanonicalArena arena, int expected, int actual, string path)
    {
        CanonicalNode e = arena.GetNode(expected);
        CanonicalNode a = arena.GetNode(actual);
        Assert.True(e.Kind == a.Kind, $"{path}: {a.Kind} where the slices give {e.Kind}");
        Assert.True(e.Length == a.Length, $"{path}: {a.Length} rows where the slices give {e.Length}");
        Assert.True(e.DType == a.DType, $"{path}: the dtype differs");
        if (e.Kind != CanonicalKind.Extension)
        {
            Assert.True(e.Validity.Kind == a.Validity.Kind, $"{path}: validity {a.Validity.Kind} where the slices give {e.Validity.Kind}");
            if (e.Validity.Kind == ValidityKind.Bitmap)
            {
                AssertSameBits(arena.GetNode(e.Validity.CanonicalNodeIndex), arena.GetNode(a.Validity.CanonicalNodeIndex), e.Length, path + " validity");
            }
        }

        switch (e.Kind)
        {
            case CanonicalKind.Bool:
                AssertSameBits(e, a, e.Length, path);
                break;

            case CanonicalKind.Primitive:
                Assert.Equal(e.PType, a.PType);
                Assert.True(e.Values.Span.SequenceEqual(a.Values.Span), $"{path}: the values differ");
                break;

            case CanonicalKind.VarBinView:
                ReadOnlySpan<Int128> expectedViews = MemoryMarshal.Cast<byte, Int128>(e.Views.Span)[..e.Length];
                ReadOnlySpan<Int128> actualViews = MemoryMarshal.Cast<byte, Int128>(a.Views.Span)[..a.Length];
                for (int row = 0; row < e.Length; row++)
                {
                    Assert.True(expectedViews[row] == actualViews[row], $"{path}: the view of row {row} differs");
                }

                Assert.True(e.DataBufferCount == a.DataBufferCount, $"{path}: {a.DataBufferCount} data buffers where the slices give {e.DataBufferCount}");
                for (int buffer = 0; buffer < e.DataBufferCount; buffer++)
                {
                    Assert.True(e.GetDataBuffer(buffer).Span.SequenceEqual(a.GetDataBuffer(buffer).Span), $"{path}: data buffer {buffer} differs");
                }

                Assert.True(arena.RecordRef(expected).PaddedViews == arena.RecordRef(actual).PaddedViews, $"{path}: the views' padding mark differs");
                Assert.False(arena.RecordRef(actual).SharesBytes, $"{path}: the gathered views stand for no other bytes");
                break;

            case CanonicalKind.Constant:
                Assert.True(e.ConstantElement.SequenceEqual(a.ConstantElement), $"{path}: the constant differs");
                break;

            case CanonicalKind.Struct:
                Assert.Equal(e.FieldCount, a.FieldCount);
                for (int field = 0; field < e.FieldCount; field++)
                {
                    AssertSame(arena, e.GetFieldIndex(field), a.GetFieldIndex(field), $"{path}.{field}");
                }

                break;

            case CanonicalKind.Extension:
                AssertSame(arena, e.StorageIndex, a.StorageIndex, path + " storage");
                break;

            default:
                Assert.Fail($"{path}: a {e.Kind} node is not one the test compares");
                break;
        }
    }

    private static void AssertSameBits(CanonicalNode expected, CanonicalNode actual, int rows, string path)
    {
        ReadOnlySpan<byte> e = expected.Bits.Span;
        ReadOnlySpan<byte> a = actual.Bits.Span;
        for (int row = 0; row < rows; row++)
        {
            int at = expected.BitOffset + row;
            int there = actual.BitOffset + row;
            Assert.True(((e[at >> 3] >> (at & 7)) & 1) == ((a[there >> 3] >> (there & 7)) & 1), $"{path}: the bit of row {row} differs");
        }
    }

    /// <summary>A batch a run takes rows of: its arena, its root node, its rows, and the rows the runs span of it.</summary>
    private readonly record struct Source(CanonicalArena Arena, int Node, int Rows, int First = 0, int End = 0) : CanonicalRuns.ISource;

    /// <summary>The schema of the test's batches, and the batches of that schema.</summary>
    private sealed class Shape
    {
        private readonly DType _id;
        private readonly DType _flag;
        private readonly DType _name;
        private readonly DType _tag;
        private readonly DType _service;
        private readonly DType _at;
        private readonly DType _atStorage;
        private readonly DType _document;
        private readonly DType _metadata;
        private readonly DType _value;
        private readonly DType _bits;

        // A few texts, long and short, which the batches hold in runs: the same from one batch to the next.
        private static readonly byte[][] Texts =
        [
            "checkout-service-of-the-first-region"u8.ToArray(), "payments"u8.ToArray(), "a text of exactly thirteen"u8.ToArray()[..13],
            "search-frontend-with-a-long-name-too"u8.ToArray(), "db"u8.ToArray(), []
        ];

        // The rows a text is held for, on average, before another is drawn: a stretch of rows that view one text.
        private readonly int _stretch;

        internal Shape(DTypeArena types, int stretch)
        {
            _stretch = stretch;
            _id = types.Primitive(PType.I64, Nullability.NonNullable);
            _flag = types.Bool(Nullability.Nullable);
            _name = types.Utf8(Nullability.Nullable);
            _tag = types.Utf8(Nullability.NonNullable);
            _service = types.Utf8(Nullability.NonNullable);
            _atStorage = types.Primitive(PType.I64, Nullability.NonNullable);
            _at = types.Extension("vortex.timestamp", _atStorage, [2, 0, 0]);
            _document = types.Variant(Nullability.Nullable);
            _metadata = types.Binary(Nullability.NonNullable);
            _value = types.Binary(Nullability.Nullable);
            _bits = types.Bool(Nullability.NonNullable);
            Root = types.Struct(
                ["id", "flag", "name", "tag", "service", "at", "document"],
                [_id, _flag, _name, _tag, _service, _at, _document],
                Nullability.NonNullable);
        }

        internal DType Root { get; }

        /// <summary>A batch of <paramref name="rows"/> rows in <paramref name="arena"/>, its columns held as the batch's number says.</summary>
        internal int Batch(CanonicalArena arena, Random random, int rows, int number)
        {
            int id = Numbers(arena, _id, rows, random);

            // Booleans under a bitmap that starts inside a byte, or valid throughout, or null throughout.
            Validity flagValidity = number switch { 1 => Validity.AllValid, 2 => Validity.AllInvalid, _ => Bitmap(arena, rows, random, offset: 3) };
            int flag = arena.AddBool(_flag, rows, flagValidity, RandomBits(arena, rows + 5, random), 5);

            // Texts with nulls; a constant in one batch, a dictionary in another, valid throughout in a third.
            int name = number switch
            {
                3 => arena.AddConstant(_name, rows, Bitmap(arena, rows, random, offset: 2), Texts[0]),
                4 => Dictionary(arena, _name, rows, random),
                5 => TextRuns(arena, _name, rows, random, Validity.AllValid, padded: true),
                _ => TextRuns(arena, _name, rows, random, Bitmap(arena, rows, random, offset: 0), padded: false),
            };

            int tag = number == 1
                ? arena.AddConstant(_tag, rows, Validity.NonNullable, Texts[1])
                : TextRuns(arena, _tag, rows, random, Validity.NonNullable, padded: true);

            // The same constant in every batch but one, so that the column is a constant only when no run takes that one.
            int service = number == 5
                ? TextRuns(arena, _service, rows, random, Validity.NonNullable, padded: true)
                : arena.AddConstant(_service, rows, Validity.NonNullable, Texts[3]);

            int at = arena.AddExtension(_at, rows, Numbers(arena, _atStorage, rows, random));

            // A variant: its rows' metadata, the same row after row, and their values, some null, under the rows' own bitmap.
            int metadata = TextRuns(arena, _metadata, rows, random, Validity.NonNullable, padded: true);
            int value = TextRuns(arena, _value, rows, random, number % 2 == 0 ? Bitmap(arena, rows, random, offset: 1) : Validity.AllValid, padded: true);
            int document = arena.AddStruct(_document, rows, number == 0 ? Bitmap(arena, rows, random, offset: 0) : Validity.AllValid, [metadata, value]);

            return arena.AddStruct(Root, rows, Validity.NonNullable, [id, flag, name, tag, service, at, document]);
        }

        private static int Numbers(CanonicalArena arena, DType dtype, int rows, Random random)
        {
            VortexBuffer values = arena.Allocate(rows * sizeof(long), sizeof(long), out Span<byte> bytes);
            Span<long> into = MemoryMarshal.Cast<byte, long>(bytes);
            for (int row = 0; row < rows; row++)
            {
                into[row] = random.NextInt64();
            }

            return arena.AddPrimitive(dtype, rows, Validity.NonNullable, PType.I64, values);
        }

        private static VortexBuffer RandomBits(CanonicalArena arena, int bits, Random random)
        {
            VortexBuffer buffer = arena.Allocate((bits + 7) / 8, 1, out Span<byte> bytes);
            random.NextBytes(bytes);
            return buffer;
        }

        private Validity Bitmap(CanonicalArena arena, int rows, Random random, int offset) =>
            Validity.Bitmap(arena.AddBool(_bits, rows, Validity.NonNullable, RandomBits(arena, rows + offset, random), offset));

        /// <summary>
        /// Texts in runs of a few rows of one value: a row that repeats the row before views its bytes one time in two, and
        /// holds a copy of its own otherwise, as two inputs hold the same text apart.
        /// </summary>
        private int TextRuns(CanonicalArena arena, DType dtype, int rows, Random random, Validity validity, bool padded)
        {
            List<byte> data = [];
            VortexBuffer views = arena.Allocate(rows * 16, 16, out Span<byte> viewBytes);
            byte[] text = Texts[random.Next(Texts.Length)];
            int at = 0;
            for (int row = 0; row < rows; row++)
            {
                if (random.Next(_stretch) == 0)
                {
                    text = Texts[random.Next(Texts.Length)];
                    at = data.Count;
                    data.AddRange(text);
                }
                else if (random.Next(Math.Max(2, _stretch / 3)) == 0 || row == 0)
                {
                    at = data.Count;
                    data.AddRange(text);
                }

                Span<byte> view = viewBytes.Slice(row * 16, 16);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)text.Length);
                if (text.Length <= 12)
                {
                    text.CopyTo(view[4..]);
                    if (!padded)
                    {
                        // Views read as they came hold anything past an inline value's bytes.
                        view[(4 + text.Length)..].Fill(0xEE);
                    }
                }
                else
                {
                    text.AsSpan(0, 4).CopyTo(view[4..]);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(view[12..], (uint)at);
                }
            }

            // Bytes past the last text, so that a text near the buffer's end is one too.
            VortexBuffer buffer = arena.Allocate(Math.Max(data.Count, 1), 1, out Span<byte> bytes);
            CollectionsMarshal.AsSpan(data).CopyTo(bytes);
            return arena.AddVarBinView(dtype, rows, validity, views, [buffer.Slice(0, data.Count)], padded);
        }

        /// <summary>A dictionary of the texts and a code a row.</summary>
        private static int Dictionary(CanonicalArena arena, DType dtype, int rows, Random random)
        {
            DType values = dtype;
            List<byte> data = [];
            VortexBuffer views = arena.Allocate(Texts.Length * 16, 16, out Span<byte> viewBytes);
            for (int i = 0; i < Texts.Length; i++)
            {
                Span<byte> view = viewBytes.Slice(i * 16, 16);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)Texts[i].Length);
                if (Texts[i].Length <= 12)
                {
                    Texts[i].CopyTo(view[4..]);
                }
                else
                {
                    Texts[i].AsSpan(0, 4).CopyTo(view[4..]);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(view[12..], (uint)data.Count);
                    data.AddRange(Texts[i]);
                }
            }

            VortexBuffer buffer = arena.Allocate(data.Count, 1, out Span<byte> bytes);
            CollectionsMarshal.AsSpan(data).CopyTo(bytes);
            int dictionary = arena.AddVarBinView(values, Texts.Length, Validity.AllValid, views, [buffer], padded: true);
            VortexBuffer codes = arena.Allocate(rows * sizeof(uint), sizeof(uint), out Span<byte> codeBytes);
            Span<uint> into = MemoryMarshal.Cast<byte, uint>(codeBytes);
            uint code = 0;
            for (int row = 0; row < rows; row++)
            {
                if (random.Next(4) == 0)
                {
                    code = (uint)random.Next(Texts.Length);
                }

                into[row] = code;
            }

            return arena.AddDictionary(dtype, rows, Validity.AllValid, codes, dictionary);
        }
    }
}
