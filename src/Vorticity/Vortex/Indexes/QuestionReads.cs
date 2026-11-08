using System;
using System.Collections.Generic;

namespace Vorticity.Indexes;

/// <summary>
/// What one question read and parsed of its files' indexes, kept for its other scans of a file while it holds the file,
/// and dropped with the last of its holds: the scans of a question's parts, an object's each, read its variant directory
/// again, a frame decompressed and walked, some 1.7 times an object of a question of the services under 5xx
/// (2026-11-02), and the two passes of an object a question reads by its filter, then its rows kept, each read its path
/// maps again. Kept until the question's end, an object's reads and its granules' geometry outlived the collections the
/// question waited through after it was read, more than a quarter of what the warm mix at 32 clients left the collector
/// to copy (2026-11-08): they now go with the last lease of their object (<see cref="Hold"/>, <see cref="Release"/>),
/// young. A file read with no hold keeps its reads until the question ends. Nothing outlives the question: an object
/// open across questions keeps none of it.
/// </summary>
/// <remarks>Under one lock, its own: the lanes of the question's scans plan their pruning side by side.</remarks>
internal sealed class QuestionReads
{
    // Each made at the first read it keeps, a question that reads none holding nothing.
    private Dictionary<(VortexFile File, int Field, string Paths), VariantDirectory?>? _directories;

    // The path maps of the question's columns, by file, column and the keys read: the two scans of an object a question reads
    // by its filter, then its rows kept, each read them again, a question of a flag of the log attributes 24 over 12 objects,
    // their regions decompressed (2026-11-08). The keys are a set: the scans name them in their own orders. The block
    // statistics are not kept: read for a scan's pruning alone, they die with it, in arrays rented for them.
    private Dictionary<Read<string>, VariantPathMap?>? _pathMaps;

    // The chunks of a file a scan reads its variant columns' keys by, by the columns and their keys: made again at each scan
    // of an object, their keys, chunks and dtypes, from the maps the question keeps.
    private Dictionary<KeyedColumns, VariantKeyReads?>? _keyReads;

    // The geometry of the granules of each file's chunks, null for none: the lanes that read the parts of one object, and
    // the parts of a scan, read their granules by it.
    private Dictionary<VortexFile, GranuleGeometry?>? _geometries;

    // The leases the question's scans hold, by file: a few at once, the objects read and the one opened ahead. Made at the
    // first, and kept for the question's next.
    private (VortexFile File, int Leases)[]? _holds;
    private int _held;

    /// <summary>The reads the question keeps, every kind and file together, for tests.</summary>
    internal int ReadsKept
    {
        get
        {
            lock (this)
            {
                return (_directories?.Count ?? 0) + (_pathMaps?.Count ?? 0) + (_keyReads?.Count ?? 0) + (_geometries?.Count ?? 0);
            }
        }
    }

    /// <summary>A scan of the question holds <paramref name="file"/>, a lease of its object: what the question reads of it stays until the last such hold is released.</summary>
    internal void Hold(VortexFile file)
    {
        lock (this)
        {
            for (int i = 0; i < _held; i++)
            {
                if (ReferenceEquals(_holds![i].File, file))
                {
                    _holds[i].Leases++;
                    return;
                }
            }

            if (_holds is null || _held == _holds.Length)
            {
                Array.Resize(ref _holds, Math.Max(4, 2 * _held));
            }

            _holds[_held++] = (file, 1);
        }
    }

    /// <summary>A hold of <paramref name="file"/> ended: at the last one, what the question read of it goes, for a collection to take while it is young.</summary>
    internal void Release(VortexFile file)
    {
        lock (this)
        {
            for (int i = 0; i < _held; i++)
            {
                if (!ReferenceEquals(_holds![i].File, file))
                {
                    continue;
                }

                if (--_holds[i].Leases > 0)
                {
                    return;
                }

                _holds[i] = _holds[--_held];
                _holds[_held] = default;
                Forget(file);
                return;
            }
        }
    }

    /// <summary>Drops what the question read of <paramref name="file"/>, under the lock: a dictionary may lose its keys as it walks them.</summary>
    private void Forget(VortexFile file)
    {
        if (_directories is { } directories)
        {
            foreach ((VortexFile File, int Field, string Paths) key in directories.Keys)
            {
                if (ReferenceEquals(key.File, file))
                {
                    directories.Remove(key);
                }
            }
        }

        if (_pathMaps is { } maps)
        {
            foreach (Read<string> key in maps.Keys)
            {
                if (key.Of(file))
                {
                    maps.Remove(key);
                }
            }
        }

        if (_keyReads is { } reads)
        {
            foreach (KeyedColumns key in reads.Keys)
            {
                if (key.Of(file))
                {
                    reads.Remove(key);
                }
            }
        }

        _geometries?.Remove(file);
    }

