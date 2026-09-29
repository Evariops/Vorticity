using System;
using System.Buffers;
using System.Numerics;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Compute;

/// <summary>
/// Per-row comparison of a canonical column against a literal or against another column, under two
/// rules that the obvious code gets wrong. Floats follow IEEE 754, so every comparison involving a NaN is false, including
/// equality against itself and inequality against anything: the kernels use C#'s own operators and
/// never a three-way <c>CompareTo</c>, which orders NaN and would answer with the row encoding's
/// total order instead. Signedness is resolved before the loop rather than folded into a cast,
/// because a negative literal against an unsigned column, or one above the signed maximum against a
/// signed column, settles every row from the sign alone, while a cast would answer the opposite
/// with nothing to show for it below the point where the two ranges part.
/// </summary>
/// <remarks>
/// Validity belongs to the caller: every kernel writes unknown for a null row and never reads the
/// value stored there.
/// </remarks>
internal static partial class ComparisonKernels
{
    /// <summary>
    /// Evaluates <c>column op literal</c> into <paramref name="destination"/>.
    /// </summary>
    /// <param name="arena">The arena the node lives in.</param>
    /// <param name="nodeIndex">The column.</param>
    /// <param name="op">The operator, with the column on the left.</param>
    /// <param name="literal">The constant.</param>
    /// <param name="destination">One <see cref="Trilean"/> state per row.</param>
    /// <exception cref="NotSupportedException">
    /// The column's canonical form or the literal's type is outside the 1.0 filter scope.
    /// </exception>
    internal static void Compare(
        CanonicalArena arena, int nodeIndex, ComparisonOp op, FilterLiteral literal,
        Span<byte> destination)
    {
        // An extension is its storage plus a label; comparing the label is meaningless and
        // comparing the storage is what a timestamp filter actually wants.
        nodeIndex = Unwrap(arena, nodeIndex);

        if (literal.Kind == FilterLiteralKind.Null)
        {
            // A comparison with a null operand yields unknown, for every row, whatever the column
            // holds. The null check is the predicate that asks the question this one cannot.
            Trilean.Fill(destination, Trilean.Unknown);
            return;
        }

        if (EncodedAnswers.IsEncoded(arena, nodeIndex))
        {
            if (EncodedAnswers.TryValues(arena, nodeIndex, destination.Length, out int values, out int count))
            {
                byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(count, 1));
                try
                {
                    Span<byte> answers = rented.AsSpan(0, count);
                    Compare(arena, values, op, literal, answers);
                    EncodedAnswers.Expand(arena, nodeIndex, answers, destination);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }

                return;
            }

            nodeIndex = arena.MaterializeEncoded(nodeIndex);
        }

