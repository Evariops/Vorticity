using System;
using Vorticity.Expressions;

namespace Vorticity;

/// <summary>The predicates that only some column types have: text matching, boolean tests, list membership.</summary>
public static class SymExtensions
{
    extension(Sym<string> column)
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

    extension<T1, T2>(Sym<(T1, T2)> key)
    {
        /// <summary>The first column of a group's composite key.</summary>
        public Sym<T1> Item1 => Aggregating.KeyItems.Item<(T1, T2), T1>(key, 0);

        /// <summary>The second column of a group's composite key.</summary>
        public Sym<T2> Item2 => Aggregating.KeyItems.Item<(T1, T2), T2>(key, 1);
    }

    extension<T1, T2, T3>(Sym<(T1, T2, T3)> key)
    {
        /// <summary>The first column of a group's composite key.</summary>
        public Sym<T1> Item1 => Aggregating.KeyItems.Item<(T1, T2, T3), T1>(key, 0);

        /// <summary>The second column of a group's composite key.</summary>
        public Sym<T2> Item2 => Aggregating.KeyItems.Item<(T1, T2, T3), T2>(key, 1);

        /// <summary>The third column of a group's composite key.</summary>
        public Sym<T3> Item3 => Aggregating.KeyItems.Item<(T1, T2, T3), T3>(key, 2);
    }

    extension<T1, T2, T3, T4>(Sym<(T1, T2, T3, T4)> key)
    {
        /// <summary>The first column of a group's composite key.</summary>
        public Sym<T1> Item1 => Aggregating.KeyItems.Item<(T1, T2, T3, T4), T1>(key, 0);

        /// <summary>The second column of a group's composite key.</summary>
        public Sym<T2> Item2 => Aggregating.KeyItems.Item<(T1, T2, T3, T4), T2>(key, 1);

        /// <summary>The third column of a group's composite key.</summary>
        public Sym<T3> Item3 => Aggregating.KeyItems.Item<(T1, T2, T3, T4), T3>(key, 2);

        /// <summary>The fourth column of a group's composite key.</summary>
        public Sym<T4> Item4 => Aggregating.KeyItems.Item<(T1, T2, T3, T4), T4>(key, 3);
    }
}
