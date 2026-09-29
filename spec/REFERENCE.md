# The reference implementation is on this machine

Building [`tools/conformance-gen`](../tools/conformance-gen) unpacked the entire Vortex Rust source
tree into the local cargo registry, for **two** versions. Every agent and every human working on a
decoder should read it there rather than guessing or fetching:

```
~/.cargo/registry/src/index.crates.io-*/vortex-0.84.0/           # what conformance-gen links
~/.cargo/registry/src/index.crates.io-*/vortex-0.86.1/           # newest published
~/.cargo/registry/src/index.crates.io-*/vortex-array-0.84.0/     # arrays, dtype, scalar, patches, stats
~/.cargo/registry/src/index.crates.io-*/vortex-file-0.84.0/      # postscript, footer, open path, writer
~/.cargo/registry/src/index.crates.io-*/vortex-layout-0.84.0/    # layout tree, zoned, scan
~/.cargo/registry/src/index.crates.io-*/vortex-fastlanes-0.84.0/ # for, bitpacked, rle, delta
~/.cargo/registry/src/index.crates.io-*/fastlanes-0.7.2/         # the kernels themselves
~/.cargo/registry/src/index.crates.io-*/vortex-fsst-0.84.0/  vortex-alp-0.84.0/  vortex-zstd-0.84.0/
~/.cargo/registry/src/index.crates.io-*/vortex-btrblocks-0.84.0/ # the default compressor + scheme order
~/.cargo/registry/src/index.crates.io-*/vortex-edition-0.84.0/   # edition enforcement
```

`0.86.1` sources are present too (pulled during dependency resolution), which is what lets us diff
a behaviour between the version our corpus is pinned to and the newest release.

Reading these is not "translating from reference code" for licensing purposes — consulting the
reference for *semantics* is the project's declared default working mode
([09-contracts.md](../docs/design/09-contracts.md) §6). Copying a kernel line for line is, and must be
noted in the file header when it happens.

---

## FastLanes: the two index functions, which are not the same

[02-format.md](../docs/design/02-format.md) §8 gives one formula. There are actually **two distinct
permutations** in `fastlanes-0.7.2`, and using one where the other belongs produces plausible,
wrong output that only shows up on real data.

`FL_ORDER = [0, 4, 2, 6, 1, 5, 3, 7]` (`src/lib.rs`) is common to both, and is its own inverse:
`FL_ORDER[FL_ORDER[i]] == i` for all i. That self-inverse property is asserted by the crate's own
test and is what makes the derivations below invertible.

`T::T` is the bit width of the unsigned element type (8/16/32/64) and
`T::LANES = 1024 / T::T` — so 128 lanes for `u8`, 64 for `u16`, 32 for `u32`, 16 for `u64`.

### 1. The bit-packing index — `src/bitpacking.rs`, `src/macros.rs`

The pack and unpack kernels walk `row` over `0..T::T` and `lane` over `0..T::LANES`. The element
they touch at `(row, lane)` is the one at **logical** index:

```
index(row, lane) = FL_ORDER[row / 8] * 16 + (row % 8) * 128 + lane
```

and the inverse, logical index `i` back to `(row, lane)` (`rows_by_index` / `lanes_by_index`,
which the crate precomputes as 1024-entry tables — worth doing the same):

```
lane     = i % T::LANES
s        = i / 128                          // (FL_ORDER[o] * 16) + lane is always < 128
fl_order = (i - s * 128 - lane) / 16        // this is the VALUE of FL_ORDER[o]
o        = FL_ORDER[fl_order]               // invertible because FL_ORDER is its own inverse
row      = o * 8 + s
```

This is the one that matters for `fastlanes.bitpacked`, and the one
[90-registry.md](../docs/design/90-registry.md) means by "O(1) positional access via the inverse
transposition" in the `take` strategy table.

Special case worth having a branch for: `W == T` packs nothing, and
`packed[LANES * row + lane]` is the value directly. `W == 0` produces a zero-length output.

