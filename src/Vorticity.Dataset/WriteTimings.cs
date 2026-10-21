using System;
using System.Diagnostics;
using System.Threading;

namespace Vorticity.Dataset;

/// <summary>
/// Where the slices of the objects a load writes spend their time, summed over every object that adds to it, so that which
/// stage of the pipeline holds the others is a number: the rows drawn by the caller, then on the thread that hands them
/// over, the rows copied into a slice, the slice's parts joined into one, sorted, and the wait for the slice before it to
/// be written; on the thread that writes a slice, its synopsis counted, and its columns and indexes written; once an
/// object's rows are all handed over, its last slice written, its synopsis finished and its writer completed. The object is
/// a sink the caller owns, as <see cref="Scanning.ScanCounters"/> is; the additions are interlocked, one a slice or a batch.
/// </summary>
internal sealed class WriteTimings
{
    private long _feed;
    private long _copy;
    private long _join;
    private long _sort;
    private long _wait;
    private long _synopsis;
    private long _write;
    private long _drain;
    private long _tables;
    private long _close;

    /// <summary>The caller's rows drawn: the time its parts took to hand each batch over.</summary>
    internal TimeSpan Feed => Of(ref _feed);

    /// <summary>The rows copied into the slices that hold them.</summary>
    internal TimeSpan Copy => Of(ref _copy);

    /// <summary>The parts of each slice joined into one.</summary>
    internal TimeSpan Join => Of(ref _join);

    /// <summary>Each slice sorted: its codes, its permutation, its rows and codes in its order.</summary>
    internal TimeSpan Sort => Of(ref _sort);

    /// <summary>The thread that hands the rows over waiting for the slice before to be written.</summary>
    internal TimeSpan Wait => Of(ref _wait);

    /// <summary>Each slice counted into the object's synopsis.</summary>
    internal TimeSpan Synopsis => Of(ref _synopsis);

    /// <summary>Each slice's columns and indexes written.</summary>
    internal TimeSpan Write => Of(ref _write);

    /// <summary>The last slice of each object written, once its rows are all handed over.</summary>
    internal TimeSpan Drain => Of(ref _drain);

    /// <summary>Each object's synopsis finished: its last hour merged, then its hours into the object's tables.</summary>
    internal TimeSpan Tables => Of(ref _tables);

    /// <summary>Each object's writer completed: its indexes finished, the chunks it held laid out, its footer.</summary>
    internal TimeSpan Close => Of(ref _close);

    internal void AddFeed(long ticks) => Interlocked.Add(ref _feed, ticks);

    internal void AddCopy(long ticks) => Interlocked.Add(ref _copy, ticks);

    internal void AddJoin(long ticks) => Interlocked.Add(ref _join, ticks);

    internal void AddSort(long ticks) => Interlocked.Add(ref _sort, ticks);

    internal void AddWait(long ticks) => Interlocked.Add(ref _wait, ticks);

    internal void AddSynopsis(long ticks) => Interlocked.Add(ref _synopsis, ticks);

    internal void AddWrite(long ticks) => Interlocked.Add(ref _write, ticks);

    internal void AddDrain(long ticks) => Interlocked.Add(ref _drain, ticks);

    internal void AddTables(long ticks) => Interlocked.Add(ref _tables, ticks);

    internal void AddClose(long ticks) => Interlocked.Add(ref _close, ticks);

    private static TimeSpan Of(ref long ticks) => TimeSpan.FromSeconds(Interlocked.Read(ref ticks) / (double)Stopwatch.Frequency);
}