    /// <summary>The variant directory of column <paramref name="field"/> of <paramref name="file"/> for <paramref name="paths"/> the question read, if any.</summary>
    internal bool TryGetDirectory(VortexFile file, int field, string paths, out VariantDirectory? directory)
    {
        lock (this)
        {
            directory = null;
            return _directories is { } kept && kept.TryGetValue((file, field, paths), out directory);
        }
    }

    /// <summary>Keeps <paramref name="directory"/>, the variant directory of column <paramref name="field"/> of <paramref name="file"/> for <paramref name="paths"/>, for the question's other scans.</summary>
    internal void KeepDirectory(VortexFile file, int field, string paths, VariantDirectory? directory)
    {
        lock (this)
        {
            (_directories ??= []).TryAdd((file, field, paths), directory);
        }
    }

    /// <summary>The path map of column <paramref name="field"/> of <paramref name="file"/> for the keys <paramref name="wanted"/> (every key under null) the question read, if any.</summary>
    internal bool TryGetPathMap(VortexFile file, int field, IReadOnlyCollection<string>? wanted, out VariantPathMap? map)
    {
        lock (this)
        {
            map = null;
            return _pathMaps is { } kept && kept.TryGetValue(new Read<string>(file, field, wanted), out map);
        }
    }

    /// <summary>Keeps <paramref name="map"/>, the path map of column <paramref name="field"/> of <paramref name="file"/> for <paramref name="wanted"/>, for the question's other scans.</summary>
    internal void KeepPathMap(VortexFile file, int field, IReadOnlyCollection<string>? wanted, VariantPathMap? map)
    {
        lock (this)
        {
            (_pathMaps ??= []).TryAdd(new Read<string>(file, field, wanted), map);
        }
    }

    /// <summary>The reads of <paramref name="file"/>'s columns by the keys <paramref name="columns"/> names the question made, if any.</summary>
    internal bool TryGetKeyReads(VortexFile file, IReadOnlyDictionary<string, string[]> columns, out VariantKeyReads? reads)
    {
        lock (this)
        {
            reads = null;
            return _keyReads is { } kept && kept.TryGetValue(new KeyedColumns(file, columns), out reads);
        }
    }

    /// <summary>Keeps <paramref name="reads"/>, the reads of <paramref name="file"/>'s columns by the keys <paramref name="columns"/> names, for the question's other scans.</summary>
    internal void KeepKeyReads(VortexFile file, IReadOnlyDictionary<string, string[]> columns, VariantKeyReads? reads)
    {
        lock (this)
        {
            (_keyReads ??= []).TryAdd(new KeyedColumns(file, columns), reads);
        }
    }

    /// <summary>The geometry of the granules of <paramref name="file"/>'s chunks the question read, null for none, if it read it.</summary>
    internal bool TryGetGeometry(VortexFile file, out GranuleGeometry? geometry)
    {
        lock (this)
        {
            geometry = null;
            return _geometries is { } kept && kept.TryGetValue(file, out geometry);
        }
    }

    /// <summary>Keeps <paramref name="geometry"/>, null for none, the geometry of the granules of <paramref name="file"/>'s chunks, for the question's other scans; the first kept stays.</summary>
    internal void KeepGeometry(VortexFile file, GranuleGeometry? geometry)
    {
        lock (this)
        {
            (_geometries ??= new Dictionary<VortexFile, GranuleGeometry?>(ReferenceEqualityComparer.Instance)).TryAdd(file, geometry);
        }
    }

    /// <summary>A file's columns read by some of their keys: the same when the file is, and each column with the same set of keys.</summary>
    private readonly struct KeyedColumns(VortexFile file, IReadOnlyDictionary<string, string[]> columns) : IEquatable<KeyedColumns>
    {
        private readonly VortexFile _file = file;
        private readonly IReadOnlyDictionary<string, string[]> _columns = columns;

        /// <summary>Whether these are columns of <paramref name="file"/>.</summary>
        internal bool Of(VortexFile file) => ReferenceEquals(_file, file);

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

    /// <summary>What a scan read of a column of a file for some of its keys, null for every one: the same read when the file, the column and the set of keys are.</summary>
    private readonly struct Read<TKey>(VortexFile file, int field, IReadOnlyCollection<TKey>? keys) : IEquatable<Read<TKey>>
    {
        private readonly VortexFile _file = file;
        private readonly int _field = field;
        private readonly IReadOnlyCollection<TKey>? _keys = keys;

        /// <summary>Whether this is a read of <paramref name="file"/>.</summary>
        internal bool Of(VortexFile file) => ReferenceEquals(_file, file);

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
