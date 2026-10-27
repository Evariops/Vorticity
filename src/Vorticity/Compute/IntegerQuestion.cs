using System;
using Vorticity.Types;

namespace Vorticity.Compute;

/// <summary>What an <see cref="IntegerQuestion"/> asks.</summary>
internal enum QuestionKind : byte
{
    /// <summary>No value is selected.</summary>
    None = 0,

    /// <summary>Every value is selected.</summary>
    All = 1,

    /// <summary>The values of a range, or the others.</summary>
    Range = 2,

    /// <summary>The values that stand for a member of a set.</summary>
    Members = 3,
}

/// <summary>
/// A predicate an encoding of integers answers on its own values, which are <see cref="Width"/>-bit patterns: a range
/// of patterns, or the patterns that stand for the members of a set. An encoding whose values are another's under a
/// transform (a frame of reference, a zigzag) asks its child the question moved into the child's patterns.
/// </summary>
/// <remarks>
/// <para>
/// A range is circular: a pattern <c>e</c> is selected when <c>(e - Low) mod 2^Width &lt;= Span</c>, or not selected
/// when <see cref="Negated"/>. One test answers every order of a signed or an unsigned column, and moves exactly under a
/// frame of reference, which only subtracts its reference from <see cref="Low"/>, wrapping as its decode wraps: a
/// pattern a writer never made reads as the decode would read it.
/// </para>
/// <para>
/// A set holds the column's members, ascending, in the column's own values. A node pattern stands for the column value
/// <c>unzigzag(e) + Shift</c>, wrapped and read signed or unsigned: what each transform above the node added, which a
/// node applies to a member to find its pattern, or to a pattern to find its member.
/// </para>
/// <para>
/// A <c>ref struct</c>, so that a question moved down a stack of encodings allocates nothing: the members are a span
/// over the scan's predicate.
/// </para>
/// </remarks>
internal readonly ref struct IntegerQuestion
{
    private IntegerQuestion(
        QuestionKind kind, int width, ulong low, ulong span, bool negated, ReadOnlySpan<Int128> members, bool signed,
        ulong shift, bool zigZag)
    {
        Kind = kind;
        Width = width;
        Low = low;
        Span = span;
        Negated = negated;
        Members = members;
        Signed = signed;
        Shift = shift;
        ZigZag = zigZag;
    }

    /// <summary>What is asked.</summary>
    internal QuestionKind Kind { get; }

    /// <summary>The bits of a pattern: 8, 16, 32 or 64.</summary>
    internal int Width { get; }

    /// <summary>The first pattern of a range.</summary>
    internal ulong Low { get; }

    /// <summary>How many patterns past <see cref="Low"/> a range reaches, wrapping.</summary>
    internal ulong Span { get; }

    /// <summary>Whether a range selects the patterns outside it.</summary>
    internal bool Negated { get; }

    /// <summary>The members of a set, ascending and distinct, as the column's values.</summary>
    internal ReadOnlySpan<Int128> Members { get; }

    /// <summary>Whether the column reads a pattern as signed.</summary>
    internal bool Signed { get; }

    /// <summary>What the transforms above add to a node's value, wrapping, to make the column's.</summary>
    internal ulong Shift { get; }

    /// <summary>Whether a node's pattern is unzigzagged before <see cref="Shift"/> is added.</summary>
    internal bool ZigZag { get; }

    /// <summary>The patterns of <see cref="Width"/> bits: all ones.</summary>
    internal ulong Mask => Width == 64 ? ulong.MaxValue : (1UL << Width) - 1;

    /// <summary>
    /// The question <paramref name="predicate"/> asks of a column of integers of <paramref name="ptype"/>, or false when
    /// it asks none an encoding of integers answers: a prefix, or a literal that is no number.
    /// </summary>
    internal static bool TryOf(EncodedPredicate predicate, PType ptype, out IntegerQuestion question)
    {
        int width = ptype.ByteWidth() * 8;
        bool signed = ptype.IsSignedInteger();
        if (predicate.Integers is { } integers)
        {
            question = OfRange(integers, width, signed);
            return true;
        }

        if (predicate.Members is { } members)
        {
            question = OfMembers(members, width, signed);
            return true;
        }

        question = default;
        return false;
    }

    /// <summary>The members of a set, ascending and distinct, asked of a column of <paramref name="width"/> bits.</summary>
    internal static IntegerQuestion OfMembers(ReadOnlySpan<Int128> members, int width, bool signed) =>
        new IntegerQuestion(QuestionKind.Members, width, 0, 0, false, members, signed, 0, false);

    /// <summary>
    /// The question a range of a column's values asks of a column of <paramref name="width"/> bits, signed or not: the
    /// range cut to the values the column holds, and settled when it holds none of them or all.
    /// </summary>
    internal static IntegerQuestion OfRange(IntegerRange range, int width, bool signed)
    {
        Int128 least = signed ? -(Int128.One << (width - 1)) : Int128.Zero;
        Int128 greatest = signed ? (Int128.One << (width - 1)) - 1 : (Int128.One << width) - 1;
        IntegerRange held = range.Within(least, greatest);
        if (held.IsEmpty)
        {
            return Settled(width, selected: held.Negated);
        }

        if (held.Low == least && held.High == greatest)
        {
            return Settled(width, selected: !held.Negated);
        }

        ulong mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        return new IntegerQuestion(
            QuestionKind.Range, width, (ulong)(held.Low & mask), (ulong)(held.High - held.Low), held.Negated, default, signed, 0, false);
    }

    /// <summary>Every pattern of <paramref name="width"/> bits selected, or none.</summary>
    internal static IntegerQuestion Settled(int width, bool selected) =>
        new IntegerQuestion(selected ? QuestionKind.All : QuestionKind.None, width, 0, 0, false, default, false, 0, false);

    /// <summary>
    /// The question on a child whose patterns are this node's less <paramref name="reference"/>, wrapping: a frame of
    /// reference's. False for a set already zigzagged, whose child would be shifted before the zigzag.
    /// </summary>
    internal bool TryShifted(ulong reference, out IntegerQuestion child)
    {
        switch (Kind)
        {
            case QuestionKind.Range:
                child = new IntegerQuestion(Kind, Width, (Low - reference) & Mask, Span, Negated, default, Signed, 0, false);
                return true;
            case QuestionKind.Members when !ZigZag:
                child = new IntegerQuestion(Kind, Width, 0, 0, false, Members, Signed, (Shift + reference) & Mask, false);
                return true;
            case QuestionKind.Members:
                child = default;
                return false;
            default:
                child = this;
                return true;
        }
    }

    /// <summary>
    /// The question on a child whose patterns are this node's zigzagged: an equality or an inequality of a single
    /// pattern, or a set; false for a wider range, whose patterns the zigzag does not keep in order.
    /// </summary>
    internal bool TryZigZagged(out IntegerQuestion child)
    {
        switch (Kind)
        {
            case QuestionKind.Range when Span == 0:
                child = new IntegerQuestion(Kind, Width, ZigZagOf(Low), 0, Negated, default, Signed, 0, false);
                return true;
            case QuestionKind.Range:
                child = default;
                return false;
            case QuestionKind.Members when !ZigZag:
                child = new IntegerQuestion(Kind, Width, 0, 0, false, Members, Signed, Shift, true);
                return true;
            case QuestionKind.Members:
                child = default;
                return false;
            default:
                child = this;
                return true;
        }
    }

    /// <summary>Whether a node's pattern is selected.</summary>
    internal bool Holds(ulong pattern) => Kind switch
    {
        QuestionKind.None => false,
        QuestionKind.All => true,
        QuestionKind.Range => (((pattern - Low) & Mask) <= Span) != Negated,
        _ => Members.BinarySearch(ColumnValue(pattern)) >= 0,
    };

    /// <summary>The column's value a node's pattern stands for: unzigzagged, shifted, wrapped and read.</summary>
    internal Int128 ColumnValue(ulong pattern)
    {
        ulong value = ((ZigZag ? UnZigZagOf(pattern & Mask) : pattern) + Shift) & Mask;
        return Signed && Width < 128 && (value >> (Width - 1)) != 0 ? (Int128)value - (Int128.One << Width) : value;
    }

    /// <summary>
    /// The node's pattern that stands for member <paramref name="member"/>, or false when no pattern does: a value
    /// outside the column's type.
    /// </summary>
    internal bool TryPattern(Int128 member, out ulong pattern)
    {
        Int128 least = Signed ? -(Int128.One << (Width - 1)) : Int128.Zero;
        Int128 greatest = Signed ? (Int128.One << (Width - 1)) - 1 : (Int128.One << Width) - 1;
        if (member < least || member > greatest)
        {
            pattern = 0;
            return false;
        }

        ulong value = ((ulong)(member & Mask) - Shift) & Mask;
        pattern = ZigZag ? ZigZagOf(value) : value;
        return true;
    }

    /// <summary>The zigzag of a <see cref="Width"/>-bit signed pattern: its sign moved to the lowest bit.</summary>
    private ulong ZigZagOf(ulong value)
    {
        ulong sign = (value >> (Width - 1)) & 1;
        return ((value << 1) ^ (0 - sign)) & Mask;
    }

    /// <summary>The <see cref="Width"/>-bit signed pattern a zigzagged pattern stands for.</summary>
    private ulong UnZigZagOf(ulong value) => ((value >> 1) ^ (0 - (value & 1))) & Mask;
}
