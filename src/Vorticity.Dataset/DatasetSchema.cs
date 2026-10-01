using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Vorticity.Types;
using Vorticity.Types.Serialization;

namespace Vorticity.Dataset;

/// <summary>A column name the dataset no longer uses, and what became of its column.</summary>
/// <param name="Name">
/// The name. No column of the dataset takes it again, so that an object written while the name was
/// in use can never lend its values to another column.
/// </param>
/// <param name="Current">The column's name now, when it was renamed; empty when it was dropped.</param>
internal readonly record struct RetiredColumn(string Name, string Current);

/// <summary>
/// The dataset's columns at one version, the clustering key over them, and the names its columns
/// had before. An object written under an earlier schema still holds its columns under those
/// names, with the types they had then, and reads as this schema through <see cref="ColumnsOf"/>.
/// </summary>
/// <remarks>
/// Columns are matched by name, which is sound because a name is never used for a second column:
/// an object's field is the column of that name now, or the column a rename moved the name to,
/// or nothing when its column was dropped. The summaries an entry carries are keyed by those
/// names too, so a column read under another name is simply not pruned by them.
/// </remarks>
internal sealed class DatasetSchema
{
    private readonly Dictionary<string, string> _renamed;
    private readonly HashSet<string> _retired;
    private readonly int _hash;

    // What ColumnsOf found for each file's dtype: a file's dtype lives in an arena of its own for as
    // long as the file is open, and an object every scan of a version reads is asked about each time.
    private readonly ConditionalWeakTable<DTypeArena, Mapped> _mapped = new ConditionalWeakTable<DTypeArena, Mapped>();

    private DatasetSchema(ReadOnlyMemory<byte> bytes, IReadOnlyList<RetiredColumn> retired, IReadOnlyList<string> clustering)
    {
        Bytes = bytes;
        Retired = retired;
        DType = bytes.IsEmpty ? default : DTypeProtobuf.Read(bytes.Span, new DTypeArena());
        Columns = DType.IsDefault ? VortexSchema.Create([]) : VortexTypes.SchemaOf(DType);
        Key = DType.IsDefault ? null : ClusteringKey.For(clustering, DType);
        _hash = DType.IsDefault ? 0 : DType.GetHashCode();
        _renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        _retired = new HashSet<string>(StringComparer.Ordinal);
        foreach (RetiredColumn column in retired)
        {
            _retired.Add(column.Name);
            if (column.Current.Length > 0)
            {
                _renamed[column.Name] = column.Current;
            }
        }
    }

    /// <summary>The schema as a header records it.</summary>
    internal ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>The schema as the engine holds it; default for a dataset that has none yet.</summary>
    internal DType DType { get; }

    /// <summary>The schema as a caller sees it, and binds a record to.</summary>
    internal VortexSchema Columns { get; }

    /// <summary>The clustering key, or null when the dataset is ordered by arrival.</summary>
    internal ClusteringKey? Key { get; }

    /// <summary>Every name a column of the dataset gave up, in the order they were given up.</summary>
    internal IReadOnlyList<RetiredColumn> Retired { get; }

    /// <summary>
    /// The schema a header names, <paramref name="previous"/> itself when the header names the same
    /// one: parsed once, and a record bound to it stays bound.
    /// </summary>
    internal static DatasetSchema Of(CommitHeader header, DatasetSchema? previous) =>
        previous is not null && previous.Bytes.Span.SequenceEqual(header.Schema.Span) && Same(previous.Retired, header.Retired)
            ? previous
            : new DatasetSchema(header.Schema, header.Retired, header.ClusteringKey);

    /// <summary>
    /// How an object of this dtype answers for these columns: null when it holds exactly them, the
    /// case of every object written since the schema last changed. Worked out once for each open
    /// file: the comparison walks every column of a schema it matches, and the mapping of an object
    /// of an earlier schema allocates.
    /// </summary>
    /// <exception cref="VortexSchemaException">The object's columns do not read as these.</exception>
    internal ObjectColumns? ColumnsOf(DType file, string objectKey)
    {
        DTypeArena arena = file.Arena;
        if (_mapped.TryGetValue(arena, out Mapped? known)
            && known.Index == file.NodeIndex
            && known.Generation == arena.Generation
            && string.Equals(known.Key, objectKey, StringComparison.Ordinal))
        {
            return known.Columns;
        }

        ObjectColumns? columns = file.GetHashCode() == _hash && file == DType ? null : ObjectColumns.Map(this, file, objectKey, strict: false);
        _mapped.AddOrUpdate(arena, new Mapped(file.NodeIndex, arena.Generation, objectKey, columns));
        return columns;
    }

    /// <summary>What <see cref="ColumnsOf"/> found for one file's dtype, at its place in its arena.</summary>
    private sealed record Mapped(int Index, int Generation, string Key, ObjectColumns? Columns);

    /// <summary>
    /// The current name of the column an object holds under <paramref name="name"/>: the name
    /// itself, or the one a rename moved it to; null when the column was dropped or never was.
    /// </summary>
    internal string? CurrentOf(string name) =>
        DType.IndexOfField(name) >= 0 ? name : _renamed.GetValueOrDefault(name);

    /// <summary>Whether a column of the dataset once had this name and gave it up.</summary>
    internal bool IsRetired(string name) => _retired.Contains(name);

