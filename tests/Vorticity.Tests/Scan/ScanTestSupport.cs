// Shared plumbing for the scan tests: where the corpus lives, how to register the decoders this
// build owns, and the instrumented segment sources the I/O assertions need.
//
// WHY THE TESTS STILL CALL A REGISTRATION HELPER. ArrayDecoderTable's static constructor is the
// one place every decoder is named, and it names every one, so `Decoders.EnsureRegistered()`
// finds every slot filled and installs nothing. It is kept as the single call site the scan tests
// share, and because it is guarded by IsImplemented it cannot mask a regression in that
// constructor - it would leave the table exactly as it found it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Tests.Scan;

internal static class Corpus
{
    /// <summary>The <c>tests/Vorticity.Conformance</c> directory.</summary>
    internal static readonly string Root = Locate();

    /// <summary>The corpus manifest.</summary>
    internal static string ManifestPath =>
        System.IO.Path.Combine(Root, "corpus", "manifest.json");

    /// <summary>The absolute path of a corpus entry, e.g. <c>containers/uncompressed_canonical</c>.</summary>
    internal static string Path(string entry, string extension = ".vortex") =>
        System.IO.Path.Combine(Root, "corpus", entry.Replace('/', System.IO.Path.DirectorySeparatorChar) + extension);

    /// <summary>The absolute path of a forged fixture, e.g. <c>negative/unknown_encoding_id</c>.</summary>
    internal static string Forged(string entry) =>
        System.IO.Path.Combine(Root, "forged", entry.Replace('/', System.IO.Path.DirectorySeparatorChar) + ".vortex");

    // Walk up from this source file rather than from AppContext.BaseDirectory: the check project
    // that builds this component lives outside the repository, so its output directory says nothing
    // about where the corpus is.
    private static string Locate([CallerFilePath] string thisFile = "")
    {
        DirectoryInfo? dir = new FileInfo(thisFile).Directory;
        while (dir is not null)
        {
            string candidate = System.IO.Path.Combine(
                dir.FullName, "tests", "Vorticity.Conformance", "corpus", "manifest.json");
            if (System.IO.File.Exists(candidate))
            {
                return System.IO.Path.Combine(dir.FullName, "tests", "Vorticity.Conformance");
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate tests/Vorticity.Conformance/corpus above '" + thisFile + "'.");
    }
}

internal static class Decoders
{
    private static readonly object Gate = new object();
    private static bool _done;

    /// <summary>Registers every decoder this build owns, once. Idempotent and thread-safe.</summary>
    internal static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_done)
            {
                return;
            }

            _done = true;
            Register(BitPackedDecoder.Instance);
            Register(ByteBoolDecoder.Instance);
            Register(DictDecoder.Instance);
            Register(FastLanesRleDecoder.Instance);
            Register(ForDecoder.Instance);
            Register(RunEndDecoder.Instance);
            Register(SequenceDecoder.Instance);
            Register(SparseDecoder.Instance);
            Register(ZigZagDecoder.Instance);
        }
    }

    private static void Register(ArrayDecoder decoder)
    {
        if (!ArrayDecoderTable.IsImplemented(decoder.EncodingId))
        {
            ArrayDecoderTable.Register(decoder);
        }
    }
}

/// <summary>
/// An <see cref="ISegmentReader"/> decorator that counts calls and records every spec it was asked
/// for. This is the only way to see the two scan properties a value comparison cannot
/// show: exactly one <c>ReadManyAsync</c> per batch, and no I/O at all for an unprojected column.
/// </summary>
internal sealed class RecordingSegmentSource : ISegmentReader
{
    private readonly ISegmentReader _inner;
    private readonly List<SegmentSpec> _requested = new List<SegmentSpec>();

    internal RecordingSegmentSource(ISegmentReader inner) => _inner = inner;

    internal int ReadManyCalls { get; private set; }

    internal int ReadCalls { get; private set; }

    internal int ReadRangeCalls { get; private set; }

    internal int LengthCalls { get; private set; }

    /// <summary>Every spec ever registered in a set this source was handed, in call order.</summary>
    internal IReadOnlyList<SegmentSpec> Requested => _requested;

    internal void ResetCounters()
    {
        ReadManyCalls = 0;
        ReadCalls = 0;
        ReadRangeCalls = 0;
        LengthCalls = 0;
        _requested.Clear();
    }

    internal bool WasRequested(SegmentSpec spec)
    {
        for (int i = 0; i < _requested.Count; i++)
        {
            SegmentSpec other = _requested[i];
            if (other.Offset == spec.Offset && other.Length == spec.Length)
            {
                return true;
            }
        }

        return false;
    }

