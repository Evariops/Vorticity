using System;
using System.Buffers;
using System.Globalization;
using System.Text.Unicode;

namespace Vorticity.Types;

/// <summary>
/// Renders a <see cref="DType"/> as text. This is what a dump tool prints and what a failing
/// assertion shows, so two properties matter beyond correctness: the rendering never consults the
/// current culture, and it stays bounded. A dtype may legally nest 64 deep with a struct at every
/// level, which a plain recursive renderer would turn into an output exponential in size for a
/// file that is otherwise perfectly valid; the two constants below are the bounds that prevent it.
/// </summary>
public static class DTypeFormatter
{
    /// <summary>
    /// A Struct or Union with more fields than this renders as <c>struct{...}</c> /
    /// <c>union{...}</c>. A 400-column schema pasted into a test failure helps nobody, and the
    /// full shape is still reachable through the accessors.
    /// </summary>
    public const int MaxRenderedFields = 32;

    /// <summary>
    /// Hard ceiling on the rendered length. Reached only by pathological nesting; the output is
    /// then truncated with a trailing <c>...</c> rather than growing exponentially.
    /// </summary>
    public const int MaxRenderedLength = 2048;

    /// <summary>Renders <paramref name="dtype"/>. Allocates exactly one string.</summary>
    public static string Format(DType dtype)
    {
        Span<char> stack = stackalloc char[256];
        ValueStringWriter writer = new(stack, MaxRenderedLength);
        try
        {
            Append(ref writer, dtype);
            return writer.Build();
        }
        finally
        {
            writer.Dispose();
        }
    }

    internal static void Append(ref ValueStringWriter w, DType d)
    {
        if (w.IsFull)
        {
            return;
        }

        if (d.IsDefault)
        {
            w.Append("<default>");
            return;
        }

        switch (d.Kind)
        {
            case DTypeKind.Null:
                // `table Null {}` has no nullable field, so there is no "?" to print.
                w.Append("null");
                return;

            case DTypeKind.Extension:
                // Extension has no nullability of its own; the storage dtype already shows it.
                w.Append("ext(");
                w.AppendUtf8(d.ExtensionIdUtf8);
                w.Append(", ");
                Append(ref w, d.StorageType);
                w.Append(')');
                return;

            case DTypeKind.Bool:
                w.Append("bool");
                break;

            case DTypeKind.Primitive:
                w.Append(d.PType.Name());
                break;

            case DTypeKind.Decimal:
                w.Append("decimal(");
                w.AppendInt64(d.Precision);
                w.Append(',');
                w.AppendInt64(d.Scale);
                w.Append(')');
                break;

            case DTypeKind.Utf8:
                w.Append("utf8");
                break;

            case DTypeKind.Binary:
                w.Append("binary");
                break;

            case DTypeKind.Variant:
                w.Append("variant");
                break;

            case DTypeKind.Struct:
                AppendFields(ref w, d, "struct{", union: false);
                break;

            case DTypeKind.Union:
                AppendFields(ref w, d, "union{", union: true);
                break;

            case DTypeKind.List:
                w.Append("list(");
                Append(ref w, d.ElementType);
                w.Append(')');
                break;

            case DTypeKind.FixedSizeList:
                w.Append("fsl(");
                Append(ref w, d.ElementType);
                w.Append(", ");
                w.AppendUInt64(d.FixedSize);
                w.Append(')');
                break;

            case DTypeKind.Map:
                w.Append("map(");
                Append(ref w, d.KeyType);
                w.Append(", ");
                Append(ref w, d.ValueType);
                w.Append(')');
                break;

            default:
                w.Append("unknown(");
                w.AppendInt64((byte)d.Kind);
                w.Append(')');
                break;
        }

        if (d.IsNullable)
        {
            w.Append('?');
        }
    }

    private static void AppendFields(ref ValueStringWriter w, DType d, string opening, bool union)
    {
        w.Append(opening);
        int count = d.FieldCount;
        if (count > MaxRenderedFields)
        {
            w.Append("...");
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                if (i != 0)
                {
                    w.Append(", ");
                }

                w.AppendUtf8(d.GetFieldNameUtf8(i));
                w.Append(": ");
                Append(ref w, d.GetField(i));
                if (union)
                {
                    w.Append(" = ");
                    w.AppendInt64(d.GetTypeId(i));
                }

                if (w.IsFull)
                {
                    // Stop descending: the writer refuses further characters, and continuing
                    // would walk an exponentially large tree to produce nothing.
                    break;
                }
            }
        }

        w.Append('}');
    }
}

