---
name: Bug report
about: A file that does not read, a value that comes back wrong, a crash
labels: bug
---

A **security** vulnerability does not go here. SECURITY.md says how to report one privately, and
what the library does and does not guarantee against a malformed or a lying file.

## The file

A bug in a file format is only reproducible with the bytes. Attach the `.vortex` file, or say how
to produce it — the writer options, or the corpus entry id if it is one of ours.

If the file cannot be shared, `dotnet run --project tools/vxdump -c Release -- <file> --schema
--layout` prints the dtype and the layout tree and no value at all; that output is usually enough
to place the fault.

## What was run

The command, the API call, or the few lines that reproduce it.

## What happened, and what was expected

The exception and its stack trace, or the value read against the value the file holds.

## Where

- Operating system and architecture:
- `dotnet --version`:
- Which assembly: `Vorticity`, `Vorticity.Dataset`, or `Vorticity.RowEncoding`
- Whether the file was written by this library, by Vortex Rust, or by something else

## Already checked

- [ ] The file reads with the Rust implementation, or it does not read there either
