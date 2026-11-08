using System;
using System.Collections.Generic;
using System.Threading;

namespace Vorticity.Indexes;

/// <summary>
/// What one question read and parsed of its files' indexes, kept for its other scans and dropped with it: the scans of a
/// question's parts, an object's each, read its variant directory again, a frame decompressed and walked, some 1.7 times an
/// object of a question of the services under 5xx (2026-11-02). Nothing outlives the question: an object open across
/// questions keeps none of it.
/// </summary>
internal sealed class QuestionReads
{
    // Made at the first directory kept, a question that reads none holding nothing; the dictionary its own lock, the lanes
    // of the question's scans planning their pruning side by side.
    private Dictionary<(VortexFile File, int Field, string Paths), VariantDirectory?>? _directories;

    /// <summary>The variant directory of column <paramref name="field"/> of <paramref name="file"/> for <paramref name="paths"/> the question read, if any.</summary>
    internal bool TryGetDirectory(VortexFile file, int field, string paths, out VariantDirectory? directory)
    {
        directory = null;
        if (Volatile.Read(ref _directories) is not { } kept)
        {
            return false;
        }

        lock (kept)
        {
            return kept.TryGetValue((file, field, paths), out directory);
        }
    }

    /// <summary>Keeps <paramref name="directory"/>, the variant directory of column <paramref name="field"/> of <paramref name="file"/> for <paramref name="paths"/>, for the question's other scans.</summary>
    internal void KeepDirectory(VortexFile file, int field, string paths, VariantDirectory? directory)
    {
        Dictionary<(VortexFile File, int Field, string Paths), VariantDirectory?> kept = Volatile.Read(ref _directories)
            ?? Interlocked.CompareExchange(ref _directories, [], null) ?? _directories!;
        lock (kept)
        {
            kept.TryAdd((file, field, paths), directory);
        }
    }

    // The path maps and the block statistics of the question's columns, by file, column and the keys read: the two scans of
    // an object a question reads by its filter, then its rows kept, each read them again, a question of a flag of the log
    // attributes 24 of each over 12 objects, their regions decompressed (2026-11-08). The keys are a set: the scans name
    // them in their own orders.
    private Dictionary<Read<string>, VariantPathMap?>? _pathMaps;
    private Dictionary<Read<(string Key, Types.Variants.PathClass Class)>, VariantBlockStats?>? _blockStats;

    /// <summary>The path map of column <paramref name="field"/> of <paramref name="file"/> for the keys <paramref name="wanted"/> (every key under null) the question read, if any.</summary>
    internal bool TryGetPathMap(VortexFile file, int field, IReadOnlyCollection<string>? wanted, out VariantPathMap? map) =>
        TryGet(ref _pathMaps, new Read<string>(file, field, wanted), out map);

    /// <summary>Keeps <paramref name="map"/>, the path map of column <paramref name="field"/> of <paramref name="file"/> for <paramref name="wanted"/>, for the question's other scans.</summary>
    internal void KeepPathMap(VortexFile file, int field, IReadOnlyCollection<string>? wanted, VariantPathMap? map) =>
        Keep(ref _pathMaps, new Read<string>(file, field, wanted), map);

    /// <summary>The block statistics of column <paramref name="field"/> of <paramref name="file"/> for the keys <paramref name="wanted"/> the question read, if any.</summary>
    internal bool TryGetBlockStats(
        VortexFile file, int field, IReadOnlyCollection<(string Key, Types.Variants.PathClass Class)> wanted, out VariantBlockStats? stats) =>
        TryGet(ref _blockStats, new Read<(string Key, Types.Variants.PathClass Class)>(file, field, wanted), out stats);

    /// <summary>Keeps <paramref name="stats"/>, the block statistics of column <paramref name="field"/> of <paramref name="file"/> for <paramref name="wanted"/>, for the question's other scans.</summary>
    internal void KeepBlockStats(
        VortexFile file, int field, IReadOnlyCollection<(string Key, Types.Variants.PathClass Class)> wanted, VariantBlockStats? stats) =>
        Keep(ref _blockStats, new Read<(string Key, Types.Variants.PathClass Class)>(file, field, wanted), stats);

    // The chunks of a file a scan reads its variant columns' keys by, by the columns and their keys: made again at each scan
    // of an object, their keys, chunks and dtypes, from the maps the question keeps.
    private Dictionary<KeyedColumns, VariantKeyReads?>? _keyReads;

    /// <summary>The reads of <paramref name="file"/>'s columns by the keys <paramref name="columns"/> names the question made, if any.</summary>
    internal bool TryGetKeyReads(VortexFile file, IReadOnlyDictionary<string, string[]> columns, out VariantKeyReads? reads)
    {
        reads = null;
        if (Volatile.Read(ref _keyReads) is not { } kept)
        {
            return false;
        }

        lock (kept)
        {
            return kept.TryGetValue(new KeyedColumns(file, columns), out reads);
        }
    }

