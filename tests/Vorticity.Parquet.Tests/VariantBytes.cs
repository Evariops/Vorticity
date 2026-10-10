using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// Variant metadata and values encoded by the standard's rules, for a test to build a file from and
/// to expect: every count, id and offset in the fewest bytes that hold it, and an object's values in
/// the order its fields are given, which is their names'.
/// </summary>
internal static class VariantBytes
{
    /// <summary>The variant null.</summary>
    internal static byte[] Null => [0x00];

    /// <summary>A metadata of <paramref name="names"/>, in that order, declared sorted or not.</summary>
    internal static byte[] Metadata(bool sorted, params string[] names)
    {
        byte[][] strings = names.Select(Encoding.UTF8.GetBytes).ToArray();
        int width = Width((uint)System.Math.Max(strings.Sum(s => s.Length), names.Length));
        List<byte> bytes = [(byte)(0x01 | (sorted ? 0x10 : 0) | ((width - 1) << 6))];
        Put(bytes, (uint)names.Length, width);
        uint offset = 0;
        Put(bytes, offset, width);
        foreach (byte[] text in strings)
        {
            offset += (uint)text.Length;
            Put(bytes, offset, width);
        }

        foreach (byte[] text in strings)
        {
            bytes.AddRange(text);
        }

        return bytes.ToArray();
    }

    /// <summary>An int32.</summary>
    internal static byte[] Int(int value)
    {
        byte[] bytes = new byte[5];
        bytes[0] = 5 << 2;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), value);
        return bytes;
    }

    /// <summary>A string: short up to 63 bytes, the string primitive past that.</summary>
    internal static byte[] Text(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        if (utf8.Length <= 63)
        {
            return [(byte)((utf8.Length << 2) | 1), .. utf8];
        }

        byte[] bytes = new byte[5 + utf8.Length];
        bytes[0] = 16 << 2;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), utf8.Length);
        utf8.CopyTo(bytes, 5);
        return bytes;
    }

    /// <summary>An object of <paramref name="fields"/>, given in their names' order.</summary>
    internal static byte[] Object(params (int Id, byte[] Value)[] fields)
    {
        int size = fields.Sum(f => f.Value.Length);
        int idWidth = Width((uint)fields.Select(f => f.Id).DefaultIfEmpty(0).Max());
        int offsetWidth = Width((uint)size);
        bool large = fields.Length > 255;
        List<byte> bytes = [(byte)(((((large ? 1 : 0) << 4) | ((idWidth - 1) << 2) | (offsetWidth - 1)) << 2) | 2)];
        Put(bytes, (uint)fields.Length, large ? 4 : 1);
        foreach ((int id, _) in fields)
        {
            Put(bytes, (uint)id, idWidth);
        }

        Offsets(bytes, fields.Select(f => f.Value), offsetWidth);
        foreach ((_, byte[] value) in fields)
        {
            bytes.AddRange(value);
        }

        return bytes.ToArray();
    }

    /// <summary>An array of <paramref name="elements"/>.</summary>
    internal static byte[] Array(params byte[][] elements)
    {
        int size = elements.Sum(e => e.Length);
        int offsetWidth = Width((uint)size);
        bool large = elements.Length > 255;
        List<byte> bytes = [(byte)(((((large ? 1 : 0) << 2) | (offsetWidth - 1)) << 2) | 3)];
        Put(bytes, (uint)elements.Length, large ? 4 : 1);
        Offsets(bytes, elements, offsetWidth);
        foreach (byte[] element in elements)
        {
            bytes.AddRange(element);
        }

        return bytes.ToArray();
    }

    private static void Offsets(List<byte> bytes, IEnumerable<byte[]> values, int width)
    {
        uint offset = 0;
        foreach (byte[] value in values)
        {
            Put(bytes, offset, width);
            offset += (uint)value.Length;
        }

        Put(bytes, offset, width);
    }

    private static int Width(uint value) => value <= 0xFF ? 1 : value <= 0xFFFF ? 2 : value <= 0xFF_FFFF ? 3 : 4;

    private static void Put(List<byte> bytes, uint value, int width)
    {
        for (int i = 0; i < width; i++)
        {
            bytes.Add((byte)(value >> (8 * i)));
        }
    }
}
