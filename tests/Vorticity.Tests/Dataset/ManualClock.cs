// A clock a test moves by hand, so that an object can be older than a retention window without a
// test waiting for one.
using System;

namespace Vorticity.Tests.Dataset;

internal sealed class ManualClock : TimeProvider
{
    internal ManualClock(DateTimeOffset now) => Now = now;

    internal DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;

    internal void Advance(TimeSpan by) => Now += by;
}
