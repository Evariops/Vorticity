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

            Assert.Equal(4_000, cache.HeldBytes);
        }
        finally
        {
            await reader.DisposeAsync();
        }
    }

    /// <summary>
    /// A set whose every segment the cache holds reads nothing and waits for no read in flight: completed where it is
    /// asked, with every permit of the session's reads taken by others and its source untouched.
    /// </summary>
    [Fact]
    public async Task ASetTheCacheServesWholeTakesNoPermitAndReadsNothing()
    {
        byte[] content = Pattern(40_000);
        using TempFile file = new TempFile(content);
        SegmentCache cache = new SegmentCache(1 << 20);
        await using VortexSession session = VortexSession.Create(options =>
        {
            options.SegmentCache = cache;
            options.MaxConcurrentReads = 1;
        });
        CountingSegmentSource source = new CountingSegmentSource(new FileSegmentSource(file.Path_));
        ISegmentReader reader = SessionReader.Wrap(source, session);
        try
        {
            using (SegmentRequestSet first = new SegmentRequestSet())
            {
                for (int i = 0; i < 3; i++)
                {
                    first.Add(Spec((ulong)(i * 8_192), 1_000));
                }

                await reader.ReadManyAsync(first, CancellationToken.None);
            }

            long requests = source.Requests;
            await session.ReadGate.WaitAsync(TestContext.Current.CancellationToken);
            try
            {
                using SegmentRequestSet again = new SegmentRequestSet();
                for (int i = 0; i < 3; i++)
                {
                    again.Add(Spec((ulong)(i * 8_192), 1_000));
                }

                ValueTask read = reader.ReadManyAsync(again, CancellationToken.None);
                Assert.True(read.IsCompletedSuccessfully);
                await read;
                Assert.True(again.IsPopulated);
                Assert.Equal(requests, source.Requests);
                Assert.True(again.GetBuffer(2).Span.SequenceEqual(content.AsSpan(16_384, 1_000)));
            }
            finally
            {
                session.ReadGate.Release();
            }
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
            Assert.Equal(1_000, cache.HeldBytes);
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
    public async Task ASessionGivenNoCacheKeepsItsSegmentsInTheProcesssUntilItsFileCloses()
    {
        byte[] content = Pattern(40_000);
        using TempFile file = new TempFile(content);
        await using VortexSession session = VortexSession.Create(_ => { });
        FileSegmentSource source = new FileSegmentSource(file.Path_);
        ISegmentReader reader = SessionReader.Wrap(source, session);
        try
        {
            using SegmentOwner read = await reader.ReadAsync(Spec(2_048, 1_000), CancellationToken.None);
            Assert.True(SegmentCache.Shared.TryGet(source, 2_048, 1_000, out SegmentOwner kept, out VortexBuffer view));
            try
            {
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

        Assert.False(SegmentCache.Shared.TryGet(source, 2_048, 1_000, out _, out _));
    }

    [Fact]
    public async Task ASessionThatHoldsKeysOrGivesACacheOfNoBytesKeepsNothing()
    {
        // A session with a keyring would leave the plaintext of its sealed files to every session of the process.
        using VortexKeyring keyring = Sealing.SealedObjects.Keyring();
        byte[] content = Pattern(40_000);
        using TempFile file = new TempFile(content);
        foreach (Action<VortexSessionOptions> configure in (Action<VortexSessionOptions>[])[o => o.Keyring = keyring, o => o.SegmentCache = new SegmentCache(0)])
        {
            await using VortexSession session = VortexSession.Create(configure);
            Assert.Null(session.Options.ReadCache);
            FileSegmentSource source = new FileSegmentSource(file.Path_);
            ISegmentReader reader = SessionReader.Wrap(source, session);
            try
            {
                using SegmentOwner read = await reader.ReadAsync(Spec(2_048, 1_000), CancellationToken.None);
                Assert.False(SegmentCache.Shared.TryGet(source, 2_048, 1_000, out _, out _));
            }
            finally
            {
                await reader.DisposeAsync();
            }
        }
    }

    [Fact]
    public void KeptSegmentsLeaveInTheirClockOrderAndWithTheirFile()
    {
        // Keeps, lookups and file closes drawn at random, against a model of what the cache must
        // hold: past the budget the oldest kept leaves first unless a lookup used it since an
        // eviction last passed it, which then passes it over once and makes it the newest
        // (a clock, or second chance); keeping it again changes nothing; and a file that closes
        // takes its segments and no other.
        Random random = new Random(42);
        object[] files = [new object(), new object(), new object(), new object(), new object(), new object()];
        const long capacity = 64 * 40;
        SegmentCache cache = new SegmentCache(capacity);
        List<(object File, long Offset, bool Used)> model = [];
        for (int step = 0; step < 5_000; step++)
        {
            object file = files[random.Next(files.Length)];
            long offset = random.Next(30) * 64L;
            int what = random.Next(10);
            int at = model.FindIndex(entry => ReferenceEquals(entry.File, file) && entry.Offset == offset);
            if (what < 5)
            {
                NativeSegmentOwner owner = AlignedBufferPool.Shared.Rent(64, 64);
                cache.Add(file, offset, owner, owner.Buffer);
                owner.Release();
                if (at < 0)
                {
                    while ((model.Count + 1) * 64L > capacity)
                    {
                        (object File, long Offset, bool Used) oldest = model[^1];
                        model.RemoveAt(model.Count - 1);
                        if (oldest.Used)
                        {
                            model.Insert(0, oldest with { Used = false });
                        }
                    }

                    model.Insert(0, (file, offset, false));
                }
            }
            else if (what < 9)
            {
                bool found = cache.TryGet(file, offset, 64, out SegmentOwner kept, out _);
                Assert.Equal(at >= 0, found);
                if (found)
                {
                    kept.Release();
                    model[at] = model[at] with { Used = true };
                }
            }
            else
            {
                cache.Evict(file);
                model.RemoveAll(entry => ReferenceEquals(entry.File, file));
            }

            Assert.Equal(model.Count * 64L, cache.HeldBytes);
        }

        // Each segment the model holds is there.
        Assert.NotEmpty(model);
        foreach ((object file, long offset, _) in model)
        {
            Assert.True(cache.TryGet(file, offset, 64, out SegmentOwner kept, out _));
            kept.Release();
        }

        cache.Clear();
        Assert.Equal(0, cache.HeldBytes);
        Assert.False(cache.TryGet(model[0].File, model[0].Offset, 64, out _, out _));
    }
}
