using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Vorticity.Zstd.Tests.Support;

/// <summary>
/// libzstd 1.5.7 (tools/native-ref/build.sh) called one-shot, the semantics Vorticity.Zstd mirrors: the
/// fuzzing oracle. The platform's decoder is a streaming one, and its streaming path applies rules
/// the one-shot path does not (a window cap, a per-block size check), so on corrupted frames it
/// would disagree for reasons that are not bugs.
/// </summary>
internal sealed unsafe class LibzstdOracle
{
    private readonly delegate* unmanaged<nint, byte*, nuint, byte*, nuint, nint, nuint> _decompressUsingDDict;
    private readonly delegate* unmanaged<byte*, nuint, nuint> _findFrameCompressedSize;
    private readonly delegate* unmanaged<nuint, uint> _isError;
    private readonly delegate* unmanaged<byte*, nuint, nint> _createDDict;
    private readonly delegate* unmanaged<nint> _createDCtx;

    /// <summary>
    /// This thread's decompression context, never freed (tests are short-lived): a context decodes
    /// one frame at a time, and the tests that ask the oracle run side by side.
    /// </summary>
    [ThreadStatic]
    private static nint t_context;

    private LibzstdOracle(nint library)
    {
        _createDCtx = (delegate* unmanaged<nint>)NativeLibrary.GetExport(library, "ZSTD_createDCtx");
        _decompressUsingDDict = (delegate* unmanaged<nint, byte*, nuint, byte*, nuint, nint, nuint>)NativeLibrary.GetExport(library, "ZSTD_decompress_usingDDict");
        _findFrameCompressedSize = (delegate* unmanaged<byte*, nuint, nuint>)NativeLibrary.GetExport(library, "ZSTD_findFrameCompressedSize");
        _isError = (delegate* unmanaged<nuint, uint>)NativeLibrary.GetExport(library, "ZSTD_isError");
        _createDDict = (delegate* unmanaged<byte*, nuint, nint>)NativeLibrary.GetExport(library, "ZSTD_createDDict");
    }

    private static readonly Lazy<LibzstdOracle?> Shared = new(Load);

    /// <summary>The oracle, or null when the native reference has not been built.</summary>
    public static LibzstdOracle? Instance => Shared.Value;

    private static LibzstdOracle? Load()
    {
        string path = Path.Combine(TestData.RepositoryRoot, "tools", "native-ref", "out", "libzstd_ref" + (OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so"));
        return File.Exists(path) && NativeLibrary.TryLoad(path, out nint library) ? new LibzstdOracle(library) : null;
    }

    /// <summary>A digested dictionary for <see cref="Decompress"/>; never freed (tests are short-lived).</summary>
    public nint CreateDictionary(ReadOnlySpan<byte> dictionary)
    {
        fixed (byte* d = dictionary)
        {
            return _createDDict(d, (nuint)dictionary.Length);
        }
    }

    /// <summary>
    /// Decodes exactly one frame: the first frame of <paramref name="source"/> as libzstd delimits it.
    /// </summary>
    /// <returns>The content size, or -1 when libzstd refuses the frame.</returns>
    public int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, nint dictionary, out int frameSize)
    {
        frameSize = 0;
        fixed (byte* src = source)
        fixed (byte* dst = destination)
        {
            nuint size = _findFrameCompressedSize(src, (nuint)source.Length);
            if (_isError(size) != 0)
            {
                return -1;
            }

            frameSize = (int)size;
            if (t_context == 0)
            {
                t_context = _createDCtx();
            }

            nuint result = _decompressUsingDDict(t_context, dst, (nuint)destination.Length, src, size, dictionary);
            return _isError(result) != 0 ? -1 : (int)result;
        }
    }
}
