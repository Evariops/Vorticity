using System.Runtime.CompilerServices;
using Vorticity.Aggregating;
using Vorticity.Buffers;

namespace Vorticity.Tests;

/// <summary>
/// Every block given back to the pool, and every array a group by gives back to a shelf, is poisoned
/// throughout these tests: the pool lends them again, to another table of the query or of the next
/// one, so code that still read one after giving it back would read another's data in production;
/// here it reads garbage, and its test fails.
/// </summary>
internal static class PoisonedShelves
{
    [ModuleInitializer]
    internal static void Poison()
    {
        ArrayShelf.PoisonsGiven = true;
        Blocks.PoisonsGiven = true;
    }
}