    /// <summary>Keeps <paramref name="reads"/>, the reads of <paramref name="file"/>'s columns by the keys <paramref name="columns"/> names, for the question's other scans.</summary>
    internal void KeepKeyReads(VortexFile file, IReadOnlyDictionary<string, string[]> columns, VariantKeyReads? reads)
    {
        Dictionary<KeyedColumns, VariantKeyReads?> kept = Volatile.Read(ref _keyReads) ?? Interlocked.CompareExchange(ref _keyReads, [], null) ?? _keyReads!;
        lock (kept)
        {
            kept.TryAdd(new KeyedColumns(file, columns), reads);
        }
    }

    /// <summary>A file's columns read by some of their keys: the same when the file is, and each column with the same set of keys.</summary>
    private readonly struct KeyedColumns(VortexFile file, IReadOnlyDictionary<string, string[]> columns) : IEquatable<KeyedColumns>
    {
        private readonly VortexFile _file = file;
        private readonly IReadOnlyDictionary<string, string[]> _columns = columns;

        public bool Equals(KeyedColumns other)
        {
            if (!ReferenceEquals(_file, other._file) || _columns.Count != other._columns.Count)
            {
                return false;
            }

            // A dictionary the scan builder made, walked by its own enumerator, which an interface would box.
            if (_columns is Dictionary<string, string[]> mine)
            {
                foreach ((string column, string[] keys) in mine)
                {
                    if (!Same(column, keys, other._columns))
                    {
                        return false;
                    }
                }

                return true;
            }

            foreach ((string column, string[] keys) in _columns)
            {
                if (!Same(column, keys, other._columns))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Same(string column, string[] keys, IReadOnlyDictionary<string, string[]> other)
        {
            if (!other.TryGetValue(column, out string[]? theirs) || theirs.Length != keys.Length)
            {
                return false;
            }

            foreach (string key in keys)
            {
                if (Array.IndexOf(theirs, key) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is KeyedColumns other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_file), _columns.Count);
    }

    private static bool TryGet<TKey, TValue>(ref Dictionary<Read<TKey>, TValue?>? reads, Read<TKey> read, out TValue? value)
        where TValue : class
    {
        value = null;
        if (Volatile.Read(ref reads) is not { } kept)
        {
            return false;
        }

        lock (kept)
        {
            return kept.TryGetValue(read, out value);
        }
    }

    private static void Keep<TKey, TValue>(ref Dictionary<Read<TKey>, TValue?>? reads, Read<TKey> read, TValue? value)
        where TValue : class
    {
        Dictionary<Read<TKey>, TValue?> kept = Volatile.Read(ref reads) ?? Interlocked.CompareExchange(ref reads, [], null) ?? reads!;
        lock (kept)
        {
            kept.TryAdd(read, value);
        }
    }

    /// <summary>What a scan read of a column of a file for some of its keys, null for every one: the same read when the file, the column and the set of keys are.</summary>
    private readonly struct Read<TKey>(VortexFile file, int field, IReadOnlyCollection<TKey>? keys) : IEquatable<Read<TKey>>
    {
        private readonly VortexFile _file = file;
        private readonly int _field = field;
        private readonly IReadOnlyCollection<TKey>? _keys = keys;

        public bool Equals(Read<TKey> other)
        {
            if (!ReferenceEquals(_file, other._file) || _field != other._field)
            {
                return false;
            }

            if (_keys is null || other._keys is null)
            {
                return _keys is null && other._keys is null;
            }

            if (_keys.Count != other._keys.Count)
            {
                return false;
            }

            // A few keys a column: each sought among the other's, by their places when both are lists, an array's or a
            // list's, whose enumerators an interface would box.
            if (_keys is IReadOnlyList<TKey> mine && other._keys is IReadOnlyList<TKey> theirs)
            {
                for (int i = 0; i < mine.Count; i++)
                {
                    if (!Holds(theirs, mine[i]))
                    {
                        return false;
                    }
                }

                return true;
            }

            foreach (TKey key in _keys)
            {
                bool found = false;
                foreach (TKey their in other._keys)
                {
                    if (EqualityComparer<TKey>.Default.Equals(key, their))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Holds(IReadOnlyList<TKey> keys, TKey key)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (EqualityComparer<TKey>.Default.Equals(keys[i], key))
                {
                    return true;
                }
            }

            return false;
        }

        public override bool Equals(object? obj) => obj is Read<TKey> other && Equals(other);

        // The file, the column and the count of keys: the keys' order is no part of it.
        public override int GetHashCode() => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_file), _field, _keys?.Count ?? -1);
    }
}
