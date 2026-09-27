using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Unicode;

namespace Vorticity;

/// <summary>The columns of a file: named, typed fields, in order.</summary>
/// <remarks>
/// A schema is a value: two schemas with the same fields are equal. It is written as a collection
/// expression, <c>VortexSchema schema = [("day", VortexType.Int32), ("city", VortexType.Utf8)];</c>.
/// A file whose root is not a struct has a schema of one field with an empty name.
/// </remarks>
[CollectionBuilder(typeof(VortexSchema), nameof(Create))]
public sealed class VortexSchema : IReadOnlyList<VortexField>, IEquatable<VortexSchema>, ISpanFormattable, IUtf8SpanFormattable
{
    private readonly VortexField[] _fields;
    private readonly int _hash;
    private string? _text;
    private VortexType? _root;
    private Dictionary<string, int>? _names;

    private VortexSchema(VortexField[] fields, bool rootIsStruct)
    {
        _fields = fields;
        RootIsStruct = rootIsStruct;
        HashCode hash = default;
        hash.Add(rootIsStruct);
        foreach (VortexField field in fields)
        {
            hash.Add(field.Name);
            hash.Add(field.Type);
        }

        _hash = hash.ToHashCode();
    }

    /// <summary>A schema of <paramref name="fields"/>, in order.</summary>
    /// <param name="fields">The fields; names need not be unique, as the format allows.</param>
    /// <returns>The schema.</returns>
    public static VortexSchema Create(ReadOnlySpan<VortexField> fields)
    {
        VortexField[] copy = fields.ToArray();
        foreach (VortexField field in copy)
        {
            ArgumentNullException.ThrowIfNull(field.Name, nameof(fields));
            ArgumentNullException.ThrowIfNull(field.Type, nameof(fields));
        }

        return new VortexSchema(copy, rootIsStruct: true);
    }

    /// <summary>The schema of a file whose root is a single column of <paramref name="type"/>.</summary>
    internal static VortexSchema Single(VortexType type) => new VortexSchema([new VortexField(string.Empty, type)], rootIsStruct: false);

    /// <summary>Whether the file's root is a struct, which is every schema a caller builds.</summary>
    internal bool RootIsStruct { get; }

    internal VortexField[] FieldArray => _fields;

    /// <summary>The root type: a non-nullable struct of the fields, or the single column's type.</summary>
    internal VortexType Root => _root ??= RootIsStruct ? VortexType.Struct(_fields) : _fields[0].Type;

    /// <summary>Field <paramref name="index"/>.</summary>
    /// <param name="index">0-based, below <see cref="Count"/>.</param>
    public VortexField this[int index] => _fields[index];

    /// <summary>The number of top-level fields.</summary>
    public int Count => _fields.Length;

    /// <summary>
    /// The index of the field <paramref name="path"/> names: a top-level name, or a <c>.</c>-separated
    /// path through structs, whose answer is the leaf's index within its own struct.
    /// </summary>
    /// <param name="path">The name or the path. A top-level name that holds a <c>.</c> matches as a whole first.</param>
    /// <returns>The index, or -1 when no field matches.</returns>
    public int IndexOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Find(path);
    }

    /// <summary>The UTF-8 form of <see cref="IndexOf"/>, without allocating.</summary>
    /// <param name="pathUtf8">The name or the <c>.</c>-separated path, as UTF-8.</param>
    /// <param name="index">The field's index within its own struct.</param>
    /// <returns>Whether a field matches.</returns>
    public bool TryGetField(ReadOnlySpan<byte> pathUtf8, out int index)
    {
        Span<char> chars = pathUtf8.Length <= 256 ? stackalloc char[256] : new char[pathUtf8.Length];
        if (Utf8.ToUtf16(pathUtf8, chars, out _, out int written) != System.Buffers.OperationStatus.Done)
        {
            index = -1;
            return false;
        }

        index = Find(chars[..written]);
        return index >= 0;
    }

    /// <summary>The index of the first top-level field named exactly <paramref name="name"/>, or -1.</summary>
    internal int IndexOfName(ReadOnlySpan<char> name) => FieldNames.IndexOf(_fields, ref _names, name);

    /// <summary>
    /// The index of the field <paramref name="path"/> names within its own struct, or -1: a
    /// top-level name first, then a path.
    /// </summary>
    private int Find(ReadOnlySpan<char> path)
    {
        ReadOnlySpan<VortexField> fields = _fields;
        int index = IndexOfName(path);
        if (index >= 0 || !path.Contains('.'))
        {
            return index;
        }

        VortexType? type = null;
        ReadOnlySpan<char> rest = path;
        while (true)
        {
            int dot = rest.IndexOf('.');
            ReadOnlySpan<char> segment = dot < 0 ? rest : rest[..dot];
            int found = type is null ? IndexOfName(segment) : type.IndexOfField(segment);
            if (found < 0 || dot < 0)
            {
                return found;
            }

            type = fields[found].Type;
            while (type.Kind == VortexTypeKind.Extension)
            {
                type = type.StorageType!;
            }

            if (type.Kind != VortexTypeKind.Struct)
            {
                return -1;
            }

            fields = type.Fields;
            rest = rest[(dot + 1)..];
        }
    }

    /// <inheritdoc/>
    public IEnumerator<VortexField> GetEnumerator() => ((IEnumerable<VortexField>)_fields).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _fields.GetEnumerator();

    /// <inheritdoc/>
    public bool Equals([NotNullWhen(true)] VortexSchema? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null || other._hash != _hash || other.RootIsStruct != RootIsStruct || other._fields.Length != _fields.Length)
        {
            return false;
        }

        for (int i = 0; i < _fields.Length; i++)
        {
            if (_fields[i].Name != other._fields[i].Name || !_fields[i].Type.Equals(other._fields[i].Type))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is VortexSchema other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _hash;

    /// <summary>Value equality.</summary>
    public static bool operator ==(VortexSchema? left, VortexSchema? right) => left is null ? right is null : left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(VortexSchema? left, VortexSchema? right) => !(left == right);

    /// <summary>The schema as a struct type: <c>struct{day: i32, celsius: f64?, city: utf8}</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => _text ??= VortexTypeFormatter.Format(this);

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <inheritdoc/>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        string text = ToString();
        if (text.AsSpan().TryCopyTo(destination))
        {
            charsWritten = text.Length;
            return true;
        }

        charsWritten = 0;
        return false;
    }

    /// <inheritdoc/>
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        Utf8.FromUtf16(ToString(), utf8Destination, out _, out bytesWritten) == System.Buffers.OperationStatus.Done;
}
