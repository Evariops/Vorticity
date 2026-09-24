using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vorticity.IO;

/// <summary>A file's device and inode: what stays the same while its name changes, and changes when another file takes its name.</summary>
/// <param name="Device">The device the file lives on.</param>
/// <param name="Inode">The file's number on that device.</param>
internal readonly record struct FileInode(ulong Device, ulong Inode)
{
    private static readonly unsafe delegate* unmanaged<int, byte*, int> Stat = Resolve(out SizeOffset, out WideDevice);

    // Where st_size sits in the platform's struct stat, and whether st_dev takes eight bytes or four.
    private static readonly int SizeOffset;
    private static readonly bool WideDevice;

    /// <summary>Whether this platform can tell one file from another.</summary>
    internal static unsafe bool IsSupported => Stat != null;

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
        if (Stat == null)
        {
            return false;
        }

        byte* status = stackalloc byte[256];
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            if (Stat((int)handle.DangerousGetHandle(), status) != 0)
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

    // macOS's struct stat has a four-byte st_dev and its size at 96; Linux's, on the two 64-bit
    // architectures, an eight-byte st_dev and its size at 48; st_ino is at 8 on all three. The
    // x86-64 macOS symbol of the 64-bit-inode layout carries a suffix; arm64 has only that layout.
    private static unsafe delegate* unmanaged<int, byte*, int> Resolve(out int sizeOffset, out bool wideDevice)
    {
        sizeOffset = 0;
        wideDevice = false;
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
            return Export(["/usr/lib/libSystem.B.dylib"], architecture == Architecture.Arm64 ? "fstat" : "fstat$INODE64");
        }

        if (OperatingSystem.IsLinux())
        {
            sizeOffset = 48;
            wideDevice = true;
            return Export(["libc.so.6", "libc.musl-x86_64.so.1", "libc.musl-aarch64.so.1"], "fstat");
        }

        return null;
    }

    private static unsafe delegate* unmanaged<int, byte*, int> Export(string[] libraries, string symbol)
    {
        foreach (string library in libraries)
        {
            if (NativeLibrary.TryLoad(library, out IntPtr handle) && NativeLibrary.TryGetExport(handle, symbol, out IntPtr address))
            {
                return (delegate* unmanaged<int, byte*, int>)address;
            }
        }

        return null;
    }
}
