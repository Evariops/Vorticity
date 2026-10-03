using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Zstd.Tests.Support;

/// <summary>
/// libzstd 1.5.7 built with <c>ZSTD_MULTITHREAD</c> (tools/native-ref/build.sh mt): zstdmt, the frames
/// libzstd writes with <c>ZSTD_c_nbWorkers</c> at 1 or more, which the platform's libzstd cannot write
/// (built without threads). The oracle of the parallel compressor; null when it has not been built.
/// </summary>
internal sealed unsafe class LibzstdMt
{
    private const int CompressionLevel = 100;
    private const int ChecksumFlag = 201;
    private const int EnableLongDistanceMatching = 160;
    private const int NbWorkers = 400;
    private const int JobSize = 401;
    private const int OverlapLog = 402;

    private readonly delegate* unmanaged<nint> _createContext;
    private readonly delegate* unmanaged<nint, nuint> _freeContext;
    private readonly delegate* unmanaged<nint, int, int, nuint> _setParameter;
    private readonly delegate* unmanaged<nint, byte*, nuint, byte*, nuint, nuint> _compress2;
    private readonly delegate* unmanaged<void*, nuint, int, nint> _createDictionary;
    private readonly delegate* unmanaged<nint, nuint> _freeDictionary;
    private readonly delegate* unmanaged<nint, nint, nuint> _referenceDictionary;
    private readonly delegate* unmanaged<nuint, uint> _isError;
    private readonly delegate* unmanaged<nuint, nint> _errorName;

    private LibzstdMt(nint library)
    {
        _createContext = (delegate* unmanaged<nint>)NativeLibrary.GetExport(library, "ZSTD_createCCtx");
        _freeContext = (delegate* unmanaged<nint, nuint>)NativeLibrary.GetExport(library, "ZSTD_freeCCtx");
        _setParameter = (delegate* unmanaged<nint, int, int, nuint>)NativeLibrary.GetExport(library, "ZSTD_CCtx_setParameter");
        _compress2 = (delegate* unmanaged<nint, byte*, nuint, byte*, nuint, nuint>)NativeLibrary.GetExport(library, "ZSTD_compress2");
        _createDictionary = (delegate* unmanaged<void*, nuint, int, nint>)NativeLibrary.GetExport(library, "ZSTD_createCDict");
        _freeDictionary = (delegate* unmanaged<nint, nuint>)NativeLibrary.GetExport(library, "ZSTD_freeCDict");
        _referenceDictionary = (delegate* unmanaged<nint, nint, nuint>)NativeLibrary.GetExport(library, "ZSTD_CCtx_refCDict");
        _isError = (delegate* unmanaged<nuint, uint>)NativeLibrary.GetExport(library, "ZSTD_isError");
        _errorName = (delegate* unmanaged<nuint, nint>)NativeLibrary.GetExport(library, "ZSTD_getErrorName");
    }

    /// <summary>The library, loaded once; null when tools/native-ref/out/libzstd_mt.dylib is missing.</summary>
    public static LibzstdMt? Instance { get; } = Load();

    public static string LibraryPath => Path.Combine(RepositoryRoot(), "tools", "native-ref", "out", "libzstd_mt.dylib");

    private static LibzstdMt? Load() =>
        File.Exists(LibraryPath) && NativeLibrary.TryLoad(LibraryPath, out nint library) ? new LibzstdMt(library) : null;

    private static string RepositoryRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    /// <summary>
    /// <c>ZSTD_compress2</c> with <paramref name="workers"/> worker threads (0 compresses on the
    /// calling thread, as the platform does), the dictionary prepared at the level when given.
    /// </summary>
    public byte[] Compress(
        ReadOnlySpan<byte> source, int level, int workers, bool checksum = false, bool longDistance = false,
        byte[]? dictionary = null, int jobSize = 0, int overlapLog = 0)
    {
        nint context = _createContext();
        nint prepared = 0;
        try
        {
            Check(_setParameter(context, CompressionLevel, level));
            Check(_setParameter(context, NbWorkers, workers));
            Check(_setParameter(context, ChecksumFlag, checksum ? 1 : 0));
            if (longDistance)
            {
                Check(_setParameter(context, EnableLongDistanceMatching, 1));
            }

            if (jobSize != 0)
            {
                Check(_setParameter(context, JobSize, jobSize));
            }

            if (overlapLog != 0)
            {
                Check(_setParameter(context, OverlapLog, overlapLog));
            }

            if (dictionary is not null)
            {
                fixed (byte* bytes = dictionary)
                {
                    prepared = _createDictionary(bytes, (nuint)dictionary.Length, level);
                }

                Check(_referenceDictionary(context, prepared));
            }

            byte[] output = new byte[source.Length + (source.Length >> 7) + 4096];
            fixed (byte* src = source)
            fixed (byte* dst = output)
            {
                nuint written = _compress2(context, dst, (nuint)output.Length, src, (nuint)source.Length);
                Check(written);
                return output.AsSpan(0, (int)written).ToArray();
            }
        }
        finally
        {
            _freeContext(context);
            if (prepared != 0)
            {
                _freeDictionary(prepared);
            }
        }
    }

    private void Check(nuint result)
    {
        if (_isError(result) != 0)
        {
            throw new InvalidOperationException("libzstd: " + Marshal.PtrToStringAnsi(_errorName(result)));
        }
    }
}
