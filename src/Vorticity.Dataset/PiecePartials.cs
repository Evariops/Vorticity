using System;
using System.Collections.Generic;
using System.Threading;
using Vorticity.Aggregating;

namespace Vorticity.Dataset;

/// <summary>
/// The partials the lanes of one question learn of the pieces of the objects cut between them, each object's kept until its
/// pieces tile its rows, then merged into the object's partial, which the handle keeps as if the object had been learned
/// whole (<see cref="LearnedPartials.KeepMerged"/>).
/// </summary>
/// <remarks>
/// A question cuts an object the same way however often it runs, so a piece learned twice is the same rows; one that
/// overlaps a piece of another cut starts the object's afresh. An object some lane never finished stays here, unlearned,
/// until the question's source goes.
/// </remarks>
internal sealed class PiecePartials
{
    private readonly Lock _gate = new Lock();
    private readonly Dictionary<(UInt128 Uid, UInt128 Key), List<(RowRange Rows, AggregationPartial Partial)>> _objects = [];
    private readonly HashSet<RowRange> _cut = [];

    /// <summary>Notes <paramref name="piece"/>, a lane's piece of an object that learns its partial, as the question cut it.</summary>
    internal void Expect(RowRange piece)
    {
        lock (_gate)
        {
            _cut.Add(piece);
        }
    }

    /// <summary>
    /// Whether <paramref name="rows"/> are a lane's piece the question cut of an object that learns its partial: a scan of
    /// rows that only cut an object, a window's, learns nothing of it.
    /// </summary>
    internal bool Expects(RowRange rows)
    {
        lock (_gate)
        {
            return _cut.Contains(rows);
        }
    }

    /// <summary>
    /// Takes <paramref name="partial"/>, of <paramref name="family"/> over <paramref name="piece"/> of the object
    /// <paramref name="held"/>, and once the object's pieces tile its rows, has the handle merge and keep its partial.
    /// </summary>
    internal void Learned(
        VortexDataset dataset, ScanSource source, in PositionedObject held, LearnedFamily family, UInt128 key, RowRange piece,
        AggregationPartial partial, ScanOptions options)
    {
        AggregationPartial[] whole;
        lock (_gate)
        {
            (UInt128, UInt128) of = (held.Entry.Uid, key);
            if (!_objects.TryGetValue(of, out List<(RowRange Rows, AggregationPartial Partial)>? pieces))
            {
                _objects[of] = pieces = [];
            }

            foreach ((RowRange rows, AggregationPartial _) in pieces)
            {
                if (rows.Start < piece.End && piece.Start < rows.End)
                {
                    pieces.Clear();
                    break;
                }
            }

            pieces.Add((piece, partial));
            pieces.Sort(static (a, b) => a.Rows.Start.CompareTo(b.Rows.Start));
            long next = held.FirstRow;
            foreach ((RowRange rows, AggregationPartial _) in pieces)
            {
                if (rows.Start != next)
                {
                    return;
                }

                next = rows.End;
            }

            if (next != held.FirstRow + held.Entry.Rows)
            {
                return;
            }

            whole = pieces.ConvertAll(static p => p.Partial).ToArray();
            _objects.Remove(of);
        }

        dataset.Partials.KeepMerged(
            held.Entry.Uid, key, family, whole, source, options, dataset.Session.Options.SynopsisCache, held.Entry.Bytes, dataset.LearnedStore);
    }
}
