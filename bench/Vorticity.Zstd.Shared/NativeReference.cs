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
    private readonly nint _context;
    private readonly nint _compressionContext;

    private NativeReference(nint library)
    {
        var create = (delegate* unmanaged<nint>)NativeLibrary.GetExport(library, "ZSTD_createDCtx");
        var createCompression = (delegate* unmanaged<nint>)NativeLibrary.GetExport(library, "ZSTD_createCCtx");
        _decompress = (delegate* unmanaged<nint, byte*, nuint, byte*, nuint, nuint>)NativeLibrary.GetExport(library, "ZSTD_decompressDCtx");
        _compress = (delegate* unmanaged<nint, byte*, nuint, byte*, nuint, int, nuint>)NativeLibrary.GetExport(library, "ZSTD_compressCCtx");
        _isError = (delegate* unmanaged<nuint, uint>)NativeLibrary.GetExport(library, "ZSTD_isError");
        _context = create();
        _compressionContext = createCompression();
    }

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
