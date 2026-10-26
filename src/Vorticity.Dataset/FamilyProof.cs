using System;
using System.Collections.Generic;
using Vorticity.Aggregating;
using Vorticity.Scanning;

namespace Vorticity.Dataset;

/// <summary>
/// A family as each object makes it (<see cref="LearnedFamily.Without"/>), and the key its partial is kept under: the conjuncts
/// the object's summaries prove true over its every row left out, so that a question whose window moves finds the partials of
/// the objects it holds whole; the key its identity and the version's columns', so that a change of the schema, a column
/// dropped and added again under its name, learns them afresh.
/// </summary>
/// <remarks>
/// A conjunct on the key's first column alone that an object's summaries neither prove nor refute is an edge of such a window,
/// which would learn a partial each time it moves: the object makes no family, and is read as the question reads it.
/// </remarks>
internal sealed class FamilyProof
{
    private readonly LearnedFamily _family;
    private readonly (SummaryPruner Prover, bool Leading)[] _conjuncts;
    private readonly UInt128 _schema;
    private readonly Dictionary<ulong, (LearnedFamily Family, UInt128 Key)> _families = [];

    /// <param name="family">The question's family.</param>
    /// <param name="schema">The version's columns.</param>
    internal FamilyProof(LearnedFamily family, DatasetSchema schema)
    {
        _family = family;
        _schema = System.IO.Hashing.XxHash128.HashToUInt128(schema.Bytes.Span);
        string? leading = schema.Key?.InstantPath;
        VortexExpr[] conjuncts = family.Conjuncts;
        _conjuncts = new (SummaryPruner, bool)[conjuncts.Length];
        for (int i = 0; i < conjuncts.Length; i++)
        {
            HashSet<string> paths = [];
            conjuncts[i].CollectFields(paths);
            _conjuncts[i] = (new SummaryPruner(conjuncts[i]), leading is not null && paths.Count == 1 && paths.Contains(leading));
        }
    }

    /// <summary>The family of the object of <paramref name="rows"/> rows whose summaries are <paramref name="summaries"/>, and its key; null for an edge.</summary>
    internal (LearnedFamily Family, UInt128 Key)? Of(ObjectSummaries summaries, long rows)
    {
        ulong proven = 0;
        for (int i = 0; i < _conjuncts.Length && i < 64; i++)
        {
            if (_conjuncts[i].Prover.MustMatch(summaries.Columns, rows))
            {
                proven |= 1UL << i;
            }
            else if (_conjuncts[i].Leading)
            {
                return null;
            }
        }

        if (!_families.TryGetValue(proven, out (LearnedFamily Family, UInt128 Key) known))
        {
            LearnedFamily own = _family.Without(proven);
            _families[proven] = known = (own, own.Identity ^ _schema);
        }

        return known;
    }
}
