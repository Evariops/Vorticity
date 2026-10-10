# row-vectors

Emits golden row-encoding vectors from the reference `vortex-row`, for
[`tests/Vorticity.Conformance/RowVectors`](../../tests/Vorticity.Conformance/RowVectors) to compare
against byte for byte.

## Why this exists separately from `conformance-gen`

`vortex-row` is marked `publish = false` upstream. It only lives inside the Vortex monorepo and has
never been pushed to crates.io, so the exact crates.io pin `conformance-gen` uses cannot reach it. This
crate takes it as a git dependency pinned to the tag `0.86.1`, and takes every other Vortex crate from
the same tag, so that one resolved copy of `vortex-array` serves them all. It is the only git
dependency in the repository.

[The row encoding section](../../docs/design/04-conformance.md#7-the-row-encoding) of the conformance
design explains why it is worth the trouble: being merely order-compatible is not enough, because two
implementations could each be internally consistent and still disagree, which would silently break
any comparison across languages.

## Running it

CI does not build it. Run it by hand when the pin moves, and commit the output.

```sh
cd tools/row-vectors
CARGO_NET_GIT_FETCH_WITH_CLI=true cargo run --release -- \
  ../../tests/Vorticity.Conformance/row-vectors/vectors.jsonl
```

`CARGO_NET_GIT_FETCH_WITH_CLI=true` is needed on any machine whose git config rewrites
`https://github.com/` to SSH, because cargo's built-in git client cannot authenticate through that,
while the git CLI can.

## Keeping the two halves in step

The case list here and `RowVectorCases.Names` on the C# side are two halves of one table, and each
case name must build the same columns on both sides. Nothing enforces that mechanically, and nothing
needs to: if the two sides build different inputs, the encoded bytes differ and the test fails naming
the case. A silent pass is not one of the possible outcomes.

Moving the pin means regenerating, since the row format is experimental upstream and its byte layout
may change between releases. `vectors.jsonl` records the version it came from on its first line, and
the C# test asserts that version rather than accepting whatever it finds.