        CanonicalNode node = arena.GetNode(nodeIndex);
        switch (node.Kind)
        {
            case CanonicalKind.Null:
                Trilean.Fill(destination, Trilean.Unknown);
                return;

            case CanonicalKind.Bool:
                CompareBool(arena, node, op, literal, destination);
                return;

            case CanonicalKind.Primitive:
                ComparePrimitive(arena, node, op, literal, destination);
                return;

            case CanonicalKind.VarBinView:
                CompareBytes(arena, node, op, literal, destination);
                return;

            case CanonicalKind.Decimal:
                CompareDecimalValues(
                    node.Storage, node.Values.Span, ValidityMask.From(arena, node.Validity), op,
                    literal, destination);
                return;

            case CanonicalKind.FixedSizeList:
                CompareFixedBytes(arena, node, op, literal, destination);
                return;

            case CanonicalKind.Constant:
                CompareConstant(arena, node, op, literal, destination);
                return;

            default:
                throw new NotSupportedException(
                    $"A filter cannot compare a {node.Kind} column. A filter evaluates " +
                    "booleans, primitives, decimals, utf8, binary and fixed-size lists of bytes, " +
                    "plus extensions over those.");
        }
    }

    /// <summary>Evaluates <c>StartsWith</c>, <c>Contains</c> or <c>Like</c> over a byte column.</summary>
    /// <param name="arena">The arena.</param>
    /// <param name="nodeIndex">The column.</param>
    /// <param name="op">Which predicate.</param>
    /// <param name="pattern">The pattern, a bytes literal.</param>
    /// <param name="escape">The byte that quotes a wildcard, for <c>Like</c>.</param>
    /// <param name="destination">One state per row.</param>
    /// <exception cref="NotSupportedException">The column is not utf8 or binary.</exception>
    internal static void StringMatch(
        CanonicalArena arena, int nodeIndex, StringMatchOp op, FilterLiteral pattern, byte escape,
        Span<byte> destination)
    {
        int storage = Unwrap(arena, nodeIndex);
        if (EncodedAnswers.IsEncoded(arena, storage))
        {
            // A pattern search is dearer than a compare, so this is where answering the distinct
            // values instead of the rows gains the most.
            if (EncodedAnswers.TryValues(arena, storage, destination.Length, out int values, out int count))
            {
                byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(count, 1));
                try
                {
                    Span<byte> answers = rented.AsSpan(0, count);
                    StringMatch(arena, values, op, pattern, escape, answers);
                    EncodedAnswers.Expand(arena, storage, answers, destination);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }

                return;
            }

            storage = arena.MaterializeEncoded(storage);
        }

        CanonicalNode node = arena.GetNode(storage);

        if (node.Kind == CanonicalKind.Null)
        {
            Trilean.Fill(destination, Trilean.Unknown);
            return;
        }

        bool constant = node.Kind == CanonicalKind.Constant &&
                        node.DType.Kind is DTypeKind.Utf8 or DTypeKind.Binary;
        if (node.Kind != CanonicalKind.VarBinView && !constant)
        {
            throw new NotSupportedException(
                $"{op} matches bytes, so it evaluates utf8 and binary columns and extensions over " +
                $"those; this one is {node.Kind}.");
        }

        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        if (mask.AllInvalid)
        {
            Trilean.Fill(destination, Trilean.Unknown);
            return;
        }

        // A `_` takes one character of text and one byte of binary; without one, both match alike, and
        // the byte matcher serves text too.
        ReadOnlySpan<byte> needle = pattern.BytesValue;
        bool text = op == StringMatchOp.Like && node.DType.Kind == DTypeKind.Utf8 && BytePattern.HasUnescapedOne(needle, escape);
        if (constant)
        {
            // One value, so one match decides every row -- the same collapse the comparisons make,
            // and it reaches further here because a pattern search is dearer than a compare.
            MatchConstant(node, mask, op, needle, escape, text, destination);
            return;
        }

        // The operator becomes a type argument: the switch is asked once per column instead of once
        // per row, and the framework's vectorised search is what runs inside.
        switch (op)
        {
            case StringMatchOp.StartsWith:
                StartsWithViews(node, mask, needle, destination);
                return;
            case StringMatchOp.Contains:
                MatchCore<ContainsMatch>(node, mask, needle, escape, destination);
                return;
            default:
                MatchLike(node, mask, needle, escape, text, destination);
                return;
        }
    }

    /// <summary>The bytes of a pattern whose plan is read onto the stack; a longer one rents them.</summary>
    private const int StackPattern = 256;

    /// <summary>
    /// A <c>like</c> over a column: read once into a plan when it has no <c>_</c>, walked by the
    /// backtracking matcher for each row otherwise.
    /// </summary>
    private static void MatchLike(
        CanonicalNode node, ValidityMask mask, ReadOnlySpan<byte> pattern, byte escape, bool text,
        Span<byte> destination)
    {
        if (text)
        {
            MatchCore<LikeTextMatch>(node, mask, pattern, escape, destination);
            return;
        }

        byte[]? rentedLiterals = null;
        int[]? rentedEnds = null;
        bool small = pattern.Length <= StackPattern;
        Span<byte> literals = small
            ? stackalloc byte[StackPattern]
            : (rentedLiterals = ArrayPool<byte>.Shared.Rent(pattern.Length));
        Span<int> ends = small
            ? stackalloc int[(StackPattern / 2) + 1]
            : (rentedEnds = ArrayPool<int>.Shared.Rent((pattern.Length / 2) + 1));
        try
        {
            if (!BytePattern.TryPlan(pattern, escape, literals, ends, out LikePlan plan))
            {
                MatchCore<LikeMatch>(node, mask, pattern, escape, destination);
            }
            else if (plan.Count != 1)
            {
                PlanCore(node, mask, plan, destination);
            }
            else
            {
                // One segment is one search, which the loops of the other predicates already make
                // without reading the plan again at every row.
                ReadOnlySpan<byte> literal = plan.Segment(0);
                switch ((plan.AnchoredStart, plan.AnchoredEnd))
                {
                    case (true, true):
                        MatchCore<EqualsMatch>(node, mask, literal, escape, destination);
                        break;
                    case (true, false):
                        MatchCore<StartsWithMatch>(node, mask, literal, escape, destination);
                        break;
                    case (false, true):
                        MatchCore<EndsWithMatch>(node, mask, literal, escape, destination);
                        break;
                    default:
                        MatchCore<ContainsMatch>(node, mask, literal, escape, destination);
                        break;
                }
            }
        }
        finally
        {
            if (rentedLiterals is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedLiterals);
                ArrayPool<int>.Shared.Return(rentedEnds!);
            }
        }
    }

    /// <summary>A planned <c>like</c> over a column, with the validity resolved.</summary>
    private static void PlanCore(
        CanonicalNode node, ValidityMask mask, in LikePlan plan, Span<byte> destination)
    {
        ViewValues values = new ViewValues(node);
        bool allValid = mask.AllValid;
        for (int i = 0; i < destination.Length; i++)
        {
            if (!allValid && !mask.IsValid(i))
            {
                destination[i] = Trilean.Unknown;
                continue;
            }

            destination[i] = plan.Holds(values.At(i)) ? Trilean.True : Trilean.False;
        }
    }

    /// <summary>A byte-pattern predicate over a constant column: one match, then a fill.</summary>
    private static void MatchConstant(
        CanonicalNode node, ValidityMask mask, StringMatchOp op, ReadOnlySpan<byte> pattern,
        byte escape, bool text, Span<byte> destination)
    {
        ReadOnlySpan<byte> value = node.ConstantElement;
        bool holds = op switch
        {
            StringMatchOp.StartsWith => StartsWithMatch.Holds(value, pattern, escape),
            StringMatchOp.Contains => ContainsMatch.Holds(value, pattern, escape),
            _ when text => LikeTextMatch.Holds(value, pattern, escape),
            _ => LikeMatch.Holds(value, pattern, escape),
        };

        byte state = holds ? Trilean.True : Trilean.False;
        if (mask.AllValid)
        {
            Trilean.Fill(destination, state);
            return;
        }

        ExpandBits(mask.Bits, mask.BitOffset, destination, Trilean.Unknown, state);
    }

    /// <summary>One byte-pattern predicate, with the operator and the validity resolved.</summary>
    private static void MatchCore<TMatch>(
        CanonicalNode node, ValidityMask mask, ReadOnlySpan<byte> pattern, byte escape,
        Span<byte> destination)
        where TMatch : struct, IBytesMatch
    {
        ViewValues values = new ViewValues(node);
        bool allValid = mask.AllValid;
        for (int i = 0; i < destination.Length; i++)
        {
            if (!allValid && !mask.IsValid(i))
            {
                // A null matches nothing and fails to match nothing: unknown, exactly as a
                // comparison against it is.
                destination[i] = Trilean.Unknown;
                continue;
            }

            destination[i] = TMatch.Holds(values.At(i), pattern, escape)
                ? Trilean.True
                : Trilean.False;
        }
    }

    /// <summary>One byte-pattern predicate as a type, so the dispatch leaves the loop.</summary>
    private interface IBytesMatch
    {
        static abstract bool Holds(ReadOnlySpan<byte> value, ReadOnlySpan<byte> pattern, byte escape);
    }

    private readonly struct StartsWithMatch : IBytesMatch
    {
        public static bool Holds(ReadOnlySpan<byte> value, ReadOnlySpan<byte> pattern, byte escape) =>
            BytePattern.StartsWith(value, pattern);
    }

    private readonly struct ContainsMatch : IBytesMatch
    {
        public static bool Holds(ReadOnlySpan<byte> value, ReadOnlySpan<byte> pattern, byte escape) =>
            BytePattern.Contains(value, pattern);
    }

    private readonly struct EndsWithMatch : IBytesMatch
    {
        public static bool Holds(ReadOnlySpan<byte> value, ReadOnlySpan<byte> pattern, byte escape) =>
            value.EndsWith(pattern);
    }

    private readonly struct EqualsMatch : IBytesMatch
    {
        public static bool Holds(ReadOnlySpan<byte> value, ReadOnlySpan<byte> pattern, byte escape) =>
            value.SequenceEqual(pattern);
    }

    private readonly struct LikeMatch : IBytesMatch
    {
        public static bool Holds(ReadOnlySpan<byte> value, ReadOnlySpan<byte> pattern, byte escape) =>
            BytePattern.Like(value, pattern, escape);
    }

    private readonly struct LikeTextMatch : IBytesMatch
    {
        public static bool Holds(ReadOnlySpan<byte> value, ReadOnlySpan<byte> pattern, byte escape) =>
            BytePattern.LikeText(value, pattern, escape);
    }

    /// <summary>Evaluates <c>column IN (literals)</c>, which is an OR of equalities.</summary>
    /// <param name="arena">The arena.</param>
    /// <param name="nodeIndex">The column.</param>
    /// <param name="literals">The candidates.</param>
    /// <param name="destination">One state per row.</param>
    /// <param name="scratch">A second buffer of the same length.</param>
    /// <param name="prepared">
    /// The candidates already hashed for this column's kind, or <see langword="null"/>.
    /// </param>
    internal static void In(
        CanonicalArena arena, int nodeIndex, ReadOnlySpan<FilterLiteral> literals,
        Span<byte> destination, Span<byte> scratch, CandidateSet? prepared)
    {
        int storage = Unwrap(arena, nodeIndex);
        if (EncodedAnswers.IsEncoded(arena, storage))
        {
            if (EncodedAnswers.TryValues(arena, storage, destination.Length, out int values, out int count))
            {
                byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(count * 2, 1));
                try
                {
                    Span<byte> answers = rented.AsSpan(0, count);
                    In(arena, values, literals, answers, rented.AsSpan(count, count), prepared);
                    EncodedAnswers.Expand(arena, storage, answers, destination);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }

                return;
            }

            nodeIndex = arena.MaterializeEncoded(storage);
        }

        if (prepared is not null && CandidatesFor(arena, nodeIndex) == prepared.Kind)
        {
            Apply(arena, arena.GetNode(Unwrap(arena, nodeIndex)), prepared, destination);
            return;
        }

        // `x IN (a, b)` is `x = a OR x = b`, three-valued logic included: a null x is unknown
        // against every candidate, so the disjunction stays unknown, and a candidate that is itself
        // null makes that one comparison unknown rather than false. One pass per candidate is what
        // that costs when the column or the candidates are not a set.
        Compare(arena, nodeIndex, ComparisonOp.Equal, literals[0], destination);
        for (int i = 1; i < literals.Length; i++)
        {
            Compare(arena, nodeIndex, ComparisonOp.Equal, literals[i], scratch);
            Trilean.Or(destination, scratch);
        }
    }

    /// <summary>The kind of candidate set <paramref name="nodeIndex"/> can be read against.</summary>
    /// <param name="arena">The arena.</param>
    /// <param name="nodeIndex">The column, before the extension wrapper is removed.</param>
    /// <returns><see cref="CandidateKind.None"/> when an OR of equalities is the only answer.</returns>
    /// <remarks>
    /// A decimal is a kind of its own at every width. Its literals arrive unscaled, at the column's
    /// scale, so membership is membership of the integers it stores, but it refuses an unsigned
    /// literal and takes the sixteen and thirty-two byte ones an integer column refuses.
    /// </remarks>
    internal static CandidateKind CandidatesFor(CanonicalArena arena, int nodeIndex)
    {
        // An encoded column is answered through its values, so theirs is the kind a set is built
        // for.
        int storage = Unwrap(arena, nodeIndex);
        if (EncodedAnswers.IsEncoded(arena, storage))
        {
            storage = arena.GetNode(storage).EncodedValuesIndex;
        }

        CanonicalNode column = arena.GetNode(storage);
        switch (column.Kind)
        {
            case CanonicalKind.Primitive:
                PType ptype = column.PType;
                return ptype.IsSignedInteger() ? CandidateKind.Signed
                    : ptype.IsUnsignedInteger() ? CandidateKind.Unsigned
                    : ptype.IsFloat() ? CandidateKind.Float
                    : CandidateKind.None;

            case CanonicalKind.Decimal:
                return column.Storage switch
                {
                    <= DecimalStorageType.I64 => CandidateKind.Decimal,
                    DecimalStorageType.I128 => CandidateKind.Decimal128,
                    _ => CandidateKind.Decimal256,
                };

            case CanonicalKind.VarBinView:
            case CanonicalKind.FixedSizeList:
                return CandidateKind.Bytes;

            default:
                return CandidateKind.None;
        }
    }

    /// <summary>Answers every row of <paramref name="column"/> from a set built for its kind.</summary>
    private static void Apply(
        CanonicalArena arena, CanonicalNode column, CandidateSet set, Span<byte> destination)
    {
        ValidityMask mask = ValidityMask.From(arena, column.Validity);
        if (set is InSet numbers)
        {
            PType ptype = column.Kind == CanonicalKind.Primitive
                ? column.PType
                : column.Storage switch
                {
                    DecimalStorageType.I8 => PType.I8,
                    DecimalStorageType.I16 => PType.I16,
                    DecimalStorageType.I32 => PType.I32,
                    _ => PType.I64,
                };
            numbers.Apply(ptype, column.Values.Span, mask, destination);
            return;
        }

        BytesInSet bytes = (BytesInSet)set;
        switch (column.Kind)
        {
            case CanonicalKind.VarBinView:
                bytes.Apply(column, mask, destination);
                return;

            case CanonicalKind.FixedSizeList:
                // In the equality's order: a list of nulls answers before its elements are asked
                // whether they are bytes.
                if (mask.AllInvalid)
                {
                    Trilean.Fill(destination, Trilean.Unknown);
                    return;
                }

                bytes.Apply(
                    FixedBytes(arena, column, destination.Length), checked((int)column.FixedSize), mask,
                    destination);
                return;

            default:
                bytes.Apply(
                    column.Values.Span, column.Storage == DecimalStorageType.I128 ? 16 : Int256.ByteCount,
                    mask, destination);
                return;
        }
    }

    /// <summary>Evaluates a column's null check, which is never unknown.</summary>
    /// <param name="arena">The arena.</param>
    /// <param name="nodeIndex">The column.</param>
    /// <param name="isNull">Whether the predicate asks for null rather than for not null.</param>
    /// <param name="destination">One state per row.</param>
    internal static void NullCheck(
        CanonicalArena arena, int nodeIndex, bool isNull, Span<byte> destination)
    {
        int storage = Unwrap(arena, nodeIndex);
        CanonicalNode node = arena.GetNode(storage);
        ValidityMask mask = ValidityMask.From(arena, node.Validity);

        if (node.Kind == CanonicalKind.Null)
        {
            Trilean.Fill(destination, isNull ? Trilean.True : Trilean.False);
            return;
        }

        if (mask.AllValid)
        {
            Trilean.Fill(destination, isNull ? Trilean.False : Trilean.True);
            return;
        }

        if (mask.AllInvalid)
        {
            Trilean.Fill(destination, isNull ? Trilean.True : Trilean.False);
            return;
        }

        ExpandBits(
            mask.Bits, mask.BitOffset, destination,
            whenFalse: isNull ? Trilean.True : Trilean.False, whenTrue: isNull ? Trilean.False : Trilean.True);
    }

    /// <summary>Compares a constant column, which is one comparison and then a fill.</summary>
    /// <remarks>
    /// <para>
    /// The whole point of the constant form: every row holds the same value, so every row has the
    /// same answer, and the per-row loop the other kernels run collapses to a fill. The comparison
    /// itself goes through <see cref="ComparePrimitiveValues"/>, the kernel that would have run
    /// anyway, so the rules on NaN and on signedness are applied by the one piece of code that
    /// implements them rather than restated here.
    /// </para>
    /// <para>
    /// The element is read in place. Wrapping it in a one-row arena node, so the kernel could take
    /// a node like every other caller, appends a record per filter evaluation, and a selective
    /// filter evaluates once per block: that is real memory on a single read path. Memoizing the
    /// node only moves the cost, onto every record in the arena. The element was always just bytes.
    /// </para>
    /// </remarks>
    private static void CompareConstant(
        CanonicalArena arena, CanonicalNode node, ComparisonOp op, FilterLiteral literal,
        Span<byte> destination)
    {
        DType dtype = node.DType;
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        if (mask.AllInvalid)
        {
            Trilean.Fill(destination, Trilean.Unknown);
            return;
        }

        byte state;
        switch (dtype.Kind)
        {
            case DTypeKind.Primitive:
            {
                // One row, and an all-valid mask for it: what is wanted here is the answer the value
                // gives, and whether a given row is null is applied below, over the rows this fills.
                Span<byte> one = stackalloc byte[1];
                ComparePrimitiveValues(
                    dtype.PType, node.ConstantElement, ValidityMask.From(arena, Validity.AllValid),
                    op, literal, one);
                state = one[0];
                break;
            }

            case DTypeKind.Utf8:
            case DTypeKind.Binary:
            {
                if (literal.Kind != FilterLiteralKind.Bytes)
                {
                    throw Mismatch("utf8 or binary", literal.Kind);
                }

                // `Apply(op, order)` is what `OrderViews` evaluates per row, so the operator's
                // semantics are the ones already written down rather than a second copy.
                state = Trilean.From(
                    true, Apply(op, node.ConstantElement.SequenceCompareTo(literal.BytesValue)));
                break;
            }

            case DTypeKind.Decimal:
            {
                Span<byte> one = stackalloc byte[1];
                CompareDecimalValues(
                    node.Storage, node.ConstantElement, ValidityMask.From(arena, Validity.AllValid),
                    op, literal, one);
                state = one[0];
                break;
            }

            default:
                throw new NotSupportedException(
                    $"A filter cannot compare a constant {dtype.Kind} column. A filter " +
                    "evaluates booleans, primitives, decimals, utf8 and binary, plus extensions " +
                    "over those.");
        }

        if (mask.AllValid)
        {
            Trilean.Fill(destination, state);
            return;
        }

        // A nullable constant: the value answers the same everywhere, and the nulls are unknown.
        ExpandBits(mask.Bits, mask.BitOffset, destination, Trilean.Unknown, state);
    }

    /// <summary>An extension node's storage; anything else unchanged.</summary>
    internal static int Unwrap(CanonicalArena arena, int nodeIndex) =>
        arena.GetNode(nodeIndex).Kind == CanonicalKind.Extension
            ? arena.GetNode(nodeIndex).StorageIndex
            : nodeIndex;

    private static void CompareBool(
        CanonicalArena arena, CanonicalNode node, ComparisonOp op, FilterLiteral literal,
        Span<byte> destination)
    {
        if (literal.Kind != FilterLiteralKind.Bool)
        {
            throw Mismatch("bool", literal.Kind);
        }

        // false < true, which is the order every SQL engine uses and the one the row encoding uses.
        bool wanted = literal.BoolValue;
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        ReadOnlySpan<byte> bits = node.Bits.Span;
        int offset = node.BitOffset;

        // A bool column has exactly two possible answers once the operator and the literal are
        // fixed, so the whole per-row decision -- `CompareTo`, the `switch` on the operator,
        // `Trilean.From` -- collapses to picking one of two bytes. Left per row, that dispatch
        // costs more than comparing eight bytes of an integer column does, which is upside down
        // for one bit per row.
        byte whenTrue = Trilean.From(true, Apply(op, true.CompareTo(wanted)));
        byte whenFalse = Trilean.From(true, Apply(op, false.CompareTo(wanted)));

        if (mask.AllInvalid)
        {
            Trilean.Fill(destination, Trilean.Unknown);
            return;
        }

        if (mask.AllValid)
        {
            ExpandBits(bits, offset, destination, whenFalse, whenTrue);
            return;
        }

        ExpandBits(bits, offset, destination, whenFalse, whenTrue);
        MarkUnknown(mask.Bits, mask.BitOffset, destination);
    }

    /// <summary>Overwrites with <see cref="Trilean.Unknown"/> the state of every row whose bit is clear.</summary>
    /// <param name="validity">The validity bitmap.</param>
    /// <param name="offset">The bit of row 0.</param>
    /// <param name="destination">One state per row, those of the valid rows kept.</param>
    internal static void MarkUnknown(ReadOnlySpan<byte> validity, int offset, Span<byte> destination)
    {
        int i = 0;
        if (WordBytes.IsAccelerated)
        {
            WordBytes spread = WordBytes.Create();
            Vector512<byte> unknown = Vector512.Create(Trilean.Unknown);
            ref byte into = ref MemoryMarshal.GetReference(destination);
            for (; i <= destination.Length - 64; i += 64)
            {
                ulong word = BitWords.Load(validity, offset + i);
                Vector512.ConditionalSelect(spread.Clear(word), unknown, Vector512.LoadUnsafe(ref into, (nuint)i))
                    .StoreUnsafe(ref into, (nuint)i);
            }
        }

        for (; i < destination.Length; i++)
        {
            if (!CanonicalSupport.BitAt(validity, offset + i))
            {
                destination[i] = Trilean.Unknown;
            }
        }
    }

    /// <summary>Writes one of two states per row, from a bitmap, eight rows per byte read.</summary>
    /// <remarks>
    /// The bitmap is LSB-first, so a whole byte answers eight consecutive rows and the shift
    /// amounts are constants the JIT folds into the selects. Only the lead-in to a byte boundary
    /// and the tail read a bit at a time, and a sliced column is the only thing that makes the
    /// lead-in non-empty.
    /// <para>
    /// With AVX-512, whole blocks of 64 rows go first, a word each. This is also how a validity
    /// becomes states, the null check's and those of an answer every valid row shares.
    /// </para>
    /// </remarks>
    private static void ExpandBits(
        ReadOnlySpan<byte> bits, int offset, Span<byte> destination, byte whenFalse, byte whenTrue)
    {
        int i = 0;

        // Where AVX-512 is, 64 rows a step, however the bitmap is aligned: the word in every lane,
        // shuffled to a byte per bit, and a blend of the two states by the zero bytes.
        if (WordBytes.IsAccelerated)
        {
            WordBytes spread = WordBytes.Create();
            Vector512<byte> clear = Vector512.Create(whenFalse);
            Vector512<byte> set = Vector512.Create(whenTrue);
            ref byte into = ref MemoryMarshal.GetReference(destination);
            for (; i <= destination.Length - 64; i += 64)
            {
                ulong word = BitWords.Load(bits, offset + i);
                Vector512.ConditionalSelect(spread.Clear(word), clear, set).StoreUnsafe(ref into, (nuint)i);
            }
        }

        int bit = offset + i;
        for (; i < destination.Length && (bit & 7) != 0; i++, bit++)
        {
            destination[i] = (bits[bit >> 3] & (1 << (bit & 7))) != 0 ? whenTrue : whenFalse;
        }

        for (; i + 8 <= destination.Length; i += 8, bit += 8)
        {
            int block = bits[bit >> 3];
            destination[i] = (block & 0x01) != 0 ? whenTrue : whenFalse;
            destination[i + 1] = (block & 0x02) != 0 ? whenTrue : whenFalse;
            destination[i + 2] = (block & 0x04) != 0 ? whenTrue : whenFalse;
            destination[i + 3] = (block & 0x08) != 0 ? whenTrue : whenFalse;
            destination[i + 4] = (block & 0x10) != 0 ? whenTrue : whenFalse;
            destination[i + 5] = (block & 0x20) != 0 ? whenTrue : whenFalse;
            destination[i + 6] = (block & 0x40) != 0 ? whenTrue : whenFalse;
            destination[i + 7] = (block & 0x80) != 0 ? whenTrue : whenFalse;
        }

        for (; i < destination.Length; i++, bit++)
        {
            destination[i] = (bits[bit >> 3] & (1 << (bit & 7))) != 0 ? whenTrue : whenFalse;
        }
    }

    private static void ComparePrimitive(
        CanonicalArena arena, CanonicalNode node, ComparisonOp op, FilterLiteral literal,
        Span<byte> destination)
    {
        ComparePrimitiveValues(
            node.PType, node.Values.Span, ValidityMask.From(arena, node.Validity), op, literal,
            destination);
    }

    /// <summary>
    /// The primitive comparison over the values, without a node: one state per row of
    /// <paramref name="destination"/>, read from the first bytes of <paramref name="values"/>.
    /// </summary>
    /// <remarks>
    /// Split out so the constant form can reach it. A constant column's element is a one-row values
    /// buffer and nothing more, so it wants exactly this and not an arena node built to carry it.
    /// </remarks>
    private static void ComparePrimitiveValues(
        PType ptype, ReadOnlySpan<byte> values, ValidityMask mask, ComparisonOp op,
        FilterLiteral literal, Span<byte> destination)
    {
        if (ptype.IsFloat())
        {
            CompareFloat(ptype, values, mask, op, ToDouble(literal, "a float"), destination);
            return;
        }

        if (ptype.IsSignedInteger())
        {
            CompareSigned(ptype, values, mask, op, literal, destination);
            return;
        }

        CompareUnsigned(ptype, values, mask, op, literal, destination);
    }

    private static void CompareFloat(
        PType ptype, ReadOnlySpan<byte> values, ValidityMask mask, ComparisonOp op, double wanted,
        Span<byte> destination)
    {
        // The two per-row switches -- one on the physical type to read the value, one on the
        // operator to compare it -- are resolved before the loop, as they are on the integer path:
        // `CompareOp` picks the operator as a type and `CompareCore` monomorphises on the value
        // type.
        //
        // IEEE 754 survives that move, which is the thing to watch: `IOrderOp` is written with C#'s
        // own operators, so every ordering and equality against NaN is false, and inequality true,
        // which the zone pruning agrees with by never pruning on `!=`. What would break the rule is
        // `CompareTo`, which orders NaN and would give the row encoding's total order -- and
        // nothing here calls it. Widening a `Half` or a `float` to `double` is
        // exact, so the answers are unchanged.
        ReadOnlySpan<byte> bytes = values;
        switch (ptype)
        {
            case PType.F16:
                CompareOp<Half, double>(bytes, mask, op, wanted, destination);
                break;
            case PType.F32:
                CompareOp<float, double>(bytes, mask, op, wanted, destination);
                break;
            default:
                CompareOp<double, double>(bytes, mask, op, wanted, destination);
                break;
        }
    }

    private static void CompareSigned(
        PType ptype, ReadOnlySpan<byte> values, ValidityMask mask, ComparisonOp op, FilterLiteral literal,
        Span<byte> destination)
    {
        long wanted;
        switch (literal.Kind)
        {
            case FilterLiteralKind.Signed:
                wanted = literal.SignedValue;
                break;

            case FilterLiteralKind.Unsigned when literal.UnsignedValue <= long.MaxValue:
                wanted = (long)literal.UnsignedValue;
                break;

            case FilterLiteralKind.Unsigned:
                // Above i64::MaxValue: every value of a signed column is below it, so the answer
                // is the same for every non-null row and no comparison is needed.
                FillFromOrder(mask, op, -1, destination);
                return;

            case FilterLiteralKind.Float:
                CompareSignedAgainstFloat(ptype, values, mask, op, literal.FloatValue, destination);
                return;

            default:
                throw Mismatch("a signed integer", literal.Kind);
        }

        CompareSigned(ptype, values, mask, op, wanted, destination);
    }

    private static void CompareSigned(
        PType ptype, ReadOnlySpan<byte> values, ValidityMask mask, ComparisonOp op, long wanted,
        Span<byte> destination)
    {
        switch (ptype)
        {
            case PType.I8:
                CompareOp<sbyte, long>(values, mask, op, wanted, destination);
                break;
            case PType.I16:
                CompareOp<short, long>(values, mask, op, wanted, destination);
                break;
            case PType.I32:
                CompareOp<int, long>(values, mask, op, wanted, destination);
                break;
            default:
                CompareOp<long, long>(values, mask, op, wanted, destination);
                break;
        }
    }

    private static void CompareUnsigned(
        PType ptype, ReadOnlySpan<byte> values, ValidityMask mask, ComparisonOp op, FilterLiteral literal,
        Span<byte> destination)
    {
        ulong wanted;
        switch (literal.Kind)
        {
            case FilterLiteralKind.Unsigned:
                wanted = literal.UnsignedValue;
                break;

            case FilterLiteralKind.Signed when literal.SignedValue >= 0:
                wanted = (ulong)literal.SignedValue;
                break;

            case FilterLiteralKind.Signed:
                // Negative: every value of an unsigned column is above it. `x > -1` is true for
                // every row, and casting the literal instead would have made it false for almost
                // all of them.
                FillFromOrder(mask, op, 1, destination);
                return;

            case FilterLiteralKind.Float:
                CompareUnsignedAgainstFloat(ptype, values, mask, op, literal.FloatValue, destination);
                return;

            default:
                throw Mismatch("an unsigned integer", literal.Kind);
        }

        CompareUnsigned(ptype, values, mask, op, wanted, destination);
    }

    private static void CompareUnsigned(
        PType ptype, ReadOnlySpan<byte> values, ValidityMask mask, ComparisonOp op, ulong wanted,
        Span<byte> destination)
    {
        switch (ptype)
        {
            case PType.U8:
                CompareOp<byte, ulong>(values, mask, op, wanted, destination);
                break;
            case PType.U16:
                CompareOp<ushort, ulong>(values, mask, op, wanted, destination);
                break;
            case PType.U32:
                CompareOp<uint, ulong>(values, mask, op, wanted, destination);
                break;
            default:
                CompareOp<ulong, ulong>(values, mask, op, wanted, destination);
                break;
        }
    }

    /// <summary>
    /// Resolves the operator out of the loop and calls the typed comparison.
    /// </summary>
    /// <typeparam name="TValue">The column's own element type.</typeparam>
    /// <typeparam name="TWide">
    /// The type the comparison happens in: <see cref="long"/> for a signed column,
    /// <see cref="ulong"/> for an unsigned one. Widening one element is free; what it buys is that
    /// the literal is compared in a type that can hold it, which is the signedness rule every
    /// kernel here follows.
    /// </typeparam>
    private static void CompareOp<TValue, TWide>(
        ReadOnlySpan<byte> bytes, ValidityMask mask, ComparisonOp op, TWide wanted,
        Span<byte> destination)
        where TValue : unmanaged, INumberBase<TValue>
        where TWide : unmanaged, INumberBase<TWide>, IComparisonOperators<TWide, TWide, bool>
    {
        switch (op)
        {
            case ComparisonOp.Equal:
                CompareCore<TValue, TWide, EqualOp>(bytes, mask, wanted, destination);
                break;
            case ComparisonOp.NotEqual:
                CompareCore<TValue, TWide, NotEqualOp>(bytes, mask, wanted, destination);
                break;
            case ComparisonOp.Less:
                CompareCore<TValue, TWide, LessOp>(bytes, mask, wanted, destination);
                break;
            case ComparisonOp.LessOrEqual:
                CompareCore<TValue, TWide, LessOrEqualOp>(bytes, mask, wanted, destination);
                break;
            case ComparisonOp.Greater:
                CompareCore<TValue, TWide, GreaterOp>(bytes, mask, wanted, destination);
                break;
            default:
                CompareCore<TValue, TWide, GreaterOrEqualOp>(bytes, mask, wanted, destination);
                break;
        }
    }

    /// <summary>The comparison loop, with the type, the operator and the validity all resolved.</summary>
    /// <remarks>
    /// <para>
    /// Three questions that are properties of the call rather than of the row are answered before
    /// the loop runs: which physical type the column has, whether a row is valid, and which
    /// operator is being applied. Asked per row -- a switch to read the value, a switch on the
    /// validity kind, a switch on an ordering that had to be computed first -- they cost several
    /// times the comparison itself.
    /// </para>
    /// <para>
    /// The operator arrives as a struct with a static abstract member, which the runtime
    /// devirtualizes and inlines for a value-type instantiation, so the body is one compare and one
    /// store. The three-way ordering goes with it: a two-way question needs no <c>CompareTo</c>.
    /// </para>
    /// <para>
    /// Still a byte per row, deliberately. A <c>Vector128</c> form of this loop is slower than the
    /// scalar one, and the cause is the output: a <see cref="Trilean"/> is one byte per row, so a
    /// vectorized compare has to narrow its mask back down to bytes, and the narrowing costs more
    /// than the compare saves. That is a statement about the output representation rather than
    /// about vectorizing comparisons, and it would not hold if a trilean were carried as two
    /// bitmaps.
    /// </para>
    /// <para>
    /// Nor does it hold on x86 with AVX-512, where nothing is narrowed: a compare writes a mask
    /// register, a mask is a word, and a word is spread back to 64 bytes by one shuffle, an and and
    /// a min. <see cref="Lanes{T, TOp}"/> answers whole blocks of 64 rows that way.
    /// </para>
    /// </remarks>
    private static void CompareCore<TValue, TWide, TOp>(
        ReadOnlySpan<byte> bytes, ValidityMask mask, TWide wanted, Span<byte> destination)
        where TValue : unmanaged, INumberBase<TValue>
        where TWide : unmanaged, INumberBase<TWide>, IComparisonOperators<TWide, TWide, bool>
        where TOp : struct, IOrderOp
    {
        if (mask.AllInvalid)
        {
            Trilean.Fill(destination, Trilean.Unknown);
            return;
        }

        ReadOnlySpan<TValue> values = MemoryMarshal.Cast<byte, TValue>(bytes)[..destination.Length];

        // Where AVX-512 is, 64 rows a step in the column's own type, when the literal is one of its
        // values; the rest, and a literal only the wider type holds, are the scalar loops' below.
        int done = 0;
        if (WordBytes.IsAccelerated && Vector512<TValue>.IsSupported && InLanes(wanted, out TValue literal))
        {
            done = Lanes<TValue, TOp>(values, literal, mask, destination);
        }

        if (mask.AllValid)
        {
            for (int i = done; i < destination.Length; i++)
            {
                destination[i] = TOp.Holds(TWide.CreateTruncating(values[i]), wanted)
                    ? Trilean.True
                    : Trilean.False;
            }

            return;
        }

        NullableCore<TValue, TWide, TOp>(values, mask, wanted, destination, done);
    }

    /// <summary>
    /// The literal in the column's own type, when it is exactly one of that type's values: then
    /// comparing in the column's type answers what comparing in the wider one does, since widening
    /// is exact and keeps the order. A literal outside the range, a fraction for a float column, or
    /// a NaN is left to the scalar loops, which compare in the wider type.
    /// </summary>
    private static bool InLanes<TValue, TWide>(TWide wanted, out TValue literal)
        where TValue : INumberBase<TValue>
        where TWide : INumberBase<TWide>, IComparisonOperators<TWide, TWide, bool>
    {
        literal = TValue.CreateTruncating(wanted);
        return TWide.CreateTruncating(literal) == wanted;
    }

    /// <summary>
    /// The verdicts of whole blocks of 64 rows: the operator over 512-bit vectors of the column's
    /// own type, their lanes' masks gathered into a word, the word spread back to a byte a row.
    /// </summary>
    /// <returns>The rows answered, a multiple of 64; the rest are the caller's.</returns>
    /// <remarks>
    /// A null row is compared like any other and its verdict replaced by unknown: its slot holds
    /// some value, validity governing its meaning and not its existence, and a compare of the lanes
    /// a block holds costs the same whatever they hold.
    /// </remarks>
    private static int Lanes<T, TOp>(ReadOnlySpan<T> values, T literal, ValidityMask mask, Span<byte> destination)
        where TOp : struct, IOrderOp
    {
        int rows = destination.Length;
        Vector512<T> right = Vector512.Create(literal);
        WordBytes spread = WordBytes.Create();
        ref T from = ref MemoryMarshal.GetReference(values);
        ref byte into = ref MemoryMarshal.GetReference(destination);
        int i = 0;
        if (mask.AllValid)
        {
            for (; i <= rows - 64; i += 64)
            {
                ulong held = Verdicts<T, TOp>(ref Unsafe.Add(ref from, i), right);
                spread.Ones(held).StoreUnsafe(ref into, (nuint)i);
            }

            return i;
        }

        // A valid row's verdict is 1 or 0 and a null row's 2: the ones of the verdicts it holds,
        // with the ones of the nulls doubled over them.
        ReadOnlySpan<byte> bits = mask.Bits;
        int offset = mask.BitOffset;
        for (; i <= rows - 64; i += 64)
        {
            ulong valid = BitWords.Load(bits, offset + i);
            ulong held = Verdicts<T, TOp>(ref Unsafe.Add(ref from, i), right) & valid;
            Vector512<byte> unknown = spread.Ones(~valid);
            (spread.Ones(held) | (unknown + unknown)).StoreUnsafe(ref into, (nuint)i);
        }

        return i;
    }

    /// <summary>Sixty-four rows' verdicts from <paramref name="at"/>, bit <c>i</c> for row <c>i</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Verdicts<T, TOp>(ref T at, Vector512<T> right)
        where TOp : struct, IOrderOp
    {
        int lanes = Vector512<T>.Count;
        ulong held = 0;
        for (int row = 0; row < 64; row += lanes)
        {
            held |= TOp.Lanes(Vector512.LoadUnsafe(ref at, (nuint)row), right).ExtractMostSignificantBits() << row;
        }

        return held;
    }

    /// <summary>The nullable half of <see cref="CompareOp{TValue,TWide}"/>, eight rows per byte.</summary>
    /// <param name="values">The column's values, already cast and trimmed to the row count.</param>
    /// <param name="mask">Per-row validity; the caller has ruled out all-valid and all-invalid.</param>
    /// <param name="wanted">The literal, widened once by the caller.</param>
    /// <param name="destination">One <see cref="Trilean"/> per row.</param>
    /// <param name="start">The first row to answer: those before it the vector blocks answered.</param>
    /// <remarks>
    /// The same column, operator and literal cost several times more nullable than all-valid, and
    /// the whole of that gap is validity resolution: the all-valid loop is already a handful of
    /// instructions with no jump, while asking the mask row by row recomputes the byte index, the
    /// bit mask and the bounds check for every row before branching on the bit.
    /// <para>
    /// This reads the byte once for eight rows, exactly as <c>ExpandBits</c> does for
    /// <c>CompareBool</c>. The inner eight are straight-line: shift, test, select. The comparison
    /// itself is untouched -- it was never the cost.
    /// </para>
    /// <para>
    /// The lead-in walks single rows until the bit cursor is byte-aligned, because a mask carries a
    /// bit offset below 8 that the column's first row need not start on.
    /// </para>
    /// </remarks>
    private static void NullableCore<TValue, TWide, TOp>(
        ReadOnlySpan<TValue> values, ValidityMask mask, TWide wanted, Span<byte> destination, int start)
        where TValue : unmanaged, INumberBase<TValue>
        where TWide : unmanaged, INumberBase<TWide>, IComparisonOperators<TWide, TWide, bool>
        where TOp : struct, IOrderOp
    {
        ReadOnlySpan<byte> bits = mask.Bits;
        int bit = mask.BitOffset + start;
        int i = start;

        for (; i < destination.Length && (bit & 7) != 0; i++, bit++)
        {
            destination[i] = CanonicalSupport.BitAt(bits, bit)
                ? (TOp.Holds(TWide.CreateTruncating(values[i]), wanted) ? Trilean.True : Trilean.False)
                : Trilean.Unknown;
        }

        for (; i + 8 <= destination.Length; i += 8, bit += 8)
        {
            int block = bits[bit >> 3];
            if (block == 0)
            {
                // Eight nulls in a row: the comparison is not merely unnecessary, it is the branch
                // this kernel is paying for. Scattered validity still hits this on a quarter of its
                // bytes, and a run of nulls hits it on all of them.
                destination.Slice(i, 8).Fill(Trilean.Unknown);
                continue;
            }

            for (int k = 0; k < 8; k++)
            {
                // The comparison stays inside the validity test on purpose. Hoisting it out --
                // computing the result for every row and selecting afterwards -- is the branchless
                // form, and this is the one site in these kernels where it would be legal, since
                // the index is in range whatever the bit says: validity governs a value's meaning,
                // not its existence. It is also half again slower, because running the comparison
                // for the null rows costs more than the jump it removes. The remedy for a branch
                // must be cheaper than the branch.
                destination[i + k] = (block & (1 << k)) != 0
                    ? (TOp.Holds(TWide.CreateTruncating(values[i + k]), wanted)
                        ? Trilean.True
                        : Trilean.False)
                    : Trilean.Unknown;
            }
        }

        for (; i < destination.Length; i++, bit++)
        {
            destination[i] = CanonicalSupport.BitAt(bits, bit)
                ? (TOp.Holds(TWide.CreateTruncating(values[i]), wanted) ? Trilean.True : Trilean.False)
                : Trilean.Unknown;
        }
    }

    /// <summary>One comparison operator, as a type the JIT can inline through.</summary>
    private interface IOrderOp
    {
        /// <summary>Whether the operator holds for this pair.</summary>
        /// <typeparam name="T">The comparison type.</typeparam>
        /// <param name="left">The column's value.</param>
        /// <param name="right">The literal.</param>
        static abstract bool Holds<T>(T left, T right)
            where T : IComparisonOperators<T, T, bool>;

        /// <summary>
        /// The operator over vectors, a lane of all ones where it holds: the same IEEE 754 answers
        /// as <see cref="Holds{T}"/>, every ordering and equality against a NaN false and
        /// inequality true.
        /// </summary>
        /// <typeparam name="T">The lane type, the column's own.</typeparam>
        /// <param name="left">The column's values.</param>
        /// <param name="right">The literal in every lane.</param>
        static abstract Vector512<T> Lanes<T>(Vector512<T> left, Vector512<T> right);
    }

    private readonly struct EqualOp : IOrderOp
    {
        public static bool Holds<T>(T left, T right)
            where T : IComparisonOperators<T, T, bool> => left == right;

        public static Vector512<T> Lanes<T>(Vector512<T> left, Vector512<T> right) =>
            Vector512.Equals(left, right);
    }

    private readonly struct NotEqualOp : IOrderOp
    {
        public static bool Holds<T>(T left, T right)
            where T : IComparisonOperators<T, T, bool> => left != right;

        public static Vector512<T> Lanes<T>(Vector512<T> left, Vector512<T> right) =>
            ~Vector512.Equals(left, right);
    }

    private readonly struct LessOp : IOrderOp
    {
        public static bool Holds<T>(T left, T right)
            where T : IComparisonOperators<T, T, bool> => left < right;

        public static Vector512<T> Lanes<T>(Vector512<T> left, Vector512<T> right) =>
            Vector512.LessThan(left, right);
    }

    private readonly struct LessOrEqualOp : IOrderOp
    {
        public static bool Holds<T>(T left, T right)
            where T : IComparisonOperators<T, T, bool> => left <= right;

        public static Vector512<T> Lanes<T>(Vector512<T> left, Vector512<T> right) =>
            Vector512.LessThanOrEqual(left, right);
    }

    private readonly struct GreaterOp : IOrderOp
    {
        public static bool Holds<T>(T left, T right)
            where T : IComparisonOperators<T, T, bool> => left > right;

        public static Vector512<T> Lanes<T>(Vector512<T> left, Vector512<T> right) =>
            Vector512.GreaterThan(left, right);
    }

    private readonly struct GreaterOrEqualOp : IOrderOp
    {
        public static bool Holds<T>(T left, T right)
            where T : IComparisonOperators<T, T, bool> => left >= right;

        public static Vector512<T> Lanes<T>(Vector512<T> left, Vector512<T> right) =>
            Vector512.GreaterThanOrEqual(left, right);
    }

    /// <summary>
    /// A signed integer column against a float literal, exactly.
    /// </summary>
    /// <remarks>
    /// <c>x &lt; 3.5</c> on an integer column is a legal and ordinary predicate, so it is answered
    /// rather than refused. It is answered in the column's domain, not the literal's: comparing
    /// each value as a <see cref="double"/> is exact below 2^53 and off by one above it, whereas the
    /// integers below a float are exactly the integers below its ceiling, those above it the ones
    /// above its floor, and none equals a float that is not integral. So the literal is placed on
    /// the integers once, and the rows are compared by the same integer kernel every other literal
    /// goes through. A literal beyond the column's range decides every row alike.
    /// </remarks>
    private static void CompareSignedAgainstFloat(
        PType ptype, ReadOnlySpan<byte> values, ValidityMask mask, ComparisonOp op, double wanted,
        Span<byte> destination)
    {
        if (!TryPlace(op, wanted, out ComparisonOp exact, out double bound))
        {
            FillAnswer(mask, op == ComparisonOp.NotEqual, destination);
        }
        else if (bound >= 9223372036854775808.0)
        {
            // Every value of the column is below the bound.
            FillAnswer(mask, exact is ComparisonOp.Less or ComparisonOp.LessOrEqual or ComparisonOp.NotEqual, destination);
        }
        else if (bound < -9223372036854775808.0)
        {
            FillAnswer(mask, exact is ComparisonOp.Greater or ComparisonOp.GreaterOrEqual or ComparisonOp.NotEqual, destination);
        }
        else
        {
            CompareSigned(ptype, values, mask, exact, (long)bound, destination);
        }
    }

    /// <summary>The same for an unsigned column; see <see cref="CompareSignedAgainstFloat"/>.</summary>
    private static void CompareUnsignedAgainstFloat(
        PType ptype, ReadOnlySpan<byte> values, ValidityMask mask, ComparisonOp op, double wanted,
        Span<byte> destination)
    {
        if (!TryPlace(op, wanted, out ComparisonOp exact, out double bound))
        {
            FillAnswer(mask, op == ComparisonOp.NotEqual, destination);
        }
        else if (bound >= 18446744073709551616.0)
        {
            FillAnswer(mask, exact is ComparisonOp.Less or ComparisonOp.LessOrEqual or ComparisonOp.NotEqual, destination);
        }
        else if (bound < 0)
        {
            FillAnswer(mask, exact is ComparisonOp.Greater or ComparisonOp.GreaterOrEqual or ComparisonOp.NotEqual, destination);
        }
        else
        {
            CompareUnsigned(ptype, values, mask, exact, (ulong)bound, destination);
        }
    }

    /// <summary>
    /// Moves a comparison against a float onto the integers: the integer bound the same rows
    /// compare against, under the same operator.
    /// </summary>
    /// <param name="op">The operator.</param>
    /// <param name="wanted">The float literal.</param>
    /// <param name="exact">The operator to apply to <paramref name="bound"/>.</param>
    /// <param name="bound">An integral value, possibly beyond every integer type's range.</param>
    /// <returns>
    /// <see langword="false"/> when no integer relates to the literal: a NaN, or an equality with a
    /// float that is not integral, which no row satisfies and every row is unequal to.
    /// </returns>
    private static bool TryPlace(ComparisonOp op, double wanted, out ComparisonOp exact, out double bound)
    {
        exact = op;
        bound = wanted;
        if (double.IsNaN(wanted))
        {
            return false;
        }

        double floor = Math.Floor(wanted);
        switch (op)
        {
            case ComparisonOp.Equal:
            case ComparisonOp.NotEqual:
                return floor == wanted;
            case ComparisonOp.Less:
            case ComparisonOp.GreaterOrEqual:
                bound = Math.Ceiling(wanted);
                return true;
            default:
                bound = floor;
                return true;
        }
    }

    private static void CompareBytes(
        CanonicalArena arena, CanonicalNode node, ComparisonOp op, FilterLiteral literal,
        Span<byte> destination)
    {
        if (literal.Kind != FilterLiteralKind.Bytes)
        {
            throw Mismatch("utf8 or binary", literal.Kind);
        }

        // The operator becomes a type, the validity question is asked once, and the views span is
        // taken once instead of through a property on every row. `Apply(op, order)` is exactly
        // `TOp.Holds(order, 0)`, so the operator semantics are the ones already written down
        // rather than a second copy of them.
        ReadOnlySpan<byte> wanted = literal.BytesValue;
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        if (mask.AllInvalid)
        {
            Trilean.Fill(destination, Trilean.Unknown);
            return;
        }

        switch (op)
        {
            case ComparisonOp.Equal:
                EqualViews(node, mask, wanted, equal: true, destination);
                break;
            case ComparisonOp.NotEqual:
                EqualViews(node, mask, wanted, equal: false, destination);
                break;
            case ComparisonOp.Less:
                OrderViews<LessOp>(node, mask, wanted, destination);
                break;
            case ComparisonOp.LessOrEqual:
                OrderViews<LessOrEqualOp>(node, mask, wanted, destination);
                break;
            case ComparisonOp.Greater:
                OrderViews<GreaterOp>(node, mask, wanted, destination);
                break;
            default:
                OrderViews<GreaterOrEqualOp>(node, mask, wanted, destination);
                break;
        }
    }

    /// <summary>
    /// Fills every non-null row with the answer implied by a comparison whose order is already
    /// known, for the cases where the literal is out of the column's range entirely.
    /// </summary>
    /// <param name="mask">Row validity.</param>
    /// <param name="op">The operator.</param>
    /// <param name="order">The sign of <c>column - literal</c>, the same for every row.</param>
    /// <param name="destination">One state per row.</param>
    private static void FillFromOrder(
        ValidityMask mask, ComparisonOp op, int order, Span<byte> destination) =>
        FillAnswer(mask, Apply(op, order), destination);

    /// <summary>One answer for every valid row, and unknown for a null.</summary>
    private static void FillAnswer(ValidityMask mask, bool answer, Span<byte> destination)
    {
        byte state = answer ? Trilean.True : Trilean.False;
        if (mask.AllValid)
        {
            Trilean.Fill(destination, state);
            return;
        }

        if (mask.AllInvalid)
        {
            Trilean.Fill(destination, Trilean.Unknown);
            return;
        }

        ExpandBits(mask.Bits, mask.BitOffset, destination, Trilean.Unknown, state);
    }

    /// <summary>Turns the sign of a three-way comparison into the operator's answer.</summary>
    private static bool Apply(ComparisonOp op, int order) => op switch
    {
        ComparisonOp.Equal => order == 0,
        ComparisonOp.NotEqual => order != 0,
        ComparisonOp.Less => order < 0,
        ComparisonOp.LessOrEqual => order <= 0,
        ComparisonOp.Greater => order > 0,
        _ => order >= 0,
    };

    private static double ToDouble(FilterLiteral literal, string columnKind) => literal.Kind switch
    {
        FilterLiteralKind.Float => literal.FloatValue,
        FilterLiteralKind.Signed => literal.SignedValue,
        FilterLiteralKind.Unsigned => literal.UnsignedValue,
        _ => throw Mismatch(columnKind, literal.Kind),
    };

    private static NotSupportedException Mismatch(string columnKind, FilterLiteralKind literal) =>
        new NotSupportedException(
            $"A filter on a {columnKind} column cannot be compared against a " +
            $"{literal} literal.");
}
