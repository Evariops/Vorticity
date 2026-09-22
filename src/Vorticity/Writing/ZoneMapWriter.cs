using System;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Editions;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// Builds the zones array and the metadata that describes it. The zones dtype is never written:
/// the reader derives it from the aggregate spec list, so the struct built here must match that
/// derivation in field order and nullability, and every aggregate must carry an explicit options
/// payload even for its default, since empty options decode as the opposite default.
/// </summary>
internal static class ZoneMapWriter
{
    /// <summary>The options bytes for <c>NumericalAggregateOpts { skip_nans: true }</c>, the default.</summary>
    private static ReadOnlySpan<byte> SkipNaNs => [0x08, 0x01];

    /// <summary>
    /// Builds the zones array for one column, in the writer's <paramref name="blobs"/>, or returns
    /// <see langword="false"/> when it gets no zone map. <paramref name="strings"/> holds bounded
    /// extremes cut to <paramref name="stringBytes"/>, one per zone.
    /// </summary>
    internal static bool TryBuild(
        ArrayBlobWriter.Workspace blobs,
        DType column,
        IReadOnlyList<BlockStats> zones,
        EncodingDictionary encodings,
        uint zoneLength,
        out byte[] metadata,
        out ArrayBlobWriter.BlobLease blob,
        IReadOnlyList<ZoneString>? strings = null,
        int stringBytes = 0)
    {
        metadata = [];
        blob = default;

        if (zones.Count == 0 || zoneLength == 0)
        {
            return false;
        }

        // An all-null column would otherwise get a min/max pair of nothing: bytes that license no
        // pruning.
        bool bounds = zones[0].IsSummarizable && AnyBounds(zones);
        bool bounded = stringBytes > 0 && strings is not null && strings.Count == zones.Count
            && column.Kind is (DTypeKind.Utf8 or DTypeKind.Binary) && AnyPresent(strings);

        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();

        // The arena is the sole owner of what it rents from the shared pool: left unreset, its
        // blocks are freed on the finalizer thread instead of returning to the pool.
        try
        {
            AggregateSpecList specs = new AggregateSpecList();
            int fields = (bounds ? 2 : 0) + (bounded ? 2 : 0) + 1;
            Span<int> columns = stackalloc int[fields];
            string[] names = new string[fields];
            DType[] dtypes = new DType[fields];
            int at = 0;

            // Aggregates and the layout that carries them are independent ids, so an edition may
            // hold one without the other; asserted rather than assumed, because silently dropping
            // a zone map would change the pruning the caller asked for.
            RequireAggregate(encodings.Target, "vortex.null_count");
            if (bounds)
            {
                RequireAggregate(encodings.Target, "vortex.min");
                RequireAggregate(encodings.Target, "vortex.max");
                specs.Add("vortex.min"u8, SkipNaNs);
                specs.Add("vortex.max"u8, SkipNaNs);
                DType bound = types.Primitive(column.PType, Nullability.Nullable);
                Field(columns, names, dtypes, ref at, Bounds(arena, types, column, zones, wantMin: true), "vortex.min()", bound);
                Field(columns, names, dtypes, ref at, Bounds(arena, types, column, zones, wantMin: false), "vortex.max()", bound);
            }

            if (bounded)
            {
                // The options are the limit as eight raw little-endian bytes, not a message, and
                // the display name carries the limit too.
                RequireAggregate(encodings.Target, "vortex.bounded_min");
                RequireAggregate(encodings.Target, "vortex.bounded_max");
                byte[] limit = new byte[sizeof(ulong)];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(limit, (ulong)stringBytes);
                string n = stringBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
                DType element = column.Kind == DTypeKind.Utf8
                    ? types.Utf8(Nullability.Nullable)
                    : types.Binary(Nullability.Nullable);

                specs.Add("vortex.bounded_min"u8, limit);
                Field(columns, names, dtypes, ref at, BoundedMin(arena, types, element, strings!), "vortex.bounded_min(" + n + ")", element);

                specs.Add("vortex.bounded_max"u8, limit);
                DType partial = BoundedMaxPartial(types, element);
                Field(columns, names, dtypes, ref at, BoundedMax(arena, types, element, partial, strings!), "vortex.bounded_max(" + n + ")", partial);
            }

            specs.Add("vortex.null_count"u8, default);
            Field(
                columns, names, dtypes, ref at, NullCounts(arena, types, zones), "vortex.null_count()",
                types.Primitive(PType.U64, Nullability.NonNullable));

            DType dtype = types.Struct(names, dtypes, Nullability.NonNullable);
            int root = arena.AddStruct(dtype, zones.Count, Arrays.Validity.NonNullable, columns);
            blob = ArrayBlobWriter.Write(blobs, arena, root, encodings);
            metadata = ZonedMetadata.Serialize(ZonedMetadata.Create(zoneLength, specs));
            return true;
        }
        finally
        {
            // The blob is a copy by the time Write returns, so nothing outlives the arena.
            arena.Reset();
        }
    }

