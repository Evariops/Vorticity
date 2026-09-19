// The Rust reader, in this process.
//
// docs/05-benchmarks.md §2: both implementations measured on the same bytes, with the same clock
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
    /// The .NET reader has no lazy state: <c>CanonicalArena</c> is the only representation it has,
    /// so a batch is decompressed by the time its row count exists. A ratio built on
    /// <see cref="ScanAll"/> therefore compares a scan that decompresses against one that does not,
    /// on every compressed encoding -- which on the 1M-row axis was most of what the per-encoding
    /// ratios were measuring.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "vxbench_scan_canonical", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long ScanCanonical(string path);

    /// <summary>Reads a file and writes it back out with the reference's default strategy.</summary>
    /// <param name="path">The file to round-trip, as a UTF-8 C string.</param>
    /// <returns>The row count, or negative on failure.</returns>
    /// <remarks>
    /// The write axis had no reference at all: docs/05 compares reading on five axes and writing on
    /// none, so every write-side change in this repository was measured against its own past rather
    /// than against the implementation it is a port of. The READ is inside the measurement on both
    /// sides -- same file, same reader -- so it is common-mode, and
    /// <see cref="ScanCanonical"/> is the number to subtract when it is a large share.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "vxbench_write", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long Write(string path);

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
    /// .NET side uses exactly this shape. docs/05's take figure was "0.32x of a full scan", a ratio against
    /// ourselves that says nothing about whether the path is fast.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "vxbench_take", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long Take(string path, long count, long stride);

    /// <summary>
    /// Scans <paramref name="path"/> and folds every decoded value into one 64-bit checksum.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The checksum, or a negative error code.</returns>
    /// <remarks>
    /// The precondition `--ffi-check` never had (BENCH-AUDIT.md A3): a row count is not evidence of
    /// a decode. `Checksum.cs` computes the same number on our side, byte for byte by the same
    /// encoding, and the two must agree.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "vxbench_scan_checksum", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long ScanChecksum(string path);

    /// <summary>
    /// Scans <paramref name="path"/> canonically on the reference's worker pool with exactly
    /// <paramref name="threads"/> workers.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="threads">Worker threads; must be positive.</param>
    /// <returns>Rows, or a negative error code.</returns>
    /// <remarks>
    /// The thread count is pinned on both sides (docs/05 §5): a ratio between an n-lane reader and
    /// a reference free to use every core measures a threading model, not a decoder. At 1 this is
    /// NOT <see cref="ScanCanonical"/> -- it still pays the pool hand-off, which is the point.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "vxbench_scan_canonical_threads", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long ScanCanonicalThreads(string path, long threads);

    /// <summary>Opens a file and scans one field of every batch.</summary>
    /// <param name="path">The file to scan, as a UTF-8 C string.</param>
    /// <param name="field">The root field to project.</param>
    /// <returns>The row count, or negative on failure.</returns>
    [LibraryImport(Library, EntryPoint = "vxbench_scan_projected", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long ScanProjected(string path, string field);

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
    /// It is not a stable denominator. `tools/vxbench-rs` builds with `lto = true` and
    /// `codegen-units = 1`, so adding a function no reader calls still rebuilds the crate and can
    /// move the inlining inside one that readers do call. An additive change to `lib.rs` took the
    /// reference's `dict_u64_codes` scan from 804 to 592 us with our own side unchanged, which is
    /// a 29 % move in every ratio over that file and looks exactly like a regression.
    ///
    /// Hashing the artefact rather than its sources, because the artefact is what ran. A release
    /// build of unchanged sources is bit-identical on this toolchain, so this does not fire on a
    /// rebuild that changed nothing.
    /// </remarks>
    internal static string? Fingerprint => _fingerprint ??= ComputeFingerprint();

    private static string? _fingerprint;

    private static string? ComputeFingerprint()
    {
        string? path = Locate();
        if (path is null)
        {
            return null;
        }

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
