using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Writing;

/// <summary>
/// Where a column's hashed postings go when another object holds them: each value's hash, the XXH3-64 of its bytes as hashed
/// postings key it, with the block that holds it, as the rows come. A dataset hands the identifiers of the objects of a
/// consolidation to the index of their partition this way, and the objects keep none of their own.
/// </summary>
internal interface IHashTap
{
    /// <summary>Notes that a value of hash <paramref name="hash"/> lies in block <paramref name="block"/> of the object.</summary>
    void Note(ulong hash, uint block);

    /// <summary>A chunk of the object went out: where the tap may lay what it holds aside, no row being noted meanwhile.</summary>
    ValueTask ChunkClosedAsync(CancellationToken cancellationToken);
}