    private static void RequireAggregate(VortexEdition target, string id)
    {
        if (EditionRegistry.Contains(target, ComponentKind.Aggregate, id))
        {
            return;
        }

        VortexEdition? introduced = EditionRegistry.IntroducedIn(ComponentKind.Aggregate, id);
        throw new VortexUnsupportedException(
            id,
            VortexComponentKind.Aggregate,
            introduced is null
                ? "No core edition contains it, so no target can emit it."
                : $"The write targets edition {EditionRegistry.Name(target)}, which does not " +
                  $"contain it; it was introduced in {EditionRegistry.Name(introduced.Value)}.");
    }

    private static bool AnyBounds(IReadOnlyList<BlockStats> zones)
    {
        for (int i = 0; i < zones.Count; i++)
        {
            if (zones[i].HasBounds)
            {
                return true;
            }
        }

        return false;
    }

    private static int Bounds(
        CanonicalArena arena, DTypeArena types, DType column,
        IReadOnlyList<BlockStats> zones, bool wantMin)
    {
        PType ptype = column.PType;
        int width = ptype.ByteWidth();
        int count = zones.Count;

        VortexBuffer values = arena.Allocate(count * width, width, out Span<byte> destination);
        int valid = 0;
        for (int i = 0; i < count; i++)
        {
            if (!zones[i].HasBounds)
            {
                continue;
            }

            valid++;
            Write(destination.Slice(i * width, width), ptype, wantMin ? zones[i].Min : zones[i].Max);
        }

        Validity validity = Validity(arena, types, zones, valid, count);
        DType dtype = types.Primitive(ptype, Nullability.Nullable);
        return arena.AddPrimitive(dtype, count, validity, ptype, values);
    }

    private static int NullCounts(
        CanonicalArena arena, DTypeArena types, IReadOnlyList<BlockStats> zones)
    {
        int count = zones.Count;
        VortexBuffer values = arena.Allocate(count * sizeof(ulong), sizeof(ulong), out Span<byte> bytes);
        for (int i = 0; i < count; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                bytes.Slice(i * sizeof(ulong), sizeof(ulong)), (ulong)zones[i].NullCount);
        }

