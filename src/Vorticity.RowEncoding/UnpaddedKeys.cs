using System;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.RowEncoding;

/// <summary>
/// An ascending row-encoded key with its texts and binaries unpadded, as a sparse index of a dataset's objects may code its
/// keys: a non-empty value's leading byte, then its bytes, a zero written <c>01 01</c> and a one <c>01 02</c>, then a zero
/// that ends it; an empty value, a null and a value of a fixed width as the row encoding writes them. The row encoding
/// pads a text to blocks of 32 bytes, each followed by a byte that says whether another follows, so that a key compares
/// as it is read; an index's keys are compared whole, and this code compares as the row encoding does in fewer bytes.
/// </summary>
/// <remarks>
/// <para>
/// The order holds: a value's bytes map in order (<c>00 &lt; 01 &lt; 02…</c> to <c>01 01 &lt; 01 02 &lt; 02…</c>), and the
/// zero that ends a value lies below any byte of a longer one. The code is free of prefixes, the zero appearing at a value's
/// end alone: a key of fewer values is a prefix of a longer key's bytes exactly when the row encoding's is, so bounds made of
/// whole values compare as before. A text in UTF-8 holds neither byte escaped and takes its bytes and two more.
/// </para>
/// <para>
/// A key is described by the encoded width of each of its values (<see cref="RowEncoder.EncodedWidth"/>): a fixed one, or
/// <see cref="RowEncoder.VariableWidth"/>. Values past the widths given are copied as they are; a width the encoding does
/// not tell, a struct's or a list's holding a text, codes nothing. Only ascending keys are coded, nulls first or last.
/// </para>
/// </remarks>
internal static class UnpaddedKeys
{
    /// <summary>The byte that ends a non-empty value.</summary>
    private const byte End = 0x00;

    /// <summary>The byte that escapes a zero (<c>01 01</c>) or a one (<c>01 02</c>) of a value.</summary>
    private const byte Escape = 0x01;

    /// <summary>The leading byte of an ascending non-empty text or binary.</summary>
    private const byte NonEmpty = 0x02;

    /// <summary>The marker of a block of the row encoding that another follows.</summary>
    private const byte Continuation = 0xFF;

