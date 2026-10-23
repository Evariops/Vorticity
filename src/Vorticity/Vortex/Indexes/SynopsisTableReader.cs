using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.File;
using Vorticity.Types;

namespace Vorticity.Indexes;

/// <summary>
/// The reads of an object's synopsis tables: through its open file, or with no open of it, from its table of contents
/// (<see cref="SynopsisContents"/>), by a reader of the object or from the regions held at hand (<see cref="IHeldRegions"/>);
/// each chunk's rows decoded at its table's row type (<see cref="SynopsisTableInfo.RowType"/>).
/// </summary>
internal static class SynopsisTableReader
{
    /// <summary>
    /// Every synopsis table of <paramref name="file"/>, read through its index directory, which names each table's list of
    /// chunks: the lists read together, then the chunks, each table's rows decoded at the struct the file's schema and its
    /// slices' paths give it (<see cref="SynopsisTableInfo.RowType"/>), over the file's encodings. A table whose list does not hold its checksum
    /// or does not parse, or whose keys name paths the file's slices do not, and a chunk whose region does not hold its
    /// checksum or does not decode, are left out.
    /// </summary>
    internal static async ValueTask<SynopsisTableSet> ReadAsync(VortexFile file, CancellationToken cancellationToken)
    {
        // The tables lie in the file itself: an append drops them. A table's entry names its list alone.
        IndexDirectory? directory = await file.ReadIndexDirectoryAsync(cancellationToken).ConfigureAwait(false);
        List<(SynopsisTableInfo Table, IndexSegment List)> listed = [];
        IReadOnlyList<SynopsisPath> paths = [];
        foreach (IndexEntry entry in directory?.Entries ?? [])
        {
            if (entry.Kind == IndexKinds.Slices && SliceTable.FromBytes(entry.Options) is { } slices)
            {
                paths = slices.Paths;
            }
            else if (entry.Kind == IndexKinds.SynopsisTable && SynopsisTableInfo.From(entry.Options, out _) is { } table
                && entry.Runs is [IndexRun { Origin: 0, Payload: [IndexSegment list], PayloadDTypes.Count: 0 }])
            {
                listed.Add((table, list));
            }
        }

        if (listed.Count == 0)
        {
            return new SynopsisTableSet(new ScanContext([]), []);
        }

        DTypeArena types = new DTypeArena();
        List<Wanted> found = [];
        List<Apart> aparts = [];
        bool split = false;
        using (IO.SegmentRequestSet requests = new IO.SegmentRequestSet(listed.Count))
        {
            int[] slots = new int[listed.Count];
            for (int t = 0; t < listed.Count; t++)
            {
                IndexSegment list = listed[t].List;
                slots[t] = requests.Add(new Serialization.Schemas.SegmentSpec(list.Offset, list.Length, list.AlignmentExponent, 0, 0));
            }

            await file.Segments.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);

            for (int t = 0; t < listed.Count; t++)
            {
                (SynopsisTableInfo table, IndexSegment list) = listed[t];
                if (!Named(table, paths.Count) || SynopsisContents.TableChunks.Listed(list, requests.GetBuffer(slots[t]).Span) is not { } chunks)
                {
                    VortexTelemetry.SynopsisTableCorruption();
                    continue;
                }

                DType row = table.RowType(file.DType, paths, types);
                (DType main, DType distinct) = table.Distinct.Length > 0 ? SynopsisTableInfo.RowHalves(row, table.Distinct.Length, types) : default;
                for (int c = 0; c < chunks.Count; c++)
                {
                    TableChunk chunk = chunks[c];
                    bool apart = chunk.Distinct is not null && table.Distinct.Length > 0;
                    found.Add(new Wanted(table, chunk, apart ? main : row));
                    aparts.Add(apart ? new Apart(row, distinct, Layouts.FieldMask.All) : default);
                    split |= apart;
                }
            }
        }

        if (found.Count == 0)
        {
            return new SynopsisTableSet(new ScanContext([]), []);
        }

