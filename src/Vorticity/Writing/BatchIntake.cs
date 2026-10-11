using System;
using System.Buffers;
using System.Numerics;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// What a writer checks and reshapes in the batches it is given, whatever format it writes: that
/// their columns are of the file's types, a record's members in the file's order, and the rows a
/// selection keeps.
/// </summary>
internal static class BatchIntake
{
    /// <summary>Per column of a file of <paramref name="schema"/>, the member of <typeparamref name="TRecord"/> that holds it.</summary>
    /// <exception cref="VortexSchemaException">A column of the file has no member.</exception>
    internal static int[] Members<TRecord>(VortexSchema schema)
        where TRecord : IVortexRecord<TRecord>
    {
        VortexSchema record = TRecord.Schema;
        int[]? map = WriteBinding.Map(typeof(TRecord), record, schema.FieldArray);
        int[] members = new int[schema.Count];
        Array.Fill(members, -1);
        for (int member = 0; member < record.Count; member++)
        {
            members[map is null ? member : map[member]] = member;
        }

        int missing = Array.IndexOf(members, -1);
        if (missing >= 0)
        {
            throw new VortexSchemaException(
                $"Column '{schema[missing].Name}' of the file has no member of {typeof(TRecord).Name}; a record written as columns covers every column.");
        }

        return members;
    }

    /// <summary>
    /// A batch typed by <typeparamref name="TRecord"/>, laid out in <paramref name="scratch"/> as a
    /// struct of the file's columns in the file's order, holding the rows its selection keeps.
    /// </summary>
    /// <param name="scratch">Where the struct is laid out, over the batch's buffers.</param>
    /// <param name="columns">The batch.</param>
    /// <param name="members">Per column of the file, its member, from <see cref="Members"/>.</param>
    /// <param name="schema">The file's struct.</param>
    /// <exception cref="VortexSchemaException">A member's column is not of the file's type.</exception>
    internal static int Columns<TRecord>(CanonicalArena scratch, Columns<TRecord> columns, int[] members, DType schema)
        where TRecord : IVortexRecord<TRecord>
    {
        int[] fields = ArrayPool<int>.Shared.Rent(members.Length);
        try
        {
            for (int column = 0; column < members.Length; column++)
            {
                int node = columns.ColumnNode(members[column]);
                DType value = columns.Arena.GetNode(node).DType;

                // The member's name only on a refusal: a batch that fits builds no string.
                if (!Fits(value, schema.GetField(column)))
                {
                    throw Misfit(value, schema.GetField(column), $"Member '{TRecord.Schema[members[column]].Name}' of {typeof(TRecord).Name}");
                }

                fields[column] = scratch.ReferenceFrom(columns.Arena, node);
            }

            int root = scratch.AddStruct(schema, columns.RowCount, Validity.NonNullable, fields.AsSpan(0, members.Length));
            return columns.SelectionWords.IsEmpty ? root : Selected(scratch, root, columns.SelectionWords);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(fields);
        }
    }

    /// <summary>The rows of <paramref name="node"/> a selection keeps, gathered into <paramref name="scratch"/>.</summary>
    internal static int Selected(CanonicalArena scratch, int node, ReadOnlySpan<ulong> words)
    {
        int count = 0;
        foreach (ulong word in words)
        {
            count += BitOperations.PopCount(word);
        }

        int[] indices = ArrayPool<int>.Shared.Rent(Math.Max(count, 1));
        try
        {
            int n = 0;
            for (int w = 0; w < words.Length; w++)
            {
                ulong word = words[w];
                while (word != 0)
                {
                    indices[n++] = (w << 6) + BitOperations.TrailingZeroCount(word);
                    word &= word - 1;
                }
            }

            return CanonicalFilter.Apply(scratch, node, indices.AsSpan(0, n));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(indices);
        }
    }

    /// <summary>Refuses a column that is not of the file's type: a pass-through writes the file's own types, a non-nullable column into a nullable one aside.</summary>
    /// <exception cref="VortexSchemaException">It is not.</exception>
    internal static void RequireFits(DType value, DType column, string what)
    {
        if (!Fits(value, column))
        {
            throw Misfit(value, column, what);
        }
    }

    private static VortexSchemaException Misfit(DType value, DType column, string what) =>
        new($"{what} is {VortexTypes.FromDType(value)}, and the file holds {VortexTypes.FromDType(column)} there.");

    private static bool Fits(DType value, DType column)
    {
        if (value.Kind != column.Kind || (value.IsNullable && !column.IsNullable))
        {
            return false;
        }

        switch (column.Kind)
        {
            case DTypeKind.Primitive:
                return value.PType == column.PType;
            case DTypeKind.Decimal:
                return value.Precision == column.Precision && value.Scale == column.Scale;
            case DTypeKind.List:
                return Fits(value.ElementType, column.ElementType);
            case DTypeKind.FixedSizeList:
                return value.FixedSize == column.FixedSize && Fits(value.ElementType, column.ElementType);
            case DTypeKind.Extension:
                return value.ExtensionIdUtf8.SequenceEqual(column.ExtensionIdUtf8)
                    && value.ExtensionMetadata.SequenceEqual(column.ExtensionMetadata)
                    && Fits(value.StorageType, column.StorageType);
            case DTypeKind.Struct:
            case DTypeKind.Union:
                if (value.FieldCount != column.FieldCount)
                {
                    return false;
                }

                for (int i = 0; i < column.FieldCount; i++)
                {
                    if (!Fits(value.GetField(i), column.GetField(i)))
                    {
                        return false;
                    }
                }

                return true;
            case DTypeKind.Map:
                return Fits(value.KeyType, column.KeyType) && Fits(value.ValueType, column.ValueType);
            default:
                return true;
        }
    }
}
