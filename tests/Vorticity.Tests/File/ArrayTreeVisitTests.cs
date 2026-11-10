// The array trees of a file's chunks visited together: each chunk's tree, in the order asked, the one a read of it alone
// loads, across more chunks than one request takes; the tails asked for in one request a batch of 256 chunks.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.IO;
using Vorticity.Layouts;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.File;

public sealed partial class ArrayTreeVisitTests
{
    [VortexRecord]
    public partial record struct Row(string Kind, long Drawn, long Rising);

    [Fact]
    public async Task TreesVisitedTogetherAreThoseReadOneByOne()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tree-visit-{Guid.NewGuid():N}.vortex");
        byte[] bytes;
        try
        {
            Random random = new Random(41);
            string[] kinds = ["alpha", "beta", "gamma", "delta", "epsilon"];
            Row[] rows = [.. Enumerable.Range(0, 150_000).Select(i => new Row(kinds[random.Next(5)], random.NextInt64(), i))];
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path, new VortexWriteOptions { ChunkTargetBytes = 1 << 13, BlockRows = 1_024 }))
            {
                await writer.WriteAsync<Row>(rows, cancellationToken);
                await writer.CompleteAsync(cancellationToken);
            }

            bytes = await System.IO.File.ReadAllBytesAsync(path, cancellationToken);
        }
        finally
        {
            System.IO.File.Delete(path);
        }

        RequestCounter source = new RequestCounter(new MemorySegmentSource(bytes));
        await using VortexFile file = await VortexSession.Default.OpenAsync(source, cancellationToken: cancellationToken);
        List<LayoutNode> flats = [];
        for (int field = 0; field < 3; field++)
        {
            (LayoutNode node, _) = VortexFileWriter.AppendPlan.ColumnNode(file.LayoutTree, field, codes: true);
            for (int c = 0; c < VortexFileWriter.AppendPlan.ChunkCount(node); c++)
            {
                flats.Add(VortexFileWriter.AppendPlan.Chunk(node, c));
            }
        }

        Assert.InRange(flats.Count, 257, 512);
        List<string> expected = [];
        foreach (LayoutNode flat in flats)
        {
            ArrayNodeArena alone = new ArrayNodeArena();
            await file.LoadArrayTreeAsync(flat, alone, cancellationToken);
            expected.Add(Signature(alone.Root));
        }

        int before = source.Requests;
        List<(int At, string Tree)> visited = [];
        await file.VisitArrayTreesAsync(flats, new ArrayNodeArena(), visited, static (into, at, tree) => into.Add((at, Signature(tree.Root))), cancellationToken);

        Assert.Equal(Enumerable.Range(0, flats.Count), visited.Select(v => v.At));
        Assert.Equal(expected, visited.Select(v => v.Tree));
        Assert.Contains(expected, tree => tree.StartsWith(ArrayEncodingId.Dict.ToString(), StringComparison.Ordinal));
        Assert.Contains(expected, tree => !tree.StartsWith(ArrayEncodingId.Dict.ToString(), StringComparison.Ordinal));
        Assert.InRange(source.Requests - before, 1, 2);
    }

    private static string Signature(ArrayNode node)
    {
        StringBuilder text = new StringBuilder(node.Encoding.ToString());
        for (int i = 0; i < node.ChildCount; i++)
        {
            text.Append(i == 0 ? '(' : ',').Append(Signature(node.GetChild(i)));
        }

        text.Append(node.ChildCount > 0 ? ")" : string.Empty);

        return text.ToString();
    }

    /// <summary>A source that counts the requests asked of it, one range or many.</summary>
    private sealed class RequestCounter(ISegmentSource inner) : ISegmentSource
    {
        private int _requests;

        public long Length => inner.Length;

        internal int Requests => _requests;

        public ValueTask<SegmentLease> ReadAsync(SegmentRange range, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return inner.ReadAsync(range, cancellationToken);
        }

        public ValueTask ReadAsync(ReadOnlyMemory<SegmentRange> ranges, Memory<SegmentLease> leases, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return inner.ReadAsync(ranges, leases, cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
