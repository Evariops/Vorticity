using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>
/// A durable write: the file's bytes reach the device before <c>CompleteAsync</c> returns, and
/// everything the postscript names reaches it before the postscript does, which a file system may
/// otherwise persist first.
/// </summary>
public sealed class DurableWriteTests
{
    private const int Rows = 10_000;

    private static readonly VortexSchema Schema = [("id", VortexType.Int64)];

    [Fact]
    public async Task ADurableFileReachesTheDeviceBeforeItsPostscriptAndWholeAtTheEnd()
    {
        string path = TempPath();
        try
        {
            List<long> flushes;
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter(path, Schema, new VortexWriteOptions { Durable = true }))
            {
                flushes = FilePipeWriter.WatchDiskFlushes(writer.FilePipe!);
                await WriteRowsAsync(writer, 0);
                await writer.CompleteAsync(TestContext.Current.CancellationToken);
            }

            Assert.Equal(new[] { PostscriptStart(path), new FileInfo(path).Length }, flushes);
            await using VortexFile file = await VortexFile.OpenAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(Rows, file.RowCount);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ADurableAppendReachesTheDeviceBeforeItsNewPostscript()
    {
        string path = TempPath();
        try
        {
            await using (VortexFileWriter first = VortexSession.Default.CreateWriter(path, Schema))
            {
                await WriteRowsAsync(first, 0);
                await first.CompleteAsync(TestContext.Current.CancellationToken);
            }

            List<long> flushes;
            await using (VortexFileWriter append = await VortexSession.Default.AppendAsync(path, new VortexWriteOptions { Durable = true }, TestContext.Current.CancellationToken))
            {
                flushes = FilePipeWriter.WatchDiskFlushes(append.FilePipe!);
                await WriteRowsAsync(append, Rows);
                await append.CompleteAsync(TestContext.Current.CancellationToken);
            }

            Assert.Equal(new[] { PostscriptStart(path), new FileInfo(path).Length }, flushes);
            await using VortexFile file = await VortexFile.OpenAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(2 * Rows, file.RowCount);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AFileIsNotFlushedToTheDeviceUnlessAsked()
    {
        string path = TempPath();
        try
        {
            List<long> flushes;
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter(path, Schema))
            {
                flushes = FilePipeWriter.WatchDiskFlushes(writer.FilePipe!);
                await WriteRowsAsync(writer, 0);
                await writer.CompleteAsync(TestContext.Current.CancellationToken);
            }

            Assert.False(new VortexWriteOptions().Durable);
            Assert.Empty(flushes);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static async Task WriteRowsAsync(VortexFileWriter writer, long first)
    {
        ColumnsBuilder builder = writer.Builder();
        for (long i = first; i < first + Rows; i++)
        {
            builder.Column<long>(0).Append(i);
        }

        await writer.WriteAsync(builder, CancellationToken.None);
    }

    /// <summary>Where the postscript starts: before it, and its eight-byte end record, the length the end record gives.</summary>
    private static long PostscriptStart(string path)
    {
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        int postscript = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(bytes.Length - 8 + 2));
        return bytes.Length - 8 - postscript;
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"vorticity-durable-{Guid.NewGuid():N}.vortex");
}
