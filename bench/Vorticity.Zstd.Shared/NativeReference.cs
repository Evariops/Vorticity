using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Vorticity.Zstd.Bench;

/// <summary>One call of a codec bound to its context: the size written, or -1 on an error.</summary>
internal delegate int SpanCodec(ReadOnlySpan<byte> source, Span<byte> destination);

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
    private readonly delegate* unmanaged<void*, nuint, int, nint> _createDictionary;
    private readonly delegate* unmanaged<nint, nint, nuint> _referenceDictionary;
    private readonly delegate* unmanaged<nint, byte*, nuint, byte*, nuint, nuint> _compress2;
    private readonly delegate* unmanaged<nint> _createCompression;
    private readonly delegate* unmanaged<nint> _createDecompression;
    private readonly delegate* unmanaged<void*, nuint, nint> _createDecompressionDictionary;
    private readonly delegate* unmanaged<nint, nint, nuint> _referenceDecompressionDictionary;
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
        _createDictionary = (delegate* unmanaged<void*, nuint, int, nint>)NativeLibrary.GetExport(library, "ZSTD_createCDict");
        _referenceDictionary = (delegate* unmanaged<nint, nint, nuint>)NativeLibrary.GetExport(library, "ZSTD_CCtx_refCDict");
        _compress2 = (delegate* unmanaged<nint, byte*, nuint, byte*, nuint, nuint>)NativeLibrary.GetExport(library, "ZSTD_compress2");
        _createDecompressionDictionary = (delegate* unmanaged<void*, nuint, nint>)NativeLibrary.GetExport(library, "ZSTD_createDDict");
        _referenceDecompressionDictionary = (delegate* unmanaged<nint, nint, nuint>)NativeLibrary.GetExport(library, "ZSTD_DCtx_refDDict");
        VersionAddress = NativeLibrary.GetExport(library, "ZSTD_versionNumber");
        _createCompression = createCompression;
        _createDecompression = create;
        _context = create();
        _compressionContext = createCompression();
        _sequencesContext = createCompression();
    }

    /// <summary>Where <c>ZSTD_versionNumber</c> is loaded: the library's slide, for a profiler.</summary>
    public nint VersionAddress { get; }

    /// <summary>The reference library; <c>VORTICITY_ZSTD_LIBZSTD</c> names another build of it, for experiments on libzstd itself.</summary>
    public static string LibraryPath =>
        Environment.GetEnvironmentVariable("VORTICITY_ZSTD_LIBZSTD") is { Length: > 0 } path
            ? path
            : Path.Combine(BenchFrames.RepositoryRoot, "tools", "native-ref", "out", "libzstd_ref.dylib");

    /// <summary>The same library built with threads (build.sh mt): zstdmt.</summary>
    public static string ThreadedLibraryPath => Path.Combine(BenchFrames.RepositoryRoot, "tools", "native-ref", "out", "libzstd_mt.dylib");

    /// <summary>The reference library, or null when tools/native-ref/build.sh has not been run.</summary>
    public static NativeReference? TryLoad() => TryLoad(LibraryPath);

    /// <summary>The library built with threads, or null when tools/native-ref/build.sh mt has not been run.</summary>
    public static NativeReference? TryLoadThreaded() => TryLoad(ThreadedLibraryPath);

    private static NativeReference? TryLoad(string path) =>
        File.Exists(path) && NativeLibrary.TryLoad(path, out nint library) ? new NativeReference(library) : null;

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

    /// <summary>
    /// A context that compresses with <paramref name="dictionary"/> prepared at <paramref name="level"/>,
    /// as the platform's encoder does (a <c>ZSTD_CDict</c>, then <c>ZSTD_CCtx_refCDict</c>): its
    /// <c>ZSTD_compress2</c> writes the frame. <c>ZSTD_createCDict</c> copies the dictionary, which
    /// prepares it as <c>ZSTD_createCDict_byReference</c> does.
    /// </summary>
    public Func<byte[], byte[], int> WithDictionary(byte[] dictionary, int level)
    {
        SpanCodec compress = CompressorWith(dictionary, level);
        return (source, destination) => compress(source, destination);
    }

    /// <summary><see cref="WithDictionary"/>, on spans.</summary>
    public SpanCodec CompressorWith(byte[] dictionary, int level)
    {
        nint prepared;
        fixed (byte* bytes = dictionary)
        {
            prepared = _createDictionary(bytes, (nuint)dictionary.Length, level);
        }

        nint context = _createCompression();
        if (prepared == 0 || _isError(_referenceDictionary(context, prepared)) != 0)
        {
            throw new InvalidOperationException("the reference could not prepare the dictionary");
        }

        return (source, destination) =>
        {
            fixed (byte* src = source)
            fixed (byte* dst = destination)
            {
                nuint result = _compress2(context, dst, (nuint)destination.Length, src, (nuint)source.Length);
                return _isError(result) != 0 ? -1 : (int)result;
            }
        };
    }

    /// <summary>
    /// A context that decompresses with <paramref name="dictionary"/>, as the platform's decoder does
    /// (a <c>ZSTD_DDict</c>, then <c>ZSTD_DCtx_refDDict</c>): its <c>ZSTD_decompressDCtx</c> decodes the frame.
    /// </summary>
    public SpanCodec DecompressorWith(byte[] dictionary)
    {
        nint prepared;
        fixed (byte* bytes = dictionary)
        {
            prepared = _createDecompressionDictionary(bytes, (nuint)dictionary.Length);
        }

        nint context = _createDecompression();
        if (prepared == 0 || _isError(_referenceDecompressionDictionary(context, prepared)) != 0)
        {
            throw new InvalidOperationException("the reference could not load the dictionary");
        }

        return (source, destination) =>
        {
            fixed (byte* src = source)
            fixed (byte* dst = destination)
            {
                nuint result = _decompress(context, dst, (nuint)destination.Length, src, (nuint)source.Length);
                return _isError(result) != 0 ? -1 : (int)result;
            }
        };
    }

    /// <summary>
    /// <c>ZSTD_compress2</c> at <paramref name="level"/> with <paramref name="workers"/> worker threads
    /// (the threaded library only), on a context of its own kept from one call to the next.
    /// </summary>
    public Func<byte[], byte[], int> WithWorkers(int level, int workers)
    {
        const int CompressionLevel = 100;
        const int NbWorkers = 400;
        nint context = _createCompression();
        if (_isError(_setParameter(context, CompressionLevel, level)) != 0 || _isError(_setParameter(context, NbWorkers, workers)) != 0)
        {
            throw new InvalidOperationException("this library has no workers");
        }

        return (source, destination) =>
        {
            fixed (byte* src = source)
            fixed (byte* dst = destination)
            {
                nuint result = _compress2(context, dst, (nuint)destination.Length, src, (nuint)source.Length);
                return _isError(result) != 0 ? -1 : (int)result;
            }
        };
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
