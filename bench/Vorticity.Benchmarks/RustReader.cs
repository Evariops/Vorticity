// The Rust reader, in this process.
//
// Both implementations are measured on the same bytes, with the same clock
// and the same page-cache state, because cross-process comparison is exactly the noise that makes
// a 1.4x ratio unreadable. The native side is tools/vxbench-rs, a cdylib over the same
// `vortex = "=0.86.1"` pin the corpus was generated with.
//
// THE LIBRARY IS OPTIONAL. It is ~36 MB of Rust that has to be built by hand, so its absence is a
// first-class state rather than a crash: `Available` is false, the comparison benchmarks skip, and
// the absolute-figure benchmarks run as they always did. What must NOT happen is a ratio quietly
// reported against nothing.
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Vorticity.Benchmarks;

/// <summary>P/Invoke into the Vortex Rust reader.</summary>
internal static partial class RustReader
{
    /// <summary>The cdylib's base name; the loader adds the platform's prefix and extension.</summary>
    private const string Library = "vxbench";

    private static readonly Lazy<bool> AvailableLazy = new Lazy<bool>(Probe);

    /// <summary>Whether the native library loaded and answered.</summary>
    internal static bool Available => AvailableLazy.Value;

    /// <summary>The empty call: the FFI floor, so it can be subtracted where it matters.</summary>
    /// <returns>Zero.</returns>
    [LibraryImport(Library, EntryPoint = "vxbench_noop")]
    internal static partial long NoOp();

    /// <summary>Upstream's default split: a chunk of more than 100 000 rows cut into splits of 100 000.</summary>
    internal const long SplitDefault = 0;

    /// <summary>One split per chunk of the file.</summary>
    internal const long SplitPerChunk = 1;

    /// <summary>Sets how every later scan of the reference splits its file.</summary>
    /// <param name="mode"><see cref="SplitDefault"/> or <see cref="SplitPerChunk"/>.</param>
    /// <returns>Zero, or negative for an unknown mode.</returns>
    /// <remarks>
    /// Neither is the reference's faster on every file: under the default its flat reader decodes a
    /// chunk whole again for each split it is cut into, ten times on the corpus's chunks of a million
    /// rows; one split per chunk decodes it once but materializes a chunk of smaller arrays in one
    /// piece. On one core the harnesses time both and keep the faster, file by file
    /// (<see cref="FasterSplit"/>); `vxbench_set_split` in `tools/vxbench-rs` has the measurements.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "vxbench_set_split")]
    internal static partial long SetSplit(long mode);

    /// <summary>
    /// Times <paramref name="call"/> under both of the reference's splits, sets the faster, and
    /// returns it.
    /// </summary>
    /// <param name="call">The reference's side of an axis, on its file.</param>
    /// <param name="rounds">Timed calls per split, alternating, after one of each to warm them.</param>
    /// <returns>The split now set: the one whose median call took less.</returns>
    /// <remarks>
    /// A figure for the reference that one of its own settings beats is not its figure, and which
    /// one wins depends on the file, so the choice is measured on the spot rather than written down.
    /// The calls alternate so that drift falls on both.
    /// </remarks>
    internal static long FasterSplit(Func<long> call, int rounds)
    {
        ArgumentNullException.ThrowIfNull(call);
        double[][] times = [new double[rounds], new double[rounds]];
        for (int round = -1; round < rounds; round++)
        {
            for (int mode = 0; mode < 2; mode++)
            {
                Require(SetSplit(mode), "split");
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                call();
                double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                if (round >= 0)
                {
                    times[mode][round] = elapsed;
                }
            }
        }

        Array.Sort(times[0]);
        Array.Sort(times[1]);
        long faster = times[SplitPerChunk][rounds / 2] < times[SplitDefault][rounds / 2] ? SplitPerChunk : SplitDefault;
        Require(SetSplit(faster), "split");
        return faster;
    }

    /// <summary>
    /// Opens a file and walks every batch of every column, WITHOUT decompressing.
    /// </summary>
    /// <param name="path">The file to scan, as a UTF-8 C string.</param>
    /// <returns>The row count, or negative on failure.</returns>
    /// <remarks>
    /// Upstream's scan hands back arrays in the file's own encodings -- an FsstArray, a DictArray,
    /// a RunEndArray -- and <c>len()</c> answers from their metadata. So this measures layout
    /// reading and array deserialization, and NOT the decoders. Use <see cref="ScanCanonical"/> for
    /// any ratio against a .NET scan, whose <c>RecordBatch</c> is canonical by construction.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "vxbench_scan_all", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long ScanAll(string path);

