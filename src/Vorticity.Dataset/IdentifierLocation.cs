namespace Vorticity.Dataset;

/// <summary>
/// What the postings of an identifier place each of its values at (<see cref="DatasetOptions.IdentifierLocation"/>): a value
/// lies at a place of an object, which a lookup of it reads.
/// </summary>
public enum IdentifierLocation
{
    /// <summary>
    /// A unit of granules whose rows hold about a chunk's bytes of the identifier, the default: fewer places, so fewer bytes
    /// of postings a row, and a lookup reads that much of the identifier for each unit a value lies in, then a granule of
    /// each other column at the rows that hold it.
    /// </summary>
    Chunk,

    /// <summary>
    /// The granule: about a byte of postings a row more, and a lookup reads a granule of each column for each granule a
    /// value lies in. For a dataset whose questions are mostly lookups of one value.
    /// </summary>
    Granule,
}
