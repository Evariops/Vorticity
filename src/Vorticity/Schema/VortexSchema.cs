using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
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
        Span<byte> buffer = path.Length <= 128 ? stackalloc byte[512] : new byte[Encoding.UTF8.GetMaxByteCount(path.Length)];
        int written = Encoding.UTF8.GetBytes(path, buffer);
        return TryGetField(buffer[..written], out int index) ? index : -1;
    }

    /// <summary>The UTF-8 form of <see cref="IndexOf"/>, without allocating.</summary>
    /// <param name="pathUtf8">The name or the <c>.</c>-separated path, as UTF-8.</param>
    /// <param name="index">The field's index within its own struct.</param>
    /// <returns>Whether a field matches.</returns>
    public bool TryGetField(ReadOnlySpan<byte> pathUtf8, out int index)
    {
        index = IndexOfName(_fields, pathUtf8);
        if (index >= 0)
        {
            return true;
        }

        ReadOnlySpan<VortexField> fields = _fields;
        ReadOnlySpan<byte> rest = pathUtf8;
        while (true)
        {
            int dot = rest.IndexOf((byte)'.');
            ReadOnlySpan<byte> segment = dot < 0 ? rest : rest[..dot];
            int found = IndexOfName(fields, segment);
            if (found < 0)
            {
                index = -1;
                return false;
            }

            if (dot < 0)
            {
                index = found;
                return true;
            }

            VortexType type = fields[found].Type;
            while (type.Kind == VortexTypeKind.Extension)
            {
                type = type.StorageType!;
            }

            if (type.Kind != VortexTypeKind.Struct)
            {
                index = -1;
                return false;
            }

            fields = type.Fields;
            rest = rest[(dot + 1)..];
        }
    }

    /// <summary>The field a <c>.</c>-separated path names, or null.</summary>
    internal VortexField? Resolve(string path)
    {
        int top = IndexOfName(_fields, Encoding.UTF8.GetBytes(path));
        if (top >= 0)
        {
            return _fields[top];
        }

        ReadOnlySpan<VortexField> fields = _fields;
        VortexField? current = null;
        foreach (string segment in path.Split('.'))
        {
            if (current is { } parent)
            {
                VortexType type = parent.Type;
                while (type.Kind == VortexTypeKind.Extension)
                {
                    type = type.StorageType!;
                }

                if (type.Kind != VortexTypeKind.Struct)
                {
                    return null;
                }

                fields = type.Fields;
            }

            int found = IndexOfName(fields, Encoding.UTF8.GetBytes(segment));
            if (found < 0)
            {
                return null;
            }

            current = fields[found];
        }

        return current;
    }

    private static int IndexOfName(ReadOnlySpan<VortexField> fields, ReadOnlySpan<byte> nameUtf8)
    {
        Span<char> chars = nameUtf8.Length <= 256 ? stackalloc char[256] : new char[nameUtf8.Length];
        if (Utf8.ToUtf16(nameUtf8, chars, out _, out int written) != System.Buffers.OperationStatus.Done)
        {
            return -1;
        }

        ReadOnlySpan<char> name = chars[..written];
        for (int i = 0; i < fields.Length; i++)
        {
            if (name.SequenceEqual(fields[i].Name))
            {
                return i;
            }
        }

        return -1;
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
