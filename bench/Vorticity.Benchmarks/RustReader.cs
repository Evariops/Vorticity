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

    /// <summary>Opens a file and scans every column of every batch.</summary>
    /// <param name="path">The file to scan, as a UTF-8 C string.</param>
    /// <returns>The row count, or negative on failure.</returns>
    [LibraryImport(Library, EntryPoint = "vxbench_scan_all", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long ScanAll(string path);

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
