// A census of a shredded variant's paths taken from its shredding as it lies counts, path by path and class by class, what a
// census of its rows put back together counts: over random shreddings of random documents, nested objects, lists, values of
// another class than their key's column, rows that are no object, the variant null and a null column among them; and read
// for the keys the rows hold alone, a shredded key no row holds left undecoded, it counts the same. A writer that counts its
// own rows' paths counts them all.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Scanning;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed partial class ShreddedVariantWriteTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task ACensusOfTheShreddingAsItLiesIsTheCensusOfItsRowsPutBack(int seed)
    {
        Random random = new Random(seed);
        Doc[] rows = [.. Enumerable.Range(0, 3_000).Select(i => new Doc(i, Document(random), random.Next(10) == 0 ? null : Document(random)))];
        Dictionary<string, VariantShredding> shredding = new()
        {
            [nameof(Doc.Attributes)] = Shredding(random),
            [nameof(Doc.Extra)] = Shredding(random),
        };

        string path = await WriteAsync(rows, shredding, degree: 1);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            foreach (string column in new[] { nameof(Doc.Attributes), nameof(Doc.Extra) })
            {
                List<(string, long, long)> whole = await CountAsync(file, column, apart: false);
                List<(string, long, long)> apart = await CountAsync(file, column, apart: true);
                List<(string, long, long)> keyed = await CountAsync(file, column, apart: true, Held());
                Assert.True(whole.Count > 5, $"{column}: {whole.Count} paths");
                Assert.Contains(whole, path => path.Item2 > 0 && path.Item3 > 0);
                Assert.Equal(whole, apart);
                Assert.Equal(whole, keyed);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AShreddingNoneOfWhoseKeysTheReadNamesIsCountedFromItsResidualAlone()
    {
        Random random = new Random(7);
        Doc[] rows = [.. Enumerable.Range(0, 2_000).Select(i => new Doc(i, Document(random), Document(random)))];
        VariantShredding missing = VariantShredding.Object(("missing", VariantShredding.Primitive(VariantClass.String)));
        string path = await WriteAsync(rows, new() { [nameof(Doc.Attributes)] = missing, [nameof(Doc.Extra)] = missing }, degree: 1);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<(string, long, long)> whole = await CountAsync(file, nameof(Doc.Attributes), apart: false);
            Assert.True(whole.Count > 5, $"{whole.Count} paths");
            Assert.Equal(whole, await CountAsync(file, nameof(Doc.Attributes), apart: true, Held()));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// A writer that counts its rows' paths itself, as an append's does, given rows past its last whole block: those reach the
    /// file as it completes, and are counted too, so that it writes the directory of every row, the one a census of the rows
    /// in order makes.
    /// </summary>
    [Fact]
    public async Task AWriterCountsThePathsOfTheRowsItCompletesWith()
    {
        Doc[] rows = Counted(new Random(9));
        string path = await CountedAsync(rows, degree: 1);
        PathCensus census = new PathCensus(new VortexWriteOptions().StringBoundBytes, 1_024);
        try
        {
            foreach (Doc row in rows)
            {
                census.Add(row.Attributes.Metadata.Span, row.Attributes.Value.Span);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            (Vorticity.Indexes.VariantDirectory? written, _, _) = await Vorticity.Indexes.VariantDirectory.ReadAsync(
                file, file.DType.IndexOfField("Attributes"u8), null, Ct);
            Assert.NotNull(written);
            Assert.Equal(
                Convert.ToHexString(Vorticity.Indexes.VariantDirectory.From(census, null, 1_024).ToBytes()), Convert.ToHexString(written.ToBytes()));
        }
        finally
        {
            census.Release();
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// A writer that counts its rows' paths itself, as an append's does: at eight lanes, a call's rows counted in slices on lanes
    /// of their own and merged, it writes the directories one lane writes, byte for byte.
    /// </summary>
    [Fact]
    public async Task AWritersCensusCountedInSlicesWritesTheDirectoryOneLaneWrites()
    {
        Doc[] rows = Counted(new Random(9));
        string one = await CountedAsync(rows, degree: 1);
        long sliced = System.Threading.Interlocked.Read(ref VortexFileWriter.CensusesSliced);
        string eight = await CountedAsync(rows, degree: 8);
        try
        {
            Assert.True(System.Threading.Interlocked.Read(ref VortexFileWriter.CensusesSliced) > sliced, "no call was counted in slices");
            await using VortexFile alone = await VortexFile.OpenAsync(one, Ct);
            await using VortexFile lanes = await VortexFile.OpenAsync(eight, Ct);
            foreach (string column in new[] { nameof(Doc.Attributes), nameof(Doc.Extra) })
            {
                int field = alone.DType.IndexOfField(System.Text.Encoding.UTF8.GetBytes(column));
                (Vorticity.Indexes.VariantDirectory? expected, _, _) = await Vorticity.Indexes.VariantDirectory.ReadAsync(alone, field, null, Ct);
                (Vorticity.Indexes.VariantDirectory? actual, _, _) = await Vorticity.Indexes.VariantDirectory.ReadAsync(lanes, field, null, Ct);
                Assert.NotNull(expected);
                Assert.NotNull(actual);
                Assert.Equal(Convert.ToHexString(expected.ToBytes()), Convert.ToHexString(actual.ToBytes()));
            }
        }
        finally
        {
            System.IO.File.Delete(one);
            System.IO.File.Delete(eight);
        }
    }

    /// <summary>Seventy thousand documents, past the last of their 1,024-row blocks by 368.</summary>
    private static Doc[] Counted(Random random) =>
        [.. Enumerable.Range(0, 70_000).Select(i => new Doc(i, Document(random), random.Next(10) == 0 ? null : Document(random)))];

    /// <summary>The rows written in one chunk by a writer that counts their paths itself, at <paramref name="degree"/> lanes.</summary>
    private static async Task<string> CountedAsync(Doc[] rows, int degree)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-counted-{Guid.NewGuid():N}.vortex");
        VortexWriteOptions options = new VortexWriteOptions { BlockRows = 1_024, ChunkTargetBytes = 1 << 28, CountsPaths = true, CarriesContents = true, DegreeOfParallelism = degree };
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Doc>(path, options);
        await writer.WriteAsync<Doc>(rows, Ct);
        await writer.CompleteAsync(Ct);
        return path;
    }

    /// <summary>The keys a document may hold, "missing" not among them, which a shredding may name.</summary>
    private static ApartKeys Held()
    {
        ApartKeys keys = new ApartKeys();
        foreach (string key in new[] { "status", "duration", "route", "ok", "user", "tags", "big", "http.route" }.Concat(Enumerable.Range(0, 30).Select(k => $"k{k}")))
        {
            keys.Add(System.Text.Encoding.UTF8.GetBytes(key));
        }

        return keys;
    }

    /// <summary>
    /// The paths of <paramref name="column"/>, read as the shredding holds it or put back, of the rows a third of which are left
    /// and the others kept: each path and class with its rows kept and left, in order; apart, of the keys <paramref name="keys"/>
    /// names alone, or every key.
    /// </summary>
    private static async Task<List<(string Path, long Kept, long Left)>> CountAsync(VortexFile file, string column, bool apart, ApartKeys? keys = null)
    {
        EdgeCounts counts = new EdgeCounts();
        PathCensus kept = new PathCensus();
        PathCensus left = new PathCensus();
        FieldExpr field = new ColumnFieldExpr(column);
        ScanBuilder read = file.ScanBuilder().Project(column);
        await foreach (RecordBatch batch in (apart ? read.ApartAsync(keys) : read.ExecuteAsync()).WithCancellation(Ct))
        {
            int rows = batch.RowCount;
            ulong[] keep = new ulong[(rows + 63) >> 6];
            ulong[] other = new ulong[keep.Length];
            for (int row = 0; row < rows; row++)
            {
                ulong bit = 1UL << (row & 63);
                if ((batch.StartRow + row) * 2_654_435_761L % 3 == 0)
                {
                    other[row >> 6] |= bit;
                }
                else
                {
                    keep[row >> 6] |= bit;
                }
            }

            int node = FilterEvaluator.Resolve(batch.Arena, batch.RootIndex, field, rows);
            if (apart)
            {
                Assert.True(ApartCensus.IsGroup(batch.Arena, node), column);
                ApartCensus.Add(batch.Arena, node, rows, keep, other, counts);
            }
            else
            {
                PathCensus.Counting counting = PathCensus.Prepare(batch.Arena, node, rows);
                kept.Add(counting, keep);
                left.Add(counting, other);
            }
        }

        counts.Add(kept, left);
        return [.. counts.Entries().Select(entry => (Convert.ToHexString(entry.Key), entry.Kept, entry.Left)).OrderBy(entry => entry.Item1, StringComparer.Ordinal)];
    }
}
