using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Vorticity.Zstd.Bench;

/// <summary>
/// libzstd 1.5.7 built with today's compiler (tools/native-ref/build.sh), called through function
/// pointers: the speed a well-compiled C decoder reaches, measured in the same process as Vorticity.Zstd.
/// Native code belongs here, in the measurement, and never in the library.
/// </summary>
internal sealed unsafe class NativeReference
{
    private readonly delegate* unmanaged<nint, byte*, nuint, byte*, nuint, nuint> _decompress;
    private readonly delegate* unmanaged<nint, byte*, nuint, byte*, nuint, int, nuint> _compress;
    private readonly delegate* unmanaged<nuint, uint> _isError;
    private readonly delegate* unmanaged<nint, int, int, nuint> _setParameter;
    private readonly delegate* unmanaged<nint, void*, nuint, byte*, nuint, nuint> _generateSequences;
    private readonly nint _context;
    private readonly nint _compressionContext;
    private readonly nint _sequencesContext;

    private NativeReference(nint library)
    {
        var create = (delegate* unmanaged<nint>)NativeLibrary.GetExport(library, "ZSTD_createDCtx");
        var createCompression = (delegate* unmanaged<nint>)NativeLibrary.GetExport(library, "ZSTD_createCCtx");
        _decompress = (delegate* unmanaged<nint, byte*, nuint, byte*, nuint, nuint>)NativeLibrary.GetExport(library, "ZSTD_decompressDCtx");
        _compress = (delegate* unmanaged<nint, byte*, nuint, byte*, nuint, int, nuint>)NativeLibrary.GetExport(library, "ZSTD_compressCCtx");
        _isError = (delegate* unmanaged<nuint, uint>)NativeLibrary.GetExport(library, "ZSTD_isError");
        _setParameter = (delegate* unmanaged<nint, int, int, nuint>)NativeLibrary.GetExport(library, "ZSTD_CCtx_setParameter");
        _generateSequences = (delegate* unmanaged<nint, void*, nuint, byte*, nuint, nuint>)NativeLibrary.GetExport(library, "ZSTD_generateSequences");
        VersionAddress = NativeLibrary.GetExport(library, "ZSTD_versionNumber");
        _context = create();
        _compressionContext = createCompression();
        _sequencesContext = createCompression();
    }

    /// <summary>Where <c>ZSTD_versionNumber</c> is loaded: the library's slide, for a profiler.</summary>
    public nint VersionAddress { get; }

    public static string LibraryPath => Path.Combine(BenchFrames.RepositoryRoot, "tools", "native-ref", "out", "libzstd_ref.dylib");

    /// <summary>The reference library, or null when tools/native-ref/build.sh has not been run.</summary>
    public static NativeReference? TryLoad() =>
        File.Exists(LibraryPath) && NativeLibrary.TryLoad(LibraryPath, out nint library) ? new NativeReference(library) : null;

    /// <summary><c>ZSTD_decompressDCtx</c>: the decoded size, or -1 on an error.</summary>
    public int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        fixed (byte* src = source)
        fixed (byte* dst = destination)
        {
            nuint result = _decompress(_context, dst, (nuint)destination.Length, src, (nuint)source.Length);
            return _isError(result) != 0 ? -1 : (int)result;
        }
    }

    /// <summary>
    /// <c>ZSTD_generateSequences</c> at <paramref name="level"/>: libzstd's match finding alone (its
    /// blocks are then stored raw, so it never splits them), the sequences copied out, 16 bytes each.
    /// On a context of its own, which keeps collecting.
    /// </summary>
    /// <returns>The number of sequences, block delimiters included, or -1 on an error.</returns>
    public int GenerateSequences(ReadOnlySpan<byte> source, int level, Span<byte> sequences)
    {
        const int CompressionLevel = 100;
        _setParameter(_sequencesContext, CompressionLevel, level);
        fixed (byte* src = source)
        fixed (byte* seqs = sequences)
        {
            nuint result = _generateSequences(_sequencesContext, seqs, (nuint)(sequences.Length / 16), src, (nuint)source.Length);
            return _isError(result) != 0 ? -1 : (int)result;
        }
    }

    /// <summary><c>ZSTD_compressCCtx</c>, on a context kept from one call to the next: the frame's size, or -1 on an error.</summary>
    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination, int level)
    {
        fixed (byte* src = source)
        fixed (byte* dst = destination)
        {
            nuint result = _compress(_compressionContext, dst, (nuint)destination.Length, src, (nuint)source.Length, level);
            return _isError(result) != 0 ? -1 : (int)result;
        }
    }
}
