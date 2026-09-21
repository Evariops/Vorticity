using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Editions;

namespace Vorticity;

public sealed partial class VortexFile
{
    private VortexSchema? _publicSchema;
    private FileScanSource? _scanSource;
    private VortexFileStatistics? _publicStatistics;
    private VortexMetadata? _metadata;
    private VortexEdition? _edition;

    /// <summary>The file's columns.</summary>
    public VortexSchema Schema => _publicSchema ??= VortexTypes.SchemaOf(DType);

    /// <summary>The file's length in bytes.</summary>
    public long Length => FileLength;

    /// <summary>The identity of this version of the file's bytes, drawn by every write and append; empty for a file another writer produced.</summary>
    public Guid Identity => StoredIdentity ?? Guid.Empty;

    /// <summary>The oldest edition that contains every component the file declares: the readers that can open it.</summary>
    public VortexEdition Edition => _edition ??= ComputeEdition();

    /// <summary>The file's statistics, per top-level column; empty when the file carries none.</summary>
    public VortexFileStatistics Statistics => _publicStatistics ??= new VortexFileStatistics(this);

    /// <summary>The user metadata the file carries, by key.</summary>
    public VortexMetadata Metadata => _metadata ??= new VortexMetadata(this);

    internal FileScanSource ScanSource => _scanSource ??= new FileScanSource(this);

    /// <summary>A scan of the file typed by <typeparamref name="TRecord"/>, whose members are the columns read.</summary>
    /// <typeparam name="TRecord">The record; its members bind to columns by name.</typeparam>
    /// <returns>A fresh scan.</returns>
    public Scan<TRecord> Scan<TRecord>()
        where TRecord : IVortexRecord<TRecord> => new Scan<TRecord>(ScanSource);

    /// <summary>A scan of the file for a caller without a record type: columns by name, filters as text.</summary>
    /// <param name="columns">The columns to read, by top-level name or <c>.</c>-separated path; none reads every column.</param>
    /// <returns>A fresh scan.</returns>
    public Scan Scan(params ReadOnlySpan<string> columns) => new Scan(ScanSource, columns);

    /// <summary>
    /// Whether the file may hold a row <paramref name="predicate"/> is true for, from its statistics
    /// alone: false is a proof, true is not.
    /// </summary>
    /// <typeparam name="TRecord">The record the predicate is written against.</typeparam>
    /// <param name="predicate">A lambda over the record's columns.</param>
    /// <returns>False when the statistics prove no row matches.</returns>
    public bool MayMatch<TRecord>(Func<Probe<TRecord>, Predicate> predicate)
        where TRecord : IVortexRecord<TRecord>
    {
        ArgumentNullException.ThrowIfNull(predicate);
        RecordBinding binding = RecordBinding.For<TRecord>(Schema, Session.Options.Extensions);
        Predicate filter = predicate(new Probe<TRecord>(binding));
        return !filter.IsNone && (filter.IsAll || ScanSource.MayMatch(filter.Node!));
    }

    /// <summary>The file's indexes: kind, column, blocks, runs and bytes.</summary>
    /// <param name="cancellationToken">Cancels the read of the index directory.</param>
    /// <returns>The indexes; empty when the file carries none.</returns>
    public async ValueTask<ImmutableArray<VortexIndexInfo>> GetIndexesAsync(CancellationToken cancellationToken = default) =>
        [.. await ReadIndexesAsync(cancellationToken).ConfigureAwait(false)];

    private VortexEdition ComputeEdition()
    {
        VortexEdition edition = EditionRegistry.ReadForeverFloor;
        for (int i = 0; i < ArrayEncodingCount; i++)
        {
            edition = Later(edition, EditionRegistry.IntroducedIn(ComponentKind.Array, GetArrayEncodingId(i)));
        }

        for (int i = 0; i < LayoutEncodingCount; i++)
        {
            edition = Later(edition, EditionRegistry.IntroducedIn(ComponentKind.Layout, GetLayoutEncodingId(i)));
        }

        foreach (VortexField field in Schema)
        {
            edition = Extensions(edition, field.Type);
        }

        return edition;
    }

    private static VortexEdition Extensions(VortexEdition edition, VortexType type)
    {
        if (type.ExtensionId is { } id)
        {
            edition = Later(edition, EditionRegistry.IntroducedIn(ComponentKind.DType, id));
        }

        foreach (VortexField field in type.Fields)
        {
            edition = Extensions(edition, field.Type);
        }

        if (type.ElementType is { } element)
        {
            edition = Extensions(edition, element);
        }

        return type.StorageType is { } storage ? Extensions(edition, storage) : edition;
    }

    private static VortexEdition Later(VortexEdition current, VortexEdition? introduced) =>
        introduced is { } found && found > current ? found : current;
}

/// <summary>A file's statistics, one entry per top-level column.</summary>
public sealed class VortexFileStatistics
{
    private readonly VortexFile _file;

    internal VortexFileStatistics(VortexFile file) => _file = file;

    /// <summary>The number of columns with statistics: the file's columns, or zero when it carries none.</summary>
    public int Count => _file.HasFileStatistics ? _file.FileStatistics.FieldCount : 0;

    /// <summary>The statistics of column <paramref name="index"/>.</summary>
    /// <param name="index">The column's position in the file's schema.</param>
    public FieldStatistics this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, Count == 0 ? "The file carries no statistics." : $"The file has {Count} columns.");
            }

            FileStatistics statistics = _file.FileStatistics;
            return statistics.GetField(index).Typed(_file.Schema.RootIsStruct ? _file.Schema[index].Type : _file.Schema[0].Type, VortexTypes.FromDType(statistics.GetSumDType(index).IsDefault ? statistics.GetFieldDType(index) : statistics.GetSumDType(index)));
        }
    }
}

/// <summary>The user metadata of a file: small values by key, read on demand.</summary>
public sealed class VortexMetadata
{
    private readonly VortexFile _file;

    internal VortexMetadata(VortexFile file)
    {
        _file = file;
        ImmutableArray<string>.Builder keys = ImmutableArray.CreateBuilder<string>(file.MetadataCount);
        for (int i = 0; i < file.MetadataCount; i++)
        {
            keys.Add(file.GetMetadataKey(i));
        }

        Keys = keys.MoveToImmutable();
    }

    /// <summary>The keys, in the order the file stores them.</summary>
    public ImmutableArray<string> Keys { get; }

    /// <summary>The value of <paramref name="key"/>, read from the tail the open already holds when it can.</summary>
    /// <param name="key">A key of <see cref="Keys"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value, copied: it outlives the file.</returns>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">The file has no such key.</exception>
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_file.TryGetMetadataIndex(System.Text.Encoding.UTF8.GetBytes(key), out int index))
        {
            throw new System.Collections.Generic.KeyNotFoundException($"The file has no metadata '{key}'.");
        }

        SegmentOwner owner = await _file.ReadMetadataAsync(index, cancellationToken).ConfigureAwait(false);
        try
        {
            return owner.Buffer.Span.ToArray();
        }
        finally
        {
            owner.Release();
        }
    }
}
