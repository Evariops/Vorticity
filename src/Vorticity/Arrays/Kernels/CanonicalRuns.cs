using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
using Vorticity.Layouts;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>
/// Rows taken run after run from several nodes of one dtype, a merge's runs where they lie in its inputs' batches,
/// gathered into one node that owns its bytes: each column in one pass over the runs.
/// </summary>
/// <remarks>
/// <para>
/// The node is the one the same rows give when each source's rows are sliced, the slices concatenated, the runs' rows
/// gathered from them in order and the result copied with its repeats shared
/// (<see cref="CanonicalSlice"/>, <see cref="CanonicalConcat"/>, <c>CanonicalFilter</c>,
/// <see cref="CanonicalArena.CopyFrom"/>): the same kinds, the same validities, the same views, a text that repeats the
/// one copied last viewing its bytes. That way copied every fixed value and every view three times over, and a
/// consolidation of 74 appends of metric records, whose blocks take 240 runs of 80 batches each, spent 16.6 % of its
/// processor assembling them (2026-11-18).
/// </para>
/// <para>
/// A column a source holds as a dictionary or as runs is decoded over the rows the runs span of that source, into the
/// arena gathered into; a constant is read as its element. A source's arena is read and never written. What a source
/// holds of a column, its buffers and where they start, is read once a column and kept beside the others': read at each
/// run, the records of eighty arenas were what a run cost, more than its rows.
/// </para>
/// </remarks>
internal static class CanonicalRuns
{
    private const int ViewSize = CanonicalSupport.ViewSize;
    private const int ViewWords = ViewSize / sizeof(uint);
    private const uint MaxInline = CanonicalSupport.MaxInlineViewLength;
    private const int Align = CanonicalSupport.MaxRequiredAlignment;

    /// <summary>Bytes past the values a text's gather may write over, a block of its copy, as <see cref="CanonicalArena.CopyFrom"/> leaves.</summary>
    private const int GatherSlack = 32;

    private const int StackSources = 64;
    private const int StackFields = 32;

    /// <summary>A node rows are taken from: its arena, the node, and the rows the runs span of it, which a column it holds encoded is decoded over.</summary>
    internal interface ISource
    {
        /// <summary>The arena that holds the node, read and never written.</summary>
        CanonicalArena Arena { get; }

        /// <summary>The node, of the dtype gathered.</summary>
        int Node { get; }

        /// <summary>The first row a run takes of the node.</summary>
        int First { get; }

        /// <summary>The row past the last a run takes of the node.</summary>
        int End { get; }
    }

    /// <summary>Rows <see cref="Row"/> to <see cref="Row"/> + <see cref="Rows"/> of source <see cref="Source"/>, the next rows of the node gathered.</summary>
    internal readonly record struct Run(int Source, int Row, int Rows);

    /// <summary>
    /// Whether <see cref="Gather"/> takes nodes of <paramref name="dtype"/>: booleans, numbers, texts, structs, variants and
    /// extensions of those. A list shares its elements between rows and a decimal may widen from one source to the next,
    /// which the gather by slices handles.
    /// </summary>
    internal static bool Takes(DType dtype)
    {
        switch (dtype.Kind)
        {
            case DTypeKind.Null:
            case DTypeKind.Bool:
            case DTypeKind.Primitive:
            case DTypeKind.Utf8:
            case DTypeKind.Binary:
            case DTypeKind.Variant:
                return true;

            case DTypeKind.Struct:
                for (int field = 0; field < dtype.FieldCount; field++)
                {
                    if (!Takes(dtype.GetField(field)))
                    {
                        return false;
                    }
                }

                return true;

            case DTypeKind.Extension:
                return Takes(dtype.StorageType);

            default:
                return false;
        }
    }