        SynopsisTableSet set = await DecodeAsync(file.Segments, file.CreateIndexContext(0), [.. found], split ? [.. aparts] : null, cancellationToken).ConfigureAwait(false);
        set.Types = types;
        return set;
    }

    /// <summary>Whether every path <paramref name="table"/>'s keys, measures and distinct sketches name is one of the <paramref name="paths"/> a file's slices name.</summary>
    private static bool Named(SynopsisTableInfo table, int paths)
    {
        foreach (int[] entries in (ReadOnlySpan<int[]>)[table.Keys, table.Measures, table.Distinct])
        {
            foreach (int entry in entries)
            {
                if (SynopsisPath.Names(entry) && SynopsisPath.Place(entry) >= paths)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// The synopsis tables of a file its table of contents <paramref name="contents"/> names, read from
    /// <paramref name="source"/> without an open: the chunks <paramref name="reads"/> keeps, every one when it
    /// is null, their regions in one request set, each chunk's rows decoded at its table's <see cref="SynopsisTableInfo.RowType"/>
    /// over the file's schema, which the contents hold, and only the fields of them <paramref name="reads"/> names, in a context
    /// taken from <paramref name="contexts"/>, which the set gives back once let go.
    /// </summary>
    /// <exception cref="VortexFormatException">The contents' schema does not decode.</exception>
    internal static async ValueTask<SynopsisTableSet> ReadAsync(
        IO.ISegmentReader source, SynopsisContents contents, ISynopsisReads? reads, ScanContexts contexts, VortexReadOptions? options,
        CancellationToken cancellationToken)
    {
        await LoadListsAsync(source, contents, reads, cancellationToken).ConfigureAwait(false);
        Wanted[] wanted = WantedOf(contents, reads, out Apart[]? apart);
        (ArrayEncodingId[] resolved, string[] ids) = contents.EncodingTable;
        ScanContext context = contexts.RentDetached(options ?? VortexReadOptions.Default, resolved, ids);
        SynopsisTableSet set = await DecodeAsync(source, context, wanted, apart, cancellationToken, contexts, reads).ConfigureAwait(false);
        set.Types = contents.TableRowTypes.Arena;
        return set;
    }

    /// <summary>
    /// The tables <see cref="ReadAsync(IO.ISegmentReader, SynopsisContents, ISynopsisReads?, ScanContexts, VortexReadOptions?, CancellationToken)"/>
    /// reads, when <paramref name="held"/> holds every chunk wanted of object <paramref name="uid"/>: with no reader of the
    /// object, no wait and no request of its own; null when one is not there, which a reader then reads.
    /// </summary>
    /// <remarks>
    /// A warm question asks every object this, where each opened a reader of the store, one of the cache and one of the
    /// session over it, a few hundred bytes and three layers of waits an object, for regions the disk held.
    /// </remarks>
    /// <exception cref="VortexFormatException">The contents' schema does not decode.</exception>
    internal static SynopsisTableSet? ReadHeld(
        IHeldRegions held, UInt128 uid, SynopsisContents contents, ISynopsisReads? reads, ScanContexts contexts, VortexReadOptions? options)
    {
        if (!LoadListsHeld(held, uid, contents, reads))
        {
            return null;
        }

        // Every chunk held, asked before one is read: a read that falls back to the reader then counts no hit twice.
        Wanted[] wanted = WantedOf(contents, reads, out Apart[]? apart);
        if (wanted.Length == 0)
        {
            return null;
        }

        for (int i = 0; i < wanted.Length; i++)
        {
            if (!held.Holds(uid, wanted[i].Chunk.Region) || (Sketched(apart, i) && !held.Holds(uid, wanted[i].Chunk.Distinct!.Value)))
            {
                return null;
            }
        }

        (ArrayEncodingId[] resolved, string[] ids) = contents.EncodingTable;
        ScanContext context = contexts.RentDetached(options ?? VortexReadOptions.Default, resolved, ids);
        IO.SegmentRequestSet requests = context.Segments;
        bool missed = false;
        bool kept = false;
        try
        {
            if (Add(requests, wanted, apart) is false)
            {
                missed = true;
                return null;
            }

            // Each chunk's slot filled from the chunk, in the chunks' order: a slot's region found by a walk of the chunks
            // cost the square of a table cut in tens of thousands of chunks.
            for (int i = 0; i < wanted.Length; i++)
            {
                if (!FillHeld(held, uid, requests, wanted[i].Slot, wanted[i].Chunk.Region)
                    || (Sketched(apart, i) && !FillHeld(held, uid, requests, apart![i].Slot, wanted[i].Chunk.Distinct!.Value)))
                {
                    missed = true;
                    return null;
                }
            }

            requests.Complete();
            SynopsisTableSet set = Decoded(context, wanted, apart, requests, contexts, reads, verified: true);
            set.Types = contents.TableRowTypes.Arena;
            kept = true;
            return set;
        }
        catch
        {
            context.Dispose();
            throw;
        }
        finally
        {
            if (!kept)
            {
                requests.Release();
            }

            if (missed)
            {
                contexts.Return(context);
            }
        }
    }

    /// <summary>
    /// Loads the lists of the chunks the contents name apart (<see cref="SynopsisContents.TableChunks.List"/>) of the tables
    /// <paramref name="reads"/> reads, every one when it is null, that no read loaded yet: their regions read from
    /// <paramref name="source"/> in one round trip. A list that does not hold its checksum, or chunks the entry does not say,
    /// stays unloaded, counted, and its table is read as rows. Then tells <paramref name="reads"/> (<see cref="ISynopsisReads.Loaded"/>).
    /// </summary>
    internal static async ValueTask LoadListsAsync(IO.ISegmentReader source, SynopsisContents contents, ISynopsisReads? reads, CancellationToken cancellationToken)
    {
        if (contents.Unloaded(reads) is { } unloaded)
        {
            using IO.SegmentRequestSet requests = new IO.SegmentRequestSet(unloaded.Count);
            int[] slots = new int[unloaded.Count];
            for (int i = 0; i < unloaded.Count; i++)
            {
                IndexSegment list = unloaded[i].List!.Value;
                slots[i] = requests.Add(new Serialization.Schemas.SegmentSpec(list.Offset, list.Length, list.AlignmentExponent, 0, 0));
            }

            await source.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            for (int i = 0; i < unloaded.Count; i++)
            {
                if (!unloaded[i].Load(requests.GetBuffer(slots[i]).Span))
                {
                    VortexTelemetry.SynopsisTableCorruption();
                }
            }
        }

        reads?.Loaded(contents);
    }

    /// <summary>
    /// <see cref="LoadListsAsync"/> from <paramref name="held"/> alone, the lists of object <paramref name="uid"/>, with no reader
    /// and no request: false when it does not hold a list wanted, which a reader then reads.
    /// </summary>
    internal static bool LoadListsHeld(IHeldRegions held, UInt128 uid, SynopsisContents contents, ISynopsisReads? reads)
    {
        if (contents.Unloaded(reads) is { } unloaded)
        {
            foreach (SynopsisContents.TableChunks listed in unloaded)
            {
                if (!held.TryRead(uid, listed.List!.Value, out Buffers.SegmentOwner owner))
                {
                    return false;
                }

                try
                {
                    if (!listed.Load(owner.Buffer.Span))
                    {
                        return false;
                    }
                }
                finally
                {
                    owner.Release();
                }
            }
        }

        reads?.Loaded(contents);
        return true;
    }

    /// <summary>
    /// The chunks of <paramref name="contents"/>'s tables <paramref name="reads"/> keeps, every one when it is null, each with
    /// its table's row type: counted, then named, in an array of their number, a few an object, where lists of them grew for
    /// each. By index, as every list of the contents: an interface's enumerator is an allocation a list. A table whose list of
    /// chunks did not load has none read. A chunk that lays its distinct sketches apart decodes the rest of its row from its
    /// region, and the sketches from theirs when <paramref name="reads"/> decodes one of them, which <paramref name="apart"/>
    /// says at the chunk's place; null when no chunk's are read.
    /// </summary>
    private static Wanted[] WantedOf(SynopsisContents contents, ISynopsisReads? reads, out Apart[]? apart)
    {
        apart = null;
        int count = 0;
        for (int t = 0; t < contents.Tables.Count; t++)
        {
            (SynopsisTableInfo table, IReadOnlyList<TableChunk> chunks) = contents.Tables[t];
            if (reads?.ReadsTable(table) == false || chunks is SynopsisContents.TableChunks { Loaded: false })
            {
                continue;
            }

            for (int c = 0; c < chunks.Count; c++)
            {
                count += reads?.Reads(table, chunks[c]) ?? true ? 1 : 0;
            }
        }

        // The tables' row types, built once for the contents and shared by every read of them.
        DType[] rows = contents.TableRowTypes.Rows;
        Wanted[] wanted = count == 0 ? [] : new Wanted[count];
        int at = 0;
        for (int t = 0; t < contents.Tables.Count && at < count; t++)
        {
            (SynopsisTableInfo table, IReadOnlyList<TableChunk> chunks) = contents.Tables[t];
            if (reads?.ReadsTable(table) == false || chunks is SynopsisContents.TableChunks { Loaded: false })
            {
                continue;
            }

            Layouts.FieldMask? sketches = null;
            bool asked = false;
            for (int c = 0; c < chunks.Count; c++)
            {
                TableChunk chunk = chunks[c];
                if (!(reads?.Reads(table, chunk) ?? true))
                {
                    continue;
                }

                // A chunk that names the sketches' region of a table that holds none decodes as a whole, and fails to.
                if (chunk.Distinct is null || table.Distinct.Length == 0)
                {
                    wanted[at++] = new Wanted(table, chunk, rows[t]);
                    continue;
                }

                // The fields of the sketches the read decodes, the same for every chunk of the table.
                (DType[] mains, DType[] distincts) = contents.TableApartTypes;
                if (!asked)
                {
                    sketches = DistinctFields(table, rows[t], reads);
                    asked = true;
                }

                if (sketches is { } fields)
                {
                    (apart ??= new Apart[count])[at] = new Apart(rows[t], distincts[t], fields);
                }

                wanted[at++] = new Wanted(table, chunk, mains[t]);
            }
        }

        return wanted;
    }

    /// <summary>
    /// The fields of <paramref name="table"/>'s distinct sketches, the last of its row type <paramref name="row"/>, that
    /// <paramref name="reads"/> decodes, every one when it is null, as a mask of their own struct; null for none.
    /// </summary>
    private static Layouts.FieldMask? DistinctFields(SynopsisTableInfo table, DType row, ISynopsisReads? reads)
    {
        Layouts.FieldMask fields = reads?.Fields(table) ?? Layouts.FieldMask.All;
        int width = row.FieldCount;
        int first = width - table.Distinct.Length;
        int selected = fields.SelectedCount(width);
        int before = fields.SelectedCount(first);
        if (selected == before)
        {
            return null;
        }

        if (fields.IsAll || selected - before == table.Distinct.Length)
        {
            return Layouts.FieldMask.All;
        }

        Span<int> picked = stackalloc int[selected - before];
        for (int s = before; s < selected; s++)
        {
            picked[s - before] = fields.SelectedField(s) - first;
        }

        return Layouts.FieldMasks.Of(picked);
    }

    /// <summary>Whether <paramref name="apart"/> says the read fetches the distinct sketches of chunk <paramref name="i"/> from their own region.</summary>
    private static bool Sketched(Apart[]? apart, int i) => apart is not null && !apart[i].Row.IsDefault;

    /// <summary>
    /// Fills <paramref name="slot"/> of <paramref name="requests"/> with <paramref name="region"/> of object <paramref name="uid"/>
    /// as <paramref name="held"/> holds it, unless a chunk that names the same region filled it; false when it holds it no more.
    /// </summary>
    private static bool FillHeld(IHeldRegions held, UInt128 uid, IO.SegmentRequestSet requests, int slot, IndexSegment region)
    {
        if (requests.IsFilled(slot))
        {
            return true;
        }

        held.Ask(uid, region);
        if (!held.TryRead(uid, region, out Buffers.SegmentOwner owner))
        {
            return false;
        }

        requests.SetResult(slot, owner);
        requests.NoteCacheHit();
        return true;
    }

    /// <summary>
    /// Registers each chunk's region in <paramref name="requests"/>, and that of its distinct sketches when <paramref name="apart"/>
    /// says they are read, their slots noted; false when <paramref name="wanted"/> is empty.
    /// </summary>
    private static bool Add(IO.SegmentRequestSet requests, Wanted[] wanted, Apart[]? apart)
    {
        for (int i = 0; i < wanted.Length; i++)
        {
            IndexSegment region = wanted[i].Chunk.Region;
            wanted[i].Slot = requests.Add(new Serialization.Schemas.SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0));
            if (Sketched(apart, i))
            {
                IndexSegment distinct = wanted[i].Chunk.Distinct!.Value;
                apart![i].Slot = requests.Add(new Serialization.Schemas.SegmentSpec(distinct.Offset, distinct.Length, distinct.AlignmentExponent, 0, 0));
            }
        }

        return wanted.Length > 0;
    }

    /// <summary>
    /// Reads the chunks of <paramref name="wanted"/> in one request set, the context's own, and decodes each into
    /// <paramref name="context"/>'s arena at its table's row type, only the fields <paramref name="reads"/> names of
    /// it; a chunk whose region does not hold its checksum, or does not decode, is left out.
    /// </summary>
    private static async ValueTask<SynopsisTableSet> DecodeAsync(
        IO.ISegmentReader source, ScanContext context, Wanted[] wanted, Apart[]? apart, CancellationToken cancellationToken, ScanContexts? pool = null,
        ISynopsisReads? reads = null)
    {
        // The context's set, which a context given back holds empty: the rows lie in the bytes read, which the set of
        // tables keeps and lets go with them.
        IO.SegmentRequestSet requests = context.Segments;
        bool kept = false;
        try
        {
            if (!Add(requests, wanted, apart))
            {
                return new SynopsisTableSet(context, [], pool);
            }

            await source.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            SynopsisTableSet set = Decoded(context, wanted, apart, requests, pool, reads, verified: false);
            kept = true;
            return set;
        }
        catch
        {
            context.Dispose();
            throw;
        }
        finally
        {
            if (!kept)
            {
                requests.Release();
            }
        }
    }

    /// <summary>
    /// The chunks of <paramref name="wanted"/>, read into <paramref name="requests"/>, each decoded into <paramref name="context"/>'s
    /// arena at its table's row type, only the fields <paramref name="reads"/> names of it; a chunk whose region does not hold
    /// its checksum, or does not decode, is left out, and counted (<c>vortex.synopsis_tables.corrupt</c>): its object is read
    /// as rows. The checksums of regions taken where they were held, checked as they were read (<see cref="IHeldRegions.TryRead"/>),
    /// are not reckoned again (<paramref name="verified"/>). A chunk that lays its distinct sketches apart is decoded from its region, and from
    /// theirs when <paramref name="apart"/> says the read takes them, into one struct of the fields read.
    /// </summary>
    private static SynopsisTableSet Decoded(
        ScanContext context, Wanted[] wanted, Apart[]? apart, IO.SegmentRequestSet requests, ScanContexts? pool, ISynopsisReads? reads, bool verified)
    {
        ReadTable[] tables = new ReadTable[wanted.Length];
        int read = 0;
        for (int i = 0; i < wanted.Length; i++)
        {
            (SynopsisTableInfo table, TableChunk chunk, DType row, int slot) = wanted[i];
            Buffers.VortexBuffer bytes = requests.GetBuffer(slot);
            bool sketched = Sketched(apart, i);
            Buffers.VortexBuffer sketches = sketched ? requests.GetBuffer(apart![i].Slot) : default;
            if (!verified && (!chunk.Region.Holds(bytes.Span) || (sketched && !chunk.Distinct!.Value.Holds(sketches.Span))))
            {
                VortexTelemetry.SynopsisTableCorruption();
                continue;
            }

            // A table's rows are a struct of one array node: its decoder reads the fields the projection
            // names and no others, a question of a count reading 3 of a table's 16 columns.
            Layouts.FieldMask fields = reads?.Fields(table) ?? Layouts.FieldMask.All;
            ulong encoded = reads?.Encoded(table) ?? 0;
            try
            {
                // The rows decoded where they lie, in the bytes read, which the set keeps: a copy of them into the arena
                // was a tenth of a question that folds a million rows of runs.
                context.Decode.LoadBlob(bytes);
                ArrayNode root = context.Nodes.Root;
                if (!fields.IsAll)
                {
                    context.ExchangePushedFields(fields);
                    context.FieldsHonoured = false;
                }

                // The keys a group by reads by their codes stay a dictionary's, where their values were gathered at
                // every row and then looked up again, a third of a question that folds a million rows of runs.
                if (encoded != 0)
                {
                    context.ExchangeEncodedFields(encoded);
                }

                int node = context.Decode.DecodeRoot(in root, row, chunk.Rows);
                bool narrowed = !fields.IsAll && context.FieldsHonoured;
                if (chunk.Distinct is not null && table.Distinct.Length > 0)
                {
                    node = Unite(context, node, narrowed, row, fields, sketches, sketched ? apart![i] : default, chunk.Rows);
                    narrowed = !fields.IsAll;
                }

                tables[read++] = new ReadTable(table, chunk, node) { Fields = narrowed ? fields : Layouts.FieldMask.All };
            }
            catch (VortexFormatException)
            {
                VortexTelemetry.SynopsisTableCorruption();
            }
            finally
            {
                // A root no struct decoder read leaves the projection pushed, which no other chunk is to take.
                if (!fields.IsAll)
                {
                    context.ExchangePushedFields(Layouts.FieldMask.All);
                }

                if (encoded != 0)
                {
                    context.ExchangeEncodedFields(0);
                }
            }
        }

        return new SynopsisTableSet(context, read == tables.Length ? tables : tables[..read], pool);
    }

    /// <summary>
    /// The struct of the fields <paramref name="fields"/> names of a chunk that lays its distinct sketches apart, every one
    /// of its row when it names every one: those of the rest of its row, node <paramref name="rest"/> of struct
    /// <paramref name="restRow"/>, which holds them alone when <paramref name="restNarrowed"/>; then those of its sketches,
    /// decoded from <paramref name="bytes"/> when <paramref name="apart"/> says the read takes them.
    /// </summary>
    private static int Unite(
        ScanContext context, int rest, bool restNarrowed, DType restRow, Layouts.FieldMask fields, Buffers.VortexBuffer bytes, in Apart apart, int rows)
    {
        if (apart.Row.IsDefault)
        {
            // The rest of the row alone, the read naming none of the sketches: as decoded when it names every field of it.
            int width = restRow.FieldCount;
            return restNarrowed || fields.SelectedCount(width) == width ? rest : Joined(context, restRow, width, fields, rest, false, -1, false);
        }

        (int sketches, bool sketchesNarrowed) = DecodeSketches(context, bytes, apart, rows);
        return Joined(context, apart.Row, apart.Row.FieldCount - apart.Distinct.FieldCount, fields, rest, restNarrowed, sketches, sketchesNarrowed);
    }

    /// <summary>
    /// Decodes the distinct sketches of a chunk, <paramref name="bytes"/> read of their region, into <paramref name="context"/>'s
    /// arena at <paramref name="apart"/>'s struct, only the fields it names: their node, and whether it holds those alone.
    /// </summary>
    private static (int Node, bool Narrowed) DecodeSketches(ScanContext context, Buffers.VortexBuffer bytes, in Apart apart, int rows)
    {
        context.Decode.LoadBlob(bytes);
        ArrayNode root = context.Nodes.Root;
        if (apart.Fields.IsAll)
        {
            return (context.Decode.DecodeRoot(in root, apart.Distinct, rows), false);
        }

        context.ExchangePushedFields(apart.Fields);
        context.FieldsHonoured = false;
        try
        {
            int node = context.Decode.DecodeRoot(in root, apart.Distinct, rows);
            return (node, context.FieldsHonoured);
        }
        finally
        {
            context.ExchangePushedFields(Layouts.FieldMask.All);
        }
    }

    /// <summary>
    /// The struct of the fields <paramref name="fields"/> names of <paramref name="row"/>, of a chunk that lays its distinct
    /// sketches apart: those before <paramref name="first"/> from node <paramref name="rest"/>, the rest of the row, those from
    /// it on from node <paramref name="sketches"/>, the sketches, -1 when the read takes none; each node holding the fields
    /// named alone when it is narrowed, every one of its struct otherwise. At <paramref name="row"/> itself when every field
    /// is read, at a struct of the fields read in the decoding context's types otherwise, as a projection's
    /// (<c>StructDecoder</c>).
    /// </summary>
    private static int Joined(
        ScanContext context, DType row, int first, Layouts.FieldMask fields, int rest, bool restNarrowed, int sketches, bool sketchesNarrowed)
    {
        CanonicalArena arena = context.Canonical;
        int count = fields.SelectedCount(row.FieldCount);
        using Arrays.Decoders.Canonical.Scratch<int> children = new Arrays.Decoders.Canonical.Scratch<int>(count, stackalloc int[64]);
        int r = 0;
        int d = 0;
        for (int s = 0; s < count; s++)
        {
            int field = fields.SelectedField(s);
            children.Span[s] = field < first
                ? arena.GetNode(rest).GetFieldIndex(restNarrowed ? r++ : field)
                : arena.GetNode(sketches).GetFieldIndex(sketchesNarrowed ? d++ : field - first);
        }

        int length = arena.GetNode(rest).Length;
        Validity validity = arena.GetNode(rest).Validity;
        if (fields.IsAll)
        {
            return arena.AddStruct(row, length, validity, children.Span);
        }

        using Arrays.Decoders.Canonical.Scratch<int> names = new Arrays.Decoders.Canonical.Scratch<int>(count, stackalloc int[64]);
        using Arrays.Decoders.Canonical.Scratch<DType> types = new Arrays.Decoders.Canonical.Scratch<DType>(count, default);
        for (int s = 0; s < count; s++)
        {
            names.Span[s] = context.Types.InternName(row.GetFieldNameUtf8(fields.SelectedField(s)));
            types.Span[s] = DTypeImport.Into(context.Types, arena.GetNode(children.Span[s]).DType);
        }

        return arena.AddStruct(context.Types.Struct(names.Span, types.Span, row.Nullability), length, validity, children.Span);
    }

    /// <summary>A chunk of a table a read fetches: the table, the chunk, the row type its region decodes at, and the slot of its region in the read.</summary>
    private record struct Wanted(SynopsisTableInfo Table, TableChunk Chunk, DType Row)
    {
        internal int Slot { get; set; }

        internal readonly void Deconstruct(out SynopsisTableInfo table, out TableChunk chunk, out DType row, out int slot) =>
            (table, chunk, row, slot) = (Table, Chunk, Row, Slot);
    }

    /// <summary>
    /// The distinct sketches a read fetches of a chunk that lays them apart (<see cref="TableChunk.Distinct"/>): the row type
    /// of the whole chunk, the struct of its sketches, the fields of these the read decodes, and the slot of their region in
    /// the read. A default <see cref="Row"/> where a chunk's are not read.
    /// </summary>
    private record struct Apart(DType Row, DType Distinct, Layouts.FieldMask Fields)
    {
        internal int Slot { get; set; }
    }
}

/// <summary>An object's synopsis tables as read: each table, its rows a node of <see cref="Arena"/>, which disposing lets go.</summary>
internal sealed class SynopsisTableSet : IDisposable
{
    private readonly ScanContext _context;
    private readonly ScanContexts? _pool;
    private int _disposed;

    /// <param name="context">The context the rows were decoded into.</param>
    /// <param name="tables">Each chunk read of a table, and its rows.</param>
    /// <param name="pool">The pool the context goes back to once the set is let go; null to dispose it.</param>
    internal SynopsisTableSet(ScanContext context, IReadOnlyList<ReadTable> tables, ScanContexts? pool = null)
    {
        _context = context;
        _pool = pool;
        Tables = tables;
    }

    /// <summary>Each chunk read of a table, and its rows, a table's chunks in the order of their buckets.</summary>
    internal IReadOnlyList<ReadTable> Tables { get; }

    /// <summary>The arena the rows lie in.</summary>
    internal CanonicalArena Arena => _context.Canonical;

    /// <summary>The arena of the rows' dtypes, where a struct over their columns is built; null when the reader does not say.</summary>
    internal DTypeArena? Types { get; set; }

    /// <summary>Lets the rows go, once, and the bytes they lie in: a context given back twice would be lent to two readers.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _context.Segments.Release();
        if (_pool is { } pool)
        {
            pool.Return(_context);
        }
        else
        {
            _context.Dispose();
        }
    }
}

