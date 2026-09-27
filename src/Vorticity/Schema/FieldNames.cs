using System;
using System.Collections.Generic;
using System.Threading;

namespace Vorticity;

/// <summary>
/// The first of a struct's fields to bear a name: a walk over a few fields, and over more a
/// dictionary of their names built once, since a schema and a type never change.
/// </summary>
internal static class FieldNames
{
    /// <summary>The fields up to which a walk finds a name as fast as a dictionary does.</summary>
    internal const int WalkUpTo = 16;

    /// <summary>The index of the first of <paramref name="fields"/> named exactly <paramref name="name"/>, or -1.</summary>
    /// <param name="fields">The fields, which never change.</param>
    /// <param name="index">The dictionary of their names, built by the first call that needs it.</param>
    /// <param name="name">The name.</param>
    internal static int IndexOf(VortexField[] fields, ref Dictionary<string, int>? index, ReadOnlySpan<char> name)
    {
        if (fields.Length <= WalkUpTo)
        {
            for (int i = 0; i < fields.Length; i++)
            {
                if (name.SequenceEqual(fields[i].Name))
                {
                    return i;
                }
            }

            return -1;
        }

        Dictionary<string, int> names = index ?? Build(fields, ref index);
        return names.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out int found) ? found : -1;
    }

    private static Dictionary<string, int> Build(VortexField[] fields, ref Dictionary<string, int>? index)
    {
        Dictionary<string, int> names = new Dictionary<string, int>(fields.Length, StringComparer.Ordinal);
        for (int i = 0; i < fields.Length; i++)
        {
            // Names need not be unique: the first keeps the name, as a walk finds it first.
            names.TryAdd(fields[i].Name, i);
        }

        return Interlocked.CompareExchange(ref index, names, null) ?? names;
    }
}

/// <summary>
/// A struct's columns by the name of the record member that binds one: exactly, then with case
/// ignored, as dictionaries built once for the binding when the struct is wide.
/// </summary>
/// <param name="fields">The struct's columns.</param>
internal sealed class ColumnNames(VortexField[] fields)
{
    private Dictionary<string, int>? _exact;
    private Dictionary<string, (int First, int Second)>? _loose;

    /// <summary>The first column named exactly <paramref name="name"/>, or -1.</summary>
    internal int Exact(string name) => FieldNames.IndexOf(fields, ref _exact, name);

    /// <summary>The first two columns <paramref name="name"/> names with case ignored, -1 for either that is not there.</summary>
    internal (int First, int Second) Loose(string name)
    {
        if (fields.Length <= FieldNames.WalkUpTo)
        {
            int first = -1;
            for (int i = 0; i < fields.Length; i++)
            {
                if (string.Equals(fields[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (first >= 0)
                    {
                        return (first, i);
                    }

                    first = i;
                }
            }

            return (first, -1);
        }

        _loose ??= Loosely(fields);
        return _loose.TryGetValue(name, out (int First, int Second) found) ? found : (-1, -1);
    }

    private static Dictionary<string, (int First, int Second)> Loosely(VortexField[] fields)
    {
        Dictionary<string, (int First, int Second)> names =
            new Dictionary<string, (int First, int Second)>(fields.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < fields.Length; i++)
        {
            if (!names.TryGetValue(fields[i].Name, out (int First, int Second) held))
            {
                names.Add(fields[i].Name, (i, -1));
            }
            else if (held.Second < 0)
            {
                names[fields[i].Name] = (held.First, i);
            }
        }

        return names;
    }
}
