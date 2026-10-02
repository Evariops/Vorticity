using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Vorticity.IO;

/// <summary>
/// The system calls a read of a local file makes, called directly where the framework makes more
/// of them: an open under a shared lock, and a mapping.
/// </summary>
/// <remarks>
/// <para>
/// <c>File.OpenHandle</c> on Unix opens the file, asks <c>fstat</c> whether it is a directory and
/// takes with <c>flock</c> the shared lock its <see cref="FileShare"/> stands for, which it drops
/// with another <c>flock</c> before it closes the file; the reader then asks the file's length with
/// another <c>fstat</c>. Here the lock is taken the same way and <c>close</c> drops it, so a writer
/// that opens the file without sharing it is kept out as before, and one <c>fstat</c> answers both
/// questions: two system calls fewer, a microsecond and a half of an open and a close on macOS.
/// The lock is not taken by <c>open</c> itself, O_SHLOCK, which would have to be told not to wait
/// for it with O_NONBLOCK: the framework takes a descriptor that has it for a pipe, and reads it
/// without its offsets.
/// </para>
/// <para>
/// A mapping through <c>MemoryMappedFile</c> asks the length once more and allocates five objects,
/// two of them finalizable; <c>mmap</c> and <c>munmap</c> called directly do neither. A positional
/// read through <c>RandomAccess</c> of a handle the framework did not open first asks whether it
/// seeks, <c>lseek</c>, and whether it blocks, <c>fcntl</c>; <c>pread</c> called directly does not.
/// </para>
/// <para>
/// Every failure, and every platform without these calls, falls back to the framework's, which
/// then fails as it always has, with the exception it always threw.
/// </para>
/// </remarks>
internal static unsafe class NativeFile
{
    // open's flags on macOS: read-only (0) and close-on-exec (O_CLOEXEC).
    private const int ReadFlags = 0x1000000;

    // flock's: a shared lock (LOCK_SH), refused rather than waited for (LOCK_NB).
    private const int SharedLockNow = 1 | 4;
    private const int ProtRead = 1;
    private const int MapShared = 1;

    // The bytes of a path encoded on the stack; a longer one is rented.
    private const int StackName = 512;

    // open is variadic, and reads its third argument, the mode, only under O_CREAT: a call that
    // passes the first two alone is the call C makes. The arm64 calling convention is the one this
    // code is tested on.
    private static readonly delegate* unmanaged<byte*, int, int> Open =
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? (delegate* unmanaged<byte*, int, int>)FileInode.Export(["/usr/lib/libSystem.B.dylib"], "open")
            : null;

    private static readonly delegate* unmanaged<int, int, int> Flock =
        Open != null ? (delegate* unmanaged<int, int, int>)FileInode.Export(["/usr/lib/libSystem.B.dylib"], "flock") : null;

    private static readonly delegate* unmanaged<void*, nuint, int, int, int, long, void*> Mmap =
        (delegate* unmanaged<void*, nuint, int, int, int, long, void*>)Unix("mmap");

    private static readonly delegate* unmanaged<void*, nuint, int> Munmap =
        (delegate* unmanaged<void*, nuint, int>)Unix("munmap");

    private static readonly delegate* unmanaged<int, byte*, nuint, long, nint> Pread =
        (delegate* unmanaged<int, byte*, nuint, long, nint>)Unix("pread");

    // Linux's madvise and its MADV_DONTFORK, which keeps a mapping out of a forked child, as
    // MemoryMappedFile asks of every mapping it makes there; macOS has no such advice.
    private const int DontFork = 10;

    private static readonly delegate* unmanaged<void*, nuint, int, int> Madvise =
        OperatingSystem.IsLinux() ? (delegate* unmanaged<void*, nuint, int, int>)Unix("madvise") : null;

