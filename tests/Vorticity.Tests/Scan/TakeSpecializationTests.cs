// Every specialized take, against the fallback it replaced, over the whole corpus.
//
// A `DecodeSelected` override is an optimization with a correctness obligation: it must produce a
// node INDISTINGUISHABLE from what the default produces - decode the node whole, then gather - for
// the same rows. Many encodings override it -- the reflection test below finds every one -- each
// with its own arithmetic, and each with a way to be subtly wrong that no single hand-written
// fixture would reach: an off-by-one in a bit-packed lane index, a patch applied at the
// pre-selection position, a run boundary searched with `>=` instead of `>`.
//
// SO THE ORACLE IS THE FULL SCAN, over every in-scope corpus file. For each file the test reads
// every row, then takes a scattered set of indices and asserts the values match the ones the full
// scan put there. That covers every encoding the corpus contains, at whatever nesting depth it
// contains it, without naming any of them - and it keeps covering new ones as the corpus grows.
//
// The indices are chosen to be awkward on purpose: the first row, the last row, both sides of the
// 1024-element FastLanes block boundary, and a prime stride through the middle so no two land in
// the same block or the same run.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class TakeSpecializationTests
{
    [Fact]
    public async Task EveryCorpusFileTakesWhatAFullScanPutsAtThoseIndices()
    {
        Decoders.EnsureRegistered();

        int files = 0;
        long rows = 0;
        StringBuilder failures = new StringBuilder();

        foreach (CorpusEntry entry in CorpusManifest.InScope())
        {
            List<string> all = await ReadAll(entry.Path);
            if (all.Count < 2)
            {
                continue;
            }

            long[] wanted = Indices(all.Count);
            List<string> expected = [];
            foreach (long index in wanted)
            {
                expected.Add(all[(int)index]);
            }

            List<string> taken = await ReadTake(entry.Path, wanted);
            files++;
            rows += taken.Count;

            if (!Same(expected, taken))
            {
                failures.Append(entry.Id)
                    .Append(": take returned ")
                    .Append(taken.Count)
                    .Append(" values, expected ")
                    .Append(expected.Count)
                    .Append('\n');
                for (int i = 0; i < Math.Min(expected.Count, taken.Count); i++)
                {
                    if (expected[i] != taken[i])
                    {
                        failures.Append("    row ").Append(wanted[i])
                            .Append(": full scan ").Append(expected[i])
                            .Append(", take ").Append(taken[i]).Append('\n');
                        break;
                    }
                }
            }
        }

        Console.Out.WriteLine(
            $"TAKE SWEEP: {files} corpus files, {rows} taken values compared against a full scan.");

        // The coverage floor: without it a run where every file was skipped would pass silently.
        // Not the in-scope count - the corpus carries r0 and r1 files by design, and a file with
        // fewer than two rows has no scattered take to make.
        Assert.True(files > 550, $"only {files} files were compared");
        Assert.Equal(string.Empty, failures.ToString());
    }

    /// <summary>
    /// A take that asks for EVERY row must still be right, which is the boundary the pushdown
    /// short-circuits on.
    /// </summary>
    /// <remarks>
    /// `ExecuteWithTake` skips the selection when the split's rows are all wanted, on the grounds
    /// that pushing an identity selection costs a gather for nothing. That is a branch, and a
    /// branch that fires on the largest input is exactly the one to pin.
    /// </remarks>
    [Fact]
    public async Task TakingEveryRowReturnsTheWholeFile()
    {
        Decoders.EnsureRegistered();
        string path = Corpus.Path("containers/zoned_many_zones_nulls");

        List<string> all = await ReadAll(path);
        long[] every = new long[all.Count];
        for (int i = 0; i < every.Length; i++)
        {
            every[i] = i;
        }

        Assert.Equal(all, await ReadTake(path, every));
    }

    /// <summary>
    /// Two rows either side of a FastLanes block boundary, which is where positional access
    /// computes a different block for each.
    /// </summary>
    [Fact]
    public async Task RowsEitherSideOfABlockBoundaryAreBothCorrect()
    {
        Decoders.EnsureRegistered();
        string path = Corpus.Path("distributions/high_cardinality_i64_r8193");

        List<string> all = await ReadAll(path);
        long[] wanted = [1022, 1023, 1024, 1025, 2047, 2048, 8191, 8192];

        List<string> expected = [];
        foreach (long index in wanted)
        {
            expected.Add(all[(int)index]);
        }

        Assert.Equal(expected, await ReadTake(path, wanted));
    }

    /// <summary>
    /// <c>SelectsWithoutFullDecode</c> says exactly which encodings override <c>DecodeSelected</c>.
    /// </summary>
    /// <remarks>
    /// A FLAG THAT DESCRIBES CODE MUST BE CHECKED AGAINST THE CODE. `FlatLayoutReader` routes a take
    /// through the retained-chunk cache for every encoding whose `DecodeSelected` is the fallback,
    /// and straight through for every encoding that overrides it. Both halves are load-bearing, in
    /// opposite directions: the flag set on an encoding that does not override leaves it decoding
    /// its node once per wanted row, and the flag missing on one that does forces a full decode on
    /// an encoding such as `fsst` that reaches one row without one.
    ///
    /// A hand-maintained list would drift the first time a decoder is specialized, so the list is
    /// not maintained here: the declaring type of the method IS the fact, and the flag is asserted
    /// against it.
    /// </remarks>
    [Fact]
    public void TheSelectionFlagMatchesTheOverridesItDescribes()
    {
        Decoders.EnsureRegistered();

        StringBuilder wrong = new StringBuilder();
        int specialized = 0;
        foreach (ArrayEncodingId id in Enum.GetValues<ArrayEncodingId>())
        {
            if (id == ArrayEncodingId.Unknown || !ArrayDecoderTable.IsImplemented(id))
            {
                continue;
            }

            ArrayDecoder decoder = ArrayDecoderTable.Get(id, id.ToString());
            MethodInfo? method = decoder.GetType().GetMethod(
                nameof(ArrayDecoder.DecodeSelected),
                BindingFlags.Instance | BindingFlags.Public);
            bool overridden = method is not null && method.DeclaringType != typeof(ArrayDecoder);

            if (overridden)
            {
                specialized++;
            }

            if (overridden != decoder.SelectsWithoutFullDecode)
            {
                wrong.Append(id.ToString())
                    .Append(": overrides DecodeSelected = ")
                    .Append(overridden)
                    .Append(", SelectsWithoutFullDecode = ")
                    .Append(decoder.SelectsWithoutFullDecode)
                    .Append('\n');
            }
        }

        Assert.Equal(string.Empty, wrong.ToString());

        // Not a ceiling: a lower bound that says the sweep found the overrides at all, so a broken
        // reflection lookup cannot pass this test by finding nothing anywhere.
        Assert.True(specialized >= 10, $"only {specialized} specialized decoders were found");
    }

    /// <summary>Awkward on purpose: block boundaries, both ends, and a prime stride between.</summary>
    /// <summary>
    /// A take of a few rows of text this writer wrote plain, whose offsets it bit-packs, reads only
    /// the offsets of the rows it wants: rows that share an offset, row zero, the last row, empty
    /// values, nulls, a chunk's first and last rows, and the same rows as a full scan put there.
    /// </summary>
    [Fact]
    public async Task ASparseTakeOfWrittenTextReadsWhatAFullScanReads()
    {
        Decoders.EnsureRegistered();
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-sparse-take-{Guid.NewGuid():N}.vortex");
        try
        {
            const int rows = 100_000;
            await WriteTextAsync(path, rows);
            List<string> all = await ReadAll(path);
            long[] wanted = [0, 1, 2, 3, 699, 700, 1023, 1024, 5000, 5001, 65_535, 65_536, 99_998, 99_999];
            List<string> expected = [];
            foreach (long index in wanted)
            {
                expected.Add(all[(int)index]);
            }

            Assert.Equal(expected, await ReadTake(path, wanted));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>A nullable text column of <paramref name="rows"/> rows, written plain: a null row in seven, and every length from none to twenty.</summary>
    private static async Task WriteTextAsync(string path, int rows)
    {
        DTypeArena types = new DTypeArena();
        DType text = types.Utf8(Nullability.Nullable);
        DType schema = types.Struct(["t"], [text], Nullability.NonNullable);
        await using VortexFileWriter writer = VortexFileWriter.Create(
            path, schema, new VortexWriteOptions { Compression = CompressionProfile.None });
        const int batch = 16_384;
        for (int start = 0; start < rows; start += batch)
        {
            int count = Math.Min(batch, rows - start);
            CanonicalArena arena = new CanonicalArena();
            Vorticity.Buffers.VortexBuffer heap = arena.Allocate(count * 20, 8, out Span<byte> heapBytes);
            Vorticity.Buffers.VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> viewBytes);
            Vorticity.Buffers.VortexBuffer bits = arena.Allocate((count + 7) / 8, 8, out Span<byte> bitBytes);
            viewBytes.Clear();
            bitBytes.Clear();
            int used = 0;
            for (int i = 0; i < count; i++)
            {
                int row = start + i;
                if (row % 7 == 3)
                {
                    continue;
                }

                bitBytes[i >> 3] |= (byte)(1 << (i & 7));
                int length = row % 21;
                for (int b = 0; b < length; b++)
                {
                    heapBytes[used + b] = (byte)('a' + ((row + b) % 26));
                }

                Span<byte> view = viewBytes.Slice(i * 16, 16);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view, length);
                if (length <= 12)
                {
                    heapBytes.Slice(used, length).CopyTo(view[4..]);
                }
                else
                {
                    heapBytes.Slice(used, 4).CopyTo(view[4..]);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view[12..], used);
                    used += length;
                }
            }

            int validity = arena.AddBool(types.Bool(Nullability.NonNullable), count, Validity.NonNullable, bits, 0);
            int column = arena.AddVarBinView(text, count, Validity.Bitmap(validity), views, [heap.Slice(0, Math.Max(used, 1))]);
            int root = arena.AddStruct(schema, count, Validity.NonNullable, [column]);
            using RecordBatch record = new RecordBatch(arena, root, start);
            await writer.WriteAsync(record, CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
    }

    private static long[] Indices(int rowCount)
    {
        SortedSet<long> wanted = [0, rowCount - 1];
        ReadOnlySpan<long> boundaries = [1023, 1024, 1025, 2047, 2048];
        foreach (long boundary in boundaries)
        {
            if (boundary < rowCount)
            {
                wanted.Add(boundary);
            }
        }

        for (long i = 7; i < rowCount; i += 1021)
        {
            wanted.Add(i);
        }

        return [.. wanted];
    }

    private static bool Same(List<string> expected, List<string> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        for (int i = 0; i < expected.Count; i++)
        {
            if (expected[i] != actual[i])
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<List<string>> ReadAll(string path)
    {
        List<string> values = [];
        await using VortexFile file = await VortexFile.OpenAsync(path, OpenOptionsFor(path), CancellationToken.None);
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            Values.DescribeRows(batch, values);
        }

        return values;
    }

    private static async Task<List<string>> ReadTake(string path, long[] wanted)
    {
        List<string> values = [];
        await using VortexFile file = await VortexFile.OpenAsync(path, OpenOptionsFor(path), CancellationToken.None);
        await foreach (RecordBatch batch in file.ScanBuilder().Take(wanted).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            Values.DescribeRows(batch, values);
        }

        return values;
    }

    /// <summary>Open options for a corpus path: the schema out of band when the file has none.</summary>
    /// <param name="path">The corpus file about to be opened.</param>
    /// <remarks>
    /// <c>types/no_dtype_segment</c> reached this sweep only when <c>vortex.map</c> gained a decoder
    /// and the file became in-scope. Opening it without a DType is a <c>VortexFormatException</c>,
    /// so the donor is a real corpus file with the identical schema.
    /// </remarks>
    private static VortexOpenOptions OpenOptionsFor(string path) =>
        path.Contains("no_dtype_segment", StringComparison.Ordinal)
            ? new VortexOpenOptions { DType = OutOfBandSchema.Value }
            : VortexOpenOptions.Default;

    private static readonly Lazy<DType> OutOfBandSchema = new Lazy<DType>(static () =>
    {
        VortexFile donor = VortexFile
            .OpenAsync(
                CorpusManifest.Get("types/user_metadata_segments").Path,
                VortexOpenOptions.Default,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        return donor.DType;
    });
}
