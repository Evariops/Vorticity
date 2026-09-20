# Changelog

Nothing has been released yet, so nothing carries a version number. This entry describes what the
first release will contain; the release itself will date it and give it one.

## Unreleased

### `Vorticity`

The Vortex columnar file format in pure .NET, with `System.IO.Hashing` as its one runtime
dependency. The public API is async throughout, and the hot paths allocate nothing per row.

**Reading** is complete for the 1.0 scope: opening a file, the layout tree, all 37 array encodings
that have a decoder and all 7 layouts, typed column access, scans with projection and row ranges,
filter pushdown, zone-map pruning, random access by row index with the selection pushed down into
the encoding rather than gathered afterwards, skipping and locating indexes, and a key cursor with
seek, step, rank and distinct.

**Writing** produces files Vortex Rust reads back: a fused single-pass writer, exact block
statistics, a rule-based scheme chooser, append to an existing file, and recovery from a torn tail.
Edition targeting is enforced on the way out, so a file cannot claim a component its target does not
contain.

**Not in this release**, each by decision rather than omission:

* No sampling compressor. The writer measures the whole block in one fused pass instead, which is
  what makes a sample unnecessary rather than unaffordable.
* The same rows written in different batch sizes produce a valid file of a different size. The
  determinism promised is *the same batches give the same bytes*, not *the same rows give the same
  bytes*; `docs/11-write-strategy.md` states the limit and its cost.
* No encryption. The format reserves a slot for it and this library writes it empty.
* No S3 client. `Vorticity.Dataset` defines the store seam; an implementation is a caller's.

### `Vorticity.Dataset` — experimental

A versioned dataset over an object store: immutable data and commit objects, a prolly tree of
leaves, compaction, vacuum, and the seam an object-store client implements. **The format is this
repository's own** — no other implementation reads it — and decisions about it are still open. Treat
it as subject to change.

### `Vorticity.RowEncoding` — experimental

The byte-sortable row encoding: columns to row keys whose `memcmp` order is tuple order, byte for
byte with the reference. **Upstream marks this format experimental and reserves the right to change
its byte layout between releases**, which is why it is a package of its own rather than part of the
core.
