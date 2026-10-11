using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Vorticity.Parquet.Metadata;

namespace Vorticity.Parquet.Geospatial;

/// <summary>
/// The bounding box and the type codes of geometries in ISO WKB, gathered a value at a time as the
/// standard's geospatial statistics hold them.
/// </summary>
/// <remarks>
/// <para>
/// A coordinate that is NaN is skipped in its dimension, as the standard says, so that the empty
/// point, which WKB writes as NaN coordinates, bounds nothing; a dimension no value has is left out.
/// A type is a value's own code, its parts' not counted.
/// </para>
/// <para>
/// A value that is not ISO WKB of the seven types the standard's table names, or that holds bytes
/// past its geometry, makes the box and the types unknown, which a writer then leaves out: a box
/// that missed a value would prune a row that matches. The counts of points, rings and parts are
/// checked against the bytes left before any is read, and collections nest at most
/// <see cref="MaxDepth"/> deep.
/// </para>
/// <para>
/// A run of little-endian XY points, the common case, is bounded two coordinates at a time in a
/// vector, x in one lane and y in the other, a NaN kept out by a comparison that it fails.
/// </para>
/// </remarks>
internal sealed class WkbBounds
{
    /// <summary>How deep collections nest.</summary>
    internal const int MaxDepth = 64;

    private double _xMin = double.PositiveInfinity;
    private double _xMax = double.NegativeInfinity;
    private double _yMin = double.PositiveInfinity;
    private double _yMax = double.NegativeInfinity;
    private double _zMin = double.PositiveInfinity;
    private double _zMax = double.NegativeInfinity;
    private double _mMin = double.PositiveInfinity;
    private double _mMax = double.NegativeInfinity;

    /// <summary>One bit per type code: its dimensions' index times seven, plus its kind less one.</summary>
    private uint _types;
    private bool _unknown;
    private bool _edges;

    /// <summary>The open interval of X a wrapping box leaves out, empty when none is checked; and the X found in it.</summary>
    private double _gapLow = double.PositiveInfinity;
    private double _gapHigh = double.NegativeInfinity;
    private long _inGap;

    /// <summary>Whether a value was not ISO WKB this build reads, which leaves the box and the types unknown.</summary>
    internal bool Unknown => _unknown;

    /// <summary>Whether a value has edges: a line or a polygon, whose geodesics a box of vertices does not bound on a sphere.</summary>
    internal bool HasEdges => _edges;

    /// <summary>The X added strictly between the ends <see cref="Gap"/> set.</summary>
    internal long InGap => _inGap;

    /// <summary>Counts every X added from now on that falls strictly between <paramref name="low"/> and <paramref name="high"/>.</summary>
    internal void Gap(double low, double high)
    {
        _gapLow = low;
        _gapHigh = high;
    }

    /// <summary>Adds one value.</summary>
    internal void Add(ReadOnlySpan<byte> wkb)
    {
        if (_unknown)
        {
            return;
        }

        int at = 0;
        if (!Geometry(wkb, ref at, 0) || at != wkb.Length)
        {
            _unknown = true;
        }
    }

