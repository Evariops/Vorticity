using System;
using System.Collections.Generic;
using Vorticity.Parquet.Thrift;

namespace Vorticity.Parquet.Metadata;

/// <summary>
/// A column chunk's <c>GeospatialStatistics</c>, field 17 of its metadata: the bounding box of a
/// GEOMETRY or GEOGRAPHY column's values and the WKB codes of their types.
/// </summary>
/// <remarks>
/// X may wrap: an <c>xmin</c> past <c>xmax</c> bounds the values at or past <c>xmin</c> and those at
/// or before <c>xmax</c>, a box that crosses the antimeridian. Z and M are there only where some
/// value has them.
/// </remarks>
internal sealed class GeospatialStatistics
{
    /// <summary>Whether a bounding box is given; X and Y then are.</summary>
    internal bool HasBox { get; init; }

    internal double XMin { get; init; }

    internal double XMax { get; init; }

    internal double YMin { get; init; }

    internal double YMax { get; init; }

    internal bool HasZ { get; init; }

    internal double ZMin { get; init; }

    internal double ZMax { get; init; }

    internal bool HasM { get; init; }

    internal double MMin { get; init; }

    internal double MMax { get; init; }

    /// <summary>The WKB codes of the values' types, unique; null when the list is not given, empty when they are not known.</summary>
    internal int[]? Types { get; init; }

    /// <summary>Reads the struct <paramref name="bytes"/> holds, from its first field header.</summary>
    /// <exception cref="ParquetFormatException">The box lacks one of its four required bounds.</exception>
    internal static GeospatialStatistics Read(ReadOnlySpan<byte> bytes)
    {
        ThriftCompactReader reader = new(bytes);
        bool box = false;
        double[] bounds = [double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN];
        int found = 0;
        int[]? types = null;
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            switch (id)
            {
                case 1 when type == ThriftType.Struct:
                    box = true;
                    short inner = reader.EnterStruct();
                    while (reader.ReadFieldHeader(out ThriftType boundType, out short bound))
                    {
                        if (bound is >= 1 and <= 8 && boundType == ThriftType.Double)
                        {
                            bounds[bound - 1] = reader.ReadDouble();
                            found |= 1 << (bound - 1);
                        }
                        else
                        {
                            reader.Skip(boundType);
                        }
                    }

                    reader.ExitStruct(inner);
                    break;

                case 2 when type == ThriftType.List:
                    int count = reader.ReadListHeader(out ThriftType element);
                    ThriftCompactReader.Expect(element, ThriftType.I32);
                    List<int> codes = [];
                    for (int i = 0; i < count; i++)
                    {
                        codes.Add(reader.ReadI32());
                    }

                    types = [.. codes];
                    break;

                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct(saved);
        if (box && (found & 0x0F) != 0x0F)
        {
            ParquetThrow.Format("A bounding box lacks one of xmin, xmax, ymin and ymax.");
        }

        return new GeospatialStatistics
        {
            HasBox = box,
            XMin = bounds[0],
            XMax = bounds[1],
            YMin = bounds[2],
            YMax = bounds[3],
            HasZ = (found & 0x30) == 0x30,
            ZMin = bounds[4],
            ZMax = bounds[5],
            HasM = (found & 0xC0) == 0xC0,
            MMin = bounds[6],
            MMax = bounds[7],
            Types = types,
        };
    }

    /// <summary>Writes the struct as field <paramref name="id"/> of the struct being written.</summary>
    internal void Write(ref ThriftCompactWriter writer, short id)
    {
        short saved = writer.BeginStructField(id);
        if (HasBox)
        {
            short box = writer.BeginStructField(1);
            writer.WriteDoubleField(1, XMin);
            writer.WriteDoubleField(2, XMax);
            writer.WriteDoubleField(3, YMin);
            writer.WriteDoubleField(4, YMax);
            if (HasZ)
            {
                writer.WriteDoubleField(5, ZMin);
                writer.WriteDoubleField(6, ZMax);
            }

            if (HasM)
            {
                writer.WriteDoubleField(7, MMin);
                writer.WriteDoubleField(8, MMax);
            }

            writer.EndStruct(box);
        }

        if (Types is { } types)
        {
            writer.WriteListField(2, ThriftType.I32, types.Length);
            foreach (int code in types)
            {
                writer.WriteI32Element(code);
            }
        }

        writer.EndStruct(saved);
    }

    /// <summary>Whether the box bounds the X <paramref name="x"/>, its wrap across the antimeridian taken.</summary>
    internal bool BoundsX(double x) => XMin <= XMax ? x >= XMin && x <= XMax : x >= XMin || x <= XMax;
}
