using System;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using Vorticity.IO;
using Xunit;
using static Vorticity.Tests.IO.IoTestData;

namespace Vorticity.Tests.IO;

/// <summary>
/// The open, the positional read and the mapping a local file is read with: what the framework's
/// would do, the lock its sharing stands for included, in fewer system calls where the platform
/// allows.
/// </summary>
public sealed class NativeFileTests
{
    private const int FileLength = 40_000;

    [Fact]
    public void AnOpenGivesTheFileAndItsLength()
    {
        byte[] content = Pattern(FileLength);
        using TempFile file = new TempFile(content);
        using SafeFileHandle handle = NativeFile.OpenRead(file.Path_, out long length);

        Assert.Equal(FileLength, length);
        Assert.Equal(FileLength, RandomAccess.GetLength(handle));
        byte[] read = new byte[100];
        Assert.Equal(100, RandomAccess.Read(handle, read, 1_000));
        Assert.True(read.AsSpan().SequenceEqual(content.AsSpan(1_000, 100)));
    }

    [Fact]
    public void APositionalReadReadsAtItsOffsetAndShortAtTheEnd()
    {
        byte[] content = Pattern(FileLength);
        using TempFile file = new TempFile(content);
        using SafeFileHandle handle = NativeFile.OpenRead(file.Path_, out _);

        foreach ((int offset, int length) in new[] { (0, 1), (17, 4_096), (FileLength - 1_000, 1_000), (FileLength / 2, 3) })
        {
            byte[] read = new byte[length];
            Assert.Equal(length, NativeFile.Read(handle, read, offset));
            Assert.True(read.AsSpan().SequenceEqual(content.AsSpan(offset, length)));
        }

        byte[] past = new byte[100];
        Assert.Equal(10, NativeFile.Read(handle, past, FileLength - 10));
        Assert.Equal(0, NativeFile.Read(handle, past, FileLength));
    }

    [Fact]
    public void AReaderKeepsOutAWriterThatSharesNothingUntilItCloses()
    {
        using TempFile file = new TempFile(Pattern(FileLength));
        SafeFileHandle reader = NativeFile.OpenRead(file.Path_, out _);
        Assert.Throws<IOException>(() => OpenUnshared(file.Path_).Dispose());

        reader.Dispose();
        OpenUnshared(file.Path_).Dispose();
    }

    [Fact]
    public void AFileAWriterHoldsUnsharedIsRefused()
    {
        using TempFile file = new TempFile(Pattern(FileLength));
        using (OpenUnshared(file.Path_))
        {
            Assert.Throws<IOException>(() => NativeFile.OpenRead(file.Path_, out _).Dispose());
        }

        NativeFile.OpenRead(file.Path_, out _).Dispose();
    }

    [Fact]
    public void ADirectoryOrAMissingFileIsRefusedAsTheFrameworkRefusesIt()
    {
        using TempFile file = new TempFile(Pattern(10));
        string directory = Path.GetDirectoryName(file.Path_)!;
        Assert.Throws<UnauthorizedAccessException>(() => NativeFile.OpenRead(directory, out _).Dispose());
        Assert.Throws<FileNotFoundException>(() => NativeFile.OpenRead(file.Path_ + ".missing", out _).Dispose());
    }

    [Fact]
    public void AMappingReadsTheFileAndOutlivesItsHandle()
    {
        byte[] content = Pattern(FileLength);
        using TempFile file = new TempFile(content);
        MappedFileOwner mapping;
        using (SafeFileHandle handle = NativeFile.OpenRead(file.Path_, out long length))
        {
            mapping = MappedFileOwner.Map(handle, length, ownsHandle: false);
        }

        using (mapping)
        {
            Assert.True(mapping.View(0, FileLength, 0).Span.SequenceEqual(content));
        }
    }

    [Fact]
    public void AMappingDroppedWithoutItsLastReleaseIsUnmappedByItsFinalizer()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "only Unix maps with mmap called directly");
        using TempFile file = new TempFile(Pattern(FileLength));
        using SafeFileHandle handle = NativeFile.OpenRead(file.Path_, out long length);

        long before = MappedFileOwner.FinalizedMappingCount;
        Drop(handle, length);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.True(MappedFileOwner.FinalizedMappingCount > before);
    }

    // Out of line, so that no local of the test keeps the owner reachable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Drop(SafeFileHandle handle, long length) =>
        _ = MappedFileOwner.Map(handle, length, ownsHandle: false);

    private static SafeFileHandle OpenUnshared(string path) =>
        global::System.IO.File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
}
