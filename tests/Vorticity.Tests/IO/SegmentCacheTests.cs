using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.IO;
using Xunit;
using static Vorticity.Tests.IO.IoTestData;

namespace Vorticity.Tests.IO;

/// <summary>
/// The segments a session keeps across scans: what the cache counts is what it holds, so a segment
/// read as a view of a larger block is kept in a block of its own.
/// </summary>
public sealed class SegmentCacheTests
{
    [Fact]
    public async Task ASegmentReadAsAViewOfARunIsKeptInABlockOfItsOwn()
    {
        byte[] content = Pattern(40_000);
        using TempFile file = new TempFile(content);
        SegmentCache cache = new SegmentCache(1 << 20);
        await using VortexSession session = VortexSession.Create(options => options.SegmentCache = cache);
        FileSegmentSource source = new FileSegmentSource(file.Path_);
        ISegmentReader reader = SessionReader.Wrap(source, session);
        try
        {
            using SegmentRequestSet set = new SegmentRequestSet();
            for (int i = 0; i < 4; i++)
            {
                set.Add(Spec((ulong)(i * 1_024), 1_000));
            }

            await reader.ReadManyAsync(set, CancellationToken.None);

            // One run, read once and published as views of its block.
            Assert.Same(set.GetOwner(0), set.GetOwner(3));
            Assert.True(set.GetOwner(1).Buffer.Length > 1_000);

            Assert.True(cache.TryGet(source, 1_024, 1_000, out SegmentOwner kept, out VortexBuffer view));
            try
            {
                Assert.Equal(1_000, kept.Buffer.Length);
                Assert.True(view.Span.SequenceEqual(content.AsSpan(1_024, 1_000)));
            }
            finally
            {
                kept.Release();
            }

            Assert.Equal(4_000, cache.Size);
        }
        finally
        {
            await reader.DisposeAsync();
        }
    }

    [Fact]
    public async Task ASlotFilledBeforeTheReadIsNotKept()
    {
        // Filled from memory the caller already holds, a segment its scan kept or the tail its file
        // read: the session did not read it, and keeping it would hold its bytes twice.
        byte[] content = Pattern(40_000);
        using TempFile file = new TempFile(content);
        SegmentCache cache = new SegmentCache(1 << 20);
        await using VortexSession session = VortexSession.Create(options => options.SegmentCache = cache);
        FileSegmentSource source = new FileSegmentSource(file.Path_);
        ISegmentReader reader = SessionReader.Wrap(source, session);
        try
        {
            using SegmentRequestSet set = new SegmentRequestSet();
            int held = set.Add(Spec(0, 1_000));
            set.Add(Spec(8_192, 1_000));
            using (SegmentOwner owner = PinnedArraySegmentOwner.CopyOf(content.AsSpan(0, 1_000), 64))
            {
                set.SetSharedResult(held, owner, owner.Buffer);
            }

            await reader.ReadManyAsync(set, CancellationToken.None);

            Assert.False(cache.TryGet(source, 0, 1_000, out _, out _));
            Assert.Equal(1_000, cache.Size);
        }
        finally
        {
            await reader.DisposeAsync();
        }
    }

    [Fact]
    public async Task ASegmentThatIsAllItsBlockHoldsIsKeptWithoutACopy()
    {
        byte[] content = Pattern(40_000);
        using TempFile file = new TempFile(content);
        SegmentCache cache = new SegmentCache(1 << 20);
        await using VortexSession session = VortexSession.Create(options => options.SegmentCache = cache);
        FileSegmentSource source = new FileSegmentSource(file.Path_);
        ISegmentReader reader = SessionReader.Wrap(source, session);
        try
        {
            using SegmentOwner read = await reader.ReadAsync(Spec(2_048, 1_000), CancellationToken.None);
            Assert.True(cache.TryGet(source, 2_048, 1_000, out SegmentOwner kept, out VortexBuffer view));
            try
            {
                Assert.Same(read, kept);
                Assert.True(view.Span.SequenceEqual(content.AsSpan(2_048, 1_000)));
            }
            finally
            {
                kept.Release();
            }
        }
        finally
        {
            await reader.DisposeAsync();
        }
    }
}