/// <summary>
/// A growable char writer with a hard length budget. Starts on a caller-supplied stack buffer and
/// escalates to <see cref="ArrayPool{T}"/>; nothing but the final <see cref="Build"/> allocates.
/// </summary>
internal ref struct ValueStringWriter
{
    private Span<char> _buffer;
    private char[]? _rented;
    private int _length;
    private readonly int _budget;
    private bool _truncated;

    internal ValueStringWriter(Span<char> initial, int budget)
    {
        _buffer = initial;
        _rented = null;
        _length = 0;
        _budget = budget;
        _truncated = false;
    }

    /// <summary>True once the budget is exhausted; callers stop descending.</summary>
    internal readonly bool IsFull => _length >= _budget;

    internal readonly int Length => _length;

    internal void Append(char c)
    {
        if (_length >= _budget)
        {
            _truncated = true;
            return;
        }

        Grow(1);
        _buffer[_length++] = c;
    }

    internal void Append(scoped ReadOnlySpan<char> text)
    {
        int n = text.Length;
        if (n == 0)
        {
            return;
        }

        if (_length + n > _budget)
        {
            n = _budget - _length;
            _truncated = true;
            if (n <= 0)
            {
                return;
            }
        }

        Grow(n);
        text[..n].CopyTo(_buffer[_length..]);
        _length += n;
    }

    /// <summary>
    /// Appends UTF-8 bytes, up to the remaining budget. Invalid sequences decode to U+FFFD rather
    /// than throwing: the bytes come from a file and a malformed name must not turn diagnostics
    /// into a second failure.
    /// </summary>
    /// <remarks>
    /// The bytes are file-supplied and unbounded — nothing caps the length of a struct field name,
    /// an extension id or a string scalar — so the work and the buffer are both sized by the
    /// budget, never by the input. Decoding the whole span first and clamping afterwards would
    /// rent (and decode) megabytes to produce at most <see cref="DTypeFormatter.MaxRenderedLength"/>
    /// characters: no allocation here may be sized directly by a file-supplied value without a cap.
    /// </remarks>
    internal void AppendUtf8(scoped ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty || IsFull)
        {
            return;
        }

        int room = _budget - _length;

        // A UTF-8 sequence never decodes to more UTF-16 units than it has bytes, so `room` bytes
        // is a safe upper bound on the input the budget can still accept, and capping the rental
        // at it cannot lose a character that would have been rendered. The truncation decision
        // stays on decoded units - Utf8.ToUtf16 stops at a whole scalar - so a 3000-byte / 1000-char
        // name still renders whole, and a surrogate pair is never split at the boundary.
        int reserve = Math.Min(room, utf8.Length);
        Grow(reserve);

        Utf8.ToUtf16(
            utf8,
            _buffer.Slice(_length, Math.Min(room, _buffer.Length - _length)),
            out int read,
            out int written,
            replaceInvalidSequences: true,
            isFinalBlock: true);

        _length += written;
        if (read < utf8.Length)
        {
            _truncated = true;
        }
    }

    internal void AppendInt64(long value)
    {
        Span<char> tmp = stackalloc char[24];
        if (value.TryFormat(tmp, out int written, default, CultureInfo.InvariantCulture))
        {
            Append(tmp[..written]);
        }
    }

    internal void AppendUInt64(ulong value)
    {
        Span<char> tmp = stackalloc char[24];
        if (value.TryFormat(tmp, out int written, default, CultureInfo.InvariantCulture))
        {
            Append(tmp[..written]);
        }
    }

    internal void AppendDouble(double value)
    {
        Span<char> tmp = stackalloc char[32];
        if (value.TryFormat(tmp, out int written, default, CultureInfo.InvariantCulture))
        {
            Append(tmp[..written]);
        }
    }

    internal void AppendSingle(float value)
    {
        Span<char> tmp = stackalloc char[32];
        if (value.TryFormat(tmp, out int written, default, CultureInfo.InvariantCulture))
        {
            Append(tmp[..written]);
        }
    }

    internal void AppendHalf(Half value)
    {
        Span<char> tmp = stackalloc char[32];
        if (value.TryFormat(tmp, out int written, default, CultureInfo.InvariantCulture))
        {
            Append(tmp[..written]);
        }
    }

    internal readonly string Build()
    {
        if (_truncated)
        {
            // The caller asked for a bounded rendering; say so rather than pretending it is whole.
            return string.Concat(_buffer[.._length], "...");
        }

        return new string(_buffer[.._length]);
    }

    internal void Dispose()
    {
        char[]? rented = _rented;
        _rented = null;
        _buffer = default;
        if (rented is not null)
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private void Grow(int extra)
    {
        if (_length + extra <= _buffer.Length)
        {
            return;
        }

        int size = Math.Max(_buffer.Length * 2, _length + extra);
        char[] next = ArrayPool<char>.Shared.Rent(size);
        _buffer[.._length].CopyTo(next);
        char[]? old = _rented;
        _rented = next;
        _buffer = next;
        if (old is not null)
        {
            ArrayPool<char>.Shared.Return(old);
        }
    }
}
