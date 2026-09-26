using System;
using Vorticity.Serialization.FlatBuffers;

namespace Vorticity.Tests;

/// <summary>
/// A root read without a budget of its own, for tests over buffers they build by hand. The library
/// has no such root: it reads every one with a budget, so that no traversal of a file goes unbounded.
/// </summary>
internal static class FlatBufferTestRoots
{
    // Shared by every table these roots reach, and far more than the tests visit together.
    private static int s_budget = int.MaxValue;

    extension(FlatBufferTable)
    {
        /// <summary>The root table of <paramref name="buffer"/>, charged to a budget no test exhausts.</summary>
        /// <param name="buffer">The whole FlatBuffer, starting at its root uoffset.</param>
        /// <returns>The root table.</returns>
        internal static FlatBufferTable Root(ReadOnlySpan<byte> buffer) => FlatBufferTable.Root(buffer, ref s_budget);
    }
}
