// A count or an any told to leave rows out, as a dataset does for the rows it deleted from a file
// without rewriting it. Every answer is held against the values the test wrote, whichever tier
// gives it: a sorted column's slices, an index's rows, a whole verdict of the zone maps, or the
// decode. A wrong proof would count a row left out, and a row left out is never counted.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Keys;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class ScanExclusionTests
{
    private const int Rows = 20_000;

    /// <summary>Rows per block, which is a zone of every column's map.</summary>
    private const int Block = 1_024;

    /// <summary>Every tier the count may take, then each switched off down to the decode.</summary>
    private static readonly (bool Prune, TerminalTiers Tiers)[] Configurations =
    [
        (true, TerminalTiers.All),
        (true, TerminalTiers.All & ~TerminalTiers.ExactCover),
        (true, TerminalTiers.All & ~(TerminalTiers.ExactCover | TerminalTiers.FullBlock)),
        (false, TerminalTiers.All),
    ];

    /// <summary>
    /// A sorted column's slices, one key of two rows, an index's rows, a band the zone maps decide
    /// whole once the slices are switched off, and a column only the decode answers.
    /// </summary>
    public static TheoryData<string> Filters => new TheoryData<string>
    {
        "key range",
        "key = tie",
        "rare group",
        "band = 7",
        "noise < 100",
    };

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task ACountLeavesOutTheRowsItIsToldToWhicheverTierAnswers(string name)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = await WriteAsync(ct);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            ScanExplanation plan = await file.ScanBuilder().Where(Filter(name)).ExplainAsync(ct);
            Assert.Equal(name != "noise < 100", plan.Count!.ExactCover);

            ExcludedRows excluded = Excluded();
            foreach (RowRange? range in (RowRange?[])[null, new RowRange(3_000, 15_000)])
            {
                long expected = Expected(name, excluded, range);
                Assert.True(expected > 0, "some kept row should match, or the test proves nothing");
                foreach ((bool prune, TerminalTiers tiers) in Configurations)
                {
                    Assert.Equal(expected, await Build(file, name, range).WithPruning(prune).WithTiers(tiers).CountExcludingAsync(excluded, ct));
                    Assert.True(await Build(file, name, range).WithPruning(prune).WithTiers(tiers).AnyExcludingAsync(excluded, ct));
                }
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task EveryRowAFilterKeepsLeftOutCountsNothingOnEveryTier(string name)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = await WriteAsync(ct);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            List<long> matching = [];
            for (long row = 0; row < Rows; row++)
            {
                if (Matches(name, row))
                {
                    matching.Add(row);
                }
            }

            ExcludedRows excluded = new ExcludedRows(matching);
            foreach ((bool prune, TerminalTiers tiers) in Configurations)
            {
                Assert.Equal(0, await Build(file, name, null).WithPruning(prune).WithTiers(tiers).CountExcludingAsync(excluded, ct));
                Assert.False(await Build(file, name, null).WithPruning(prune).WithTiers(tiers).AnyExcludingAsync(excluded, ct));
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task WithoutAFilterTheRowsLeftOutAreSubtracted()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = await WriteAsync(ct);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            ExcludedRows excluded = Excluded();
            Assert.Equal(Rows - excluded.ExcludedCount, await file.ScanBuilder().CountExcludingAsync(excluded, ct));
            Assert.Equal(
                12_000 - (excluded.ExcludedBefore(15_000) - excluded.ExcludedBefore(3_000)),
                await file.ScanBuilder().Rows(new RowRange(3_000, 15_000)).CountExcludingAsync(excluded, ct));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AKeyOrderedScanStepsOverTheRowsLeftOutBothWays()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = await WriteAsync(ct);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            ExcludedRows excluded = Excluded();
            foreach (string? name in (string?[])[null, "noise < 100"])
            {
                List<long> kept = [];
                for (long row = 0; row < Rows; row++)
                {
                    if (!excluded.Excludes(row) && (name is null || Matches(name, row)))
                    {
                        kept.Add(Key(row));
                    }
                }

                kept.Sort();
                foreach (bool descending in (bool[])[false, true])
                {
                    ScanBuilder scan = file.ScanBuilder().InKeyOrder("key", descending).Project("key");
                    if (name is not null)
                    {
                        scan.Where(Filter(name));
                    }

                    List<long> walked = [];
                    await foreach (RecordBatch batch in scan.ExecuteExcludingAsync(excluded).WithCancellation(ct))
                    {
                        VortexColumn keys = batch.Column("key"u8.ToArray());
                        for (int row = 0; row < keys.Length; row++)
                        {
                            walked.Add(keys.AsPrimitive<long>()[row]);
                        }
                    }

                    Assert.Equal(descending ? [.. Enumerable.Reverse(kept)] : kept, walked);
                }
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// A cursor on the sorted column, whose ranks are rows, and one on the indexed column, whose
    /// ranks are counted against the keys of the rows left out: both walk, rank and seek as a
    /// cursor over the kept rows alone would.
    /// </summary>
    [Theory]
    [InlineData("key")]
    [InlineData("group")]
    public async Task AKeyCursorStepsAndRanksOverTheRowsLeftOut(string column)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = await WriteAsync(ct);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            ExcludedRows excluded = Excluded();
            Func<long, long> value = column == "key" ? Key : Group;
            List<(long Key, long Row)> kept = [];
            List<ExcludedKey> gone = [];
            for (long row = 0; row < Rows; row++)
            {
                if (excluded.Excludes(row))
                {
                    gone.Add(new ExcludedKey(FilterLiteral.From(value(row)), row));
                }
                else
                {
                    kept.Add((value(row), row));
                }
            }

            kept.Sort();
            gone.Sort(static (left, right) => left.Key.SignedValue != right.Key.SignedValue
                ? left.Key.SignedValue.CompareTo(right.Key.SignedValue)
                : left.Row.CompareTo(right.Row));
            await using Vorticity.Keys.KeyCursor cursor = await file.Keys(column)
                .Excluding(excluded, ranksAreRows: false, _ => ValueTask.FromResult<ExcludedKey[]>([.. gone]))
                .OpenAsync(ct);

            List<(long Key, long Row)> up = [];
            for (bool at = await cursor.SeekFirstAsync(ct); at; at = await cursor.NextAsync(ct))
            {
                up.Add((cursor.Key.SignedValue, cursor.Row));
            }

            Assert.Equal(kept, up);
            List<(long Key, long Row)> down = [];
            for (bool at = await cursor.SeekLastAsync(ct); at; at = await cursor.PreviousAsync(ct))
            {
                down.Add((cursor.Key.SignedValue, cursor.Row));
            }

            down.Reverse();
            Assert.Equal(kept, down);

            foreach (long key in (long[])[0, 1, 5, 2_000, 3_000, 4_999, 9_999])
            {
                Assert.Equal(kept.Count(entry => entry.Key < key), await cursor.RankAsync(FilterLiteral.From(key), ct));
            }

            for (int rank = 0; rank < kept.Count; rank += 97)
            {
                Assert.True(await cursor.SeekRankAsync(rank, ct));
                Assert.Equal(kept[rank], (cursor.Key.SignedValue, cursor.Row));
                Assert.Equal(kept.Count(entry => entry.Key == kept[rank].Key), await cursor.CountAtKeyAsync(ct));
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task RowsLeftOutServeNoTakeAndNoScanInFileOrder()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = await WriteAsync(ct);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            ExcludedRows excluded = Excluded();
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await file.ScanBuilder().Where(Filter("noise < 100")).Take([1, 2, 3]).CountExcludingAsync(excluded, ct));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (RecordBatch batch in file.ScanBuilder().ExecuteExcludingAsync(excluded).WithCancellation(ct))
                {
                    Assert.Fail("a scan in file order should not start");
                }
            });
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static ScanBuilder Build(VortexFile file, string name, RowRange? range)
    {
        ScanBuilder scan = file.ScanBuilder().Where(Filter(name));
        if (range is { } rows)
        {
            scan.Rows(rows);
        }

        return scan;
    }

    /// <summary>
    /// Runs across the key range's two ends, one row of the tie, the start of band 7 and a whole
    /// block, and a row every 97 on top: runs that cross zones, runs inside one, and single rows.
    /// </summary>
    private static ExcludedRows Excluded()
    {
        SortedSet<long> rows = [];
        foreach ((long start, long end) in (ReadOnlySpan<(long, long)>)[(0, 1), (1_000, 1_100), (2_048, 3_072), (3_990, 4_010), (6_001, 6_002), (7_000, 7_500), (13_990, 14_100), (Rows - 1, Rows)])
        {
            for (long row = start; row < end; row++)
            {
                rows.Add(row);
            }
        }

        for (long row = 50; row < Rows; row += 97)
        {
            rows.Add(row);
        }

        return new ExcludedRows(rows);
    }

    /// <summary>The rows of <paramref name="range"/> the filter keeps and the exclusion does not name, from the values written.</summary>
    private static long Expected(string name, ExcludedRows excluded, RowRange? range)
    {
        RowRange rows = range ?? new RowRange(0, Rows);
        long count = 0;
        for (long row = rows.Start; row < rows.End; row++)
        {
            count += Matches(name, row) && !excluded.Excludes(row) ? 1 : 0;
        }

        return count;
    }

    private static bool Matches(string name, long row) => name switch
    {
        "key range" => Key(row) >= 2_000 && Key(row) < 7_000,
        "key = tie" => Key(row) == 3_000,
        "rare group" => Group(row) == 5,
        "band = 7" => Band(row) == 7,
        "noise < 100" => Noise(row) < 100,
        _ => throw new ArgumentException("unknown filter " + name, nameof(name)),
    };

    private static VortexExpr Filter(string name) => name switch
    {
        "key range" => Expr.And(
            Expr.Ge(Expr.Field("key"), Expr.Literal(FilterLiteral.From(2_000L))),
            Expr.Lt(Expr.Field("key"), Expr.Literal(FilterLiteral.From(7_000L)))),
        "key = tie" => Expr.Eq(Expr.Field("key"), Expr.Literal(FilterLiteral.From(3_000L))),
        "rare group" => Expr.Eq(Expr.Field("group"), Expr.Literal(FilterLiteral.From(5L))),
        "band = 7" => Expr.Eq(Expr.Field("band"), Expr.Literal(FilterLiteral.From(7L))),
        "noise < 100" => Expr.Lt(Expr.Field("noise"), Expr.Literal(FilterLiteral.From(100L))),
        _ => throw new ArgumentException("unknown filter " + name, nameof(name)),
    };

    /// <summary>Sorted, every key on two rows.</summary>
    private static long Key(long row) => row / 2;

    /// <summary>The block's number: sorted too, and one value per zone.</summary>
    private static long Band(long row) => row / Block;

    /// <summary>One of 64, spread over the file: an index finds its few rows.</summary>
    private static long Group(long row) => Mix(row) % 64;

    /// <summary>One of 1000, spread over the file, in no index: only a decode finds its rows.</summary>
    private static long Noise(long row) => (Mix(row) >> 8) % 1_000;

    private static long Mix(long row) => (long)(((ulong)row * 0x9E3779B97F4A7C15UL) >> 20);

    /// <summary>The four columns in blocks of <see cref="Block"/>, with sorted runs on <c>group</c>.</summary>
    private static async Task<string> WriteAsync(CancellationToken ct)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-exclusion-{Guid.NewGuid():N}.vortex");
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        string[] names = ["key", "band", "group", "noise"];
        DType schema = types.Struct(names, [i64, i64, i64, i64], Nullability.NonNullable);
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = Block,
            DataBlockTargetBytes = null,
            IndexBudgetPerMille = 1_000_000,
            WritePolicy = WritePolicy.None.For("group", IndexSpec.SortedRuns),
        };

        await using (VortexFileWriter writer = VortexFileWriter.Create(path, schema, options))
        {
            CanonicalArena arena = new CanonicalArena();
            Func<long, long>[] values = [Key, Band, Group, Noise];
            int[] columns = new int[values.Length];
            for (int c = 0; c < values.Length; c++)
            {
                VortexBuffer buffer = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
                Span<long> column = MemoryMarshal.Cast<byte, long>(bytes);
                for (int row = 0; row < Rows; row++)
                {
                    column[row] = values[c](row);
                }

                columns[c] = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, buffer);
            }

            int root = arena.AddStruct(schema, Rows, Validity.NonNullable, columns);
            using (RecordBatch batch = new RecordBatch(arena, root, 0))
            {
                await writer.WriteAsync(batch, ct);
            }

            await writer.CompleteAsync(ct);
        }

        return path;
    }

    /// <summary>
    /// Rows left out as sorted runs, implemented here rather than borrowed from the dataset, so that
    /// the scan is held to the contract and not to one implementation of it.
    /// </summary>
    private sealed class ExcludedRows : IRowExclusion
    {
        private readonly long[] _starts;
        private readonly long[] _ends;

        /// <summary>The rows left out before each run, and past the last every one.</summary>
        private readonly long[] _before;

        /// <summary>The runs of <paramref name="rows"/>, which ascend.</summary>
        internal ExcludedRows(IEnumerable<long> rows)
        {
            List<long> starts = [];
            List<long> ends = [];
            foreach (long row in rows)
            {
                if (ends.Count > 0 && ends[^1] == row)
                {
                    ends[^1] = row + 1;
                }
                else
                {
                    starts.Add(row);
                    ends.Add(row + 1);
                }
            }

            _starts = [.. starts];
            _ends = [.. ends];
            _before = new long[_starts.Length + 1];
            for (int run = 0; run < _starts.Length; run++)
            {
                _before[run + 1] = _before[run] + (_ends[run] - _starts[run]);
            }
        }

        public long ExcludedCount => _before[^1];

        public int Runs => _starts.Length;

        public bool Excludes(long row)
        {
            int run = FirstEndingAfter(row);
            return run < Runs && _starts[run] <= row;
        }

        public long ExcludedBefore(long row)
        {
            int run = FirstEndingAfter(row);
            return _before[run] + (run < Runs ? Math.Max(0, row - _starts[run]) : 0);
        }

        public long KeptRow(long kept)
        {
            // Every run that starts at or before the candidate pushes it past its rows.
            long row = kept;
            for (int run = 0; run < Runs && _starts[run] <= row; run++)
            {
                row += _ends[run] - _starts[run];
            }

            return row;
        }

        public int FirstEndingAfter(long row)
        {
            int low = 0;
            int high = Runs;
            while (low < high)
            {
                int middle = (low + high) >>> 1;
                if (_ends[middle] <= row)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }

        public long StartOf(int run) => _starts[run];

        public long EndOf(int run) => _ends[run];
    }
}
