using System;
using System.Collections.Generic;
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

    [Fact]
    public void KeptSegmentsLeaveInTheirOrderOfUseAndWithTheirFile()
    {
        // Keeps, lookups and file closes drawn at random, against a model of what the cache must
        // hold: past the budget the least recently used leaves first, a lookup makes a segment the
        // most recently used and keeping it again does not, and a file that closes takes its
        // segments and no other.
        Random random = new Random(42);
        object[] files = [new object(), new object(), new object(), new object(), new object(), new object()];
        const long capacity = 64 * 40;
        SegmentCache cache = new SegmentCache(capacity);
        List<(object File, long Offset)> model = [];
        for (int step = 0; step < 5_000; step++)
        {
            object file = files[random.Next(files.Length)];
            long offset = random.Next(30) * 64L;
            int what = random.Next(10);
            if (what < 5)
            {
                NativeSegmentOwner owner = AlignedBufferPool.Shared.Rent(64, 64);
                cache.Add(file, offset, owner, owner.Buffer);
                owner.Release();
                if (!model.Contains((file, offset)))
                {
                    while ((model.Count + 1) * 64L > capacity)
                    {
                        model.RemoveAt(model.Count - 1);
                    }

                    model.Insert(0, (file, offset));
                }
            }
            else if (what < 9)
            {
                bool found = cache.TryGet(file, offset, 64, out SegmentOwner kept, out _);
                Assert.Equal(model.Contains((file, offset)), found);
                if (found)
                {
                    kept.Release();
                    model.Remove((file, offset));
                    model.Insert(0, (file, offset));
                }
            }
            else
            {
                cache.Evict(file);
                model.RemoveAll(entry => ReferenceEquals(entry.File, file));
            }

            Assert.Equal(model.Count * 64L, cache.Size);
        }

        // Each segment the model holds is there; looked up from the oldest, they leave again in
        // the order the model says once the budget is spent.
        Assert.NotEmpty(model);
        foreach ((object file, long offset) in model)
        {
            Assert.True(cache.TryGet(file, offset, 64, out SegmentOwner kept, out _));
            kept.Release();
        }

        cache.Clear();
        Assert.Equal(0, cache.Size);
        Assert.False(cache.TryGet(model[0].File, model[0].Offset, 64, out _, out _));
    }
}
