using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Vorticity.Layouts;

namespace Vorticity;

/// <summary>
/// A record bound to the columns of a file: which column each member reads, where it sits in a
/// projected batch, and what the scan must read.
/// </summary>
/// <remarks>
/// Built once per record type and file schema, at the first sink of a scan or at a writer's
/// creation. The name match and the nullability are checked here; the .NET type of each member is
/// checked the first time a <c>Column&lt;T&gt;</c> of it is asked for, when <c>T</c> is known.
/// </remarks>
internal sealed class RecordBinding
{
    private readonly Type?[] _checked;

    private RecordBinding(
        Type recordType, VortexSchema record, VortexField[] fileFields, int[] fileIndex, int[] batchIndex,
        RecordBinding?[] nested, string[][] paths, int[][] indexPaths, VortexExtensionRegistry? extensions)
    {
        RecordType = recordType;
        Record = record;
        FileFields = fileFields;
        FileIndex = fileIndex;
        BatchIndex = batchIndex;
        Nested = nested;
        Paths = paths;
        IndexPaths = indexPaths;
        Extensions = extensions;
        _checked = new Type?[fileIndex.Length];
    }

    /// <summary>Per member, the column's field indices from the file's root.</summary>
    internal int[][] IndexPaths { get; }

    internal Type RecordType { get; }

    /// <summary>The record's own schema.</summary>
    internal VortexSchema Record { get; }

    /// <summary>The fields of the struct the record is bound to: the file's root fields, or a nested struct's.</summary>
    internal VortexField[] FileFields { get; }

    /// <summary>Per member, the index of its column among <see cref="FileFields"/>.</summary>
    internal int[] FileIndex { get; }

    /// <summary>Per member, the index of its column in a batch projected by <see cref="Mask"/>.</summary>
    internal int[] BatchIndex { get; }

    /// <summary>Per member, the binding of a nested record, or null for a leaf.</summary>
    internal RecordBinding?[] Nested { get; }

    /// <summary>Per member, the column's path from the file's root, by name.</summary>
    internal string[][] Paths { get; }

    internal VortexExtensionRegistry? Extensions { get; }

    /// <summary>The columns the scan reads for this record, as a mask of the bound struct.</summary>
    internal FieldMask Mask { get; private set; }

    /// <summary>The file type of member <paramref name="member"/>.</summary>
    internal VortexType TypeOf(int member) => FileFields[FileIndex[member]].Type;

    /// <summary>The binding of <typeparamref name="TRecord"/> to <paramref name="file"/>, built once per schema.</summary>
    internal static RecordBinding For<TRecord>(VortexSchema file, VortexExtensionRegistry? extensions)
        where TRecord : IVortexRecord<TRecord>
    {
        if (Cache<TRecord>.Bindings.TryGetValue(file, out RecordBinding? binding))
        {
            return binding;
        }

        if (!file.RootIsStruct)
        {
            throw new VortexSchemaException(
                $"{typeof(TRecord).Name} binds to the columns of a file whose root is a struct; this file holds one column of {file.Root}. Read it with the tool scan, file.Scan().");
        }

        binding = Bind(typeof(TRecord), TRecord.Schema, file.FieldArray, [], [], extensions);
        Cache<TRecord>.Bindings.AddOrUpdate(file, binding);
        return binding;
    }

    /// <summary>The binding of a nested record <typeparamref name="TNested"/> held by member <paramref name="member"/>.</summary>
    internal RecordBinding NestedFor<TNested>(int member)
        where TNested : IVortexRecord<TNested>
    {
        RecordBinding? nested = (uint)member < (uint)Nested.Length ? Nested[member] : null;
        if (nested is not null && ReferenceEquals(nested.RecordType, typeof(TNested)))
        {
            return nested;
        }

        if (nested is null || !nested.Record.Equals(TNested.Schema))
        {
            throw new VortexSchemaException(
                $"Member {member} of {RecordType.Name} is not the nested record {typeof(TNested).Name}.");
        }

        RecordBinding typed = nested.Retarget(typeof(TNested));
        Nested[member] = typed;
        return typed;
    }

    /// <summary>Checks, once per member and type, that <typeparamref name="T"/> reads member <paramref name="member"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Require<T>(int member)
    {
        if ((uint)member >= (uint)_checked.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(member), member, $"{RecordType.Name} has {_checked.Length} members.");
        }

