using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vorticity.IO;

/// <summary>A file's device and inode: what stays the same while its name changes, and changes when another file takes its name.</summary>
/// <param name="Device">The device the file lives on; on Windows, the volume's serial number.</param>
/// <param name="Inode">The file's number on that device; on Windows, the low half of its 128-bit file id.</param>
/// <param name="InodeHigh">The high half of a Windows file id, which ReFS uses; zero elsewhere.</param>
internal readonly record struct FileInode(ulong Device, ulong Inode, ulong InodeHigh = 0)
{
    private static readonly unsafe delegate* unmanaged<int, byte*, int> Stat = Resolve(out SizeOffset, out WideDevice, out VersionedStat, out StatVersion);

    // Windows has no fstat: GetFileInformationByHandleEx answers the same two questions, the file's
    // id on its volume (FileIdInfo) and its length (FileStandardInfo).
    private static readonly unsafe delegate* unmanaged<IntPtr, int, byte*, uint, int> FileInformation =
        OperatingSystem.IsWindows() ? (delegate* unmanaged<IntPtr, int, byte*, uint, int>)Export(["kernel32.dll"], "GetFileInformationByHandleEx") : null;

    private const int FileStandardInfo = 1;
    private const int FileIdInfo = 18;

    // Windows 11 24H2 answers the same questions from a path, without opening the file: an open
    // costs a hundred microseconds where antivirus and endpoint filters inspect every one, this
    // call a fifth of that. Earlier Windows does not export it.
    private static readonly unsafe delegate* unmanaged<char*, int, byte*, uint, int> FileInformationByName =
        OperatingSystem.IsWindows()
            ? (delegate* unmanaged<char*, int, byte*, uint, int>)Export(["kernel32.dll", "kernelbase.dll"], "GetFileInformationByName")
            : null;

    private const int FileStatBasicByNameInfo = 3;

    /// <summary>
    /// The identity and length of the file at <paramref name="path"/>, read without opening it;
    /// false where the platform cannot, or when the path is a link or a junction.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="identity">The file's identity, when this returns true.</param>
    /// <param name="length">The file's length, when this returns true.</param>
    /// <remarks>
    /// FILE_STAT_BASIC_INFORMATION: the length (EndOfFile) at 48, the reparse tag at 60, the
    /// volume's serial number at 80 and the 128-bit file id at 88, the same two numbers
    /// <see cref="TryGet"/> reads from a handle. A reparse point is described as itself rather
    /// than as what it points to, so it is declined: its identity would not be its target's.
    /// </remarks>
    internal static unsafe bool TryGetByName(string path, out FileInode identity, out long length)
    {
        identity = default;
        length = 0;
        if (FileInformationByName == null)
        {
            return false;
        }

        byte* status = stackalloc byte[104];
        fixed (char* name = path)
        {
            if (FileInformationByName(name, FileStatBasicByNameInfo, status, 104) == 0)
            {
                return false;
            }
        }

        ulong low = *(ulong*)(status + 88);
        ulong high = *(ulong*)(status + 96);
        if (*(uint*)(status + 60) != 0 || (low == 0 && high == 0))
        {
            return false;
        }

        identity = new FileInode(*(ulong*)(status + 80), low, high);
        length = *(long*)(status + 48);
        return true;
    }

    // Where st_size sits in the platform's struct stat, and whether st_dev takes eight bytes or four.
    private static readonly int SizeOffset;
    private static readonly bool WideDevice;

    // A glibc older than 2.33 exports no fstat: its headers inline a call to __fxstat, which takes
    // the version of the struct stat layout first.
    private static readonly unsafe delegate* unmanaged<int, int, byte*, int> VersionedStat;
    private static readonly int StatVersion;

    /// <summary>Whether this platform can tell one file from another.</summary>
    internal static unsafe bool IsSupported => Stat != null || VersionedStat != null || FileInformation != null;

    /// <summary>The identity of the file behind <paramref name="handle"/>.</summary>
    /// <param name="handle">An open file.</param>
    /// <param name="length">The file's length, which the platform's answer must repeat.</param>
    /// <param name="identity">The identity, when this returns true.</param>
    /// <returns>False when the platform cannot tell, or answered with a length other than <paramref name="length"/>.</returns>
    /// <remarks>
    /// The length check guards against a layout of <c>struct stat</c> this code does not expect:
    /// two files taken for one would have one's bytes served for the other's.
    /// </remarks>
    internal static unsafe bool TryGet(SafeFileHandle handle, long length, out FileInode identity)
    {
        identity = default;
        if (!IsSupported)
        {
            return false;
        }

        if (FileInformation != null)
        {
            return TryGetOnWindows(handle, length, out identity);
        }

        byte* status = stackalloc byte[256];
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            int descriptor = (int)handle.DangerousGetHandle();
            if ((Stat != null ? Stat(descriptor, status) : VersionedStat(StatVersion, descriptor, status)) != 0)
            {
                return false;
            }
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }

        if (*(long*)(status + SizeOffset) != length)
        {
            return false;
        }

        ulong device = WideDevice ? *(ulong*)status : *(uint*)status;
        ulong inode = *(ulong*)(status + 8);
        if (inode == 0)
        {
            return false;
        }

        identity = new FileInode(device, inode);
        return true;
    }

    /// <summary>
    /// The length of the regular file open as <paramref name="descriptor"/>, from one fstat; false
    /// for anything else, and off macOS, where this code does not read the file's type.
    /// </summary>
    /// <param name="descriptor">A descriptor its caller keeps open across the call.</param>
    /// <param name="length">The file's length, when this returns true.</param>
    /// <remarks>macOS's st_mode is the two bytes at 4, the type in its top four bits.</remarks>
    internal static unsafe bool TryGetRegularLength(int descriptor, out long length)
    {
        length = 0;
        if (Stat == null || !OperatingSystem.IsMacOS())
        {
            return false;
        }

        byte* status = stackalloc byte[256];
        if (Stat(descriptor, status) != 0 || (*(ushort*)(status + 4) & 0xF000) != 0x8000)
        {
            return false;
        }

        length = *(long*)(status + SizeOffset);
        return true;
    }

    // FILE_STANDARD_INFO has the length (EndOfFile) at 8; FILE_ID_INFO is the volume's 64-bit serial
    // number and then the file's 128-bit id, which NTFS fills in its low half only and ReFS in both.
    private static unsafe bool TryGetOnWindows(SafeFileHandle handle, long length, out FileInode identity)
    {
        identity = default;
        byte* standard = stackalloc byte[24];
        byte* id = stackalloc byte[24];
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            IntPtr raw = handle.DangerousGetHandle();
            if (FileInformation(raw, FileStandardInfo, standard, 24) == 0 || FileInformation(raw, FileIdInfo, id, 24) == 0)
            {
                return false;
            }
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }

        if (*(long*)(standard + 8) != length)
        {
            return false;
        }

        ulong low = *(ulong*)(id + 8);
        ulong high = *(ulong*)(id + 16);
        if (low == 0 && high == 0)
        {
            return false;
        }

        identity = new FileInode(*(ulong*)id, low, high);
        return true;
    }

    // macOS's struct stat has a four-byte st_dev and its size at 96; Linux's, on the two 64-bit
    // architectures, an eight-byte st_dev and its size at 48; st_ino is at 8 on all three. The
    // x86-64 macOS symbol of the 64-bit-inode layout carries a suffix; arm64 has only that layout.
    // The layout version __fxstat takes for that struct stat is 1 on x86-64 and 0 on arm64.
    private static unsafe delegate* unmanaged<int, byte*, int> Resolve(
        out int sizeOffset,
        out bool wideDevice,
        out delegate* unmanaged<int, int, byte*, int> versionedStat,
        out int statVersion)
    {
        sizeOffset = 0;
        wideDevice = false;
        versionedStat = null;
        statVersion = 0;
        if (!Environment.Is64BitProcess)
        {
            return null;
        }

        Architecture architecture = RuntimeInformation.ProcessArchitecture;
        if (architecture is not (Architecture.Arm64 or Architecture.X64))
        {
            return null;
        }

        if (OperatingSystem.IsMacOS())
        {
            sizeOffset = 96;
            return (delegate* unmanaged<int, byte*, int>)Export(["/usr/lib/libSystem.B.dylib"], architecture == Architecture.Arm64 ? "fstat" : "fstat$INODE64");
        }

        if (OperatingSystem.IsLinux())
        {
            sizeOffset = 48;
            wideDevice = true;
            delegate* unmanaged<int, byte*, int> stat =
                (delegate* unmanaged<int, byte*, int>)Export(["libc.so.6", "libc.musl-x86_64.so.1", "libc.musl-aarch64.so.1"], "fstat");
            if (stat == null)
            {
                versionedStat = (delegate* unmanaged<int, int, byte*, int>)Export(["libc.so.6"], "__fxstat");
                statVersion = architecture == Architecture.X64 ? 1 : 0;
            }

            return stat;
        }

        return null;
    }

    /// <summary>The address of <paramref name="symbol"/> in the first of <paramref name="libraries"/> that exports it, or zero.</summary>
    internal static IntPtr Export(string[] libraries, string symbol)
    {
        foreach (string library in libraries)
        {
            if (NativeLibrary.TryLoad(library, out IntPtr handle) && NativeLibrary.TryGetExport(handle, symbol, out IntPtr address))
            {
                return address;
            }
        }

        return IntPtr.Zero;
    }
}
