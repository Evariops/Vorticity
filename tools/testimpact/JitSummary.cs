using System;
using System.Collections.Generic;
using System.IO;

namespace Vorticity.Tools.TestImpact;

/// <summary>
/// Reads the list of methods the JIT compiled in one process, as <c>DOTNET_JitDisasmSummary</c>
/// writes it, into method keys of the form <c>Namespace.Type+Nested:Method</c>.
/// </summary>
/// <remarks>
/// A method is compiled the first time it is called, and with inlining turned off
/// (<c>DOTNET_JitNoInline</c>) none is folded into its caller, so the list is every method the
/// process ran. Generic arguments are dropped, which merges the instantiations of one method: a
/// change to its source changes them all.
/// </remarks>
internal static class JitSummary
{
    private const string Marker = "JIT compiled ";

    /// <summary>The keys of the methods of this repository in a summary file.</summary>
    /// <param name="path">The file the JIT wrote.</param>
    /// <param name="into">Receives the keys.</param>
    internal static void Read(string path, HashSet<string> into)
    {
        foreach (string line in File.ReadLines(path))
        {
            int at = line.IndexOf(Marker, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            string? key = Key(line.AsSpan(at + Marker.Length));
            if (key is not null)
            {
                into.Add(key);
            }
        }
    }

    /// <summary>The key of one compiled method, or null when it is not a method of this repository.</summary>
    /// <param name="signature">What follows the marker: type, method, arguments and the tier in brackets.</param>
    internal static string? Key(ReadOnlySpan<char> signature)
    {
        if (!signature.StartsWith("Vorticity.", StringComparison.Ordinal))
        {
            return null;
        }

        // The type and the method are split at the last colon outside brackets before the argument
        // list, which is the first parenthesis outside brackets.
        int depth = 0;
        int colon = -1;
        int paren = -1;
        for (int i = 0; i < signature.Length && paren < 0; i++)
        {
            switch (signature[i])
            {
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    break;
                case ':' when depth == 0:
                    colon = i;
                    break;
                case '(' when depth == 0:
                    paren = i;
                    break;
                default:
                    break;
            }
        }

        if (colon < 0 || paren < colon)
        {
            return null;
        }

        return WithoutArguments(signature[..colon]) + ":" + WithoutArguments(signature[(colon + 1)..paren]);
    }

    /// <summary>A name with its bracketed generic arguments removed.</summary>
    internal static string WithoutArguments(ReadOnlySpan<char> name)
    {
        if (name.IndexOf('[') < 0)
        {
            return name.ToString();
        }

        Span<char> kept = name.Length <= 512 ? stackalloc char[name.Length] : new char[name.Length];
        int count = 0;
        int depth = 0;
        foreach (char c in name)
        {
            if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
            }
            else if (depth == 0)
            {
                kept[count++] = c;
            }
        }

        return kept[..count].ToString();
    }
}