    /// <summary>
    /// Opens a file, walks every batch AND canonicalizes it: the like-for-like counterpart of
    /// <see cref="ScanAll"/>.
    /// </summary>
    /// <param name="path">The file to scan, as a UTF-8 C string.</param>
    /// <returns>The row count, or negative on failure.</returns>
    /// <remarks>
    /// The scan this bench runs on our side decodes every column by the time its row count exists,
    /// with one exception: a constant column stays one value and a row count. The reference takes
    /// the same form, upstream's `Columnar` (`plain` in `tools/vxbench-rs/src/lib.rs`): it used to
    /// expand every constant through `RecursiveCanonical`, which our side never does. A ratio built
    /// on <see cref="ScanAll"/> compares a scan that decompresses against one that does not, on
    /// every compressed encoding -- which on the 1M-row axis was most of what the per-encoding
    /// ratios were measuring.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "vxbench_scan_canonical", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long ScanCanonical(string path);

    /// <summary>Reads a file and writes it back out with the reference's default strategy.</summary>
    /// <param name="path">The file to round-trip, as a UTF-8 C string.</param>
    /// <returns>The row count, or negative on failure.</returns>
    /// <remarks>
    /// The write axis had no reference at all: reading was compared on five axes and writing on
    /// none, so every write-side change in this repository was measured against its own past rather
    /// than against the implementation it is a port of. The READ is inside the measurement on both
    /// sides -- same file, same reader -- so it is common-mode, and
    /// <see cref="ScanCanonical"/> is the number to subtract when it is a large share.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "vxbench_write", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long Write(string path);

    /// <summary>The bytes <see cref="Write"/> writes for <paramref name="path"/>.</summary>
    /// <param name="path">The file to round-trip, as a UTF-8 C string.</param>
    /// <returns>The bytes the reference's writer produced, or negative on failure.</returns>
    /// <remarks>
    /// Not a timing axis: what a write's time bought. A writer can be fast by compressing less, and
    /// a ratio of times read alone would not say so.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "vxbench_write_bytes", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long WriteBytes(string path);

    /// <summary>Scans under <c>field &gt;= lo AND field &lt; lo + width</c>, canonicalizing.</summary>
    /// <param name="path">The file to scan, as a UTF-8 C string.</param>
    /// <param name="field">The root field the band applies to; must be an i64.</param>
    /// <param name="lo">The band's inclusive lower bound.</param>
    /// <param name="width">The band's width; the upper bound is exclusive.</param>
    /// <returns>The surviving row count, or negative on failure.</returns>
    /// <remarks>
    /// A BAND rather than one comparison, because that is what `FilterSelectivityBenchmarks` uses
    /// and what a zone map can actually prune. The filter path had no reference at all: the 6.0x
    /// the comparison kernel was worth was measured against our own past, and 230 microseconds for
    /// a 1% band was a number with nothing to compare it to.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "vxbench_scan_filtered", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long ScanFiltered(string path, string field, long lo, long width);

    /// <summary>Counts the rows under <c>field &gt;= lo AND field &lt; lo + width</c>, decoding no column it does not need.</summary>
    /// <param name="path">The file to count, as a UTF-8 C string.</param>
    /// <param name="field">The root field the band applies to; must be an i64.</param>
    /// <param name="lo">The band's inclusive lower bound.</param>
    /// <param name="width">The band's width; the upper bound is exclusive.</param>
    /// <returns>The rows in the band, or negative on failure.</returns>
    /// <remarks>
    /// The counterpart of our `CountAsync`: a projection of no column, so the reference reads the
    /// predicate's column and hands back lengths. <see cref="ScanFiltered"/> decodes every column of
    /// every row kept, which a count does not ask for; the count axis compared the two before.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "vxbench_count_filtered", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long CountFiltered(string path, string field, long lo, long width);

    /// <summary>Scans under <c>field = value</c> on a text column, canonicalizing.</summary>
    /// <param name="path">The file to scan, as a UTF-8 C string.</param>
    /// <param name="field">The root field the equality applies to; must be a string.</param>
    /// <param name="value">The needle.</param>
    /// <returns>The surviving row count, or negative on failure.</returns>
    /// <remarks>
    /// The band filter is an i64 interval, so until this pair arrived no axis asked a text column
    /// anything -- and a text column is where the remaining read time is. Equality rather than an
    /// interval because equality is what a compressed text encoding can answer cheaply: the k
    /// values of a dictionary, or a needle compressed with the column's own symbol table.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "vxbench_scan_filtered_eq_utf8", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long ScanFilteredEqUtf8(string path, string field, string value);

