using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The time-window design's requests drawn a batch at a time straight into the columns of a batch, in the order of their
/// instants: the draws of a <see cref="RequestGenerator"/>, each name a view made once, every one of them short enough to
/// lie inside its view, and a request's id written in place, sixteen bytes a row, or 32 when they look random. No record
/// and no string a row, where a
/// record a row made a string for its id and copied every name into the columns, a third of an incremental build's time.
/// </summary>
internal sealed class RequestColumns
{
    private const int ViewSize = 16;

    // Every name's view, made once: the names are the generator's, the same for every generator.
    private static readonly Int128[] ServiceViews = Views(RequestGenerator.ServiceNames);
    private static readonly Int128[][] EndpointViews = [.. RequestGenerator.EndpointNames.Select(Views)];
    private static readonly Int128[] MethodViews = Views(RequestGenerator.MethodNames);
    private static readonly Int128[] RegionViews = Views(RequestGenerator.RegionNames);
    private static readonly Int128[][] VersionViews = [.. RequestGenerator.VersionNames.Select(Views)];
    private static readonly Int128[][] HostViews = [.. RequestGenerator.HostNames.Select(Views)];

    private readonly RequestGenerator _generator;
    private readonly DType _row;

    // The bytes of an id: "req-" and twelve hexadecimal digits, or 32 of them when the ids look random.
    private readonly int _idBytes;

    /// <param name="generator">The draws, which a generator of the same seed repeats.</param>
    /// <param name="row">The row type of the dataset the batches are for: <see cref="GeneratedRequest"/>'s columns, in its order.</param>
    internal RequestColumns(RequestGenerator generator, DType row)
    {
        _generator = generator;
        _row = row;
        _idBytes = generator.RandomIds ? 32 : 16;
    }

    /// <summary>
    /// The next <paramref name="count"/> requests into <paramref name="arena"/>, rows <paramref name="first"/> on of a run
    /// that starts at <paramref name="origin"/>, a row every <paramref name="step"/>, in the instant's units: the root of a
    /// struct of the row type's columns.
    /// </summary>
    internal int Fill(CanonicalArena arena, long origin, double step, long first, int count)
    {
        VortexBuffer atBuffer = arena.AllocateUninitialized(count * sizeof(long), 64, out Span<byte> atBytes);
        VortexBuffer serviceBuffer = arena.AllocateUninitialized(count * ViewSize, 64, out Span<byte> serviceBytes);
        VortexBuffer endpointBuffer = arena.AllocateUninitialized(count * ViewSize, 64, out Span<byte> endpointBytes);
        VortexBuffer methodBuffer = arena.AllocateUninitialized(count * ViewSize, 64, out Span<byte> methodBytes);
        VortexBuffer statusBuffer = arena.AllocateUninitialized(count * sizeof(short), 64, out Span<byte> statusBytes);
        VortexBuffer regionBuffer = arena.AllocateUninitialized(count * ViewSize, 64, out Span<byte> regionBytes);
        VortexBuffer versionBuffer = arena.AllocateUninitialized(count * ViewSize, 64, out Span<byte> versionBytes);
        VortexBuffer hostBuffer = arena.AllocateUninitialized(count * ViewSize, 64, out Span<byte> hostBytes);
        VortexBuffer idViewBuffer = arena.AllocateUninitialized(count * ViewSize, 64, out Span<byte> idViewBytes);
        VortexBuffer idBuffer = arena.AllocateUninitialized(count * _idBytes, 64, out Span<byte> ids);
        VortexBuffer durationBuffer = arena.AllocateUninitialized(count * sizeof(long), 64, out Span<byte> durationBytes);
        VortexBuffer weightBuffer = arena.AllocateUninitialized(count * sizeof(double), 64, out Span<byte> weightBytes);

        Span<long> at = MemoryMarshal.Cast<byte, long>(atBytes);
        Span<Int128> services = MemoryMarshal.Cast<byte, Int128>(serviceBytes);
        Span<Int128> endpoints = MemoryMarshal.Cast<byte, Int128>(endpointBytes);
        Span<Int128> methods = MemoryMarshal.Cast<byte, Int128>(methodBytes);
        Span<short> statuses = MemoryMarshal.Cast<byte, short>(statusBytes);
        Span<Int128> regions = MemoryMarshal.Cast<byte, Int128>(regionBytes);
        Span<Int128> versions = MemoryMarshal.Cast<byte, Int128>(versionBytes);
        Span<Int128> hosts = MemoryMarshal.Cast<byte, Int128>(hostBytes);
        Span<long> durations = MemoryMarshal.Cast<byte, long>(durationBytes);
        Span<double> weights = MemoryMarshal.Cast<byte, double>(weightBytes);
        for (int i = 0; i < count; i++)
        {
            RequestDraw draw = _generator.Draw(out long id);
            at[i] = origin + (long)((first + i) * step);
            services[i] = ServiceViews[draw.Service];
            endpoints[i] = EndpointViews[draw.Service][draw.Endpoint];
            methods[i] = MethodViews[draw.Method];
            statuses[i] = draw.Status;
            regions[i] = RegionViews[draw.Region];
            versions[i] = VersionViews[draw.Service][draw.Version];
            hosts[i] = HostViews[draw.Service][draw.Host];
            durations[i] = draw.DurationNs;
            weights[i] = draw.Weight;

            // "req-" and the id's twelve hexadecimal digits, in the data buffer, two digits a byte of the id; the view holds
            // the length, the first four bytes, the buffer and the offset. Words written whole, where a copy of four bytes
            // and a digit at a time took a call each. A random-looking id spells the 128 bits of its number's hash, the
            // highest byte first, as RequestGenerator.IdOf does.
            Span<byte> text = ids.Slice(i * _idBytes, _idBytes);
            if (_generator.RandomIds)
            {
                UInt128 bits = RequestGenerator.RandomIdOf(id);
                for (int pair = 15; pair >= 0; pair--)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(text[(pair * 2)..], HexPairs[(int)(byte)bits]);
                    bits >>= 8;
                }
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(text, IdPrefix);
                for (int pair = 7; pair >= 2; pair--)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(text[(pair * 2)..], HexPairs[(int)(id & 0xFF)]);
                    id >>= 8;
                }
            }

