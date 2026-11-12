// An index given up before its payloads went out gives their arrays back.
//
// WHAT IS HELD: abandoning a builder releases each payload it still queues, once, and never lays it;
// a payload laid is released by its lay alone, and a forsaken one cannot be laid after.
using System;
using Vorticity.Arrays;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class AbandonedPayloadTests
{
    [Fact]
    public void AnAbandonedIndexReleasesWhatItQueuedOnceAndLaysNothing()
    {
        int released = 0;
        int laid = 0;
        using Builder builder = new Builder();
        PendingPayload first = Payload(() => laid++, () => released++);
        PendingPayload second = Payload(() => laid++, () => released++);
        builder.Enqueue(first);
        builder.Enqueue(second);

        builder.Abandon("over its budget");

        Assert.Equal((2, 0), (released, laid));
        Assert.Empty(builder.Pending);
        Assert.Throws<InvalidOperationException>(() => first.Build(new CanonicalArena(), new DTypeArena()));
        first.Forsake();
        Assert.Equal(2, released);
    }

    [Fact]
    public void APayloadLaidIsReleasedByItsLayAlone()
    {
        int released = 0;
        int laid = 0;
        PendingPayload payload = Payload(() => laid++, () => released++);

        payload.Build(new CanonicalArena(), new DTypeArena());
        payload.Forsake();

        Assert.Equal((1, 1), (released, laid));
    }

    private static PendingPayload Payload(Action lay, Action release) =>
        new PendingPayload(
            (arena, types) =>
            {
                lay();
                return arena.AddPrimitive(
                    types.Primitive(PType.U32, Nullability.NonNullable), 0, Validity.NonNullable, PType.U32, default);
            },
            compress: false,
            estimate: 4,
            release);

    /// <summary>A builder that only queues what it is given.</summary>
    private sealed class Builder : IndexBuilder
    {
        internal override void Accumulate(CanonicalArena arena, int nodeIndex, int start, int count)
        {
        }

        internal override void CloseBlock()
        {
        }
    }
}