/// <summary>A chunk of a table as read: the table, the chunk, and its rows, a struct node of the set's arena.</summary>
/// <param name="Table">The table.</param>
/// <param name="Chunk">The chunk read of it.</param>
/// <param name="Node">Its rows.</param>
internal readonly record struct ReadTable(SynopsisTableInfo Table, TableChunk Chunk, int Node)
{
    /// <summary>The fields of the table's row its struct holds, in their order: every one, or those a projection named.</summary>
    internal Layouts.FieldMask Fields { get; init; }
}

/// <summary>What a read of an object's synopsis tables fetches: the chunks it decodes, and the fields of each table's rows it decodes.</summary>
internal interface ISynopsisReads
{
    /// <summary>Whether <paramref name="chunk"/> of <paramref name="table"/> is read.</summary>
    bool Reads(SynopsisTableInfo table, TableChunk chunk);

    /// <summary>Whether a chunk of <paramref name="table"/> may be read: the chunks of a table it is not are not looked at, nor parsed.</summary>
    bool ReadsTable(SynopsisTableInfo table);

    /// <summary>
    /// The lists of the chunks of the tables it reads are loaded (<see cref="SynopsisTableReader.LoadListsAsync"/>): what it
    /// decides of their chunks before <see cref="Reads"/> is asked, it decides now. Called again at every load.
    /// </summary>
    void Loaded(SynopsisContents contents);

