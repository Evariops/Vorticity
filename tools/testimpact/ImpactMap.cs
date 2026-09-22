using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Vorticity.Tools.TestImpact;

/// <summary>A test class of one test assembly.</summary>
/// <param name="Assembly">The test assembly's name.</param>
/// <param name="Name">The class's full name.</param>
internal readonly record struct TestClass(string Assembly, string Name);

/// <summary>Which test classes ran which methods of the repository, as <c>testimpact map</c> recorded it.</summary>
internal sealed class ImpactMap
{
    /// <summary>The format's first line; a map with another is from an older format, and is rebuilt.</summary>
    /// <remarks>2: keys without the compiler's ordinals (<see cref="JitSummary.Stable"/>).</remarks>
    private const string Header = "testimpact 2";

    private readonly List<TestClass> _classes;
    private readonly Dictionary<string, List<int>> _byMethod;
    private readonly Dictionary<string, HashSet<int>> _byType = new(StringComparer.Ordinal);

    internal ImpactMap(string commit, List<TestClass> classes, Dictionary<string, List<int>> byMethod)
    {
        Commit = commit;
        _classes = classes;
        _byMethod = byMethod;
        foreach ((string key, List<int> runners) in byMethod)
        {
            string type = TypeOf(key);
            if (!_byType.TryGetValue(type, out HashSet<int>? set))
            {
                set = [];
                _byType[type] = set;
            }

            set.UnionWith(runners);
        }
    }

    /// <summary>The commit the map was built at.</summary>
    internal string Commit { get; }

    /// <summary>The classes the map knows, by index.</summary>
    internal IReadOnlyList<TestClass> Classes => _classes;

    /// <summary>Whether some class ran the method.</summary>
    internal bool Knows(string key) => _byMethod.ContainsKey(key);

    /// <summary>The classes that ran the method.</summary>
    internal IEnumerable<int> Running(string key) =>
        _byMethod.TryGetValue(key, out List<int>? runners) ? runners : [];

    /// <summary>The classes that ran any method of a type, or of a type nested in it.</summary>
    internal IEnumerable<int> RunningType(string type)
    {
        HashSet<int> found = [];
        foreach ((string known, HashSet<int> runners) in _byType)
        {
            if (known == type || known.StartsWith(type + "+", StringComparison.Ordinal))
            {
                found.UnionWith(runners);
            }
        }

        return found;
    }

    /// <summary>The type part of a method key.</summary>
    internal static string TypeOf(string key) => key[..key.LastIndexOf(':')];

    internal void Save(string path)
    {
        StringBuilder text = new StringBuilder();
        text.Append(Header).Append('\n');
        text.Append("commit ").Append(Commit).Append('\n');
        for (int i = 0; i < _classes.Count; i++)
        {
            text.Append("class ").Append(i.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(_classes[i].Assembly).Append('\t').Append(_classes[i].Name).Append('\n');
        }

        foreach ((string key, List<int> runners) in _byMethod)
        {
            text.Append("method ").Append(key).Append('\t');
            for (int i = 0; i < runners.Count; i++)
            {
                text.Append(i == 0 ? string.Empty : ",").Append(runners[i].ToString(CultureInfo.InvariantCulture));
            }

            text.Append('\n');
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ToString());
    }

    /// <summary>The map at <paramref name="path"/>, or null when there is none of this format.</summary>
    internal static ImpactMap? Load(string path)
    {
        if (!File.Exists(path) || File.ReadLines(path).FirstOrDefault() != Header)
        {
            return null;
        }

        string commit = string.Empty;
        List<TestClass> classes = [];
        Dictionary<string, List<int>> byMethod = new(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith("commit ", StringComparison.Ordinal))
            {
                commit = line[7..];
            }
            else if (line.StartsWith("class ", StringComparison.Ordinal))
            {
                string[] parts = line[6..].Split('\t');
                classes.Add(new TestClass(parts[1], parts[2]));
            }
            else if (line.StartsWith("method ", StringComparison.Ordinal))
            {
                int tab = line.LastIndexOf('\t');
                List<int> runners = [];
                foreach (string index in line[(tab + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    runners.Add(int.Parse(index, CultureInfo.InvariantCulture));
                }

                byMethod[line[7..tab]] = runners;
            }
        }

        return new ImpactMap(commit, classes, byMethod);
    }
}