    /// <summary>
    /// <paramref name="path"/> opened to be read, as <c>File.OpenHandle</c> opens it for reading with
    /// <see cref="FileShare.Read"/> and <see cref="FileShare.Delete"/>, and its length.
    /// </summary>
    /// <param name="path">A local file path.</param>
    /// <param name="length">The file's length.</param>
    /// <exception cref="IOException">The file could not be opened.</exception>
    internal static SafeFileHandle OpenRead(string path, out long length)
    {
        if (Open != null && Flock != null && TryOpenLocked(path, out SafeFileHandle? locked, out length))
        {
            return locked;
        }

        // System.IO.File spelled out: this library has a `Vorticity.File` namespace of its own.
        // FileShare.Delete gives Windows what Unix has without asking: a file being read can be
        // deleted or replaced under its name, the reader keeping the bytes it opened.
        SafeFileHandle handle = System.IO.File.OpenHandle(
            path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.RandomAccess);
        try
        {
            length = RandomAccess.GetLength(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        return handle;
    }

    /// <summary>
    /// Reads into <paramref name="destination"/> from <paramref name="offset"/> of the file behind
    /// <paramref name="handle"/>, as <see cref="RandomAccess.Read(SafeFileHandle, Span{byte}, long)"/>
    /// does, which reads again when the platform's call failed, and throws what it finds.
    /// </summary>
    /// <param name="handle">A readable file handle.</param>
    /// <param name="destination">Where the bytes go.</param>
    /// <param name="offset">The file offset of the first byte.</param>
    /// <returns>The bytes read, fewer than asked at the end of the file.</returns>
    internal static int Read(SafeFileHandle handle, Span<byte> destination, long offset)
    {
        if (Pread != null)
        {
            bool added = false;
            nint read;
            try
            {
                handle.DangerousAddRef(ref added);
                fixed (byte* bytes = destination)
                {
                    read = Pread((int)handle.DangerousGetHandle(), bytes, (nuint)destination.Length, offset);
                }
            }
            finally
            {
                if (added)
                {
                    handle.DangerousRelease();
                }
            }

            if (read >= 0)
            {
                return (int)read;
            }
        }

        return RandomAccess.Read(handle, destination, offset);
    }

    /// <summary>
    /// The first <paramref name="length"/> bytes of the file behind <paramref name="handle"/>,
    /// mapped read-only and shared; false where the platform's call is not at hand, or failed.
    /// </summary>
    /// <param name="handle">A readable file handle.</param>
    /// <param name="length">The bytes to map; positive, and no more than the file holds.</param>
    /// <param name="mapped">The mapping's first byte, when this returns true; <see cref="Unmap"/> takes it back.</param>
    internal static bool TryMap(SafeFileHandle handle, long length, out byte* mapped)
    {
        mapped = null;
        if (Mmap == null || Munmap == null || length <= 0)
        {
            return false;
        }

        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            void* address = Mmap(null, (nuint)length, ProtRead, MapShared, (int)handle.DangerousGetHandle(), 0);

            // MAP_FAILED is all ones.
            if (address == (void*)-1 || address == null)
            {
                return false;
            }

            // Advice, which a kernel may refuse: the mapping stands either way.
            if (Madvise != null)
            {
                _ = Madvise(address, (nuint)length, DontFork);
            }

            mapped = (byte*)address;
            return true;
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>Unmaps what <see cref="TryMap"/> mapped.</summary>
    /// <param name="mapped">The mapping's first byte.</param>
    /// <param name="length">The length it was mapped with.</param>
    internal static void Unmap(byte* mapped, long length) => Munmap(mapped, (nuint)length);

    private static bool TryOpenLocked(string path, [NotNullWhen(true)] out SafeFileHandle? handle, out long length)
    {
        handle = null;
        length = 0;

        // The name File.OpenHandle opens: against the current directory when relative, its dots
        // removed by name rather than through the links they cross, a doubled separator collapsed.
        // A malformed one throws here what File.OpenHandle throws. A rooted name with none of these
        // is its own full name.
        string full = Path.IsPathFullyQualified(path)
            && !path.Contains("/.", StringComparison.Ordinal)
            && !path.Contains("//", StringComparison.Ordinal)
            && !path.Contains('\0')
                ? path
                : Path.GetFullPath(path);
        int most = Encoding.UTF8.GetMaxByteCount(full.Length) + 1;
        byte[]? rented = most > StackName ? ArrayPool<byte>.Shared.Rent(most) : null;
        Span<byte> name = rented is null ? stackalloc byte[StackName] : rented;
        int descriptor;
        try
        {
            name[Encoding.UTF8.GetBytes(full, name)] = 0;
            fixed (byte* bytes = name)
            {
                descriptor = Open(bytes, ReadFlags);
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        // A missing file, an interrupted call: the framework's open tries again, and throws what it
        // finds.
        if (descriptor < 0)
        {
            return false;
        }

        SafeFileHandle opened = new SafeFileHandle(descriptor, ownsHandle: true);
        if (Flock(descriptor, SharedLockNow) != 0 || !FileInode.TryGetRegularLength(descriptor, out length))
        {
            // A lock held exclusively, which File.OpenHandle refuses as a sharing violation; a
            // directory, which it refuses where open does not; another file that is not regular,
            // which it opens its own way.
            opened.Dispose();
            return false;
        }

        handle = opened;
        return true;
    }

    private static IntPtr Unix(string symbol) =>
        !Environment.Is64BitProcess ? IntPtr.Zero
        : OperatingSystem.IsMacOS() ? FileInode.Export(["/usr/lib/libSystem.B.dylib"], symbol)
        : OperatingSystem.IsLinux() ? FileInode.Export(["libc.so.6", "libc.musl-x86_64.so.1", "libc.musl-aarch64.so.1"], symbol)
        : IntPtr.Zero;
}