        if (!ReferenceEquals(_checked[member], typeof(T)))
        {
            Check<T>(member);
        }
    }

    private void Check<T>(int member)
    {
        ClrFit.Require<T>(TypeOf(member), $"Member '{Record[member].Name}' of {RecordType.Name}, bound to '{string.Join('.', Paths[member])}',", Extensions);
        _checked[member] = typeof(T);
    }

    /// <summary>The index of the member named <paramref name="name"/> in the record.</summary>
    internal int MemberIndex(string name)
    {
        int index = Record.IndexOfName(name);
        return index >= 0 ? index : throw new VortexSchemaException($"{RecordType.Name} has no member '{name}'.");
    }

    private static RecordBinding Bind(
        Type recordType, VortexSchema record, VortexField[] fileFields, string[] prefix, int[] indexPrefix, VortexExtensionRegistry? extensions)
    {
        int count = record.Count;
        int[] fileIndex = new int[count];
        RecordBinding?[] nested = new RecordBinding?[count];
        string[][] paths = new string[count][];
        int[][] indexPaths = new int[count][];
        FieldMaskBuilder mask = new FieldMaskBuilder();
        ColumnNames names = new ColumnNames(fileFields);
        for (int i = 0; i < count; i++)
        {
            VortexField member = record[i];
            int found = Match(recordType, member.Name, fileFields, names);
            fileIndex[i] = found;
            VortexField column = fileFields[found];
            paths[i] = [.. prefix, column.Name];
            indexPaths[i] = [.. indexPrefix, found];
            CheckShape(recordType, member, column);

            VortexType storage = Unwrap(column.Type);
            if (member.Type.Kind == VortexTypeKind.Struct && storage.Kind == VortexTypeKind.Struct)
            {
                RecordBinding inner = Bind(recordType, VortexSchema.Create(member.Type.Fields), storage.Fields.ToArray(), paths[i], indexPaths[i], extensions);
                nested[i] = inner;
                IncludeNested(mask, found, inner.Mask);
            }
            else
            {
                mask.IncludeField(found);
            }
        }

        // The projected batch keeps the selected fields in file order, so a member's batch index is
        // the rank of its column among the selected ones.
        int[] selected = (int[])fileIndex.Clone();
        Array.Sort(selected);
        int distinct = 0;
        for (int i = 0; i < selected.Length; i++)
        {
            if (i == 0 || selected[i] != selected[i - 1])
            {
                selected[distinct++] = selected[i];
            }
        }

        int[] batchIndex = new int[count];
        for (int i = 0; i < count; i++)
        {
            batchIndex[i] = Array.BinarySearch(selected, 0, distinct, fileIndex[i]);
        }

        RecordBinding binding = new RecordBinding(recordType, record, fileFields, fileIndex, batchIndex, nested, paths, indexPaths, extensions)
        {
            Mask = mask.Build(),
        };
        return binding;
    }

    /// <summary>The same binding, named after the nested record type that reads it.</summary>
    internal RecordBinding Retarget(Type nestedType) =>
        new RecordBinding(nestedType, Record, FileFields, FileIndex, BatchIndex, Nested, Paths, IndexPaths, Extensions) { Mask = Mask };

    private static void IncludeNested(FieldMaskBuilder mask, int field, in FieldMask inner)
    {
        if (inner.IsAll)
        {
            mask.IncludeField(field);
            return;
        }

        for (int i = 0; i < inner.NamedFieldCount; i++)
        {
            int child = inner.GetNamedField(i);
            IncludePath(mask, [field, child], inner.Descend(child));
        }
    }

    private static void IncludePath(FieldMaskBuilder mask, int[] path, in FieldMask below)
    {
        if (below.IsAll)
        {
            mask.Include(path);
            return;
        }

        for (int i = 0; i < below.NamedFieldCount; i++)
        {
            int child = below.GetNamedField(i);
            IncludePath(mask, [.. path, child], below.Descend(child));
        }
    }

    private static int Match(Type recordType, string name, VortexField[] fields, ColumnNames names)
    {
        int exact = names.Exact(name);
        if (exact >= 0)
        {
            return exact;
        }

        (int found, int second) = names.Loose(name);
        if (second >= 0)
        {
            throw new VortexSchemaException(
                $"Member '{name}' of {recordType.Name} matches both '{fields[found].Name}' and '{fields[second].Name}' when case is ignored; name the column with [VortexColumn].");
        }

        if (found < 0)
        {
            throw new VortexSchemaException(
                $"Member '{name}' of {recordType.Name} has no column; the columns are {string.Join(", ", Names(fields))}.");
        }

        return found;
    }

    private static void CheckShape(Type recordType, VortexField member, VortexField column)
    {
        if (!member.Type.IsNullable && column.Type.IsNullable)
        {
            throw new VortexSchemaException(
                $"Member '{member.Name}' of {recordType.Name} is not nullable and its column '{column.Name}' is ({column.Type}); declare the member nullable.");
        }

        if (!Compatible(member.Type, column.Type))
        {
            throw new VortexSchemaException(
                $"Member '{member.Name}' of {recordType.Name} is {member.Type.NonNullable} and its column '{column.Name}' is {column.Type}.");
        }
    }

    /// <summary>Whether a record type and a file type are the same kind of column; the exact .NET fit is checked on first access.</summary>
    private static bool Compatible(VortexType record, VortexType file)
    {
        VortexType storage = Unwrap(file);
        return record.Kind switch
        {
            VortexTypeKind.Primitive => storage.Kind == VortexTypeKind.Primitive && storage.PrimitiveType == record.PrimitiveType,
            VortexTypeKind.Decimal => file.Kind == VortexTypeKind.Decimal,
            VortexTypeKind.Extension => file.Kind == VortexTypeKind.Extension && file.ExtensionId == record.ExtensionId,
            VortexTypeKind.Binary => file.Kind is VortexTypeKind.Binary or VortexTypeKind.Utf8,
            VortexTypeKind.List or VortexTypeKind.FixedSizeList =>
                file.Kind is VortexTypeKind.List or VortexTypeKind.FixedSizeList && Compatible(record.ElementType!, file.ElementType!),
            VortexTypeKind.Struct => storage.Kind == VortexTypeKind.Struct,
            _ => record.Kind == file.Kind,
        };
    }

    private static VortexType Unwrap(VortexType type)
    {
        while (type.Kind == VortexTypeKind.Extension && type.StorageType is { } storage && storage.Kind == VortexTypeKind.Struct)
        {
            type = storage;
        }

        return type;
    }

    private static IEnumerable<string> Names(VortexField[] fields)
    {
        foreach (VortexField field in fields)
        {
            yield return "'" + field.Name + "'";
        }
    }

    private static class Cache<TRecord>
    {
        internal static readonly ConditionalWeakTable<VortexSchema, RecordBinding> Bindings = [];
    }
}
