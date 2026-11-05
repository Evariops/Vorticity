// A file remembers the largest array tree its scans loaded, and a context made for it starts its
// tree buffer there. The scans' contexts are pooled and go from one file to the next with the buffer
// they grew, so what a file remembers is the length of its own trees, not the buffer that held them:
// told the buffer, a file of small trees read after one of wide trees made every context of its own
// at the wide trees' size, 4 KiB where 1 KiB served.
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Scan;

[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed partial class ArrayTreeHintTests
{
    [Fact]
    public async Task AFileRemembersItsOwnTreesNotTheBufferOfTheFileReadBefore()
    {
        string wide = TempPath();
        string narrow = TempPath();
        try
        {
            await WriteAsync(wide, Wide.Rows());
            await WriteAsync(narrow, Narrow.Rows());
            ScanOptions sequential = new ScanOptions { DegreeOfParallelism = 1, Prefetch = 0 };

            // The pooled contexts grow their buffers on the wide file's trees, then read the narrow file.
            await using VortexFile wideFile = await VortexFile.OpenAsync(wide, Ct);
            await foreach (Columns<Wide> batch in wideFile.Scan<Wide>().With(sequential).WithCancellation(Ct))
            {
                GC.KeepAlive(batch.Column<Guid>(0)[0]);
            }

            await using VortexFile narrowFile = await VortexFile.OpenAsync(narrow, Ct);
            await foreach (Columns<Narrow> batch in narrowFile.Scan<Narrow>().With(sequential).WithCancellation(Ct))
            {
                GC.KeepAlive(batch.Column<long>(0).Values[0]);
            }

            Assert.True(narrowFile.LargestArrayTree > 0, "the narrow file's scan loaded no tree");
            Assert.True(
                narrowFile.LargestArrayTree < wideFile.LargestArrayTree,
                $"the narrow file remembers a tree of {narrowFile.LargestArrayTree} bytes, the wide one's of {wideFile.LargestArrayTree}");
        }
        finally
        {
            System.IO.File.Delete(wide);
            System.IO.File.Delete(narrow);
        }
    }

    private static async Task WriteAsync<T>(string path, T[] rows)
        where T : IVortexRecord<T>
    {
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<T>(path);
        await writer.WriteAsync<T>(rows, Ct);
        await writer.CompleteAsync(Ct);
    }

    private static string TempPath() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-tree-hint-{Guid.NewGuid():N}.vortex");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Wide(Guid Id)
    {
        internal static Wide[] Rows()
        {
            Wide[] rows = new Wide[200_000];
            for (int i = 0; i < rows.Length; i++)
            {
                ulong mixed = (ulong)i * 0x9E37_79B9_7F4A_7C15UL;
                rows[i] = new Wide(new Guid((int)(mixed >> 32), (short)mixed, (short)(mixed >> 16), (byte)i, 1, 2, 3, 4, 5, 6, 7));
            }

            return rows;
        }
    }

    [VortexRecord]
    public partial record struct Narrow(long Value)
    {
        internal static Narrow[] Rows()
        {
            Narrow[] rows = new Narrow[200_000];
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new Narrow(i);
            }

            return rows;
        }
    }
}