    /// <summary>Whether keys of values of <paramref name="widths"/> can be coded: every width told.</summary>
    internal static bool Codes(ReadOnlySpan<int> widths)
    {
        foreach (int width in widths)
        {
            if (width < 0 && width != RowEncoder.VariableWidth)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The bytes <see cref="Narrow(ReadOnlySpan{byte}, ReadOnlySpan{int}, Span{byte})"/> writes of <paramref name="key"/>.</summary>
    /// <exception cref="ArgumentException">The key ends within a value, or holds one this code does not take.</exception>
    internal static int NarrowedLength(ReadOnlySpan<byte> key, ReadOnlySpan<int> widths)
    {
        int length = 0;
        int at = 0;
        for (int i = 0; at < key.Length; i++)
        {
            if (i >= widths.Length)
            {
                return length + key.Length - at;
            }

            int width = widths[i];
            if (width != RowEncoder.VariableWidth || key[at] != NonEmpty)
            {
                int whole = Unescaped(key, at, width);
                length += whole;
                at += whole;
                continue;
            }

            // The sentinel, the value's bytes each escaped or not, the end.
            length += 2;
            for (at++; ; at += RowWidths.VarBlockTotal)
            {
                int bytes = Block(key, at);
                ReadOnlySpan<byte> data = key.Slice(at, bytes);
                length += bytes + data.Count(End) + data.Count(Escape);
                if (key[at + RowWidths.VarBlockData] != Continuation)
                {
                    at += RowWidths.VarBlockTotal;
                    break;
                }
            }
        }

        return length;
    }

    /// <summary>
    /// Writes <paramref name="key"/>, a row-encoded key of values of <paramref name="widths"/>, unpadded into
    /// <paramref name="into"/>, which holds <see cref="NarrowedLength"/> bytes; the bytes written.
    /// </summary>
    /// <exception cref="ArgumentException">The key ends within a value, or holds one this code does not take.</exception>
    internal static int Narrow(ReadOnlySpan<byte> key, ReadOnlySpan<int> widths, Span<byte> into)
    {
        int written = 0;
        int at = 0;
        for (int i = 0; at < key.Length; i++)
        {
            if (i >= widths.Length)
            {
                key[at..].CopyTo(into[written..]);
                return written + key.Length - at;
            }

            int width = widths[i];
            if (width != RowEncoder.VariableWidth || key[at] != NonEmpty)
            {
                int whole = Unescaped(key, at, width);
                key.Slice(at, whole).CopyTo(into[written..]);
                written += whole;
                at += whole;
                continue;
            }

            into[written++] = NonEmpty;
            for (at++; ; at += RowWidths.VarBlockTotal)
            {
                written += Escaped(key.Slice(at, Block(key, at)), into[written..]);
                if (key[at + RowWidths.VarBlockData] != Continuation)
                {
                    at += RowWidths.VarBlockTotal;
                    break;
                }
            }

            into[written++] = End;
        }

        return written;
    }

    /// <summary><paramref name="key"/> unpadded, in an array of its own: for a bound or a writer's keys, made once.</summary>
    /// <exception cref="ArgumentException">The key ends within a value, or holds one this code does not take.</exception>
    internal static byte[] Narrow(ReadOnlySpan<byte> key, ReadOnlySpan<int> widths)
    {
        byte[] narrowed = new byte[NarrowedLength(key, widths)];
        Narrow(key, widths, narrowed);
        return narrowed;
    }

    /// <summary>The bytes <see cref="Widen(ReadOnlySpan{byte}, ReadOnlySpan{int}, Span{byte})"/> writes of <paramref name="key"/>.</summary>
    /// <exception cref="VortexFormatException">The key ends within a value, or a value's escape is none of this code's.</exception>
    internal static int WidenedLength(ReadOnlySpan<byte> key, ReadOnlySpan<int> widths)
    {
        int length = 0;
        int at = 0;
        for (int i = 0; at < key.Length; i++)
        {
            if (i >= widths.Length)
            {
                return length + key.Length - at;
            }

            int width = widths[i];
            if (width != RowEncoder.VariableWidth || key[at] != NonEmpty)
            {
                int whole = Escaped(key, at, width);
                length += whole;
                at += whole;
                continue;
            }

            int bytes = 0;
            at = Unescape(key, at + 1, default, ref bytes);
            length += 1 + (((bytes + RowWidths.VarBlockData - 1) / RowWidths.VarBlockData) * RowWidths.VarBlockTotal);
        }

        return length;
    }

    /// <summary>
    /// Writes <paramref name="key"/>, a key of values of <paramref name="widths"/> unpadded, as the row encoding has it into
    /// <paramref name="into"/>, which holds <see cref="WidenedLength"/> bytes; the bytes written.
    /// </summary>
    /// <exception cref="VortexFormatException">The key ends within a value, or a value's escape is none of this code's.</exception>
    internal static int Widen(ReadOnlySpan<byte> key, ReadOnlySpan<int> widths, Span<byte> into)
    {
        int written = 0;
        int at = 0;
        for (int i = 0; at < key.Length; i++)
        {
            if (i >= widths.Length)
            {
                key[at..].CopyTo(into[written..]);
                return written + key.Length - at;
            }

            int width = widths[i];
            if (width != RowEncoder.VariableWidth || key[at] != NonEmpty)
            {
                int whole = Escaped(key, at, width);
                key.Slice(at, whole).CopyTo(into[written..]);
                written += whole;
                at += whole;
                continue;
            }

            // The bytes first, then cut in blocks from the last back, each moved to its place and its marker written.
            into[written++] = NonEmpty;
            int bytes = 0;
            at = Unescape(key, at + 1, into[written..], ref bytes);
            int blocks = (bytes + RowWidths.VarBlockData - 1) / RowWidths.VarBlockData;
            for (int block = blocks - 1; block >= 0; block--)
            {
                int start = block * RowWidths.VarBlockData;
                int data = Math.Min(RowWidths.VarBlockData, bytes - start);
                Span<byte> placed = into.Slice(written + (block * RowWidths.VarBlockTotal), RowWidths.VarBlockTotal);
                into.Slice(written + start, data).CopyTo(placed);
                placed[data..RowWidths.VarBlockData].Clear();
                placed[RowWidths.VarBlockData] = block == blocks - 1 ? (byte)data : Continuation;
            }

            written += blocks * RowWidths.VarBlockTotal;
        }

        return written;
    }

    /// <summary><paramref name="key"/> as the row encoding has it, in an array of its own.</summary>
    /// <exception cref="VortexFormatException">The key ends within a value, or a value's escape is none of this code's.</exception>
    internal static byte[] Widen(ReadOnlySpan<byte> key, ReadOnlySpan<int> widths)
    {
        byte[] widened = new byte[WidenedLength(key, widths)];
        Widen(key, widths, widened);
        return widened;
    }

    /// <summary>
    /// The bytes the first value of <paramref name="key"/> takes unpadded, a value of the encoded width
    /// <paramref name="width"/>; -1 when the key ends first, or the width is not told.
    /// </summary>
    internal static int ValueLength(ReadOnlySpan<byte> key, int width)
    {
        if (width != RowEncoder.VariableWidth)
        {
            return width;
        }

        if (key.IsEmpty || key[0] != NonEmpty)
        {
            return key.IsEmpty ? -1 : 1;
        }

        int end = key[1..].IndexOf(End);
        return end < 0 ? -1 : end + 2;
    }

    /// <summary>
    /// The first value of <paramref name="key"/>, unpadded, a value of <paramref name="dtype"/>, as
    /// <see cref="RowEncoder.ReadFirstValue"/> reads it of the row encoding.
    /// </summary>
    /// <exception cref="VortexFormatException">The value ends past the key, or its escape is none of this code's.</exception>
    internal static FilterLiteral ReadFirstValue(ReadOnlySpan<byte> key, DType dtype)
    {
        DType storage = dtype.Kind == DTypeKind.Extension ? dtype.StorageType : dtype;
        if (storage.Kind is not (DTypeKind.Utf8 or DTypeKind.Binary) || key[0] != NonEmpty)
        {
            return RowEncoder.ReadFirstValue(key, dtype);
        }

        int bytes = 0;
        Unescape(key, 1, default, ref bytes);
        byte[] value = new byte[bytes];
        bytes = 0;
        Unescape(key, 1, value, ref bytes);
        return FilterLiteral.From(value);
    }

    /// <summary>
    /// The bytes the data of the block of the row encoding at <paramref name="at"/> holds: 32 when another follows, else its
    /// marker, 1 to 32.
    /// </summary>
    private static int Block(ReadOnlySpan<byte> key, int at)
    {
        if (at + RowWidths.VarBlockData >= key.Length)
        {
            throw new ArgumentException("The key ends within a text's blocks.", nameof(key));
        }

        byte marker = key[at + RowWidths.VarBlockData];
        if (marker != Continuation && marker is 0 or > RowWidths.VarBlockData)
        {
            throw new ArgumentException($"A text's last block is marked {marker}, past the 1 to 32 bytes a block holds.", nameof(key));
        }

        return marker == Continuation ? RowWidths.VarBlockData : marker;
    }

    /// <summary>The bytes a value written alike in both codes takes at <paramref name="at"/> of a row-encoded key: a fixed width's, or a text's leading byte alone.</summary>
    private static int Unescaped(ReadOnlySpan<byte> key, int at, int width)
    {
        if (width == RowEncoder.VariableWidth)
        {
            // An empty value or a null; a descending text's leading bytes (0xFD, 0xFE) are no code's.
            return key[at] is 0x00 or 0x01 or 0xFF
                ? 1
                : throw new ArgumentException($"A text's leading byte 0x{key[at]:X2} is no ascending key's.", nameof(key));
        }

        if (width < 0)
        {
            throw new ArgumentException("A value whose width the row encoding does not tell is not coded.", nameof(key));
        }

        return at + width <= key.Length ? width : throw new ArgumentException("The key ends within a value.", nameof(key));
    }

    /// <summary>The bytes a value written alike in both codes takes at <paramref name="at"/> of an unpadded key.</summary>
    private static int Escaped(ReadOnlySpan<byte> key, int at, int width)
    {
        if (width == RowEncoder.VariableWidth)
        {
            return 1;
        }

        return width >= 0 && at + width <= key.Length
            ? width
            : throw new VortexFormatException("An unpadded key ends within a value, or holds one whose width is not told.");
    }

    /// <summary>Writes <paramref name="data"/> into <paramref name="into"/>, each zero and one escaped; the bytes written.</summary>
    private static int Escaped(ReadOnlySpan<byte> data, Span<byte> into)
    {
        int written = 0;
        while (!data.IsEmpty)
        {
            int plain = data.IndexOfAny(End, Escape);
            if (plain < 0)
            {
                data.CopyTo(into[written..]);
                return written + data.Length;
            }

            data[..plain].CopyTo(into[written..]);
            written += plain;
            into[written++] = Escape;
            into[written++] = (byte)(data[plain] + 1);
            data = data[(plain + 1)..];
        }

        return written;
    }

    /// <summary>
    /// Reads the bytes of an unpadded value from <paramref name="at"/>, past its leading byte, to its end, each escape
    /// undone, into <paramref name="into"/> from <paramref name="written"/> when it is not empty, counting them; where the
    /// key goes on past the value's end.
    /// </summary>
    private static int Unescape(ReadOnlySpan<byte> key, int at, Span<byte> into, ref int written)
    {
        bool copy = !into.IsEmpty;
        while (true)
        {
            int plain = at < key.Length ? key[at..].IndexOfAny(End, Escape) : -1;
            if (plain < 0)
            {
                throw new VortexFormatException("An unpadded value runs past its key.");
            }

            if (copy)
            {
                key.Slice(at, plain).CopyTo(into[written..]);
            }

            written += plain;
            at += plain;
            if (key[at] == End)
            {
                if (written == 0)
                {
                    throw new VortexFormatException("An unpadded value marked non-empty holds no byte.");
                }

                return at + 1;
            }

            if (at + 1 >= key.Length || key[at + 1] is not (0x01 or 0x02))
            {
                throw new VortexFormatException("An unpadded value escapes a byte its code does not escape.");
            }

            if (copy)
            {
                into[written] = (byte)(key[at + 1] - 1);
            }

            written++;
            at += 2;
        }
    }
}
