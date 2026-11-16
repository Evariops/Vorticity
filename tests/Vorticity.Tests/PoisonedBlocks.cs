using System.Runtime.CompilerServices;
using Vorticity.Buffers;

namespace Vorticity.Tests;

/// <summary>
/// Every block given back to the pool is poisoned throughout these tests: the pool lends it again, to
/// another table of the query or of the next one, so code that still read one after giving it back
/// would read another's data in production; here it reads garbage, and its test fails.
/// </summary>
internal static class PoisonedBlocks
{
    [ModuleInitializer]
    internal static void Poison() => Blocks.PoisonsGiven = true;
}
