using System;
using System.Collections.Generic;
using System.Threading;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Editions;
using Vorticity.Layouts;
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
    /// <summary>
    /// What a zone map is built in -- its arrays' arena, its dtypes' arena, its aggregate specs --
    /// emptied before each zone map, since the blob and the metadata are copies once built: one
    /// for every zone map a completion writes, and kept for the next completion.
    /// </summary>
    internal sealed class Scratch
    {
        /// <summary>The scratch completions share; null while one holds it.</summary>
        private static Scratch? Cached;

        internal CanonicalArena Arena { get; } = new CanonicalArena();

        internal DTypeArena Types { get; } = new DTypeArena();

        internal AggregateSpecList Specs { get; } = new AggregateSpecList();

        /// <summary>The shared scratch, or a new one when another completion holds it.</summary>
        internal static Scratch Take() => Interlocked.Exchange(ref Cached, null) ?? new Scratch();

        /// <summary>Keeps the scratch, emptied, for the next completion.</summary>
        internal void Give()
        {
            Arena.Reset();
            Types.Clear();
            Specs.Clear();
            Volatile.Write(ref Cached, this);
        }
    }

    /// <summary>The options bytes for <c>NumericalAggregateOpts { skip_nans: true }</c>, the default.</summary>
    private static ReadOnlySpan<byte> SkipNaNs => [0x08, 0x01];

    /// <summary>
    /// Builds the zones array for one column, in the writer's <paramref name="blobs"/>, or returns
    /// <see langword="false"/> when it gets no zone map. <paramref name="strings"/> holds bounded
    /// extremes cut to <paramref name="stringBytes"/>, one per zone.
    /// </summary>
    internal static bool TryBuild(
        ArrayBlobWriter.Workspace blobs,
        Scratch scratch,
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

        DTypeArena types = scratch.Types;
        CanonicalArena arena = scratch.Arena;
        AggregateSpecList specs = scratch.Specs;

        // The arena is the sole owner of what it rents from the shared pool: left unreset, its
        // blocks are freed on the finalizer thread instead of returning to the pool. The dtypes
        // and the specs are emptied with it, since the blob and the metadata are copies.
        try
        {
            Span<int> columns = stackalloc int[MaxFields];
            Span<int> names = stackalloc int[MaxFields];
            FieldTypes dtypes = default;
            Span<byte> name = stackalloc byte[ZoneMapSchema.MaxDisplayNameBytes];
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

                // A scalar of the column's own type, made nullable, as the reader derives it: an
                // extension's bounds are its storage's values under its dtype.
                DType bound = DTypeImport.Into(types, column).WithNullability(Nullability.Nullable);
                Field(columns, names, dtypes, ref at, Bounds(arena, types, bound, zones, wantMin: true), Named(types, AggregateId.Min, SkipNaNs, name), bound);
                Field(columns, names, dtypes, ref at, Bounds(arena, types, bound, zones, wantMin: false), Named(types, AggregateId.Max, SkipNaNs, name), bound);
            }

            if (bounded)
            {
                // The options are the limit as eight raw little-endian bytes, not a message, and
                // the display name carries the limit too.
                RequireAggregate(encodings.Target, "vortex.bounded_min");
                RequireAggregate(encodings.Target, "vortex.bounded_max");
                Span<byte> limit = stackalloc byte[sizeof(ulong)];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(limit, (ulong)stringBytes);
                DType element = column.Kind == DTypeKind.Utf8
                    ? types.Utf8(Nullability.Nullable)
                    : types.Binary(Nullability.Nullable);

                specs.Add("vortex.bounded_min"u8, limit);
                Field(columns, names, dtypes, ref at, BoundedMin(arena, types, element, strings!), Named(types, AggregateId.BoundedMin, limit, name), element);

                specs.Add("vortex.bounded_max"u8, limit);
                DType partial = BoundedMaxPartial(types, element);
                Field(columns, names, dtypes, ref at, BoundedMax(arena, types, element, partial, strings!), Named(types, AggregateId.BoundedMax, limit, name), partial);
            }

            specs.Add("vortex.null_count"u8, default);
            Field(
                columns, names, dtypes, ref at, NullCounts(arena, types, zones), Named(types, AggregateId.NullCount, default, name),
                types.Primitive(PType.U64, Nullability.NonNullable));

            DType dtype = types.Struct(names[..at], ((ReadOnlySpan<DType>)dtypes)[..at], Nullability.NonNullable);
            int root = arena.AddStruct(dtype, zones.Count, Arrays.Validity.NonNullable, columns[..at]);
            blob = ArrayBlobWriter.Write(blobs, arena, root, encodings);
            metadata = ZonedMetadata.Serialize(ZonedMetadata.Create(zoneLength, specs));
            return true;
        }
        finally
        {
            // The blob is a copy by the time Write returns, so nothing outlives the arena.
            arena.Reset();
            types.Clear();
            specs.Clear();
        }
    }

    /// <summary>The most columns a zones struct has: a minimum, a maximum, two bounded extremes and the null count.</summary>
    private const int MaxFields = 5;

    /// <summary>The dtypes of a zones struct's columns, on the stack: a DType holds its arena, so it cannot be stackalloc'd.</summary>
    [System.Runtime.CompilerServices.InlineArray(MaxFields)]
    private struct FieldTypes
    {
        private DType _first;
    }

    /// <summary>
    /// The column name of <paramref name="aggregate"/>, interned in <paramref name="types"/>: the
    /// name the reader derives from the same options, so that the two can never disagree.
    /// </summary>
    private static int Named(DTypeArena types, AggregateId aggregate, ReadOnlySpan<byte> options, Span<byte> name) =>
        types.InternName(name[..ZoneMapSchema.WriteDisplayName(aggregate, options, name)]);

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
        CanonicalArena arena, DTypeArena types, DType bound,
        IReadOnlyList<BlockStats> zones, bool wantMin)
    {
        if (bound.Kind == DTypeKind.Extension)
        {
            return arena.AddExtension(bound, zones.Count, Bounds(arena, types, bound.StorageType, zones, wantMin));
        }

        PType ptype = bound.PType;
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
        return arena.AddPrimitive(bound, count, validity, ptype, values);
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
        Span<int> columns, Span<int> names, Span<DType> dtypes, ref int at, int column, int name, DType dtype)
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
        CanonicalArena arena, DTypeArena types, DType element, IReadOnlyList<ZoneString> strings) =>
        Views(arena, types, element, strings, max: false);

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
        int bytes = CanonicalSupport.BitmapByteCount(count);
        VortexBuffer unknown = arena.Allocate(Math.Max(bytes, 1), 1, out Span<byte> unknownBits);
        for (int i = 0; i < count; i++)
        {
            ZoneString zone = strings[i];
            if (zone.Present && zone.Max is null)
            {
                CanonicalSupport.SetBit(unknownBits, i);
            }
        }

        Span<int> children = stackalloc int[2];
        children[0] = Views(arena, types, element, strings, max: true);
        children[1] = arena.AddBool(
            types.Bool(Nullability.NonNullable), count, Arrays.Validity.NonNullable, unknown, 0);

        // The struct is present where the zone is: the minimum's validity, which is presence.
        return arena.AddStruct(partial, count, Mask(arena, types, strings, max: false), children);
    }

    /// <summary>
    /// The bound a zone contributes to <c>vortex.bounded_min</c> or, when <paramref name="max"/>,
    /// to <c>vortex.bounded_max</c>: null for a zone with no valid value, and for a maximum no cut
    /// can bound.
    /// </summary>
    private static ReadOnlyMemory<byte>? Bound(in ZoneString zone, bool max)
    {
        if (!zone.Present)
        {
            return null;
        }

        return max ? zone.Max : zone.Min;
    }

    private static DType BoundedMaxPartial(DTypeArena types, DType element)
    {
        ReadOnlySpan<string> names = ["bound", "unknown"];
        ReadOnlySpan<DType> fields = [element, types.Bool(Nullability.NonNullable)];
        return types.Struct(names, fields, Nullability.Nullable);
    }

    /// <summary>The zones' minimums, or their maximums, as a varbinview column; a zone without one is null.</summary>
    private static int Views(
        CanonicalArena arena, DTypeArena types, DType dtype, IReadOnlyList<ZoneString> strings, bool max)
    {
        int count = strings.Count;
        int heapBytes = 0;
        for (int i = 0; i < count; i++)
        {
            heapBytes += Bound(strings[i], max) is { Length: > 12 } value ? value.Length : 0;
        }

        Span<byte> data = default;
        VortexBuffer heap = heapBytes > 0 ? arena.Allocate(heapBytes, 1, out data) : VortexBuffer.Empty;
        VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> viewBytes);
        int written = 0;
        for (int i = 0; i < count; i++)
        {
            if (Bound(strings[i], max) is not { } bound)
            {
                continue;
            }

            ReadOnlySpan<byte> value = bound.Span;
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view, value.Length);
            if (value.Length <= 12)
            {
                value.CopyTo(view[4..]);
                continue;
            }

            value[..4].CopyTo(view[4..]);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view[12..], written);
            value.CopyTo(data[written..]);
            written += value.Length;
        }

        Validity validity = Mask(arena, types, strings, max);
        return heapBytes > 0
            ? arena.AddVarBinView(dtype, count, validity, views, [heap])
            : arena.AddVarBinView(dtype, count, validity, views, default);
    }

    /// <summary>The validity of <see cref="Views"/>: set where the zone has the bound.</summary>
    private static Validity Mask(CanonicalArena arena, DTypeArena types, IReadOnlyList<ZoneString> strings, bool max)
    {
        int count = strings.Count;
        int set = 0;
        for (int i = 0; i < count; i++)
        {
            set += Bound(strings[i], max) is null ? 0 : 1;
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
            if (Bound(strings[i], max) is not null)
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
