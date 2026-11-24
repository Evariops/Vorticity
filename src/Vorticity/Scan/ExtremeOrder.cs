using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Scanning;

/// <summary>
/// How the extremes several sources answer are ordered to keep the best: a set's members, a dataset's objects, a file's
/// splits. A decimal column's extreme has no one form: an unscaled value within 64 bits is read as an integer, one past
/// it as its bytes, least significant first, which no order of literals ranks, so a decimal's are compared as the
/// numbers they are.
/// </summary>
internal static class ExtremeOrder
{
    /// <summary>
    /// Whether <paramref name="candidate"/> is a better extreme than <paramref name="best"/>, or there is no best yet: a
    /// decimal column's by number, a candidate that does not read as one never winning; any other column's by the total
    /// order of keys, a candidate of another kind of literal than the best's winning.
    /// </summary>
    internal static bool Beats(FilterLiteral candidate, FilterLiteral best, bool wantMin, bool decimals)
    {
        if (candidate.Kind == FilterLiteralKind.Null)
        {
            return false;
        }

        if (best.Kind == FilterLiteralKind.Null)
        {
            return true;
        }

        if (decimals)
        {
            return ComparisonKernels.TryDecimal(candidate, out Int256 value) && ComparisonKernels.TryDecimal(best, out Int256 current)
                && (wantMin ? value.CompareTo(current) < 0 : value.CompareTo(current) > 0);
        }

        return candidate.Kind != best.Kind || (wantMin ? KeyOrder.Total(candidate, best) < 0 : KeyOrder.Total(candidate, best) > 0);
    }

    /// <summary>
    /// How <paramref name="left"/> orders against <paramref name="right"/>, both of one kind: a decimal column's by number,
    /// null when either does not read as one; any other column's by the total order of keys.
    /// </summary>
    internal static int? Compare(FilterLiteral left, FilterLiteral right, bool decimals)
    {
        if (!decimals)
        {
            return KeyOrder.Total(left, right);
        }

        return ComparisonKernels.TryDecimal(left, out Int256 a) && ComparisonKernels.TryDecimal(right, out Int256 b) ? a.CompareTo(b) : null;
    }

    /// <summary>Whether <paramref name="path"/> names a decimal column of <paramref name="schema"/>, an extension over one included.</summary>
    internal static bool IsDecimal(VortexSchema schema, string path) =>
        TerminalScan.IsDecimal(VortexTypes.ToDType(schema, new DTypeArena()), path);

    /// <summary>Whether the top-level column <paramref name="path"/> names, its one index, is a decimal column of <paramref name="schema"/>.</summary>
    internal static bool IsDecimal(VortexSchema schema, int[] path) =>
        path.Length == 1 && (uint)path[0] < (uint)schema.Count && IsDecimal(schema, schema[path[0]].Name);
}