    /// <summary>Adds the PLAIN BYTE_ARRAY values of a page: each behind its length.</summary>
    internal void AddPlain(ReadOnlySpan<byte> plain, int values)
    {
        int at = 0;
        for (int i = 0; i < values && !_unknown; i++)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(plain[at..]);
            Add(plain.Slice(at + sizeof(int), length));
            at += sizeof(int) + length;
        }
    }

    /// <summary>
    /// The statistics of the values added, the gathering started over; null when a value left them
    /// unknown. A GEOGRAPHY's box is given only where no value has an edge.
    /// </summary>
    internal GeospatialStatistics? Close(bool geography)
    {
        GeospatialStatistics? statistics = null;
        if (!_unknown)
        {
            List<int> types = [];
            for (int bit = 0; bit < 28; bit++)
            {
                if ((_types & (1u << bit)) != 0)
                {
                    types.Add((1000 * (bit / 7)) + (bit % 7) + 1);
                }
            }

            bool box = _xMin <= _xMax && _yMin <= _yMax && !(geography && _edges);
            statistics = new GeospatialStatistics
            {
                HasBox = box,
                XMin = _xMin,
                XMax = _xMax,
                YMin = _yMin,
                YMax = _yMax,
                HasZ = box && _zMin <= _zMax,
                ZMin = _zMin,
                ZMax = _zMax,
                HasM = box && _mMin <= _mMax,
                MMin = _mMin,
                MMax = _mMax,
                Types = [.. types],
            };
        }

        _xMin = _yMin = _zMin = _mMin = double.PositiveInfinity;
        _xMax = _yMax = _zMax = _mMax = double.NegativeInfinity;
        _types = 0;
        _unknown = false;
        _edges = false;
        return statistics;
    }

    /// <summary>The bounds of every coordinate the values hold, X and Y in <paramref name="bounds"/>'s first four, Z and M after; NaN where none.</summary>
    internal void Bounds(Span<double> bounds)
    {
        bounds[0] = _xMin;
        bounds[1] = _xMax;
        bounds[2] = _yMin;
        bounds[3] = _yMax;
        bounds[4] = _zMin;
        bounds[5] = _zMax;
        bounds[6] = _mMin;
        bounds[7] = _mMax;
    }

    /// <summary>The type codes seen, as the bits of <see cref="Close"/>'s list.</summary>
    internal uint TypeBits => _types;

    /// <summary>Reads one geometry at <paramref name="at"/>, which it advances; false when it is not ISO WKB this build reads.</summary>
    private bool Geometry(ReadOnlySpan<byte> wkb, ref int at, int depth)
    {
        if (depth > MaxDepth || wkb.Length - at < 5)
        {
            return false;
        }

        bool little;
        switch (wkb[at])
        {
            case 0:
                little = false;
                break;
            case 1:
                little = true;
                break;
            default:
                return false;
        }

        uint code = Unsigned(wkb[(at + 1)..], little);
        at += 5;
        uint dimensions = code / 1000;
        uint kind = code % 1000;
        if (dimensions > 3 || kind is < 1 or > 7)
        {
            return false;
        }

        if (depth == 0)
        {
            _types |= 1u << (int)((dimensions * 7) + kind - 1);
        }

        bool z = dimensions is 1 or 3;
        bool m = dimensions is 2 or 3;
        switch (kind)
        {
            case 1:
                return Points(wkb, ref at, 1, z, m, little);

            case 2:
                _edges = true;
                return Count(wkb, ref at, little, out uint points) && Points(wkb, ref at, points, z, m, little);

            case 3:
                _edges = true;
                if (!Count(wkb, ref at, little, out uint rings) || rings > (uint)(wkb.Length - at) / sizeof(uint))
                {
                    return false;
                }

                for (uint ring = 0; ring < rings; ring++)
                {
                    if (!Count(wkb, ref at, little, out uint count) || !Points(wkb, ref at, count, z, m, little))
                    {
                        return false;
                    }
                }

                return true;

            default:
                // A multi-geometry or a collection: each part a geometry of its own, behind its own header.
                if (!Count(wkb, ref at, little, out uint parts) || parts > (uint)(wkb.Length - at) / 5)
                {
                    return false;
                }

                for (uint part = 0; part < parts; part++)
                {
                    if (!Geometry(wkb, ref at, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
        }
    }

    private static bool Count(ReadOnlySpan<byte> wkb, ref int at, bool little, out uint count)
    {
        if (wkb.Length - at < sizeof(uint))
        {
            count = 0;
            return false;
        }

        count = Unsigned(wkb[at..], little);
        at += sizeof(uint);
        return true;
    }

    private static uint Unsigned(ReadOnlySpan<byte> bytes, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    /// <summary>Bounds <paramref name="count"/> points at <paramref name="at"/>, which it advances past them.</summary>
    private bool Points(ReadOnlySpan<byte> wkb, ref int at, uint count, bool z, bool m, bool little)
    {
        int width = 2 + (z ? 1 : 0) + (m ? 1 : 0);
        if (count > (uint)(wkb.Length - at) / (uint)(width * sizeof(double)))
        {
            return false;
        }

        int bytes = (int)count * width * sizeof(double);
        ReadOnlySpan<byte> coordinates = wkb.Slice(at, bytes);
        at += bytes;
        if (little && BitConverter.IsLittleEndian)
        {
            ReadOnlySpan<double> values = MemoryMarshal.Cast<byte, double>(coordinates);
            if (width == 2 && Vector128.IsHardwareAccelerated && _gapLow > _gapHigh)
            {
                Pairs(values);
                return true;
            }

            for (int i = 0; i < values.Length; i += width)
            {
                Point(values[i], values[i + 1], z ? values[i + 2] : double.NaN, m ? values[i + width - 1] : double.NaN);
            }

            return true;
        }

        for (int i = 0; i < bytes; i += width * sizeof(double))
        {
            ReadOnlySpan<byte> point = coordinates[i..];
            Point(
                Double(point, little),
                Double(point[sizeof(double)..], little),
                z ? Double(point[(2 * sizeof(double))..], little) : double.NaN,
                m ? Double(point[((width - 1) * sizeof(double))..], little) : double.NaN);
        }

        return true;
    }

    private static double Double(ReadOnlySpan<byte> bytes, bool little) =>
        little ? BinaryPrimitives.ReadDoubleLittleEndian(bytes) : BinaryPrimitives.ReadDoubleBigEndian(bytes);

    /// <summary>Bounds XY points two coordinates a vector: x in the low lane, y in the high one.</summary>
    private void Pairs(ReadOnlySpan<double> values)
    {
        ref double first = ref MemoryMarshal.GetReference(values);
        Vector128<double> low = Vector128.Create(_xMin, _yMin);
        Vector128<double> high = Vector128.Create(_xMax, _yMax);
        Vector128<double> low2 = low;
        Vector128<double> high2 = high;
        int i = 0;

        // Two points a step, in two pairs of bounds, so that one comparison need not wait for the last.
        for (; i + 4 <= values.Length; i += 4)
        {
            Vector128<double> a = Vector128.LoadUnsafe(ref first, (nuint)i);
            Vector128<double> b = Vector128.LoadUnsafe(ref first, (nuint)(i + 2));

            // A NaN is less and greater than nothing: it leaves both bounds as they are.
            low = Vector128.ConditionalSelect(Vector128.LessThan(a, low), a, low);
            high = Vector128.ConditionalSelect(Vector128.GreaterThan(a, high), a, high);
            low2 = Vector128.ConditionalSelect(Vector128.LessThan(b, low2), b, low2);
            high2 = Vector128.ConditionalSelect(Vector128.GreaterThan(b, high2), b, high2);
        }

        if (i < values.Length)
        {
            Vector128<double> a = Vector128.LoadUnsafe(ref first, (nuint)i);
            low = Vector128.ConditionalSelect(Vector128.LessThan(a, low), a, low);
            high = Vector128.ConditionalSelect(Vector128.GreaterThan(a, high), a, high);
        }

        low = Vector128.ConditionalSelect(Vector128.LessThan(low2, low), low2, low);
        high = Vector128.ConditionalSelect(Vector128.GreaterThan(high2, high), high2, high);
        _xMin = low.GetElement(0);
        _yMin = low.GetElement(1);
        _xMax = high.GetElement(0);
        _yMax = high.GetElement(1);
    }

    private void Point(double x, double y, double z, double m)
    {
        if (x > _gapLow && x < _gapHigh)
        {
            _inGap++;
        }

        if (x < _xMin)
        {
            _xMin = x;
        }

        if (x > _xMax)
        {
            _xMax = x;
        }

        if (y < _yMin)
        {
            _yMin = y;
        }

        if (y > _yMax)
        {
            _yMax = y;
        }

        if (z < _zMin)
        {
            _zMin = z;
        }

        if (z > _zMax)
        {
            _zMax = z;
        }

        if (m < _mMin)
        {
            _mMin = m;
        }

        if (m > _mMax)
        {
            _mMax = m;
        }
    }
}