    /// <summary>Scans keeping the rows of <paramref name="field"/> that start with a prefix.</summary>
    /// <param name="path">The file to scan, as a UTF-8 C string.</param>
    /// <param name="field">The root field the prefix applies to; must be a string.</param>
    /// <param name="prefix">The prefix; must contain neither <c>%</c> nor <c>_</c>.</param>
    /// <returns>The surviving row count, or negative on failure.</returns>
    /// <remarks>
    /// The reference expresses this as SQL LIKE with a trailing wildcard and we express it as
    /// `StartsWith`; each side writes what its caller would write, and the harness holds both to
    /// the same surviving row count before timing either. The two LIKE wildcards are rejected
    /// rather than escaped: a prefix carrying one would make the two sides ask different questions,
    /// and no axis needs one.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "vxbench_scan_filtered_prefix_utf8", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long ScanFilteredPrefixUtf8(string path, string field, string prefix);

    /// <summary>Takes <paramref name="count"/> rows, one every <paramref name="stride"/>.</summary>
    /// <param name="path">The file to take from, as a UTF-8 C string.</param>
    /// <param name="count">How many rows to ask for.</param>
    /// <param name="stride">The gap between them; row i is <c>i * stride + stride / 2</c>.</param>
    /// <returns>The row count actually produced, or negative on failure.</returns>
    /// <remarks>
    /// A STRIDE rather than a list, so the same call describes a scattered take of any density
    /// without marshalling an array across the ABI -- and the take axis of `--ratio-check` on the
    /// .NET side uses exactly this shape. A take figure like "0.32x of a full scan" is a ratio
    /// against ourselves that says nothing about whether the path is fast.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "vxbench_take", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long Take(string path, long count, long stride);

    /// <summary>
    /// Scans <paramref name="path"/> and folds every decoded value into one 64-bit checksum.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The checksum, or a negative error code.</returns>
    /// <remarks>
    /// The precondition `--ffi-check` never had: a row count is not evidence of
    /// a decode. `Checksum.cs` computes the same number on our side, byte for byte by the same
    /// encoding, and the two must agree.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "vxbench_scan_checksum", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long ScanChecksum(string path);

    /// <summary>
    /// Scans <paramref name="path"/> canonically on a multi-threaded Tokio runtime of exactly
    /// <paramref name="threads"/> workers, each split decoded on its own task.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="threads">Worker threads; must be positive.</param>
    /// <returns>Rows, or a negative error code.</returns>
    /// <remarks>
    /// The thread count is pinned on both sides: a ratio between an n-lane reader and a reference
    /// free to use every core measures a threading model, not a decoder. At 1 this is not
    /// <see cref="ScanCanonical"/>: it still hands the work to a worker.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "vxbench_scan_canonical_threads", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long ScanCanonicalThreads(string path, long threads);

    /// <summary>Opens a file and scans one field of every batch, without decoding it.</summary>
    /// <param name="path">The file to scan, as a UTF-8 C string.</param>
    /// <param name="field">The root field to project.</param>
    /// <returns>The row count, or negative on failure.</returns>
    /// <remarks>
    /// The projected twin of <see cref="ScanAll"/>: the row count comes off the arrays' metadata
    /// and the column stays in its file encoding. It answers "how long to get a stream of arrays
    /// you may never fully read", which is a real question about a real API and not the question a
    /// decoder ratio is built on -- that one is <see cref="ScanProjectedCanonical"/>.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "vxbench_scan_projected", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long ScanProjected(string path, string field);

    /// <summary>Opens a file, scans one field of every batch and canonicalizes it.</summary>
    /// <param name="path">The file to scan, as a UTF-8 C string.</param>
    /// <param name="field">The root field to project.</param>
    /// <returns>The row count, or negative on failure.</returns>
    /// <remarks>
    /// The like-for-like projected scan, for the same reason <see cref="ScanCanonical"/> is the
    /// like-for-like full scan: our reader materializes every column it delivers, so the reference
    /// has to materialize the one it was asked for or the ratio measures decoding against not
    /// decoding.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "vxbench_scan_projected_canonical", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long ScanProjectedCanonical(string path, string field);

