# Security

## What is guaranteed

A malformed file produces an exception, never anything worse. A file that violates the format (bad
offsets, truncation, out-of-range structural fields, exceeded caps) raises a clean
`VortexFormatException` or `VortexUnsupportedException`, with no out-of-bounds access, no unbounded
allocation and no hang. Parser fuzzing in CI enforces this on every pull request.

## What is not

A well-formed file that lies is believed. Statistics embedded in a file (zone bounds, null counts,
sortedness) are used for pruning, so a file whose structure is valid but whose statistics are false
may yield wrong results. This is inherent to every format with embedded statistics.
`VerifyStatistics` trades throughput for validation when the producer is not trusted.

Two more things are explicitly not claimed: protection against a producer aiming at resource
exhaustion below the configured caps, and any confidentiality property. The format's encryption slot
is an empty reserved table, and this library writes it empty.

## Reporting

Report a vulnerability through the repository's Security tab, with "Report a vulnerability". That
opens a private advisory visible only to you and the maintainers. A public issue is the wrong place,
and cannot be taken back.

Include the file, or the generator that reproduces it. A crash on a malformed input is a bug within
the scope of the guarantee above, and is treated as one.