    public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken)
    {
        LengthCalls++;
        return _inner.GetLengthAsync(cancellationToken);
    }

    public ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
    {
        ReadCalls++;
        _requested.Add(spec);
        return _inner.ReadAsync(spec, cancellationToken);
    }

    public ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ReadManyCalls++;
        for (int i = 0; i < requests.Count; i++)
        {
            _requested.Add(requests.GetSpec(i));
        }

        return _inner.ReadManyAsync(requests, cancellationToken);
    }

    public ValueTask<SegmentOwner> ReadRangeAsync(
        long offset, int length, int alignment, CancellationToken cancellationToken)
    {
        ReadRangeCalls++;
        return _inner.ReadRangeAsync(offset, length, alignment, cancellationToken);
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>
/// A source whose <see cref="ReadManyAsync"/> never completes synchronously: it parks on a
/// <see cref="TaskCompletionSource"/> the test releases. It drives the enumerator's asynchronous
/// path - the ManualResetValueTaskSourceCore branch - which a memory-mapped file never reaches.
/// </summary>
internal sealed class GatedSegmentSource : ISegmentReader
{
    private readonly ISegmentReader _inner;
    private readonly List<TaskCompletionSource> _gates = new List<TaskCompletionSource>();

    internal GatedSegmentSource(ISegmentReader inner) => _inner = inner;

    /// <summary>Releases every read parked so far.</summary>
    internal void ReleaseAll()
    {
        lock (_gates)
        {
            for (int i = 0; i < _gates.Count; i++)
            {
                _gates[i].TrySetResult();
            }

            _gates.Clear();
        }
    }

    internal int Parked
    {
        get
        {
            lock (_gates)
            {
                return _gates.Count;
            }
        }
    }

    public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken) =>
        _inner.GetLengthAsync(cancellationToken);

    public ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken) =>
        _inner.ReadAsync(spec, cancellationToken);

    public ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken) =>
        new ValueTask(GatedReadAsync(requests, cancellationToken));

    public ValueTask<SegmentOwner> ReadRangeAsync(
        long offset, int length, int alignment, CancellationToken cancellationToken) =>
        _inner.ReadRangeAsync(offset, length, alignment, cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private async Task GatedReadAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        TaskCompletionSource gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gates)
        {
            _gates.Add(gate);
        }

        using (cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), gate))
        {
            await gate.Task.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _inner.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// A source that hands out one dedicated <see cref="SegmentOwner"/> per slot and remembers every
/// one it ever made, so a test can assert that the scan released all of them exactly once.
/// </summary>
/// <remarks>
/// It deliberately does <b>not</b> coalesce or share: the shared-owner path of a real source hides
/// the per-segment refcount behind a run buffer, and the property under test is the scan's, not the
/// source's.
/// </remarks>
internal sealed class TrackingSegmentSource : ISegmentReader
{
    private readonly ISegmentReader _inner;
    private readonly List<TrackedOwner> _owners = new List<TrackedOwner>();

    internal TrackingSegmentSource(ISegmentReader inner) => _inner = inner;

    internal int OwnerCount
    {
        get
        {
            lock (_owners)
            {
                return _owners.Count;
            }
        }
    }

    /// <summary>How many of the owners handed out still hold a live reference.</summary>
    internal int LiveOwners
    {
        get
        {
            lock (_owners)
            {
                int live = 0;
                for (int i = 0; i < _owners.Count; i++)
                {
                    if (_owners[i].RefCount > 0)
                    {
                        live++;
                    }
                }

                return live;
            }
        }
    }

    /// <summary>True when no owner was ever released twice.</summary>
    internal bool AnyOverReleased
    {
        get
        {
            lock (_owners)
            {
                for (int i = 0; i < _owners.Count; i++)
                {
                    if (_owners[i].Frees > 1)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken) =>
        _inner.GetLengthAsync(cancellationToken);

    public ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken) =>
        _inner.ReadAsync(spec, cancellationToken);

    public ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        return new ValueTask(FillAsync(requests, cancellationToken));
    }

    public ValueTask<SegmentOwner> ReadRangeAsync(
        long offset, int length, int alignment, CancellationToken cancellationToken) =>
        _inner.ReadRangeAsync(offset, length, alignment, cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private async Task FillAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        if (requests.IsPopulated)
        {
            return;
        }

        try
        {
            for (int slot = 0; slot < requests.Count; slot++)
            {
                if (requests.IsFilled(slot))
                {
                    continue;
                }

                SegmentOwner inner = await _inner
                    .ReadAsync(requests.GetSpec(slot), cancellationToken)
                    .ConfigureAwait(false);
                TrackedOwner owner = new TrackedOwner(inner);
                lock (_owners)
                {
                    _owners.Add(owner);
                }

                requests.SetResult(slot, owner);
            }

            requests.Complete();
        }
        catch
        {
            requests.AbandonPending();
            throw;
        }
    }

    private sealed class TrackedOwner : SegmentOwner
    {
        private readonly SegmentOwner _inner;

        internal TrackedOwner(SegmentOwner inner)
        {
            _inner = inner;
            Buffer = inner.Buffer;
        }

        internal int Frees { get; private set; }

        protected override void FreeCore()
        {
            Frees++;
            _inner.Release();
        }
    }
}
