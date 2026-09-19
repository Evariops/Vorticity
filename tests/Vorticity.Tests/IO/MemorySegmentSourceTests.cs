using System.Threading;
using System.Threading.Tasks;
using Vorticity;
using Vorticity.Buffers;
using Vorticity.IO;
using Xunit;
using static Vorticity.Tests.IO.IoTestData;

namespace Vorticity.Tests.IO;

/// <summary>
/// The in-memory source held to the all-or-nothing rule the other sources follow
/// (docs/03-architecture.md §3.5): a batch either fills every slot or leaves the set as it found
/// it. It is the source tests and small readers run on, so a set it half-fills is a set every
/// caller after it inherits.
/// </summary>
public sealed class MemorySegmentSourceTests
{
    [Fact]
    public async Task A_failing_slot_leaves_no_slot_filled_behind_it()
    {
        MemorySegmentSource source = new MemorySegmentSource(Pattern(256));
        using SegmentRequestSet set = new SegmentRequestSet();

        int first = set.Add(Spec(0, 32));
        int second = set.Add(Spec(64, 32));
        int escapes = set.Add(Spec(192, 128));

        await Assert.ThrowsAsync<VortexFormatException>(
            async () => await source.ReadManyAsync(set, CancellationToken.None));

        Assert.False(set.IsFilled(first));
        Assert.False(set.IsFilled(second));
        Assert.False(set.IsFilled(escapes));
        Assert.False(set.IsPopulated);
    }

    [Fact]
    public async Task The_same_set_reads_again_after_a_failure()
    {
        // The point of releasing them: the set is usable, so a caller that retries against a
        // source able to serve the whole batch gets every slot rather than a mix of two vintages.
        using SegmentRequestSet set = new SegmentRequestSet();
        int first = set.Add(Spec(0, 32));
        int escapes = set.Add(Spec(192, 128));

        MemorySegmentSource tooShort = new MemorySegmentSource(Pattern(256));
        await Assert.ThrowsAsync<VortexFormatException>(
            async () => await tooShort.ReadManyAsync(set, CancellationToken.None));

        MemorySegmentSource whole = new MemorySegmentSource(Pattern(512));
        await whole.ReadManyAsync(set, CancellationToken.None);

        Assert.True(set.IsPopulated);
        Assert.Equal(32, set.GetBuffer(first).Length);
        Assert.Equal(128, set.GetBuffer(escapes).Length);
    }

    [Fact]
    public async Task A_populated_set_is_left_alone()
    {
        // The early return the other three sources have: a second pass over a finished set is a
        // no-op, not a re-read, and not a `SetResult` onto a slot that already holds an owner.
        MemorySegmentSource source = new MemorySegmentSource(Pattern(256));
        using SegmentRequestSet set = new SegmentRequestSet();
        int slot = set.Add(Spec(0, 32));

        await source.ReadManyAsync(set, CancellationToken.None);
        Assert.True(set.IsPopulated);
        SegmentOwner owner = set.GetOwner(slot);

        await source.ReadManyAsync(set, CancellationToken.None);

        Assert.Same(owner, set.GetOwner(slot));
        Assert.Equal(1, owner.RefCount);
    }
}
