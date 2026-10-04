using System;
using Vorticity.Expressions;

namespace Vorticity;

/// <summary>The predicates that only some column types have: text matching, boolean tests, list membership.</summary>
/// <remarks>
/// The text predicates are generic over the text type so that <c>Sym&lt;string&gt;</c> and
/// <c>Sym&lt;string?&gt;</c> both take them without a nullability warning; only <see cref="string"/>
/// satisfies the constraint among the types a column maps to.
/// </remarks>
public static class SymExtensions
{
    extension<TText>(Sym<TText> column)
        where TText : IComparable<string?>?
    {
        /// <summary>The rows whose text begins with <paramref name="prefix"/>, compared as UTF-8 bytes.</summary>
        /// <param name="prefix">The prefix.</param>
        /// <returns>The predicate.</returns>
        public Predicate StartsWith(string prefix) => SymLowering.StringMatch(column.Column, StringMatchOp.StartsWith, prefix);

        /// <summary>The rows whose text contains <paramref name="text"/>, compared as UTF-8 bytes.</summary>
        /// <param name="text">The text sought.</param>
        /// <returns>The predicate.</returns>
        public Predicate Contains(string text) => SymLowering.StringMatch(column.Column, StringMatchOp.Contains, text);

        /// <summary>The rows whose text matches the SQL pattern: <c>%</c> any run, <c>_</c> one character.</summary>
        /// <param name="pattern">The pattern.</param>
        /// <param name="escape">The character that makes the next one literal.</param>
        /// <returns>The predicate.</returns>
        public Predicate Like(string pattern, char escape = '\\') => SymLowering.StringMatch(column.Column, StringMatchOp.Like, pattern, escape);
    }

    extension(Sym<bool> column)
    {
        /// <summary>The rows whose value is true.</summary>
        public Predicate IsTrue => column == true;

        /// <summary>The rows whose value is false.</summary>
        public Predicate IsFalse => column == false;
    }

    extension(Sym<bool?> column)
    {
        /// <summary>The rows whose value is true; a null is not.</summary>
        public Predicate IsTrue => column == true;

        /// <summary>The rows whose value is false; a null is not.</summary>
        public Predicate IsFalse => column == false;
    }

    extension<T>(Sym<ReadOnlyMemory<T>> list)
    {
        /// <summary>The rows whose list holds an element equal to <paramref name="value"/>.</summary>
        /// <param name="value">The element sought.</param>
        /// <returns>The predicate.</returns>
        public Predicate Contains(T value) => SymLowering.ListContains(list.Column, value);
    }
}
