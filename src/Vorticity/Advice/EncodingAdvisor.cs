using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.IO.Pipelines;
using System.Numerics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.IO;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Advice;

/// <summary>
/// Measures, per column of some data, every way the writer can write it, on a sample of the data,
/// and ranks them for the reads a goal describes.
/// </summary>
/// <remarks>
/// <para>
/// A column's sample is its rows in windows spread over the data, written once into memory in its
/// plain form. Each candidate writes that sample again under its hint and chunk target, the file it
/// makes is opened from memory, and the column is scanned whole and read row by row. A read's time
/// is the least of its passes after one to warm it, at least three and as many as fill twenty
/// milliseconds. The storage is the goal's term, not the measurement's, so one sample prices any
/// throughput.
/// </para>
/// <para>
/// The advice is a measurement: it depends on the machine and moves with its noise, which the
/// choice's margin keeps from moving the advice. The write it produces, given the options it
/// returns, is as exact and deterministic as any other.
/// </para>
/// </remarks>
internal static class EncodingAdvisor
{
    private const int Windows = 8;
    private const int LookupChunks = 32;
    private const int BlockRows = 8_192;

    /// <summary>The fewest timed passes of a read, after the one that warms it.</summary>
    private const int Passes = 3;

    /// <summary>The most timed passes of a read.</summary>
    private const int MaxPasses = 64;

    /// <summary>
    /// How long the timed passes of a read last together at least: a scan of a fast column takes a
    /// fraction of a millisecond, which one interruption doubles, and the least of many is its time.
    /// </summary>
    private static readonly TimeSpan MeasureAtLeast = TimeSpan.FromMilliseconds(20);

    /// <summary>The rows each hint is written on while the JIT settles: a chunk at most, so that each read is quick.</summary>
    private const int SettleRows = 65_536;

    /// <summary>The rounds of reads without a compilation after which the JIT is taken to be settled, with <see cref="SettleQuiet"/>.</summary>
    private const int SettleRounds = 3;

    /// <summary>
    /// How long the process must compile nothing: longer than the runtime waits after the last
    /// compilation before it counts calls, 100 ms by default, and than the calls it then counts.
    /// </summary>
    private static readonly TimeSpan SettleQuiet = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The most a column waits for the JIT: a process whose other threads keep compiling holds back
    /// every recompilation, and its advice is measured on the code it runs then.
    /// </summary>
    private static readonly TimeSpan SettleLimit = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Under this many rows per distinct value in a chunk, a dictionary does not pay for its entries
    /// within one chunk: over a sweep of chunk sizes, a dictionary came within 10 % of its best at
    /// 100 rows per value, and 42 % above it at 13.
    /// </summary>
    private const double DictionaryAmortized = 100;

    /// <summary>The larger chunk targets a column is tried at when its chunk decides.</summary>
    private static readonly int[] LargerTargets = [4 << 20, 16 << 20];

    /// <summary>
    /// How close the bytes of a larger chunk must be to those of a candidate at the writer's own size
    /// that wrote the same encodings for the larger chunk to add nothing: what is left between the
    /// two is the noise of a measured time.
    /// </summary>
    private const double SameBytes = 0.01;

    private static readonly VortexWriteOptions Plain = new() { Compression = CompressionProfile.None, Statistics = false };

