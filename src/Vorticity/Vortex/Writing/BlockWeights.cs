using System.Threading;

namespace Vorticity.Writing;

/// <summary>
/// The canonical bytes an object's writer sends through, block after block, in the order of its flights: what the job that
/// writes the object reads behind the writer's line to place where it reads the object's size, without ending any chunk.
/// </summary>
/// <remarks>
/// <para>
/// Each flight's block is counted as its writer sizes it to cut its chunks (the bytes its columns name), once, in the order
/// of the flights: at its prologue on the writer's line, or as it goes through off the line. The running total is kept for
/// the last <see cref="Ring"/> flights, so that a job reads the total through a flight its writer says landed, which is the
/// same at any degree, rather than through whichever flights landed when it looks.
/// </para>
/// <para>
/// The job owns it, one for the object it writes, and gives it to the object's writer before its first row.
/// </para>
/// </remarks>
internal sealed class BlockWeights
{
    /// <summary>The flights a total is kept for: more than a line has in flight (<see cref="VortexFileWriter.FlightsAhead"/>).</summary>
    internal const int Ring = 16;

    // The total through each of the last flights, at the flight's place modulo Ring; and the total so far, which the flights
    // count in their order, one after another.
    private readonly long[] _through = new long[Ring];
    private long _total;

    /// <summary>Counts <paramref name="named"/> bytes for flight <paramref name="flight"/>, the one after those counted.</summary>
    internal void Weigh(long flight, long named)
    {
        _total += named;
        Volatile.Write(ref _through[flight % Ring], _total);
    }

    /// <summary>
    /// The bytes the flights up to <paramref name="flight"/> named, together: one of the last <see cref="Ring"/> counted;
    /// 0 for none.
    /// </summary>
    internal long Through(long flight) => flight < 0 ? 0 : Volatile.Read(ref _through[flight % Ring]);

    /// <summary>Forgets every flight, for the next object's writer.</summary>
    internal void Reset()
    {
        _total = 0;
        System.Array.Clear(_through);
    }
}