            uint prefix = BinaryPrimitives.ReadUInt32LittleEndian(text);
            MemoryMarshal.Write(idViewBytes[(i * ViewSize)..], new Int128((ulong)(uint)(i * _idBytes) << 32, ((ulong)prefix << 32) | (uint)_idBytes));
        }

        DType instant = _row.GetField(0);
        int[] fields =
        [
            arena.AddExtension(instant, count, arena.AddPrimitive(instant.StorageType, count, Validity.NonNullable, PType.I64, atBuffer)),
            Strings(arena, 1, count, serviceBuffer, []),
            Strings(arena, 2, count, endpointBuffer, []),
            Strings(arena, 3, count, methodBuffer, []),
            arena.AddPrimitive(_row.GetField(4), count, Validity.NonNullable, PType.I16, statusBuffer),
            Strings(arena, 5, count, regionBuffer, []),
            Strings(arena, 6, count, versionBuffer, []),
            Strings(arena, 7, count, hostBuffer, []),
            Strings(arena, 8, count, idViewBuffer, [idBuffer]),
            arena.AddPrimitive(_row.GetField(9), count, Validity.NonNullable, PType.I64, durationBuffer),
            arena.AddPrimitive(_row.GetField(10), count, Validity.NonNullable, PType.F64, weightBuffer),
        ];
        return arena.AddStruct(_row, count, Validity.NonNullable, fields);
    }

    /// <summary>"req-", the first four bytes of every id, as a word.</summary>
    private static readonly uint IdPrefix = BinaryPrimitives.ReadUInt32LittleEndian("req-"u8);

    /// <summary>The two lowercase hexadecimal digits of each byte, the high one first, as a word.</summary>
    private static readonly ushort[] HexPairs = [.. Enumerable.Range(0, 256).Select(b => (ushort)("0123456789abcdef"[b >> 4] | ("0123456789abcdef"[b & 0xF] << 8)))];

    private int Strings(CanonicalArena arena, int field, int count, VortexBuffer views, ReadOnlySpan<VortexBuffer> data) =>
        arena.AddVarBinView(_row.GetField(field), count, Validity.NonNullable, views, data, padded: true);

    /// <summary>The view of each name, its bytes inside it, zero past them.</summary>
    private static Int128[] Views(IReadOnlyList<string> names)
    {
        Int128[] views = new Int128[names.Count];
        Span<byte> view = stackalloc byte[ViewSize];
        for (int i = 0; i < names.Count; i++)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(names[i]);
            if (bytes.Length > 12)
            {
                throw new ArgumentException($"'{names[i]}' is past the twelve bytes a view holds inside it.", nameof(names));
            }

            view.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(view, bytes.Length);
            bytes.CopyTo(view[4..]);
            views[i] = MemoryMarshal.Read<Int128>(view);
        }

        return views;
    }
}