    /// <summary>The fields of <paramref name="table"/>'s rows decoded: <see cref="Layouts.FieldMask.All"/> for every one.</summary>
    Layouts.FieldMask Fields(SynopsisTableInfo table);

    /// <summary>
    /// The fields of <paramref name="table"/>'s rows, a bit each below 64, whose columns may stay a dictionary or run-end
    /// node, which the reader reads encoded (<see cref="Arrays.ScanContext.EncodedFields"/>); 0 for every column canonical.
    /// </summary>
    ulong Encoded(SynopsisTableInfo table);
}

/// <summary>
/// The regions of objects held at hand, which a read takes with no reader of their object, no wait and no request: what a
/// warm question reads an object's synopsis tables from when every region it wants is there
/// (<see cref="SynopsisTableReader.ReadHeld"/>). The session's synopsis cache holds them on the disk.
/// </summary>
internal interface IHeldRegions
{
    /// <summary>Whether region <paramref name="region"/> of object <paramref name="uid"/> is held, without reading it.</summary>
    bool Holds(UInt128 uid, IndexSegment region);

    /// <summary>Region <paramref name="region"/> of object <paramref name="uid"/>, checked against its checksum; false when it is not held.</summary>
    bool TryRead(UInt128 uid, IndexSegment region, out Buffers.SegmentOwner owner);

    /// <summary>Told of each region of object <paramref name="uid"/> a read asks for, held or not, before it reads it.</summary>
    void Ask(UInt128 uid, IndexSegment region);
}
