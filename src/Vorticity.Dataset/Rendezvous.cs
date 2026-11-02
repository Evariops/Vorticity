using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Text;

namespace Vorticity.Dataset;

/// <summary>
/// The owners of an object among a cluster's nodes (spec F, 4): each node scores the object by the XXH3-64 of its name
/// under a seed the object's identity gives, and the node of the highest score owns it, the next ones after it. A node
/// added or taken away moves only the objects it owns, the others keeping their owner.
/// </summary>
internal static class Rendezvous
{
    /// <summary>The node of <paramref name="nodes"/> that owns the object of identity <paramref name="key"/>.</summary>
    internal static string Owner(UInt128 key, IReadOnlyList<string> nodes)
    {
        string? owner = null;
        ulong best = 0;
        foreach (string node in nodes)
        {
            ulong score = Score(node, key);
            if (owner is null || score > best || (score == best && string.CompareOrdinal(node, owner) < 0))
            {
                (owner, best) = (node, score);
            }
        }

        return owner ?? throw new ArgumentException("A cluster has a node at least.", nameof(nodes));
    }

    /// <summary>The nodes of <paramref name="nodes"/> in the order they own the object of identity <paramref name="key"/>: its owner first.</summary>
    internal static string[] Order(UInt128 key, IReadOnlyList<string> nodes)
    {
        (ulong Score, string Node)[] scored = new (ulong, string)[nodes.Count];
        for (int i = 0; i < scored.Length; i++)
        {
            scored[i] = (Score(nodes[i], key), nodes[i]);
        }

        Array.Sort(scored, static (left, right) => right.Score != left.Score ? right.Score.CompareTo(left.Score) : string.CompareOrdinal(left.Node, right.Node));
        string[] order = new string[scored.Length];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = scored[i].Node;
        }

        return order;
    }

    private static ulong Score(string node, UInt128 key)
    {
        int length = Encoding.UTF8.GetByteCount(node);
        Span<byte> name = length <= 256 ? stackalloc byte[length] : new byte[length];
        Encoding.UTF8.GetBytes(node, name);
        return XxHash3.HashToUInt64(name, (long)((ulong)key ^ (ulong)(key >> 64)));
    }
}
