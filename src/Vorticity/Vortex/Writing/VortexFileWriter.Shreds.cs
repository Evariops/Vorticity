using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Indexes;
using Vorticity.Writing;

namespace Vorticity;

/// <summary>The shredded columns whose rows the writer's line takes block by block, before their chunks are compressed ahead.</summary>
public sealed partial class VortexFileWriter
{
    // Per column, the chain its blocks' rows are taken along, for a shredded column whose chunks are compressed ahead; made
    // with the line's first prologue, an empty array for a writer that has none.
    private ShredChain?[]? _chains;

    /// <summary>
    /// The chains of the shredded columns whose chunks are compressed ahead, made once: null for a writer that has none, or
    /// whose chunks gather nothing.
    /// </summary>
    private ShredChain?[]? Chains()
    {
        if (_chains is not null)
        {
            return _chains.Length == 0 ? null : _chains;
        }

        if (_shredding is not { } shredding || _gathering is not { } gathering)
        {
            _chains = [];
            return null;
        }

        ShredChain?[] chains = new ShredChain?[_fieldCount];
        bool any = false;
        for (int field = 0; field < _fieldCount; field++)
        {
            if (shredding[field] is not null && gathering[field] is not null)
            {
                chains[field] = new ShredChain(this, field);
                any = true;
            }
        }

        _chains = any ? chains : [];
        return any ? chains : null;
    }

    /// <summary>Gives back what the chains hold, for a writer done.</summary>
    private void ReleaseChains()
    {
        foreach (ShredChain? chain in _chains ?? [])
        {
            chain?.Release();
        }
    }

    /// <summary>
    /// What a chain took of a chunk it handed over (<see cref="ShredChain.Hand"/>): its shredding, every row of the chunk taken,
    /// the context what it took is carved from, and the bounds of its keys block by block.
    /// </summary>
    private sealed record TakenChunk(VariantShredder.Shredding Shredding, ScanContext Store, ChunkBlockBounds? Bounds);

    /// <summary>
    /// A shredded column whose chunks are compressed ahead, its rows taken block by block on the writer's line, each block an
    /// item that takes its rows after the block before's, rather than the whole chunk taken where it is compressed: the chunk
    /// it gathers now (its generation), the rows planned along it, and whether a block it did not take broke it, the chunk
    /// then taken whole as before. Planned and handed over on the line's ordered steps, its prologues and its owner's acts.
    /// </summary>
    private sealed class ShredChain(VortexFileWriter writer, int field)
    {
        private readonly Stack<VariantShredder.Shredding> _spare = new();
        private VariantShredder.Shredding? _shredding;
        private ScanContext? _store;
        private ChunkBlockBounds? _bounds;
        private long _generation = -1;
        private int _planned;
        private bool _broken;

        /// <summary>The shredding the chunk's rows are taken into, while one is begun.</summary>
        internal VariantShredder.Shredding Shredding => _shredding!;

        /// <summary>
        /// Plans the take of <paramref name="rows"/> rows, which fall at <paramref name="position"/> of the column's chunk of
        /// <paramref name="generation"/>: whether they are taken, right after the rows planned before them. A chunk begins
        /// with its first rows; one whose rows do not follow the rows planned is broken, and taken whole.
        /// </summary>
        internal bool Plan(long generation, long position, int rows)
        {
            if (generation != _generation)
            {
                _generation = generation;
                _broken = position != 0;
                if (!_broken)
                {
                    Begin();
                }
            }

            if (_broken || position != _planned)
            {
                _broken = true;
                return false;
            }

            _planned += rows;
            return true;
        }

        /// <summary>
        /// Hands over what was taken of the chunk of <paramref name="generation"/>, which holds <paramref name="rows"/> rows,
        /// when every row of it was taken, to the chunk compressed ahead; null when it was not, the chunk taken whole.
        /// </summary>
        internal TakenChunk? Hand(long generation, long rows)
        {
            if (generation != _generation || _broken || _planned != rows || _shredding is not { } shredding || _store is not { } store)
            {
                _generation = -1;
                return null;
            }

            TakenChunk taken = new TakenChunk(shredding, store, _bounds);
            _shredding = null;
            _store = null;
            _bounds = null;
            _generation = -1;
            return taken;
        }

        /// <summary>Takes back what a chunk handed over held, once its blob is written or given up.</summary>
        internal void Return(TakenChunk taken)
        {
            taken.Store.Canonical.Reset();
            TransitContexts.Return(taken.Store);
            lock (_spare)
            {
                _spare.Push(taken.Shredding);
            }
        }

        /// <summary>Gives back the chunk under way, for a writer done.</summary>
        internal void Release()
        {
            if (_store is { } store)
            {
                _store = null;
                store.Canonical.Reset();
                TransitContexts.Return(store);
            }

            _shredding = null;
            _generation = -1;
        }

        /// <summary>Begins a chunk: a shredding and a context to take its rows into, and its keys' bounds block by block.</summary>
        private void Begin()
        {
            if (_shredding is null)
            {
                lock (_spare)
                {
                    _shredding = _spare.Count > 0 ? _spare.Pop() : new VariantShredder.Shredding(writer._shredding![field]!);
                }
            }

            ScanContext store = _store ??= TransitContexts.Rent(writer.Account);
            store.Canonical.Reset();
            _bounds = writer._countedKeys?[field] is { } keys ? new ChunkBlockBounds(keys, writer._blockRows, writer._variantTextBound).Cleared() : null;
            _shredding.Begin(store.Canonical, _bounds);
            _planned = 0;
        }
    }
}
