# sealed-kat

Computes the known answer of the sealed format, version 1, from
[its description](../../docs/design/18-encryption.md) alone: the descriptor's bytes, the HKDF
derivation of the key and the commitment, the nonces and the trailer, written again in Python over
another AES-GCM (pyca/cryptography, which runs OpenSSL) and an HKDF written out by hand.
`SealedFormatTests.AKnownAnswerPinsTheFormat` holds the SHA-256 it prints, so the library and this
second reading of the format have to agree byte for byte.

## Why this exists

A known answer computed by the code it checks only catches a later change. Two readings of the
format that agree catch a mistake in either: the order of the derivation's inputs, a field's width,
the flag in a nonce.

## Running it

CI does not run it. Run it by hand when the format changes, and update the test's constant only
when both agree.

```sh
python3 -m venv .venv
.venv/bin/pip install cryptography
.venv/bin/python tools/sealed-kat/kat.py
```