    /// <summary>Advises on every column of <paramref name="file"/> the advice can measure.</summary>
    internal static async ValueTask<EncodingAdvice> AdviseAsync(
        VortexFile file, EncodingGoal goal, VortexSession session, CancellationToken cancellationToken)
    {
        goal.Validate();
        if (!file.IsTabular)
        {
            throw new ArgumentException(
                "An encoding advice is given per column of a table, and this file's root is not a struct.", nameof(file));
        }

        return await AdviseCoreAsync(new FileSource(file), goal, session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Advises on every column of <paramref name="rows"/> the advice can measure.</summary>
    internal static async ValueTask<EncodingAdvice> AdviseAsync<TRecord>(
        ReadOnlyMemory<TRecord> rows, EncodingGoal goal, VortexSession session, CancellationToken cancellationToken)
        where TRecord : IVortexRecord<TRecord>
    {
        goal.Validate();
        RecordSource<TRecord> source = new RecordSource<TRecord>(rows);
        await using (source.ConfigureAwait(false))
        {
            return await AdviseCoreAsync(source, goal, session, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<EncodingAdvice> AdviseCoreAsync(
        Source source, EncodingGoal goal, VortexSession session, CancellationToken cancellationToken)
    {
        long rows = source.Rows;
        RowRange[] windows = WindowsOf(rows, goal.SampleRows);
        long sampled = RowsIn(windows);
        DType root = source.DType;
        List<(string Name, string Kind, ColumnProfile Profile, List<MeasuredCandidate> Measured)> columns = [];
        for (int field = 0; field < root.FieldCount && sampled > 0; field++)
        {
            ColumnKind kind = KindOf(root.GetField(field));
            if (kind == ColumnKind.None)
            {
                continue;
            }

            string name = root.GetFieldName(field);
            EncodingHint[] hints = HintsFor(kind, root.GetField(field));
            AlignedBytes plain = await source.ColumnAsync(field, windows, session, cancellationToken).ConfigureAwait(false);
            await SettleAsync(plain, name, hints, goal, session, cancellationToken).ConfigureAwait(false);
            (List<MeasuredCandidate> measured, int chunkRows) = await CandidatesAsync(
                plain, sampled, name, hints, goal, session, cancellationToken).ConfigureAwait(false);
            ColumnProfile profile = await ProfileAsync(plain, kind, chunkRows, cancellationToken).ConfigureAwait(false);

            // A column whose values repeat across the data more than inside a chunk is tried at
            // larger chunks. What a dictionary saves turns on the rows a chunk holds, so these are
            // measured on whole chunks of the largest target, and the writer's own size again on
            // the same rows, when the data holds more than the sample.
            if (profile.RowsPerDistinctInChunk < DictionaryAmortized && profile.Distinct * 10 < profile.Rows)
            {
                RowRange[] wide = WindowsOf(rows, 2 * RowsAt(LargerTargets[^1], plain.Length, sampled), 2);
                long wideRows = RowsIn(wide);
                if (wideRows > sampled)
                {
                    plain = await source.ColumnAsync(field, wide, session, cancellationToken).ConfigureAwait(false);
                    (measured, _) = await CandidatesAsync(plain, wideRows, name, hints, goal, session, cancellationToken).ConfigureAwait(false);
                }

                long measuredRows = Math.Max(wideRows, sampled);
                List<EncodingHint> larger = [EncodingHint.Auto, EncodingHint.Dictionary];
                EncodingHint best = measured[AdviceChoice.Recommend(measured, 0, goal, rows)].Hint;
                if (!larger.Contains(best))
                {
                    larger.Add(best);
                }

                foreach (int target in LargerTargets)
                {
                    foreach (EncodingHint hint in larger)
                    {
                        (MeasuredCandidate candidate, _) = await TrialAsync(
                            plain, name, hint, target, measuredRows, goal, session, cancellationToken).ConfigureAwait(false);
                        AddDistinct(measured, candidate);
                    }
                }
            }

            columns.Add((name, NameOf(kind), profile, measured));
        }

        // The costs are per row of a scan of the data, which is what the goal's lookups are counted
        // against. Each column takes its own chunk target, which the writer gives it alone.
        ImmutableArray<ColumnEncodingAdvice>.Builder advice = ImmutableArray.CreateBuilder<ColumnEncodingAdvice>(columns.Count);
        foreach ((string name, string kind, ColumnProfile profile, List<MeasuredCandidate> measured) in columns)
        {
            int recommended = AdviceChoice.Recommend(measured, goal, rows);
            ImmutableArray<EncodingCandidate> ranked = AdviceChoice.Rank(measured, recommended, goal, rows);
            advice.Add(new ColumnEncodingAdvice(
                name, profile, ranked, ranked[0], AdviceChoice.Reason(kind, profile, measured, recommended, goal)));
        }

        return new EncodingAdvice(goal, rows, sampled, advice.MoveToImmutable());
    }

    /// <summary>
    /// Up to <paramref name="windows"/> windows of whole blocks spread evenly over the rows, together
    /// at most <paramref name="sampleRows"/>; one window of every row when there are no more than
    /// that, and the first rows when the sample does not hold a block.
    /// </summary>
    internal static RowRange[] WindowsOf(long rows, long sampleRows, int windows = Windows)
    {
        if (rows <= 0)
        {
            return [];
        }

        if (rows <= sampleRows)
        {
            return [new RowRange(0, rows)];
        }

        long blocks = sampleRows / BlockRows;
        if (blocks == 0)
        {
            return [new RowRange(0, sampleRows)];
        }

        int count = (int)Math.Min(windows, blocks);
        long window = blocks / count * BlockRows;
        RowRange[] spread = new RowRange[count];
        long span = rows - window;
        for (int i = 0; i < count; i++)
        {
            long start = count == 1 ? 0 : span * i / (count - 1) / BlockRows * BlockRows;
            spread[i] = new RowRange(start, Math.Min(start + window, rows));
        }

        return spread;
    }

    private static long RowsIn(RowRange[] windows)
    {
        long rows = 0;
        foreach (RowRange window in windows)
        {
            rows += window.Length;
        }

        return rows;
    }

    /// <summary>The rows a chunk of <paramref name="target"/> bytes holds, from the plain sample's bytes a row, in whole blocks.</summary>
    private static long RowsAt(int target, long plainBytes, long sampled)
    {
        double perRow = Math.Max((double)plainBytes / Math.Max(sampled, 1), 1.0 / 8);
        long rows = (long)Math.Ceiling(target / perRow);
        return (rows + BlockRows - 1) / BlockRows * BlockRows;
    }

    /// <summary>The kinds of column the advice measures, and the rest it passes over.</summary>
    private enum ColumnKind : byte
    {
        None,
        Bool,
        Integer,
        Float,
        Text,
    }

    private static ColumnKind KindOf(DType type) => type.Kind switch
    {
        DTypeKind.Bool => ColumnKind.Bool,
        DTypeKind.Primitive when type.PType.IsFloat() => ColumnKind.Float,
        DTypeKind.Primitive when type.PType.IsInteger() => ColumnKind.Integer,
        DTypeKind.Utf8 or DTypeKind.Binary => ColumnKind.Text,
        _ => ColumnKind.None,
    };

    private static string NameOf(ColumnKind kind) => kind switch
    {
        ColumnKind.Bool => "booleans",
        ColumnKind.Integer => "integers",
        ColumnKind.Float => "floating point",
        _ => "text",
    };

    /// <summary>
    /// The hints a column of <paramref name="type"/> is measured under besides the writer's own
    /// choice: pco for numbers of sixteen bits and more, which it stores, and OnPair for UTF-8 text,
    /// which is what its dictionary spells.
    /// </summary>
    private static EncodingHint[] HintsFor(ColumnKind kind, DType type) => kind switch
    {
        ColumnKind.Integer when type.PType.ByteWidth() >= 2 =>
            [EncodingHint.Canonical, EncodingHint.Dictionary, EncodingHint.BitPacked, EncodingHint.Zstd, EncodingHint.Pco],
        ColumnKind.Integer => [EncodingHint.Canonical, EncodingHint.Dictionary, EncodingHint.BitPacked, EncodingHint.Zstd],
        ColumnKind.Float => [EncodingHint.Canonical, EncodingHint.Dictionary, EncodingHint.Alp, EncodingHint.AlpRd, EncodingHint.Zstd, EncodingHint.Pco],
        ColumnKind.Text when type.Kind == DTypeKind.Utf8 =>
            [EncodingHint.Canonical, EncodingHint.Dictionary, EncodingHint.Fsst, EncodingHint.Zstd, EncodingHint.OnPair],
        ColumnKind.Text => [EncodingHint.Canonical, EncodingHint.Dictionary, EncodingHint.Fsst, EncodingHint.Zstd],
        _ => [EncodingHint.Canonical, EncodingHint.RunEnd],
    };

    /// <summary>
    /// The sample written under the writer's own choice and under each hint its kind takes, at the
    /// writer's own chunk size; and the rows of the first chunk the writer's own choice made.
    /// </summary>
    private static async ValueTask<(List<MeasuredCandidate> Measured, int ChunkRows)> CandidatesAsync(
        AlignedBytes plain, long rows, string name, EncodingHint[] hints, EncodingGoal goal, VortexSession session, CancellationToken cancellationToken)
    {
        (MeasuredCandidate auto, int chunkRows) = await TrialAsync(
            plain, name, EncodingHint.Auto, 0, rows, goal, session, cancellationToken).ConfigureAwait(false);
        List<MeasuredCandidate> measured = [auto];
        foreach (EncodingHint hint in hints)
        {
            (MeasuredCandidate candidate, _) = await TrialAsync(plain, name, hint, 0, rows, goal, session, cancellationToken).ConfigureAwait(false);
            AddDistinct(measured, candidate);
        }

        return (measured, chunkRows);
    }

    /// <summary>
    /// Adds a candidate unless another already says what it says: the same bytes written the same
    /// way at the same target; or, for a larger chunk, the encodings a candidate at the writer's own
    /// size wrote, within <see cref="SameBytes"/> of its bytes.
    /// </summary>
    private static void AddDistinct(List<MeasuredCandidate> measured, in MeasuredCandidate candidate)
    {
        foreach (MeasuredCandidate other in measured)
        {
            if (other.ChunkTargetBytes == candidate.ChunkTargetBytes
                && other.BytesPerValue == candidate.BytesPerValue
                && other.WrittenAs.AsSpan().SequenceEqual(candidate.WrittenAs.AsSpan()))
            {
                return;
            }

            if (candidate.ChunkTargetBytes != 0 && other.ChunkTargetBytes == 0
                && Math.Abs(other.BytesPerValue - candidate.BytesPerValue) <= SameBytes * other.BytesPerValue
                && SameEncodings(other.WrittenAs, candidate.WrittenAs))
            {
                return;
            }
        }

        measured.Add(candidate);
    }

    /// <summary>Whether two summaries name the same encodings, whatever the number of chunks each took.</summary>
    private static bool SameEncodings(ImmutableArray<string> a, ImmutableArray<string> b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        foreach (string entry in a)
        {
            bool found = false;
            foreach (string other in b)
            {
                found |= EncodingOf(other).SequenceEqual(EncodingOf(entry));
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static ReadOnlySpan<char> EncodingOf(string entry)
    {
        int count = entry.LastIndexOf(" x", StringComparison.Ordinal);
        return count < 0 ? entry : entry.AsSpan(0, count);
    }

    /// <summary>The column's rows in <paramref name="windows"/>, written in their plain form as a file of that column alone.</summary>
    private static async ValueTask<AlignedBytes> SampleAsync(
        VortexFile source, int field, RowRange[] windows, VortexSession session, CancellationToken cancellationToken)
    {
        using MemoryStream stream = new MemoryStream();
        VortexFileWriter? writer = null;
        try
        {
            FieldMask mask = FieldMask.Single(field);
            foreach (RowRange window in windows)
            {
                await foreach (RecordBatch batch in source.ScanBuilder().ProjectMask(in mask).Rows(window).ExecuteAsync()
                    .WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    writer ??= VortexFileWriter.Create(new StreamSegmentSink(stream), batch.DType, Plain, session);
                    await writer.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
                }
            }

            if (writer is not null)
            {
                await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }

        return AlignedBytes.Of(stream);
    }

    /// <summary>
    /// The sample written under <paramref name="hint"/> at <paramref name="target"/>, measured: its
    /// bytes, what its chunks became, a full scan and single-row reads; and the rows of its first
    /// chunk, which is the writer's own chunk size for the writer's own choice.
    /// </summary>
    private static async ValueTask<(MeasuredCandidate Candidate, int FirstChunkRows)> TrialAsync(
        AlignedBytes plain, string name, EncodingHint hint, int target, long rows, EncodingGoal goal, VortexSession session, CancellationToken cancellationToken)
    {
        (AlignedBytes bytes, WriteReport report) = await WriteAsync(plain, name, hint, target, 0, goal, session, cancellationToken).ConfigureAwait(false);
        VortexFile written = await OpenAsync(bytes, cancellationToken).ConfigureAwait(false);
        await using (written.ConfigureAwait(false))
        {
            double scan = await LeastSecondsAsync(written, null, cancellationToken).ConfigureAwait(false) * 1e9 / Math.Max(rows, 1);
            long[] lookups = LookupRows(report.ChunkRows);
            double lookup = lookups.Length == 0
                ? 0
                : await LeastSecondsAsync(written, lookups, cancellationToken).ConfigureAwait(false) * 1e6 / lookups.Length;
            MeasuredCandidate candidate = new MeasuredCandidate(
                hint,
                target,
                Summarize(report.Columns[0].Encodings),
                (double)report.Bytes.Total / rows,
                scan,
                lookup,
                (double)report.Bytes.Data / Math.Max(report.ChunkRows.Length, 1));
            return (candidate, report.ChunkRows.IsDefaultOrEmpty ? (int)rows : report.ChunkRows[0]);
        }
    }

    /// <summary>
    /// The sample, or its first <paramref name="firstRows"/> rows when that is not 0, written under
    /// <paramref name="hint"/> at <paramref name="target"/> with the profile the goal implies.
    /// </summary>
    private static async ValueTask<(AlignedBytes Bytes, WriteReport Report)> WriteAsync(
        AlignedBytes plain, string name, EncodingHint hint, int target, long firstRows, EncodingGoal goal, VortexSession session, CancellationToken cancellationToken)
    {
        VortexWriteOptions options = VortexWriteOptions.Default with
        {
            Compression = AdviceChoice.ProfileFor(goal.Objective),
            ChunkTargetBytes = target,
        };
        if (hint != EncodingHint.Auto)
        {
            options = options with { Hints = options.Hints.Add(name, hint) };
        }

        using MemoryStream stream = new MemoryStream(plain.Length);
        WriteReport report;
        VortexFile sample = await OpenAsync(plain, cancellationToken).ConfigureAwait(false);
        await using (sample.ConfigureAwait(false))
        {
            ScanBuilder scan = sample.ScanBuilder();
            if (firstRows > 0)
            {
                scan = scan.Rows(new RowRange(0, Math.Min(firstRows, sample.RowCount)));
            }

            VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), sample.DType, options, session);
            await using (writer.ConfigureAwait(false))
            {
                await foreach (RecordBatch batch in scan.ExecuteAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    await writer.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
                }

                report = await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return (AlignedBytes.Of(stream), report);
    }

    /// <summary>
    /// Under the JIT, reads the first rows of the sample written under every hint until the process
    /// compiles nothing more. The runtime compiles a method first without its optimizations, and
    /// recompiles the ones called often once the process has been quiet for a while: a decoder read
    /// before then runs several times slower than it will, and not by the same factor as the next
    /// one, which is enough to swap two candidates.
    /// </summary>
    private static async ValueTask SettleAsync(
        AlignedBytes plain, string name, EncodingHint[] hints, EncodingGoal goal, VortexSession session, CancellationToken cancellationToken)
    {
        // Ahead of time, nothing is ever compiled; the feature switches cannot say so, since a
        // project that publishes ahead of time turns them off for its runs under the JIT too.
        if (JitInfo.GetCompiledMethodCount() == 0)
        {
            return;
        }

        List<(VortexFile File, long[] Lookups)> slices = new List<(VortexFile File, long[] Lookups)>(hints.Length + 1);
        try
        {
            for (int i = -1; i < hints.Length; i++)
            {
                EncodingHint hint = i < 0 ? EncodingHint.Auto : hints[i];
                (AlignedBytes bytes, WriteReport report) = await WriteAsync(plain, name, hint, 0, SettleRows, goal, session, cancellationToken).ConfigureAwait(false);
                slices.Add((await OpenAsync(bytes, cancellationToken).ConfigureAwait(false), LookupRows(report.ChunkRows)));
            }

            long compiled = JitInfo.GetCompiledMethodCount();
            long start = Stopwatch.GetTimestamp();
            long quietSince = start;
            int quietRounds = 0;
            while ((quietRounds < SettleRounds || Stopwatch.GetElapsedTime(quietSince) < SettleQuiet)
                && Stopwatch.GetElapsedTime(start) < SettleLimit)
            {
                foreach ((VortexFile file, long[] lookups) in slices)
                {
                    await ReadAsync(file, null, cancellationToken).ConfigureAwait(false);
                    await ReadAsync(file, lookups, cancellationToken).ConfigureAwait(false);
                }

                long now = JitInfo.GetCompiledMethodCount();
                quietRounds = now == compiled ? quietRounds + 1 : 0;
                if (now != compiled)
                {
                    compiled = now;
                    quietSince = Stopwatch.GetTimestamp();
                }
            }
        }
        finally
        {
            foreach ((VortexFile file, _) in slices)
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A file over <paramref name="bytes"/> in the default session: no cache, one thread, so that
    /// every candidate is read the same way whatever session the advice runs in.
    /// </summary>
    private static ValueTask<VortexFile> OpenAsync(AlignedBytes bytes, CancellationToken cancellationToken) =>
        VortexFile.OpenAsync(new MemorySegmentSource(bytes.Memory), VortexOpenOptions.Default, cancellationToken);

    /// <summary>
    /// The bytes of a file in memory, from a boundary of the widest alignment a segment declares, so
    /// that every open of them is a view: a source made over bytes off that boundary copies them
    /// first, and a sample is opened once for every candidate.
    /// </summary>
    private readonly struct AlignedBytes
    {
        private readonly byte[] _array;
        private readonly int _start;

        private AlignedBytes(byte[] array, int start, int length)
        {
            _array = array;
            _start = start;
            Length = length;
        }

        internal int Length { get; }

        internal ReadOnlyMemory<byte> Memory => _array.AsMemory(_start, Length);

        /// <summary>What <paramref name="stream"/> holds, copied to an aligned place.</summary>
        internal static unsafe AlignedBytes Of(MemoryStream stream)
        {
            int length = (int)stream.Length;
            const int alignment = VortexLimits.MaxAlignment;

            // Pinned, so that the boundary found now is still the boundary on every later read.
            byte[] array = GC.AllocateUninitializedArray<byte>(length + alignment - 1, pinned: true);
            int start;
            fixed (byte* at = array)
            {
                start = (int)((alignment - ((nuint)at & (alignment - 1))) & (alignment - 1));
            }

            stream.GetBuffer().AsSpan(0, length).CopyTo(array.AsSpan(start, length));
            return new AlignedBytes(array, start, length);
        }
    }

    /// <summary>Each encoding the chunks became, with how many did, the most frequent first.</summary>
    internal static ImmutableArray<string> Summarize(ImmutableArray<string> encodings)
    {
        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string encoding in encodings)
        {
            counts[encoding] = counts.TryGetValue(encoding, out int count) ? count + 1 : 1;
        }

        List<KeyValuePair<string, int>> ordered = [.. counts];
        ordered.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
        ImmutableArray<string>.Builder summary = ImmutableArray.CreateBuilder<string>(ordered.Count);
        foreach (KeyValuePair<string, int> pair in ordered)
        {
            summary.Add(string.Create(CultureInfo.InvariantCulture, $"{pair.Key} x{pair.Value}"));
        }

        return summary.MoveToImmutable();
    }

    /// <summary>
    /// The least time of a read's passes, in seconds: one pass to warm it, then at least
    /// <see cref="Passes"/> and as many more as fill <see cref="MeasureAtLeast"/>, up to <see cref="MaxPasses"/>.
    /// </summary>
    private static async ValueTask<double> LeastSecondsAsync(VortexFile file, long[]? take, CancellationToken cancellationToken)
    {
        await ReadAsync(file, take, cancellationToken).ConfigureAwait(false);
        long enough = (long)(MeasureAtLeast.TotalSeconds * Stopwatch.Frequency);
        long least = long.MaxValue;
        long spent = 0;
        for (int pass = 0; pass < MaxPasses && (pass < Passes || spent < enough); pass++)
        {
            long start = Stopwatch.GetTimestamp();
            await ReadAsync(file, take, cancellationToken).ConfigureAwait(false);
            long elapsed = Stopwatch.GetTimestamp() - start;
            spent += elapsed;
            least = Math.Min(least, elapsed);
        }

        return (double)least / Stopwatch.Frequency;
    }

    /// <summary>Reads the file whole, or the rows of <paramref name="take"/>, every value once.</summary>
    private static async ValueTask<long> ReadAsync(VortexFile file, long[]? take, CancellationToken cancellationToken)
    {
        ScanBuilder scan = file.ScanBuilder();
        if (take is not null)
        {
            if (take.Length == 0)
            {
                return 0;
            }

            scan = scan.Take(take);
        }

        long sum = 0;
        await foreach (RecordBatch batch in scan.ExecuteAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            sum += Touch(batch);
        }

        return sum;
    }

    /// <summary>The middle row of each of up to <see cref="LookupChunks"/> chunks, spread evenly, ascending.</summary>
    internal static long[] LookupRows(ImmutableArray<int> chunkRows)
    {
        if (chunkRows.IsDefaultOrEmpty)
        {
            return [];
        }

        int chunks = chunkRows.Length;
        int stride = Math.Max(1, (chunks + LookupChunks - 1) / LookupChunks);
        List<long> rows = [];
        long start = 0;
        for (int chunk = 0; chunk < chunks; chunk++)
        {
            if (chunk % stride == 0 && chunkRows[chunk] > 0)
            {
                rows.Add(start + (chunkRows[chunk] / 2));
            }

            start += chunkRows[chunk];
        }

        return [.. rows];
    }

    /// <summary>Reads every byte of the batch's column once, as a consumer of its values does.</summary>
    private static long Touch(RecordBatch batch)
    {
        CanonicalArena arena = batch.Arena;
        CanonicalNode root = arena.GetNode(batch.RootIndex);
        CanonicalNode node = arena.GetNode(root.Kind == CanonicalKind.Struct ? root.GetFieldIndex(0) : batch.RootIndex);
        long sum = node.Kind switch
        {
            CanonicalKind.Primitive => Sum(node.Values.Span),
            CanonicalKind.Bool => Sum(node.Bits.Span),
            CanonicalKind.VarBinView => SumText(node),
            _ => node.Length,
        };

        if (node.Validity.Kind == ValidityKind.Bitmap)
        {
            sum += Sum(arena.GetNode(node.Validity.CanonicalNodeIndex).Bits.Span);
        }

        return sum;
    }

    private static long SumText(CanonicalNode node)
    {
        long sum = Sum(node.Views.Span);
        for (int i = 0; i < node.DataBufferCount; i++)
        {
            sum += Sum(node.GetDataBuffer(i).Span);
        }

        return sum;
    }

    private static long Sum(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<ulong> words = MemoryMarshal.Cast<byte, ulong>(bytes);
        Vector<ulong> total = Vector<ulong>.Zero;
        int i = 0;
        for (; i <= words.Length - Vector<ulong>.Count; i += Vector<ulong>.Count)
        {
            total += new Vector<ulong>(words[i..]);
        }

        ulong sum = Vector.Sum(total);
        for (; i < words.Length; i++)
        {
            sum += words[i];
        }

        for (int b = words.Length * sizeof(ulong); b < bytes.Length; b++)
        {
            sum += bytes[b];
        }

        return (long)sum;
    }

    /// <summary>What the sample holds: its nulls, its distinct values overall and per chunk of <paramref name="chunkRows"/> rows, its runs, its order, its lengths.</summary>
    private static async ValueTask<ColumnProfile> ProfileAsync(AlignedBytes plain, ColumnKind kind, int chunkRows, CancellationToken cancellationToken)
    {
        ProfileState state = new ProfileState(kind, Math.Max(chunkRows, 1));
        VortexFile file = await OpenAsync(plain, cancellationToken).ConfigureAwait(false);
        await using (file.ConfigureAwait(false))
        {
            await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                state.Add(batch);
            }
        }

        return state.Finish();
    }

    /// <summary>Where the advice takes a column's rows from.</summary>
    private abstract class Source : IAsyncDisposable
    {
        internal abstract long Rows { get; }

        /// <summary>The data's root, a struct whose fields are the columns.</summary>
        internal abstract DType DType { get; }

        /// <summary>The rows of column <paramref name="field"/> in <paramref name="windows"/>, written in their plain form as a file of that column alone.</summary>
        internal abstract ValueTask<AlignedBytes> ColumnAsync(int field, RowRange[] windows, VortexSession session, CancellationToken cancellationToken);

        public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FileSource(VortexFile file) : Source
    {
        internal override long Rows => file.RowCount;

        internal override DType DType => file.DType;

        internal override ValueTask<AlignedBytes> ColumnAsync(int field, RowRange[] windows, VortexSession session, CancellationToken cancellationToken) =>
            SampleAsync(file, field, windows, session, cancellationToken);
    }

    /// <summary>
    /// Rows in memory: the rows of a set of windows are written into memory, plain and whole, once
    /// for every column that asks for the same windows.
    /// </summary>
    private sealed class RecordSource<TRecord>(ReadOnlyMemory<TRecord> rows) : Source
        where TRecord : IVortexRecord<TRecord>
    {
        private VortexFile? _written;
        private RowRange[] _windows = [];

        internal override long Rows => rows.Length;

        internal override DType DType { get; } = VortexTypes.ToDType(TRecord.Schema, new DTypeArena());

        internal override async ValueTask<AlignedBytes> ColumnAsync(int field, RowRange[] windows, VortexSession session, CancellationToken cancellationToken)
        {
            if (_written is null || !windows.AsSpan().SequenceEqual(_windows))
            {
                VortexFile? previous = _written;
                _written = null;
                if (previous is not null)
                {
                    await previous.DisposeAsync().ConfigureAwait(false);
                }

                _written = await WriteAsync(windows, session, cancellationToken).ConfigureAwait(false);
                _windows = windows;
            }

            return await SampleAsync(_written, field, [new RowRange(0, _written.RowCount)], session, cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_written is not null)
            {
                await _written.DisposeAsync().ConfigureAwait(false);
                _written = null;
            }
        }

        private async ValueTask<VortexFile> WriteAsync(RowRange[] windows, VortexSession session, CancellationToken cancellationToken)
        {
            using MemoryStream stream = new MemoryStream();
            VortexFileWriter writer = session.CreateWriter(
                PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true)), TRecord.Schema, Plain);
            await using (writer.ConfigureAwait(false))
            {
                foreach (RowRange window in windows)
                {
                    await writer.WriteAsync<TRecord>(rows.Span.Slice((int)window.Start, (int)window.Length), cancellationToken).ConfigureAwait(false);
                }

                await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }

            return await OpenAsync(AlignedBytes.Of(stream), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The profile's counters, carried from one batch to the next.</summary>
    private sealed class ProfileState(ColumnKind kind, int chunkRows)
    {
        private readonly HashSet<ulong> _distinct = [];
        private readonly HashSet<ulong> _window = [];
        private long _rows;
        private long _nulls;
        private long _changes;
        private long _windowRows;
        private double _windowRatios;
        private int _windows;
        private long _bytes;
        private ulong _previous;
        private bool _previousNull;
        private bool _started;
        private bool _ascending = kind is ColumnKind.Integer or ColumnKind.Float;
        private ulong _lastValue;
        private bool _anyValue;

        internal void Add(RecordBatch batch)
        {
            CanonicalArena arena = batch.Arena;
            CanonicalNode root = arena.GetNode(batch.RootIndex);
            CanonicalNode node = arena.GetNode(root.Kind == CanonicalKind.Struct ? root.GetFieldIndex(0) : batch.RootIndex);
            ValidityReader validity = ValidityReader.Of(arena, node.Validity);
            int length = node.Length;
            ViewValues texts = kind == ColumnKind.Text ? new ViewValues(node) : default;
            ReadOnlySpan<byte> values = node.Kind == CanonicalKind.Primitive ? node.Values.Span : default;
            ReadOnlySpan<byte> bits = node.Kind == CanonicalKind.Bool ? node.Bits.Span : default;
            int bitOffset = node.Kind == CanonicalKind.Bool ? node.BitOffset : 0;
            PType ptype = node.Kind == CanonicalKind.Primitive ? node.PType : default;

            for (int row = 0; row < length; row++)
            {
                bool valid = validity.IsValid(row);
                ulong key = 0;
                if (valid)
                {
                    if (kind == ColumnKind.Text)
                    {
                        ReadOnlySpan<byte> text = texts.At(row);
                        key = XxHash3.HashToUInt64(text);
                        _bytes += text.Length;
                    }
                    else
                    {
                        key = kind == ColumnKind.Bool
                            ? (ulong)((bits[(bitOffset + row) >> 3] >> ((bitOffset + row) & 7)) & 1)
                            : Ordered(values, ptype, row);
                    }

                    _distinct.Add(key);
                    _window.Add(key);
                    if (_anyValue && key < _lastValue)
                    {
                        _ascending = false;
                    }

                    _lastValue = key;
                    _anyValue = true;
                }
                else
                {
                    _nulls++;
                }

                if (_started && (valid == _previousNull || key != _previous))
                {
                    _changes++;
                }

                _previous = key;
                _previousNull = !valid;
                _started = true;
                _rows++;

                if (++_windowRows == chunkRows)
                {
                    CloseWindow();
                }
            }
        }

        internal ColumnProfile Finish()
        {
            if (_windows == 0 && _windowRows > 0)
            {
                CloseWindow();
            }

            long valid = _rows - _nulls;
            return new ColumnProfile(
                _rows,
                _nulls,
                _distinct.Count,
                _windows == 0 ? 0 : _windowRatios / _windows,
                _rows == 0 ? 0 : (double)_rows / (_changes + 1),
                _ascending && _anyValue,
                kind == ColumnKind.Text && valid > 0 ? (double)_bytes / valid : 0);
        }

        private void CloseWindow()
        {
            _windowRatios += (double)_windowRows / Math.Max(_window.Count, 1);
            _windows++;
            _window.Clear();
            _windowRows = 0;
        }

        /// <summary>
        /// A value as an unsigned key in its own order: a signed integer with its sign bit flipped,
        /// a float's bits in the total order, an unsigned integer as it is.
        /// </summary>
        private static ulong Ordered(ReadOnlySpan<byte> values, PType ptype, int row)
        {
            switch (ptype)
            {
                case PType.I8: return (ulong)(sbyte)values[row] ^ 0x8000_0000_0000_0000UL;
                case PType.I16: return (ulong)MemoryMarshal.Read<short>(values[(row * 2)..]) ^ 0x8000_0000_0000_0000UL;
                case PType.I32: return (ulong)MemoryMarshal.Read<int>(values[(row * 4)..]) ^ 0x8000_0000_0000_0000UL;
                case PType.I64: return (ulong)MemoryMarshal.Read<long>(values[(row * 8)..]) ^ 0x8000_0000_0000_0000UL;
                case PType.U8: return values[row];
                case PType.U16: return MemoryMarshal.Read<ushort>(values[(row * 2)..]);
                case PType.U32: return MemoryMarshal.Read<uint>(values[(row * 4)..]);
                case PType.U64: return MemoryMarshal.Read<ulong>(values[(row * 8)..]);
                case PType.F32:
                {
                    uint bits = MemoryMarshal.Read<uint>(values[(row * 4)..]);
                    return (bits & 0x8000_0000u) != 0 ? ~bits : bits | 0x8000_0000u;
                }

                case PType.F64:
                {
                    ulong bits = MemoryMarshal.Read<ulong>(values[(row * 8)..]);
                    return (bits & 0x8000_0000_0000_0000UL) != 0 ? ~bits : bits | 0x8000_0000_0000_0000UL;
                }

                default:
                {
                    // A half float: its sixteen bits in the same total order.
                    ushort bits = MemoryMarshal.Read<ushort>(values[(row * 2)..]);
                    return (bits & 0x8000) != 0 ? (ushort)~bits : (ulong)(bits | 0x8000);
                }
            }
        }
    }
}
