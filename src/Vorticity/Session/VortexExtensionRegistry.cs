using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Text;

namespace Vorticity;

/// <summary>
/// A .NET type that is a Vortex extension dtype: values stored as <see cref="StorageType"/> and read
/// back as the type itself.
/// </summary>
/// <typeparam name="TSelf">The implementing type.</typeparam>
/// <remarks>
/// Register the type on <see cref="VortexSessionOptions.Extensions"/>; a <c>Column&lt;TSelf&gt;</c> and
/// a record member of type <typeparamref name="TSelf"/> then bind to a column whose extension id is
/// <see cref="Id"/>. Every member is static, so the conversion is monomorphised and costs no virtual
/// call per value.
/// </remarks>
public interface IVortexExtension<TSelf>
    where TSelf : IVortexExtension<TSelf>
{
    /// <summary>The extension id written in the file, such as <c>acme.money</c>.</summary>
    static abstract string Id { get; }

    /// <summary>The type the values are stored as; a primitive, a fixed-size list of primitives, or a decimal.</summary>
    static abstract VortexType StorageType { get; }

    /// <summary>Reads one value from its stored bytes.</summary>
    /// <param name="storage">One value of <see cref="StorageType"/>, little-endian.</param>
    /// <param name="metadata">The column's extension metadata.</param>
    /// <returns>The value.</returns>
    static abstract TSelf FromStorage(ReadOnlySpan<byte> storage, ReadOnlySpan<byte> metadata);

    /// <summary>Writes one value as its stored bytes.</summary>
    /// <param name="value">The value.</param>
    /// <param name="storage">Exactly the width of one value of <see cref="StorageType"/>.</param>
    /// <param name="metadata">The column's extension metadata.</param>
    static abstract void ToStorage(in TSelf value, Span<byte> storage, ReadOnlySpan<byte> metadata);
}

/// <summary>The extension dtypes a session knows beyond the frozen editions.</summary>
/// <remarks>Filled while the session is configured, frozen when <see cref="VortexSession.Create"/> returns.</remarks>
public sealed class VortexExtensionRegistry
{
    private readonly Dictionary<string, ExtensionRegistration> _byId = new(StringComparer.Ordinal);
    private FrozenDictionary<string, ExtensionRegistration>? _frozen;

    internal VortexExtensionRegistry()
    {
    }

    /// <summary>An empty registry, frozen.</summary>
    internal static VortexExtensionRegistry Empty { get; } = CreateFrozen();

    /// <summary>Makes <typeparamref name="TExtension"/> readable and writable in this session.</summary>
    /// <typeparam name="TExtension">The extension type.</typeparam>
    /// <exception cref="InvalidOperationException">The session is already created, or another type registered the same id.</exception>
    public void Register<TExtension>()
        where TExtension : IVortexExtension<TExtension>
    {
        if (_frozen is not null)
        {
            throw new InvalidOperationException("The session is created; its extensions are frozen. Register them inside VortexSession.Create.");
        }

        string id = TExtension.Id;
        ArgumentException.ThrowIfNullOrEmpty(id, nameof(TExtension));
        if (id.StartsWith("vortex.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"'{id}' is in the vortex namespace, which belongs to the format.");
        }

        if (_byId.TryGetValue(id, out ExtensionRegistration? existing) && existing.ClrType != typeof(TExtension))
        {
            throw new InvalidOperationException($"'{id}' is already registered by {existing.ClrType}.");
        }

        _byId[id] = new ExtensionRegistration<TExtension>();
    }

    internal void Freeze()
    {
        _frozen = _byId.ToFrozenDictionary(StringComparer.Ordinal);
        byte[][] ids = new byte[_byId.Count][];
        int i = 0;
        foreach (string id in _byId.Keys)
        {
            ids[i++] = Encoding.UTF8.GetBytes(id);
        }

        _idsUtf8 = ids;
    }

    private byte[][] _idsUtf8 = [];

    /// <summary>Whether a column of extension <paramref name="idUtf8"/> decodes in this session; allocates nothing.</summary>
    internal bool IsRegistered(ReadOnlySpan<byte> idUtf8)
    {
        foreach (byte[] id in _idsUtf8)
        {
            if (idUtf8.SequenceEqual(id))
            {
                return true;
            }
        }

        return false;
    }

    internal bool TryGet(string id, out ExtensionRegistration registration)
    {
        FrozenDictionary<string, ExtensionRegistration> table = _frozen ?? FrozenDictionary<string, ExtensionRegistration>.Empty;
        return table.TryGetValue(id, out registration!);
    }

    internal bool TryGet<TExtension>(out ExtensionRegistration registration)
    {
        FrozenDictionary<string, ExtensionRegistration> table = _frozen ?? FrozenDictionary<string, ExtensionRegistration>.Empty;
        foreach (ExtensionRegistration candidate in table.Values)
        {
            if (candidate.ClrType == typeof(TExtension))
            {
                registration = candidate;
                return true;
            }
        }

        registration = null!;
        return false;
    }

    private static VortexExtensionRegistry CreateFrozen()
    {
        VortexExtensionRegistry registry = new VortexExtensionRegistry();
        registry.Freeze();
        return registry;
    }
}

/// <summary>What the engine knows of one registered extension.</summary>
internal abstract class ExtensionRegistration
{
    internal abstract Type ClrType { get; }

    internal abstract string Id { get; }

    internal abstract VortexType StorageType { get; }
}

internal sealed class ExtensionRegistration<TExtension> : ExtensionRegistration
    where TExtension : IVortexExtension<TExtension>
{
    internal override Type ClrType => typeof(TExtension);

    internal override string Id => TExtension.Id;

    internal override VortexType StorageType => TExtension.StorageType;
}
