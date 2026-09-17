// Turning a column's per-BLOCK summaries into the zones array a `vortex.zoned` layout carries.
//
// Per block, not per chunk, since docs/11-write-strategy.md §8 stage 1: a zone is a block of
// `RowBlockSize` rows counted from row 0 of the file, which is the only granularity a zone map can
// declare, and the summaries arrive already in that shape from the ingest pass.
//
// The zones child is an ordinary array: a struct with one column per aggregate and one ROW per
// zone. Its dtype is never written -- the reader DERIVES it from the aggregate spec list in the
// layout's metadata (ZoneMapSchema.TryBuildAggregateTable) -- so the shape built here has to match
// that derivation exactly, in the same order, with the same nullability. A mismatch is not a
// warning; it is a decode of the wrong bytes.
//
// The spec list is written with the reference's own default options. `NumericalAggregateOpts` has
// IMPLICIT presence, so `skip_nans = true` -- the default, and what BlockStatsPass computes -- is
// the two bytes `08 01`, while an EMPTY payload would decode to `skip_nans = false` and claim
// bounds that include NaN. The most dangerous encoding here is the one that looks like "no options".
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

/// <summary>Builds the zones array and the metadata that describes it.</summary>
internal static class ZoneMapWriter
{
    /// <summary>`NumericalAggregateOpts { skip_nans: true }`, which is the default and is NOT empty.</summary>
    private static ReadOnlySpan<byte> SkipNaNs => [0x08, 0x01];

    /// <summary>
    /// Builds the zones array for one column, or reports that it has no usable zone map.
    /// </summary>
    /// <param name="column">The column's dtype.</param>
    /// <param name="zones">One summary per zone, in zone order.</param>
    /// <param name="encodings">The file's array-encoding dictionary.</param>
    /// <param name="metadata">The <c>vortex.zoned</c> layout metadata.</param>
    /// <param name="zoneLength">Rows per zone.</param>
    /// <param name="blob">The serialized zones array.</param>
    /// <param name="strings">
    /// A utf8 or binary column's bounded extremes, one per zone, or <see langword="null"/> for none.
    /// </param>
    /// <param name="stringBytes">The limit they were cut to: the <c>n</c> of the aggregates.</param>
    /// <returns><see langword="false"/> when this column gets no zone map.</returns>
    internal static bool TryBuild(
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

        // Bounds are emitted only when the column can carry them AND at least one zone actually
        // has them: an all-null i64 column would otherwise get a min/max pair of nothing, which
        // costs bytes and licenses no pruning.
        bool bounds = zones[0].IsSummarizable && AnyBounds(zones);
        bool bounded = stringBytes > 0 && strings is not null && strings.Count == zones.Count
            && column.Kind is (DTypeKind.Utf8 or DTypeKind.Binary) && AnyPresent(strings);

        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();

        // RESET IN A FINALLY, because this arena rents from AlignedBufferPool.Shared and is the
        // only owner of what it rents. Dropping it on the floor does not merely fail to recycle:
        // every block leaves through ~NativeSegmentOwner, so a write of N columns x M chunks
        // queues N*M native frees onto the finalizer thread and the pool it rented from stays
        // empty. That showed up as 38% of a write profile under GC.RunFinalizers.
        try
        {
            AggregateSpecList specs = new AggregateSpecList();
            int fields = (bounds ? 2 : 0) + (bounded ? 2 : 0) + 1;
            Span<int> columns = stackalloc int[fields];
            string[] names = new string[fields];
            DType[] dtypes = new DType[fields];
            int at = 0;

            // The fourth namespace the allowlist covers. It cannot fire today - all six aggregates
            // and the `vortex.zoned` layout that carries them were introduced by the same edition,
            // so a target that has the layout has the aggregates - but the two are independent ids
            // in the spec and a future edition is free to add a seventh. Asserted rather than
            // assumed, because silently dropping a zone map would quietly change the pruning the
            // caller asked for.
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
                // The options are `max_bytes.to_le_bytes()`, eight raw bytes and not a message
                // (vortex-array-0.86.1 aggregate_fn/fns/bounded_min/mod.rs), and the display name
                // carries the limit.
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
            blob = ArrayBlobWriter.Write(arena, root, encodings);
            metadata = ZonedMetadata.Serialize(ZonedMetadata.Create(zoneLength, specs));
            return true;
        }
        finally
        {
            // The blob is a copy by the time Write returns -- into a rented buffer since W-4, but
            // still a copy -- so nothing outlives the arena.
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

    /// <summary>The min or max column: the column's own dtype, made nullable.</summary>
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
    /// Records one zone column. The name is the aggregate's display form, which is what
    /// ZoneMapSchema derives: "vortex.min()" for the default options, "vortex.bounded_min(64)" with
    /// the limit.
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
    /// <c>vortex.bounded_min</c>'s partial is a bare nullable scalar: the bound, or null for a zone
    /// with no valid value.
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
    /// <c>vortex.bounded_max</c>'s partial (<c>make_bounded_max_partial_dtype</c>): a nullable
    /// struct of a nullable bound and a non-null <c>unknown</c>. A zone with no valid value is a
    /// null struct; one whose maximum no cut can bound is a null bound with <c>unknown</c> set.
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

    /// <summary>A nullable varbinview of <paramref name="values"/>, a null entry a null row.</summary>
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

    /// <summary>The validity of a nullable zone column, in the shortest form that says it.</summary>
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