    /// <summary>Opens a file and reads one batch.</summary>
    /// <param name="path">The file to open, as a UTF-8 C string.</param>
    /// <returns>The first batch's row count, or negative on failure.</returns>
    [LibraryImport(Library, EntryPoint = "vxbench_open_first_batch", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long OpenFirstBatch(string path);

    /// <summary>Opens a file and counts the batches a full scan produces.</summary>
    /// <param name="path">The file to scan, as a UTF-8 C string.</param>
    /// <returns>The batch count, or negative on failure.</returns>
    [LibraryImport(Library, EntryPoint = "vxbench_batch_count", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long BatchCount(string path);

    /// <summary>Opens a file and reads its row count from the footer.</summary>
    /// <param name="path">The file to open, as a UTF-8 C string.</param>
    /// <returns>The row count, or negative on failure.</returns>
    [LibraryImport(Library, EntryPoint = "vxbench_open_only", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long OpenOnly(string path);

    /// <summary>Turns a negative return into an exception naming what the native side reported.</summary>
    /// <param name="result">A value returned by one of the entry points.</param>
    /// <param name="what">The operation, for the message.</param>
    /// <returns><paramref name="result"/> when it is not negative.</returns>
    /// <exception cref="InvalidOperationException">The native side reported a failure.</exception>
    internal static long Require(long result, string what) => result switch
    {
        >= 0 => result,
        -1 => throw new InvalidOperationException($"{what}: the native side rejected the path."),
        -2 => throw new InvalidOperationException($"{what}: the Rust reader returned an error."),
        -3 => throw new InvalidOperationException($"{what}: the Rust reader panicked."),
        _ => throw new InvalidOperationException($"{what}: unknown native status {result}."),
    };

    /// <summary>Where the cdylib is expected, for the message when it is missing.</summary>
    internal static string ExpectedPath => Locate() ?? RelativePath();

    /// <summary>The first twelve hex digits of the loaded cdylib's SHA-256, or null if absent.</summary>
    /// <remarks>
    /// Every ratio in the repository has this binary as its denominator, and the corpus manifest
    /// does not describe it: it dates the generator and the `vortex` pin, which say what the files
    /// are, not what they are measured against.
    ///
    /// It is not a stable denominator. `tools/vxbench-rs` is one codegen unit, so adding a function
    /// no reader calls still rebuilds the crate and can move the inlining inside one that readers
    /// do call, which reads exactly like a regression of ours.
    ///
    /// Hashing the artefact rather than its sources, because the artefact is what ran. A release
    /// build of unchanged sources is bit-identical on this toolchain, so this does not fire on a
    /// rebuild that changed nothing.
    /// </remarks>
    internal static string? Fingerprint => _fingerprint ??= ComputeFingerprint();

    private static string? _fingerprint;

    private static string? ComputeFingerprint() => Locate() is { } path ? FingerprintOf(path) : null;

    /// <summary>The first twelve hex digits of the SHA-256 of the binary at <paramref name="path"/>, or null.</summary>
    internal static string? FingerprintOf(string path)
    {
        try
        {
            using FileStream file = System.IO.File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(file))[..12];
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Teaches the loader where the cdylib lives, rather than copying 36 MB into every build.
    /// </summary>
    /// <remarks>
    /// BenchmarkDotNet runs its generated harness from a directory of its own choosing, so neither
    /// the working directory nor the assembly directory is a reliable anchor - the resolver walks
    /// up from the assembly the way <see cref="Corpus"/> does. <c>VORTICITY_VXBENCH</c> overrides
    /// it outright, which is what a CI job with a prebuilt artifact wants.
    /// </remarks>
    [ModuleInitializer]
    internal static void RegisterResolver() =>
        NativeLibrary.SetDllImportResolver(
            typeof(RustReader).Assembly,
            (name, assembly, path) =>
            {
                if (!string.Equals(name, Library, StringComparison.Ordinal))
                {
                    return IntPtr.Zero;
                }

                string? found = Locate();
                return found is not null && NativeLibrary.TryLoad(found, out IntPtr handle)
                    ? handle
                    : IntPtr.Zero;
            });

    private static string RelativePath() =>
        Path.Combine("tools", "vxbench-rs", "target", "release", FileName());

    private static string? Locate()
    {
        string? configured = Environment.GetEnvironmentVariable("VORTICITY_VXBENCH");
        if (!string.IsNullOrEmpty(configured))
        {
            return configured;
        }

        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, RelativePath());
            if (System.IO.File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string FileName()
    {
        if (OperatingSystem.IsWindows())
        {
            return $"{Library}.dll";
        }

        return OperatingSystem.IsMacOS() ? $"lib{Library}.dylib" : $"lib{Library}.so";
    }

    private static bool Probe()
    {
        try
        {
            return NoOp() == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            // A stale build of the library: worth distinguishing from absence, because it is the
            // one failure a developer will otherwise read as "the harness is broken".
            Console.Error.WriteLine(
                $"{Library} loaded but is missing an entry point; rebuild {ExpectedPath}.");
            return false;
        }
    }
}