    /// <summary>
    /// The <paramref name="rows"/> rows the <paramref name="runs"/> name of the <paramref name="sources"/>, in the runs'
    /// order, as one node of <paramref name="dtype"/> in <paramref name="context"/>'s arena, its bytes its own.
    /// </summary>
    /// <param name="context">The context whose arena takes the node.</param>
    /// <param name="dtype">The dtype of every source's node, which <see cref="Takes"/> holds for.</param>
    /// <param name="sources">The nodes the runs take rows of, in the order their first run comes.</param>
    /// <param name="runs">The runs, each of rows within its source's span.</param>
    /// <param name="rows">The rows of the runs together.</param>
    /// <returns>The node, or -1 when a source holds a form the gather does not take, nothing then to keep of what it laid.</returns>
    /// <exception cref="VortexFormatException">The sources disagree in shape, or a view names bytes its node does not hold.</exception>
    internal static int Gather<TSource>(
        ArrayDecodeContext context, DType dtype, ReadOnlySpan<TSource> sources, ReadOnlySpan<Run> runs, int rows)
        where TSource : struct, ISource
    {
        Span<At> stack = stackalloc At[StackSources];
        Scratch<At> scratch = new Scratch<At>(sources.Length, stack);
        try
        {
            Span<At> at = scratch.Span;
            for (int s = 0; s < at.Length; s++)
            {
                at[s] = new At(sources[s].Node, 0, false);
            }

            return Node(new Pass<TSource>(context, sources, runs, rows), dtype, at, depth: 1);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    /// <summary>
    /// Where a source's rows of the column under way lie: a node of the source's arena, or, once decoded, of the arena
    /// gathered into (<see cref="Local"/>), whose row 0 is the source's row <see cref="Shift"/>.
    /// </summary>
    private readonly record struct At(int Node, int Shift, bool Local);

    /// <summary>A gather under way: where it gathers into, its sources, its runs and their rows.</summary>
    private readonly ref struct Pass<TSource>(ArrayDecodeContext context, ReadOnlySpan<TSource> sources, ReadOnlySpan<Run> runs, int rows)
        where TSource : struct, ISource
    {
        internal readonly ArrayDecodeContext Context = context;
        internal readonly CanonicalArena Into = context.Canonical;
        internal readonly ReadOnlySpan<TSource> Sources = sources;
        internal readonly ReadOnlySpan<Run> Runs = runs;
        internal readonly int Rows = rows;

        /// <summary>The arena that holds what source <paramref name="source"/> holds of the column under way.</summary>
        internal CanonicalArena ArenaOf(int source, bool local) => local ? Into : Sources[source].Arena;

        /// <summary>The node source <paramref name="source"/> holds the column under way at.</summary>
        internal CanonicalNode NodeOf(int source, At at) => ArenaOf(source, at.Local).GetNode(at.Node);

        /// <summary>The column of source <paramref name="source"/>, held encoded, decoded over the rows the runs span of the source.</summary>
        internal At Decoded(int source, At at)
        {
            TSource from = Sources[source];
            int slice = CanonicalSlice.SliceAcross(at.Local ? Into : from.Arena, Into, at.Node, from.First - at.Shift, from.End - from.First);
            return new At(Into.DecodedTree(slice), from.First, true);
        }
    }

    private static int Node<TSource>(in Pass<TSource> pass, DType dtype, Span<At> at, int depth)
        where TSource : struct, ISource
    {
        VortexLimits.CheckDepth(depth, VortexLimits.MaxArrayDepth, "Canonical runs");

        // The kind every source holds the column as, a constant aside, which takes the kind of the others.
        CanonicalKind kind = CanonicalKind.Constant;
        int first = -1;
        for (int s = 0; s < at.Length; s++)
        {
            CanonicalKind held = pass.NodeOf(s, at[s]).Kind;
            if (held is CanonicalKind.Dictionary or CanonicalKind.RunEnd)
            {
                at[s] = pass.Decoded(s, at[s]);
                held = pass.NodeOf(s, at[s]).Kind;
            }

            if (held == CanonicalKind.Constant)
            {
                continue;
            }

            if (first < 0)
            {
                kind = held;
                first = s;
            }
            else if (held != kind)
            {
                ArraysThrow.Format(
                    $"Source {first} canonicalizes to {kind} but source {s} to {held}; the sources of a gather all share its dtype and cannot disagree.");
            }
        }

        switch (kind)
        {
            case CanonicalKind.Constant:
                return Constants(in pass, dtype, at);
            case CanonicalKind.Null:
                return pass.Into.AddNull(dtype, pass.Rows);
            case CanonicalKind.Bool:
                return Bool(in pass, dtype, at);
            case CanonicalKind.Primitive:
                return Primitive(in pass, dtype, at);
            case CanonicalKind.VarBinView:
                return Views(in pass, dtype, at);
            case CanonicalKind.Struct:
                return Struct(in pass, dtype, at, depth);
            case CanonicalKind.Extension:
                return Extension(in pass, dtype, at, depth);
            default:
                return -1;
        }
    }

    /// <summary>Every source holds the column as a constant: the constant when they hold the same, each one's rows otherwise.</summary>
    private static int Constants<TSource>(in Pass<TSource> pass, DType dtype, ReadOnlySpan<At> at)
        where TSource : struct, ISource
    {
        ReadOnlySpan<byte> element = pass.NodeOf(0, at[0]).ConstantElement;
        bool same = true;
        for (int s = 1; s < at.Length && same; s++)
        {
            same = element.SequenceEqual(pass.NodeOf(s, at[s]).ConstantElement);
        }

        if (same)
        {
            return pass.Into.AddConstant(dtype, pass.Rows, ValidityOf(in pass, dtype, at), element);
        }

        return dtype.Kind switch
        {
            DTypeKind.Utf8 or DTypeKind.Binary => Views(in pass, dtype, at),
            DTypeKind.Primitive => Primitive(in pass, dtype, at),
            _ => -1,
        };
    }

    /// <summary>
    /// The validity of the rows gathered: the dtype's when every source is valid throughout, as a concatenation of the
    /// sources says it; otherwise the rows' bits, or every row valid or none when the rows taken are so, as a gather of
    /// rows says it.
    /// </summary>
    private static Validity ValidityOf<TSource>(in Pass<TSource> pass, DType dtype, ReadOnlySpan<At> at)
        where TSource : struct, ISource
    {
        bool every = true;
        for (int s = 0; s < at.Length && every; s++)
        {
            every = pass.NodeOf(s, at[s]).Validity.IsAllValid;
        }

        if (every)
        {
            return Validity.FromNullability(dtype.Nullability);
        }

        CanonicalArena into = pass.Into;
        int rows = pass.Rows;
        VortexBuffer bits = into.Allocate(Math.Max(CanonicalSupport.BitmapByteCount(rows), 1), 1, out Span<byte> writable);
        int position = 0;
        foreach (Run run in pass.Runs)
        {
            At from = at[run.Source];
            CanonicalNode node = pass.NodeOf(run.Source, from);
            Validity validity = node.Validity;
            if (validity.IsAllValid)
            {
                CanonicalSupport.SetBits(writable, position, run.Rows);
            }
            else if (validity.Kind == ValidityKind.Bitmap)
            {
                CanonicalNode bitmap = node.Arena.GetNode(validity.CanonicalNodeIndex);
                CanonicalSupport.CopyBits(bitmap.Bits.Span, bitmap.BitOffset + run.Row - from.Shift, writable, position, run.Rows);
            }

            position += run.Rows;
        }

        int valid = BitmapKernels.CountSet(writable, 0, rows);
        if (valid == rows)
        {
            return Validity.AllValid;
        }

        if (valid == 0)
        {
            return Validity.AllInvalid;
        }

        return Validity.Bitmap(into.AddBool(pass.Context.Types.Bool(Nullability.NonNullable), rows, Validity.NonNullable, bits, 0));
    }

    private static int Bool<TSource>(in Pass<TSource> pass, DType dtype, ReadOnlySpan<At> at)
        where TSource : struct, ISource
    {
        Validity validity = ValidityOf(in pass, dtype, at);
        int rows = pass.Rows;
        VortexBuffer bits = pass.Into.Allocate(Math.Max(CanonicalSupport.BitmapByteCount(rows), 1), 1, out Span<byte> writable);
        int position = 0;
        foreach (Run run in pass.Runs)
        {
            At from = at[run.Source];
            CanonicalNode node = pass.NodeOf(run.Source, from);
            CanonicalSupport.CopyBits(node.Bits.Span, node.BitOffset + run.Row - from.Shift, writable, position, run.Rows);
            position += run.Rows;
        }

        return pass.Into.AddBool(dtype, rows, validity, bits, 0);
    }

    /// <summary>What a source holds of a column of fixed-width values, read once a column: its values, or a constant's element, and the source's row its row 0 is.</summary>
    private struct FixedSource
    {
        internal VortexBuffer Values;
        internal int Shift;
        internal bool Constant;
    }

    private static int Primitive<TSource>(in Pass<TSource> pass, DType dtype, ReadOnlySpan<At> at)
        where TSource : struct, ISource
    {
        Span<FixedSource> stack = stackalloc FixedSource[StackSources];
        Scratch<FixedSource> scratch = new Scratch<FixedSource>(at.Length, stack);
        try
        {
            Span<FixedSource> sources = scratch.Span;
            PType ptype = dtype.PType;
            int held = -1;
            for (int s = 0; s < at.Length; s++)
            {
                CanonicalNode node = pass.NodeOf(s, at[s]);
                ref readonly CanonicalRecord record = ref node.Arena.RecordRef(node.Index);
                if (record.Kind == CanonicalKind.Constant)
                {
                    sources[s] = new FixedSource { Values = record.BufferA.Slice(0, (int)record.FixedSize), Shift = at[s].Shift, Constant = true };
                    continue;
                }

                if (held < 0)
                {
                    ptype = record.PType;
                    held = s;
                }
                else if (record.PType != ptype)
                {
                    ArraysThrow.Format($"Source {s} holds {record.PType.Name()} values where source {held} holds {ptype.Name()}.");
                }

                sources[s] = new FixedSource { Values = record.BufferA, Shift = at[s].Shift };
            }

            int width = ptype.ByteWidth();
            for (int s = 0; s < sources.Length; s++)
            {
                if (sources[s].Constant && sources[s].Values.Length != width)
                {
                    ArraysThrow.Format($"A constant of {sources[s].Values.Length} bytes stands for {ptype.Name()} values.");
                }
            }

            Validity validity = ValidityOf(in pass, dtype, at);
            int bytes = ArrayDecodeContext.CheckedMultiply(pass.Rows, width, "gathered values");
            if (bytes == 0)
            {
                return pass.Into.AddPrimitive(dtype, pass.Rows, validity, ptype, VortexBuffer.Empty);
            }

            // Uninitialized: the runs tile the rows, each writing its own.
            VortexBuffer values = pass.Into.AllocateUninitialized(bytes, Align, out Span<byte> writable);
            int offset = 0;
            foreach (Run run in pass.Runs)
            {
                ref readonly FixedSource from = ref sources[run.Source];
                int count = run.Rows * width;
                if (from.Constant)
                {
                    RowKernels.Tile(writable.Slice(offset, count), from.Values.Span);
                }
                else
                {
                    from.Values.Span.Slice((run.Row - from.Shift) * width, count).CopyTo(writable[offset..]);
                }

                offset += count;
            }

            System.Threading.Interlocked.Add(ref CanonicalArena.BytesMaterialized, bytes);
            return pass.Into.AddPrimitive(dtype, pass.Rows, validity, ptype, values);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    private static int Struct<TSource>(in Pass<TSource> pass, DType dtype, ReadOnlySpan<At> at, int depth)
        where TSource : struct, ISource
    {
        // A variant dtype names no fields: its canonical form is a struct of the rows' metadata and values, whose count and
        // dtypes the nodes carry, as a concatenation reads them.
        bool variant = dtype.Kind == DTypeKind.Variant;
        int fieldCount = variant ? pass.NodeOf(0, at[0]).FieldCount : dtype.FieldCount;
        for (int s = 0; s < at.Length; s++)
        {
            int actual = pass.NodeOf(s, at[s]).FieldCount;
            if (actual != fieldCount)
            {
                ArraysThrow.Format($"Source {s} has {actual} fields where the dtype declares {fieldCount}.");
            }
        }

        Validity validity = ValidityOf(in pass, dtype, at);
        Span<int> fieldStack = stackalloc int[StackFields];
        Span<At> childStack = stackalloc At[StackSources];
        Scratch<int> fields = new Scratch<int>(fieldCount, fieldStack);
        Scratch<At> children = new Scratch<At>(at.Length, childStack);
        try
        {
            Span<int> results = fields.Span;
            Span<At> child = children.Span;
            for (int field = 0; field < fieldCount; field++)
            {
                for (int s = 0; s < at.Length; s++)
                {
                    At from = at[s];
                    child[s] = new At(pass.NodeOf(s, from).GetFieldIndex(field), from.Shift, from.Local);
                }

                // Read before the recursion: it adds records, and the arena may move them.
                DType fieldType = variant ? pass.NodeOf(0, child[0]).DType : dtype.GetField(field);
                results[field] = Node(in pass, fieldType, child, depth + 1);
                if (results[field] < 0)
                {
                    return -1;
                }
            }

            return pass.Into.AddStruct(dtype, pass.Rows, validity, results);
        }
        finally
        {
            children.Dispose();
            fields.Dispose();
        }
    }

    private static int Extension<TSource>(in Pass<TSource> pass, DType dtype, ReadOnlySpan<At> at, int depth)
        where TSource : struct, ISource
    {
        Span<At> childStack = stackalloc At[StackSources];
        Scratch<At> children = new Scratch<At>(at.Length, childStack);
        try
        {
            Span<At> child = children.Span;
            for (int s = 0; s < at.Length; s++)
            {
                At from = at[s];
                child[s] = new At(pass.NodeOf(s, from).StorageIndex, from.Shift, from.Local);
            }

            int storage = Node(in pass, dtype.StorageType, child, depth + 1);
            return storage < 0 ? -1 : pass.Into.AddExtension(dtype, pass.Rows, storage);
        }
        finally
        {
            children.Dispose();
        }
    }

    /// <summary>
    /// What a source holds of a column of texts, read once a column: its views, or a constant's element, the bits of its
    /// validity when it has some, the source's row its row 0 is, and where its data buffers lie in its arena.
    /// </summary>
    private struct TextSource
    {
        internal const byte Valid = 0;
        internal const byte Masked = 1;
        internal const byte Invalid = 2;

        internal VortexBuffer Views;
        internal VortexBuffer Bits;
        internal int BitBase;
        internal int Shift;
        internal int DataStart;
        internal int DataCount;
        internal byte Validity;
        internal bool Constant;
        internal bool Local;

        /// <summary>Whether the source's row <paramref name="row"/> is valid, of a source that has bits.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly bool IsValid(int row)
        {
            int at = BitBase + row;
            return ((Bits.Span[at >> 3] >> (at & 7)) & 1) != 0;
        }

        /// <summary>Whether one of the <paramref name="count"/> rows from the source's row <paramref name="row"/> on is valid.</summary>
        internal readonly bool AnyValid(int row, int count)
        {
            if (Validity != Masked)
            {
                return Validity == Valid;
            }

            return BitmapKernels.CountSet(Bits.Span, BitBase + row, count) > 0;
        }
    }

    /// <summary>
    /// A column of texts: the runs' views in order, a null row's empty, and one data buffer of the bytes they name, a text
    /// that names the bytes the one before names, or equal to the one copied last, viewing that copy.
    /// </summary>
    /// <remarks>
    /// Two texts name the same bytes when they are of one source, one of its data buffers and one offset in it, as a
    /// concatenation's rebased views tell it. A constant's rows name its element.
    /// </remarks>
    private static int Views<TSource>(in Pass<TSource> pass, DType dtype, ReadOnlySpan<At> at)
        where TSource : struct, ISource
    {
        Validity validity = ValidityOf(in pass, dtype, at);

        // A stretch a row at most.
        Span<TextSource> sourceStack = stackalloc TextSource[StackSources];
        Span<int> stretchStack = stackalloc int[StackSources];
        Scratch<TextSource> held = new Scratch<TextSource>(at.Length, sourceStack);
        Scratch<int> stretches = new Scratch<int>(pass.Rows, stretchStack);
        try
        {
            // Padded where every source's views are: a constant's rows are written as a decoder writes them, unmarked.
            Span<TextSource> texts = held.Span;
            bool padded = true;
            for (int s = 0; s < at.Length; s++)
            {
                CanonicalNode node = pass.NodeOf(s, at[s]);
                ref readonly CanonicalRecord record = ref node.Arena.RecordRef(node.Index);
                TextSource text = default;
                text.Shift = at[s].Shift;
                text.Local = at[s].Local;
                Validity own = record.Validity;
                if (own.Kind == ValidityKind.Bitmap)
                {
                    CanonicalNode bitmap = node.Arena.GetNode(own.CanonicalNodeIndex);
                    text.Bits = bitmap.Bits;
                    text.BitBase = bitmap.BitOffset - at[s].Shift;
                    text.Validity = TextSource.Masked;
                }
                else
                {
                    text.Validity = own.IsAllValid ? TextSource.Valid : TextSource.Invalid;
                }

                if (record.Kind == CanonicalKind.Constant)
                {
                    text.Views = record.BufferA.Slice(0, (int)record.FixedSize);
                    text.Constant = true;
                    padded = false;
                }
                else
                {
                    text.Views = record.BufferA;
                    text.DataStart = record.DataBufferStart;
                    text.DataCount = record.DataBufferCount;
                    padded &= record.PaddedViews;
                }

                texts[s] = text;
            }

            return Views(in pass, dtype, validity, padded, texts, stretches.Span);
        }
        finally
        {
            stretches.Dispose();
            held.Dispose();
        }
    }

    /// <summary>
    /// <see cref="Views{TSource}(in Pass{TSource}, DType, ReadOnlySpan{At})"/> over what each source holds of the column
    /// (<paramref name="texts"/>): the stretches of equal views of the sources valid throughout are noted in
    /// <paramref name="stretches"/> as the bytes are sized, then read as the views are laid.
    /// </summary>
    private static int Views<TSource>(
        in Pass<TSource> pass, DType dtype, Validity validity, bool padded, ReadOnlySpan<TextSource> texts, Span<int> stretches)
        where TSource : struct, ISource
    {
        CanonicalArena into = pass.Into;
        int rows = pass.Rows;

        // Sized before anything is written: the bytes the rows name, a text that names the bytes the one before names
        // counted once, so that the gather below writes into a buffer it never grows.
        long referenced = 0;
        int noted = 0;
        Named last = default;
        foreach (Run run in pass.Runs)
        {
            ref readonly TextSource text = ref texts[run.Source];
            if (text.Validity == TextSource.Invalid)
            {
                continue;
            }

            if (text.Constant)
            {
                uint length = (uint)text.Views.Length;
                Named named = new Named(run.Source, uint.MaxValue, 0, length);
                if (length > MaxInline && !named.Is(in last) && text.AnyValid(run.Row, run.Rows))
                {
                    referenced += length;
                    last = named;
                }

                continue;
            }

            int row = run.Row - text.Shift;
            if (text.Validity == TextSource.Valid)
            {
                // A stretch of rows of one view is one text: its first row tells what it names.
                ReadOnlySpan<Int128> source = MemoryMarshal.Cast<byte, Int128>(text.Views.Span).Slice(row, run.Rows);
                for (int j = 0; j < source.Length;)
                {
                    Int128 head = source[j];
                    int end = StretchEnd(source, j);
                    stretches[noted++] = end - j;
                    j = end;
                    uint size = (uint)Low(head);
                    if (size > MaxInline)
                    {
                        Named named = new Named(run.Source, (uint)High(head), (uint)(High(head) >> 32), size);
                        if (!named.Is(in last))
                        {
                            referenced += size;
                            last = named;
                        }
                    }
                }

                continue;
            }

            ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(text.Views.Span).Slice(row * ViewWords, run.Rows * ViewWords);
            for (int j = 0; j < run.Rows; j++)
            {
                int w = j * ViewWords;
                uint size = words[w];
                if (size <= MaxInline || !text.IsValid(run.Row + j))
                {
                    continue;
                }

                Named named = new Named(run.Source, words[w + 2], words[w + 3], size);
                if (!named.Is(in last))
                {
                    referenced += size;
                    last = named;
                }
            }
        }

        int viewBytes = ArrayDecodeContext.CheckedMultiply(rows, ViewSize, "gathered views");
        if (viewBytes == 0)
        {
            return into.AddVarBinView(dtype, rows, validity, VortexBuffer.Empty, default, padded);
        }

        VortexBuffer views = into.AllocateUninitialized(viewBytes, ViewSize, out Span<byte> writable);
        Span<Int128> laid = MemoryMarshal.Cast<byte, Int128>(writable);

        // The gather copies whole blocks and writes past a value into the room the next one takes, so the buffer has a
        // block of slack past the bytes it keeps.
        Span<byte> heap = default;
        VortexBuffer data = referenced == 0
            ? VortexBuffer.Empty
            : into.AllocateUninitialized(checked((int)referenced + GatherSlack), 1, out heap).Slice(0, (int)referenced);
        bool wide = Vector256.IsHardwareAccelerated;

        // The text copied last, which one equal to it views: what it named, its first bytes, and where its copy lies.
        int filled = 0;
        last = default;
        uint lastPrefix = 0;
        int lastAt = 0;
        int position = 0;
        int read = 0;
        foreach (Run run in pass.Runs)
        {
            ref readonly TextSource text = ref texts[run.Source];
            Span<Int128> target = laid.Slice(position, run.Rows);
            position += run.Rows;
            if (text.Validity == TextSource.Invalid)
            {
                target.Clear();
                continue;
            }

            if (text.Constant)
            {
                // A constant's rows: its element's view at every valid row, the element placed once, by the run's first
                // valid row, as the rows of the expanded constant are.
                if (!text.AnyValid(run.Row, run.Rows))
                {
                    target.Clear();
                    continue;
                }

                ReadOnlySpan<byte> element = text.Views.Span;
                Int128 view = default;
                if (element.Length <= MaxInline)
                {
                    CanonicalSupport.WriteView(MemoryMarshal.AsBytes(new Span<Int128>(ref view)), element, element.Length, 0, 0);
                }
                else
                {
                    uint size = (uint)element.Length;
                    uint prefix = MemoryMarshal.Read<uint>(element);
                    Named named = new Named(run.Source, uint.MaxValue, 0, size);
                    int placed = named.Is(in last)
                        ? lastAt
                        : Placed(heap, ref filled, ref last, ref lastPrefix, ref lastAt, in named, prefix, element, wide);
                    view = new Int128((ulong)(uint)placed << 32, ((ulong)prefix << 32) | size);
                }

                target.Fill(view);
                for (int j = 0; text.Validity == TextSource.Masked && j < run.Rows; j++)
                {
                    if (!text.IsValid(run.Row + j))
                    {
                        target[j] = default;
                    }
                }

                continue;
            }

            // The data buffer the last text read lay in, resolved once a buffer rather than once a text.
            int row = run.Row - text.Shift;
            ReadOnlySpan<Int128> source = MemoryMarshal.Cast<byte, Int128>(text.Views.Span).Slice(row, run.Rows);
            uint held = uint.MaxValue;
            ReadOnlySpan<byte> bytes = default;
            bool masked = text.Validity == TextSource.Masked;
            for (int j = 0; j < source.Length;)
            {
                // A stretch of rows of one view takes one view, its first row's, placed, then laid over the stretch at once: a
                // series' rows view its attributes row after row, and read a row at a time, their texts took a tenth of a
                // consolidation of metric records' processor (2026-11-18). A row of a source that has nulls is a stretch of
                // its own, a null one's view empty: its stored view was never validated.
                int end = masked ? j + 1 : j + stretches[read++];
                Int128 head = source[j];
                if (masked && !text.IsValid(run.Row + j))
                {
                    target[j] = default;
                    j = end;
                    continue;
                }

                ulong low = Low(head);
                uint size = (uint)low;
                if (size > MaxInline)
                {
                    ulong high = High(head);
                    uint index = (uint)high;
                    uint offset = (uint)(high >> 32);
                    Named named = new Named(run.Source, index, offset, size);
                    int placed = lastAt;
                    if (!named.Is(in last))
                    {
                        if (index != held)
                        {
                            if (index >= (uint)text.DataCount)
                            {
                                NoBuffer(row + j, run.Source, index, text.DataCount);
                            }

                            bytes = pass.ArenaOf(run.Source, text.Local).DataBufferAt(text.DataStart + (int)index).Span;
                            held = index;
                        }

                        if ((ulong)offset + size > (ulong)bytes.Length)
                        {
                            PastBuffer(row + j, run.Source, offset, size, bytes.Length);
                        }

                        placed = Placed(heap, ref filled, ref last, ref lastPrefix, ref lastAt, in named, (uint)(low >> 32), bytes, wide);
                    }

                    head = new Int128((ulong)(uint)placed << 32, low);
                }

                if (end - j == 1)
                {
                    target[j] = head;
                }
                else
                {
                    target.Slice(j, end - j).Fill(head);
                }

                j = end;
            }
        }

        // The texts found equal by their bytes left room the sizing counted.
        if (filled < referenced)
        {
            data = data.Slice(0, filled);
        }

        System.Threading.Interlocked.Add(ref CanonicalArena.BytesMaterialized, viewBytes + filled);
        return filled > 0
            ? into.AddVarBinView(dtype, rows, validity, views, [data], padded)
            : into.AddVarBinView(dtype, rows, validity, views, default, padded);
    }

    private static void NoBuffer(int row, int source, uint index, int count) =>
        ArraysThrow.Format($"Row {row} of source {source} references data buffer {index}; the node has {count}.");

    private static void PastBuffer(int row, int source, uint offset, uint size, int length) =>
        ArraysThrow.Format($"Row {row} of source {source} names bytes {offset}..{(ulong)offset + size} of a {length}-byte buffer.");

    /// <summary>
    /// Where in <paramref name="heap"/> the text <paramref name="named"/> lies, one that does not name the bytes the text
    /// before it names: the copy of the text copied last when it equals it, byte for byte, or a copy of its own, made now.
    /// Its bytes are <paramref name="bytes"/> from its offset on.
    /// </summary>
    private static int Placed(
        Span<byte> heap, ref int filled, ref Named last, ref uint lastPrefix, ref int lastAt, in Named named, uint prefix, ReadOnlySpan<byte> bytes, bool wide)
    {
        int size = (int)named.Size;
        int offset = (int)named.Offset;
        if (named.Size == last.Size && prefix == lastPrefix && bytes.Slice(offset, size).SequenceEqual(heap.Slice(lastAt, size)))
        {
            last = named;
            return lastAt;
        }

        last = named;
        lastPrefix = prefix;
        lastAt = filled;

        // A value a block of its buffer's bytes can be read past is copied in whole blocks, no call; one near its buffer's
        // end takes the exact copy.
        if ((ulong)named.Offset + named.Size + GatherSlack <= (ulong)bytes.Length)
        {
            ref byte source0 = ref Unsafe.Add(ref MemoryMarshal.GetReference(bytes), (nint)offset);
            ref byte target0 = ref Unsafe.Add(ref MemoryMarshal.GetReference(heap), filled);
            if (wide)
            {
                for (int k = 0; k < size; k += 32)
                {
                    Vector256.LoadUnsafe(ref source0, (nuint)k).StoreUnsafe(ref target0, (nuint)k);
                }
            }
            else
            {
                for (int k = 0; k < size; k += 16)
                {
                    Vector128.LoadUnsafe(ref source0, (nuint)k).StoreUnsafe(ref target0, (nuint)k);
                }
            }
        }
        else
        {
            bytes.Slice(offset, size).CopyTo(heap[filled..]);
        }

        filled += size;
        return lastAt;
    }

    /// <summary>
    /// The row past the stretch of views equal to the one at <paramref name="from"/>: the next row when it differs, as a
    /// column whose rows each hold their own bytes has it; otherwise four views passed at a time while each equals the one
    /// before it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int StretchEnd(ReadOnlySpan<Int128> views, int from)
    {
        Int128 head = views[from];
        int end = from + 1;
        if (end == views.Length || views[end] != head)
        {
            return end;
        }

        ref byte first = ref Unsafe.As<Int128, byte>(ref MemoryMarshal.GetReference(views));
        end++;
        while (end + 4 <= views.Length)
        {
            // The view before the four and the four: five loads and a test.
            nuint at = (nuint)(end - 1) * ViewSize;
            Vector128<byte> a = Vector128.LoadUnsafe(ref first, at);
            Vector128<byte> b = Vector128.LoadUnsafe(ref first, at + ViewSize);
            Vector128<byte> c = Vector128.LoadUnsafe(ref first, at + (2 * ViewSize));
            Vector128<byte> d = Vector128.LoadUnsafe(ref first, at + (3 * ViewSize));
            Vector128<byte> e = Vector128.LoadUnsafe(ref first, at + (4 * ViewSize));
            if (((a ^ b) | (b ^ c) | (c ^ d) | (d ^ e)) != Vector128<byte>.Zero)
            {
                break;
            }

            end += 4;
        }

        while (end < views.Length && views[end] == head)
        {
            end++;
        }

        return end;
    }

    /// <summary>A view's first eight bytes, its length then its first bytes; and its last eight, its data buffer then its offset there.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Low(Int128 view) => (ulong)view;

    /// <inheritdoc cref="Low"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong High(Int128 view) => (ulong)(view >> 64);

    /// <summary>The bytes a text names: its source, the data buffer of its node, its offset there and its length.</summary>
    private readonly struct Named(int source, uint buffer, uint offset, uint size)
    {
        internal readonly int Source = source;
        internal readonly uint Buffer = buffer;
        internal readonly uint Offset = offset;
        internal readonly uint Size = size;

        /// <summary>Whether <paramref name="other"/> names the same bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool Is(in Named other) => Size == other.Size && Offset == other.Offset && Buffer == other.Buffer && Source == other.Source;
    }
}