        DType dtype = types.Primitive(PType.U64, Nullability.NonNullable);
        return arena.AddPrimitive(dtype, count, Arrays.Validity.NonNullable, PType.U64, values);
    }

    private static Validity Validity(
        CanonicalArena arena, DTypeArena types, IReadOnlyList<BlockStats> zones,
        int valid, int count)
    {
        if (valid == count)
        {
            return Arrays.Validity.AllValid;
        }

        if (valid == 0)
        {
            return Arrays.Validity.AllInvalid;
        }

        int bytes = CanonicalSupport.BitmapByteCount(count);
        VortexBuffer bits = arena.Allocate(Math.Max(bytes, 1), 1, out Span<byte> destination);
        for (int i = 0; i < count; i++)
        {
            if (zones[i].HasBounds)
            {
                CanonicalSupport.SetBit(destination, i);
            }
        }

        int node = arena.AddBool(
            types.Bool(Nullability.NonNullable), count, Arrays.Validity.NonNullable, bits, 0);
        return Arrays.Validity.Bitmap(node);
    }

    /// <summary>
    /// Records one zone column, named by the aggregate's display form since that is what the
    /// reader derives the field name from.
    /// </summary>
    private static void Field(
        Span<int> columns, string[] names, DType[] dtypes, ref int at, int column, string name, DType dtype)
    {
        columns[at] = column;
        names[at] = name;
        dtypes[at] = dtype;
        at++;
    }

    private static bool AnyPresent(IReadOnlyList<ZoneString> strings)
    {
        for (int i = 0; i < strings.Count; i++)
        {
            if (strings[i].Present)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <c>vortex.bounded_min</c>'s partial: a nullable scalar, null for a zone with no valid value.
    /// </summary>
    private static int BoundedMin(
        CanonicalArena arena, DTypeArena types, DType element, IReadOnlyList<ZoneString> strings)
    {
        byte[]?[] values = new byte[]?[strings.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = strings[i].Present ? strings[i].Min : null;
        }

        return Views(arena, types, element, values);
    }

    /// <summary>
    /// <c>vortex.bounded_max</c>'s partial: a nullable struct of a nullable bound and a non-null
    /// <c>unknown</c>. A zone with no valid value is a null struct; one whose maximum no cut can
    /// bound is a null bound with <c>unknown</c> set.
    /// </summary>
    private static int BoundedMax(
        CanonicalArena arena, DTypeArena types, DType element, DType partial,
        IReadOnlyList<ZoneString> strings)
    {
        int count = strings.Count;
        byte[]?[] values = new byte[]?[count];
        bool[] present = new bool[count];
        int bytes = CanonicalSupport.BitmapByteCount(count);
        VortexBuffer unknown = arena.Allocate(Math.Max(bytes, 1), 1, out Span<byte> unknownBits);
        for (int i = 0; i < count; i++)
        {
            present[i] = strings[i].Present;
            values[i] = strings[i].Present ? strings[i].Max : null;
            if (strings[i].Present && strings[i].Max is null)
            {
                CanonicalSupport.SetBit(unknownBits, i);
            }
        }

        Span<int> children = stackalloc int[2];
        children[0] = Views(arena, types, element, values);
        children[1] = arena.AddBool(
            types.Bool(Nullability.NonNullable), count, Arrays.Validity.NonNullable, unknown, 0);
        return arena.AddStruct(partial, count, Mask(arena, types, present), children);
    }

    private static DType BoundedMaxPartial(DTypeArena types, DType element)
    {
        string[] names = ["bound", "unknown"];
        DType[] fields = [element, types.Bool(Nullability.NonNullable)];
        return types.Struct(names, fields, Nullability.Nullable);
    }

    private static int Views(CanonicalArena arena, DTypeArena types, DType dtype, byte[]?[] values)
    {
        int count = values.Length;
        int heapBytes = 0;
        bool[] valid = new bool[count];
        for (int i = 0; i < count; i++)
        {
            valid[i] = values[i] is not null;
            heapBytes += values[i] is { Length: > 12 } value ? value.Length : 0;
        }

        Span<byte> data = default;
        VortexBuffer heap = heapBytes > 0 ? arena.Allocate(heapBytes, 1, out data) : VortexBuffer.Empty;
        VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> viewBytes);
        int written = 0;
        for (int i = 0; i < count; i++)
        {
            if (values[i] is not { } value)
            {
                continue;
            }

            Span<byte> view = viewBytes.Slice(i * 16, 16);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view, value.Length);
            if (value.Length <= 12)
            {
                value.CopyTo(view[4..]);
                continue;
            }

            value.AsSpan(0, 4).CopyTo(view[4..]);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view[12..], written);
            value.CopyTo(data[written..]);
            written += value.Length;
        }

        Validity validity = Mask(arena, types, valid);
        return heapBytes > 0
            ? arena.AddVarBinView(dtype, count, validity, views, [heap])
            : arena.AddVarBinView(dtype, count, validity, views, default);
    }

    private static Validity Mask(CanonicalArena arena, DTypeArena types, bool[] valid)
    {
        int count = valid.Length;
        int set = 0;
        foreach (bool bit in valid)
        {
            set += bit ? 1 : 0;
        }

        if (set == count)
        {
            return Arrays.Validity.AllValid;
        }

        if (set == 0)
        {
            return Arrays.Validity.AllInvalid;
        }

        int bytes = CanonicalSupport.BitmapByteCount(count);
        VortexBuffer bits = arena.Allocate(Math.Max(bytes, 1), 1, out Span<byte> destination);
        for (int i = 0; i < count; i++)
        {
            if (valid[i])
            {
                CanonicalSupport.SetBit(destination, i);
            }
        }

        int node = arena.AddBool(
            types.Bool(Nullability.NonNullable), count, Arrays.Validity.NonNullable, bits, 0);
        return Arrays.Validity.Bitmap(node);
    }

    private static void Write(Span<byte> destination, PType ptype, FilterLiteral value)
    {
        switch (ptype)
        {
            case PType.F16:
                System.Buffers.Binary.BinaryPrimitives.WriteHalfLittleEndian(
                    destination, (Half)value.FloatValue);
                return;
            case PType.F32:
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                    destination, (float)value.FloatValue);
                return;
            case PType.F64:
                System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(
                    destination, value.FloatValue);
                return;
            default:
                break;
        }

        ulong bits = value.Kind == FilterLiteralKind.Signed
            ? unchecked((ulong)value.SignedValue)
            : value.UnsignedValue;

        switch (destination.Length)
        {
            case 1:
                destination[0] = (byte)bits;
                return;
            case 2:
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)bits);
                return;
            case 4:
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)bits);
                return;
            default:
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(destination, bits);
                return;
        }
    }
}
