// A test that counts what a store serves a question counts it as a store without a cache serves it: every session given
// no segment cache shares the process's, which a question asked before, or the first pass of the same question, fills.
namespace Vorticity.Tests;

internal static class Uncached
{
    /// <summary>A session that keeps no segment, for a test that counts the store's reads of a question.</summary>
    internal static VortexSession Session { get; } = VortexSession.Create(static options => options.SegmentCache = new SegmentCache(0));
}
