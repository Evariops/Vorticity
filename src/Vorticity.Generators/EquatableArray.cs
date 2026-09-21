using System;
using System.Collections;
using System.Collections.Generic;

namespace Vorticity.Generators;

/// <summary>An immutable array compared by its elements, so that the generator's models cache across edits.</summary>
/// <typeparam name="T">The element type, itself compared by value.</typeparam>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
    where T : IEquatable<T>
{
    private readonly T[]? _items;

    public EquatableArray(T[] items)
    {
        _items = items;
    }

    public static EquatableArray<T> Empty => new EquatableArray<T>([]);

    public int Count => _items?.Length ?? 0;

    public T this[int index] => _items![index];

    public bool Equals(EquatableArray<T> other)
    {
        T[] mine = _items ?? [];
        T[] theirs = other._items ?? [];
        if (mine.Length != theirs.Length)
        {
            return false;
        }

        for (int i = 0; i < mine.Length; i++)
        {
            if (!mine[i].Equals(theirs[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        int hash = 17;
        foreach (T item in _items ?? [])
        {
            hash = (hash * 31) + item.GetHashCode();
        }

        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? [])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