### 2. The element transposition — `src/transpose.rs`

A different permutation, used by the transposed encodings (delta, RLE), where `lane` is `% 16`
**regardless of the element type**:

```
transpose(idx):
    lane  = idx % 16
    order = (idx / 16) % 8
    row   = idx / 128
    return lane * 64 + FL_ORDER[order] * 8 + row
```

`untranspose` is the inverse *mapping* (`output[transpose(i)] = input[i]`), **not** a second
application of `transpose`: `transpose(1) == 64` but `transpose(64) == 8`. Only `FL_ORDER` is its
own inverse; `transpose` is not. Writing `untranspose(x) = transpose(x)` is a real and tempting
bug, and it survives a round-trip test written the same wrong way in both directions.

Known values, straight from the crate's `test_transpose_known_indices`, and the right thing for
our own unit test to assert:

| idx | 0 | 1 | 16 | 32 | 48 | 64 | 128 | 1023 |
|---|---|---|---|---|---|---|---|---|
| transpose(idx) | 0 | 64 | 32 | 16 | 48 | 8 | 1 | 1023 |

Plus the two structural properties the crate asserts and we should too: `transpose` is a
permutation of `0..1024` (every output distinct and in range), and
`untranspose(transpose(v)) == v`.

### A caveat in the crate itself

`src/macros.rs` opens with: *"Warning: it is NOT wire compatible with the original FastLanes
implementation. It differs in that it iterates over the elements respecting the transposed
ordering."* That warning is about the macro-based kernels versus the original upstream FastLanes
paper implementation — **`fastlanes-0.7.2` as vendored is what Vortex writes**, so it is our wire
contract regardless of what the academic reference does. Do not "fix" our kernel to match the
paper.

---

## The array blob, read off the writer rather than the prose

`vortex-array/src/serde.rs`, `ArrayRef::serialize`. [02-format.md](../docs/design/02-format.md) §5.1
describes the shape correctly; these are the details it leaves out, each of which is a plausible
wrong guess.

The writer emits, in order:

1. A **zero-length buffer carrying the maximum alignment** of everything that follows
   (`ByteBuffer::zeroed_aligned(0, max_alignment)`). It contributes no bytes and no `Buffer` entry.
   Its only job is to make the writer align the blob's start. Do not look for it on read.
2. For each data buffer: `padding` zero bytes, then the buffer. `padding` is
   `pos.next_multiple_of(buffer.alignment()) - pos` where **`pos` starts at the blob's offset
   within the file, not at zero** (`SerializeOptions::offset`). This is why a segment is
   directly mmap-usable. It does *not* complicate reading: the padding actually written is
   recorded in that buffer's `Buffer.padding` field, so a reader still just accumulates
   `padding + length` from the blob start.
3. Padding to the FlatBuffer's own 8-byte alignment. **This padding is recorded nowhere.** It is
   not a problem because the FlatBuffer is located from the end (`len - 4 - fb_length`), but a
   reader that tries to find the FlatBuffer by walking forward past the last data buffer will land
   in the padding.
4. The `Array` FlatBuffer, built with `finish_minimal` — **no file identifier**, so do not look
   for or validate one.
5. A little-endian `u32` holding the FlatBuffer's length. Both the FlatBuffer length and every
   buffer length must fit in `u32`; the writer errors otherwise, so a reader may treat a value
   that does not fit as malformed.

`max_alignment` is the max over all buffer alignments and the FlatBuffer's own alignment, so it is
at least 8. Combined with the `alignment_exponent <= 6` cap in
[08-semantics.md](../docs/design/08-semantics.md) §6, a segment's own alignment is enough to make every
inner buffer aligned once the segment start is.

One note for the writer: *"Serializers may choose historical IDs and may provide
downgraded buffers or children that differ from the in-memory array tree."* The serialized encoding
id is not required to equal the in-memory one — which is exactly the mechanism edition targeting
uses to write an older id than the array it holds.
