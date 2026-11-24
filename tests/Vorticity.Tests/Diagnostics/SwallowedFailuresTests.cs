// The failures a path of comfort lets go: counted by where, so that one that repeats shows, and never an out-of-memory.
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using Xunit;

namespace Vorticity.Tests.Diagnostics;

public sealed class SwallowedFailuresTests
{
    /// <summary>
    /// A failure a path of comfort lets go is counted under its site; an out-of-memory is not let go, and goes on to what
    /// the process does with it, uncounted.
    /// </summary>
    [Fact]
    public void AFailureLetGoIsCountedByItsSiteAndAnOutOfMemoryIsNever()
    {
        const string Site = "tests.swallowed";
        long counted = 0;
        using MeterListener listener = new MeterListener
        {
            InstrumentPublished = (instrument, listening) =>
            {
                if (instrument.Name == "vortex.failures.swallowed")
                {
                    listening.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "vortex.site" && Site.Equals(tag.Value))
                {
                    counted += value;
                }
            }
        });
        listener.Start();

        Assert.True(VortexTelemetry.LetsGo(new IOException("A read ahead that failed."), Site));
        Assert.False(VortexTelemetry.LetsGo(new OutOfMemoryException(), Site));
        Assert.Equal(1, counted);
    }
}