    /// <summary>
    /// The names retired once the schema becomes <paramref name="next"/>, a new name mapped to the
    /// old one in <paramref name="renamed"/>, having checked that every object written under this
    /// schema reads as the next.
    /// </summary>
    /// <remarks>
    /// A column is kept under its name or a new one, and its type may widen a number within its kind
    /// or become nullable, which every value it holds survives. A new column is nullable, since the
    /// objects written before it hold no value for it, and takes a name no column ever had. A column
    /// of the clustering key orders every object and keeps its name and its type.
    /// </remarks>
    /// <exception cref="ArgumentException">The next schema is not one this one's objects read as, with the reason.</exception>
    internal IReadOnlyList<RetiredColumn> Evolve(DType next, IReadOnlyDictionary<string, string>? renamed)
    {
        if (next.Kind != DTypeKind.Struct)
        {
            throw new ArgumentException("A dataset's schema is a struct of columns.", nameof(next));
        }

        Dictionary<string, string> from = new Dictionary<string, string>(StringComparer.Ordinal);
        HashSet<string> movedAway = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string newName, string oldName) in renamed ?? new Dictionary<string, string>())
        {
            if (DType.IndexOfField(oldName) < 0)
            {
                throw new ArgumentException($"'{oldName}' is renamed, and the dataset has no column of that name.", nameof(renamed));
            }

            if (next.IndexOfField(newName) < 0)
            {
                throw new ArgumentException($"'{oldName}' is renamed to '{newName}', which the new schema does not hold.", nameof(renamed));
            }

            if (string.Equals(newName, oldName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!movedAway.Add(oldName))
            {
                throw new ArgumentException($"'{oldName}' is renamed twice.", nameof(renamed));
            }

            from[newName] = oldName;
        }

        HashSet<string> kept = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < next.FieldCount; i++)
        {
            string name = next.GetFieldName(i);
            DType type = next.GetField(i);
            string? old = from.GetValueOrDefault(name) ?? (DType.IndexOfField(name) >= 0 && !movedAway.Contains(name) ? name : null);
            if (old is null || !string.Equals(old, name, StringComparison.Ordinal))
            {
                // A name taken for a new column, or by a rename, is one no column holds or held.
                if (DType.IndexOfField(name) >= 0)
                {
                    throw new ArgumentException(
                        $"'{name}' is a column already, which the new schema renames away; a name moves to another " +
                        "column only once no object can hold it, which is never.",
                        nameof(next));
                }

                if (IsRetired(name))
                {
                    throw new ArgumentException(
                        $"'{name}' was a column of this dataset. A name is never used again, so that the objects " +
                        "written with it cannot lend their values to another column: choose another name.",
                        nameof(next));
                }
            }

            if (old is null)
            {
                if (type.Nullability != Nullability.Nullable)
                {
                    throw new ArgumentException(
                        $"'{name}' is new, and the objects written before it hold no value for it, which they read " +
                        "as null: it must be nullable.",
                        nameof(next));
                }

                continue;
            }

            kept.Add(old);
            DType was = DType.GetField(DType.IndexOfField(old));
            if (Keyed(old))
            {
                if (!string.Equals(old, name, StringComparison.Ordinal) || was != type)
                {
                    throw new ArgumentException(
                        $"'{old}' is a column of the clustering key, which orders every object: it keeps its name and its type.",
                        nameof(next));
                }
            }
            else if (ObjectColumns.ChangeOf(was, type) is null)
            {
                throw new ArgumentException(
                    $"'{old}' is {was} and cannot become {type}: a column's type changes only by widening a number " +
                    "within its kind, or by becoming nullable, which every value it holds survives.",
                    nameof(next));
            }
        }

        List<RetiredColumn> retired = [.. Retired];
        for (int i = 0; i < DType.FieldCount; i++)
        {
            string name = DType.GetFieldName(i);
            if (kept.Contains(name))
            {
                continue;
            }

            if (Keyed(name))
            {
                throw new ArgumentException($"'{name}' is a column of the clustering key, which orders every object: it cannot be dropped.", nameof(next));
            }

            Retire(retired, name, string.Empty);
        }

        foreach ((string newName, string oldName) in from)
        {
            Retire(retired, oldName, newName);
        }

        return retired;
    }

    /// <summary>Whether a top-level column carries a column of the clustering key.</summary>
    internal bool IsKeyed(string column) => Keyed(column);

    private bool Keyed(string column)
    {
        if (Key is not { } key)
        {
            return false;
        }

        foreach (string path in key.Paths)
        {
            int dot = path.IndexOf('.', StringComparison.Ordinal);
            if (string.Equals(dot < 0 ? path : path[..dot], column, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Retires a name, the column's earlier names following it to its new name or out of the schema.</summary>
    private static void Retire(List<RetiredColumn> retired, string name, string current)
    {
        for (int i = 0; i < retired.Count; i++)
        {
            if (string.Equals(retired[i].Current, name, StringComparison.Ordinal))
            {
                retired[i] = retired[i] with { Current = current };
            }
        }

        retired.Add(new RetiredColumn(name, current));
    }

    private static bool Same(IReadOnlyList<RetiredColumn> left, IReadOnlyList<RetiredColumn> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }
}
