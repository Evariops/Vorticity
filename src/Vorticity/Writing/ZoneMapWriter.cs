// Turning a column's per-chunk summaries into the zones array a `vortex.zoned` layout carries.
//
// The zones child is an ordinary array: a struct with one column per aggregate and one ROW per
// zone. Its dtype is never written -- the reader DERIVES it from the aggregate spec list in the
// layout's metadata (ZoneMapSchema.TryBuildAggregateTable) -- so the shape built here has to match
// that derivation exactly, in the same order, with the same nullability. A mismatch is not a
// warning; it is a decode of the wrong bytes.
//
// The spec list is written with the reference's own default options. `NumericalAggregateOpts` has
// IMPLICIT presence, so `skip_nans = true` -- the default, and what ZoneStatistics computes -- is
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
    /// <returns><see langword="false"/> when this column gets no zone map.</returns>
    internal static bool TryBuild(
        DType column,
        IReadOnlyList<ZoneStatistics> zones,
        EncodingDictionary encodings,
        uint zoneLength,
        out byte[] metadata,
        out byte[] blob)
    {
        metadata = [];
        blob = [];

        if (zones.Count == 0 || zoneLength == 0)
        {
            return false;
        }

        // Bounds are emitted only when the column can carry them AND at least one zone actually
        // has them: an all-null i64 column would otherwise get a min/max pair of nothing, which
        // costs bytes and licenses no pruning.
        bool bounds = zones[0].IsSummarizable && AnyBounds(zones);

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
            List<int> columns = [];

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
                columns.Add(Bounds(arena, types, column, zones, wantMin: true));
                columns.Add(Bounds(arena, types, column, zones, wantMin: false));
            }

            specs.Add("vortex.null_count"u8, default);
            columns.Add(NullCounts(arena, types, zones));

            int root = Struct(arena, types, column, bounds, columns);
            blob = ArrayBlobWriter.Write(arena, root, encodings);
            metadata = ZonedMetadata.Serialize(ZonedMetadata.Create(zoneLength, specs));
            return true;
        }
        finally
        {
            // The blob is a byte[] copy by the time Write returns, so nothing outlives the arena.
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

    private static bool AnyBounds(IReadOnlyList<ZoneStatistics> zones)
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
        IReadOnlyList<ZoneStatistics> zones, bool wantMin)
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
        CanonicalArena arena, DTypeArena types, IReadOnlyList<ZoneStatistics> zones)
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
        CanonicalArena arena, DTypeArena types, IReadOnlyList<ZoneStatistics> zones,
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

    private static int Struct(
        CanonicalArena arena, DTypeArena types, DType column, bool bounds, List<int> columns)
    {
        // Names must match ZoneMapSchema's own, which are the aggregate's display form:
        // "vortex.min()" for the default options, not "vortex.min".
        int fields = columns.Count;
        string[] names = new string[fields];
        DType[] dtypes = new DType[fields];

        int at = 0;
        if (bounds)
        {
            names[at] = "vortex.min()";
            dtypes[at++] = types.Primitive(column.PType, Nullability.Nullable);
            names[at] = "vortex.max()";
            dtypes[at++] = types.Primitive(column.PType, Nullability.Nullable);
        }

        names[at] = "vortex.null_count()";
        dtypes[at] = types.Primitive(PType.U64, Nullability.NonNullable);

        DType dtype = types.Struct(names, dtypes, Nullability.NonNullable);
        int rows = arena.GetNode(columns[0]).Length;
        return arena.AddStruct(
            dtype, rows, Arrays.Validity.NonNullable,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(columns));
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
