# Encryption: AES-256-GCM for everything Vorticity stores

Vorticity encrypts what it writes, files, datasets and the scratch of its queries, with AES-256 in
Galois/Counter Mode, and decrypts what it reads with keys its session holds. The encryption is
transparent: a sealed file opens, scans, is written and appended with the calls a plain file has,
and a dataset created encrypted stays encrypted whoever writes to it. This document is the design:
what it protects and from whom, where the encryption sits, the format of a sealed object, the keys
and their life, how the platform's cipher is used, and what all of it costs.

It is a design to build, and nothing in it exists in the code yet. Two statements of other documents
change once it does. [The scope](01-scope.md#6-what-it-leaves-out) leaves encryption out because the
format's slot for it is an empty reserved table, and that slot stays empty, as
[where it sits](#2-where-it-sits) explains. [The threat model](09-contracts.md#4-threat-model)
claims no confidentiality, and a sealed file gets the confidentiality and the integrity described
below. Stores are the subject of [17-object-storage.md](17-object-storage.md), and this document
relies on it for the key services' clients.

In this document, to seal is to encrypt and authenticate, a sealed object is what sealing produces,
and the Vortex file or commit object inside is its plaintext. The format's own "encryption" slot is
never meant when this document says sealed.

## 1. What it protects, and from whom

| who | what they get |
|---|---|
| someone who can read the storage: a bucket, a disk, a backup, a snapshot, the provider's staff | that objects exist, their keys, their sizes, when they were written and read, the identifier of the key that protects them and the random ids that bind them. Nothing of their content: no value, no column name, no schema, no statistic, no row count |
| someone who can write the storage | the power to delete an object, to put back an older copy of a whole object, or to swap a whole dataset for another sealed with the same keys. Any other change is detected before a byte of it is used: a frame altered, truncated, extended, reordered or taken from another object, an object moved under another key or swapped with another, a descriptor altered, and a plain object put where a sealed one is expected, in an encrypted dataset or in a session that refuses plaintext |
| someone inside the process, or holding a key | everything. Keys and plaintext are in memory while they are used, and the design does not pretend otherwise |

Deletion and rollback are where a writer of the storage keeps some power. A dataset notices a missing
object, and notices an object replaced by another, since each entry binds the object it names (see
[datasets](#63-datasets)). A whole dataset put back to an older state, its newest commits deleted,
cannot be told from a dataset that never had them by a reader that never saw them, and neither can a
dataset swapped whole for another. A handle refuses to move back from a version it knew, and anything
stronger needs a record outside the store, which is the application's.

Traffic analysis (sizes, timing, which ranges are read), physical side channels (power,
electromagnetic emissions) and denial of service by someone who can delete are outside the model.

The services' own encryption answers another threat. S3's server-side encryption and Azure's
encryption at rest, with the provider's keys or the customer's, decrypt on the provider's servers.
They protect what sits on the provider's disks, and with keys in a key service they add that service's
permissions to the bucket's, but the provider sees the plaintext, and so does anyone the account lets
read. Sealing keeps the keys and the plaintext in the process. Both can be used together, and neither
changes the other.

## 2. Where it sits

Three places were weighed:

| place | for | against |
|---|---|---|
| inside the Vortex format, in the encryption fields it reserves | a key per column, as Parquet's modular encryption allows | the fields are reserved and empty upstream: `footer.fbs` calls a segment's `_encryption` "reserved for future use", and `EncryptionSpec` has no field. Filling them would be this project's reading of fields upstream may define otherwise, the postscript and footer would remain readable or need further changes, and the parser, which reads structures in place, would decrypt inside each of them |
| an envelope around the whole object | the bytes inside are an unchanged Vortex file or commit object, which the Rust reader opens once decrypted. One mechanism seals every object this library writes. The parser and the decoders do not change. Every structure is hidden, the footer included | no key per column, and a sealed file cannot be read in place from a mapping |
| frames cut on the format's segments | no frame read for bytes the read does not want | an index of frames to write and read, for a saving that [what it costs](#7-what-it-costs) shows to be small |

The envelope is the design. It changes what is written: a sealed file is not a Vortex file until it
is decrypted, and no Vortex reader, this one without a key or the Rust one, reads it as one. What is
inside is byte for byte the file the writer would have written in plaintext, and
`vxdump decrypt` gives it back, so the reference reader still checks every sealed file's content.
The format's reserved fields stay empty, as upstream leaves them.

Access by column, with different readers allowed different columns, is not a goal. Columns that need
different readers belong in different datasets, each sealed with its own keys.

## 3. The sealed object

### 3.1 Layout

```
offset 0          header      "VXSEALED", the descriptor's length, the descriptor
H                 frame 0     plaintext bytes [0, F) encrypted, then their 16-byte tag
H + (F + 16)      frame 1     plaintext bytes [F, 2F) encrypted, then their tag
                  ...
                  frame n-1   the last 1 to F bytes encrypted, then their tag
                  trailer     the descriptor again, the epochs, the trailer's length, "VXSE"
```

That is an object as a writer produces it, with one epoch. An append adds an epoch of frames and a
new trailer after the old trailer ([appends](#36-appends)).

`F` is the frame size, a power of two from 4 KiB to 1 MiB, 64 KiB by default, recorded in the
descriptor. Within an epoch, frame `i` holds the epoch's plaintext bytes `[i·F, (i+1)·F)` and starts
`i·(F + 16)` bytes after the epoch's first frame, so a plaintext offset maps to its frame by a
division, with no index. Every frame but an epoch's last is full.

The descriptor is written at both ends because objects are opened from both ends. A Vortex file opens
by its tail, and its first read brings the trailer with the last frames. A commit object opens by its
head ([objects and keys](13-dataset.md#3-objects-and-keys)), and its first read brings the header with
the first frames. Either way the open costs no request beyond the one it makes in plaintext. A commit
object is never appended, so it has one epoch and a trailer whose length the descriptor gives, and the
object's length, which the answer to its first read carries, then says where its frames end. The two
copies of the descriptor are identical: a reader uses
the one its first read brings, and verification compares them.

### 3.2 The descriptor

| field | bytes | meaning |
|---|---|---|
| format version | 2 | 1, which fixes the suite: AES-256-GCM with 96-bit nonces and 128-bit tags, keys derived by HKDF-SHA-256, and key commitment |
| frame size | 1 | its base-2 logarithm, 12 to 20 |
| object id | 16 | random, or the uid of a dataset's data object |
| binding | 32 | the SHA-256 of what the writer binds the object to, such as a dataset's id and a commit object's version, or zeros |
| key id | 1 + up to 255 | the keyring's name for the key that wraps the data key |
| key context | 1 + up to 255 | what the keyring passed to the key service with the data key: a dataset's id, or nothing for a single file. It is in clear because a reader needs it to unwrap the key |
| wrapped data key | 2 + up to 1 024 | the data key, as the keyring wrapped it |
| salt | 32 | random, for the first epoch's key |
| commitment | 32 | proves which key the frames were sealed with (see [keys](#4-keys)) |

The trailer adds what is known only at the end: the plaintext length of each epoch, where its frames
start, and the salt and commitment of the epochs after the first, which [appends](#36-appends) write.

Nothing in the descriptor is authenticated by a tag of its own. It does not need one: the SHA-256 of
the descriptor, taken without its commitment, enters the derivation of every key that seals a frame,
so a descriptor changed by one bit derives other keys, and the commitment check refuses them before
any frame is decrypted. A descriptor left intact on an object moved elsewhere is caught otherwise: the
reader compares the object id and the binding with what it expects (see [datasets](#63-datasets)).

### 3.3 Frames and nonces

The frames follow the segments of Tink's streaming AEAD, AES-GCM-HKDF, in which a key derived for one
stream seals each segment under a nonce made of the segment's index and a last-segment flag. Each
epoch of each object has its own key (see [keys](#4-keys)), so the nonce only has to be unique within
one epoch, and Tink's random nonce prefix has nothing left to do. The nonce of frame `i` is twelve
bytes: `i` as a little-endian 64-bit integer, then a 32-bit word of flags whose bit 0 marks the
epoch's last frame. Frames carry no associated data. A writer emits the frames of an epoch once and in
order, so no nonce is used twice under a key, which is what the deterministic construction of NIST
SP 800-38D asks. An epoch holds at most 2^32 frames, within every limit the standard sets on the
invocations of one key.

A frame cannot move, since its index is in its nonce. An epoch cannot lose its end or be extended,
since only its last frame carries the final flag, the reader takes from the trailer which frame is
last, and GCM authenticates the length of each frame's ciphertext, so a trailer that misstates the
last epoch's length makes its last frame fail. Every epoch's first plaintext offset is bound into its
key, and the reader checks that each epoch begins where the one before it ends, so no earlier epoch's
length can be misstated either.

### 3.4 Reading a range

A plaintext range `[a, b)` maps to the frames `a / F` through `(b − 1) / F`, a contiguous ciphertext
range. A scan's coalesced reads therefore stay coalesced: each plaintext run becomes one ciphertext run,
one request on a store, one positional read on a disk, widened to whole frames. The ciphertext arrives
in a pooled buffer, the frames are decrypted from it into one 64-byte aligned block, and each segment
is a slice of that block, as with a plaintext run. A frame whose tag fails wipes the block and fails
the read, so no decoder ever sees bytes that were not authenticated.

### 3.5 Opening

A session that holds a keyring widens the first read of a file opened by its tail to the trailer and
the two frames of the default size before it, about 130 KiB. The last two frames of an epoch hold
more plaintext than the 64 KiB a plaintext open reads first, so a sealed file opens in one request on
a store, as a plain one does. A plain file opened by such a session costs 64 KiB more in that read,
which a store answers in the same round trip. On a disk, where a plaintext open reads its last
8 KiB, the sealed open reads about 130 KiB, one positional read. A file sealed with frames larger
than 64 KiB, or whose trailer is longer than usual, which only a long key id or many epochs make,
costs a second read, as a footer larger than the window does today.

A sealed file is recognised by the last bytes of its tail read, `VXSE` where a plaintext file has
`VTXF`, so a session without a keyring that opens one fails with a message saying the file is sealed.
The session's keyring then unwraps the data key, or finds it in its cache, derives the epoch's key,
compares the commitment, and from there the file is a Vortex file read through the frames.

### 3.6 Appends

A Vortex append writes new blocks after the old end of the file and a new tail after them, and never
rewrites a byte before the old end, which is what lets an abandoned append truncate the file back to
what it was. A sealed append keeps that property with epochs. It starts after the last trailer, seals
its bytes as frames of its own under the data key the file already names, with a fresh salt and so a
fresh key, and ends with a trailer that lists every epoch. The plaintext the core reads is the epochs
one after the other, exactly the plaintext append. An abandoned append is truncated back to the
previous trailer, and the file is again what it was.

Readers handle any number of epochs, since the cost is a short table. An append needs the data key the
file names, so it runs in a session whose keyring unwraps it, whether or not that session seals the
files it writes. A session that seals them refuses to append to a plain file, which would add
plaintext to it.

## 4. Keys

### 4.1 Three levels

| key | where it lives | what it seals | how often it changes |
|---|---|---|---|
| wrapping key | in the keyring: a key service such as AWS KMS or Azure Key Vault, which it never leaves, or a key the application holds | data keys only | when its owner rotates it |
| data key, 256 random bits | wrapped in the descriptor of every object it served, unwrapped in memory and cached | a dataset's objects, or the files a session seals | a dataset keeps its data key until it is rekeyed, and a session draws one when it seals its first file |
| epoch key | derived for one epoch of one object, never stored | that epoch's frames | per object, per epoch |

A dataset's data key is the one its latest commit object is sealed with. A writer unwraps it to read
that commit, and seals what it writes under it, so every writer of a dataset shares one data key and a
reader unwraps it once per process. The key service is then asked once per process and dataset, when
a handle first opens it, and no write asks it again. Each object still derives keys of its own from
fresh salts, so no two objects share a key and frame counters never repeat under one, however long
the data key lives, as the single key of a Tink streaming AEAD serves every stream it seals. A data
key per object, wrapped each time, would instead put a call to the key service on every object
written and on every object opened, tens of milliseconds and a charge each.

### 4.2 The derivation

For an epoch `e` of an object, with the data key `DK`:

```
PRK        = HKDF-Extract(salt = the epoch's salt, IKM = DK)
info       = "vorticity/sealed/v1" ‖ SHA-256(descriptor without its commitment) ‖ e ‖ the epoch's first plaintext offset
key        = HKDF-Expand(PRK, "key" ‖ info, 32)
commitment = HKDF-Expand(PRK, "commit" ‖ info, 32)
```

`e` is written as a 32-bit and the offset as a 64-bit little-endian integer. The hash leaves the
commitment out because the commitment is computed from it. The salt is 32 random bytes, so two epochs
never derive the same key short of a collision of 256-bit values. The descriptor's hash binds the
object id, the binding, the frame size, the key id and the key context, so none of them can change
without changing the key. The epoch's index and first offset bind its place, so the epochs of an
appended file cannot be reordered or dropped from the middle.

The commitment is compared, in constant time, before any frame is decrypted. GCM alone does not commit
to its key: a ciphertext can be crafted that authenticates under two keys, and a reader whose keyring
holds several keys could be shown different plaintexts for one object. The AWS Encryption SDK commits to
its data key for the same reason, with a value derived from that key and stored with the message.

### 4.3 The keyring

A session holds a keyring, which generates and unwraps data keys:

```csharp
public abstract class VortexKeyring
{
    public abstract ValueTask<DataKey> GenerateAsync(ReadOnlyMemory<byte> context, CancellationToken cancellationToken);
    public abstract ValueTask<DataKey> UnwrapAsync(string keyId, ReadOnlyMemory<byte> wrapped, ReadOnlyMemory<byte> context, CancellationToken cancellationToken);
}
```

A `DataKey` holds the key in native memory, the id of the key that wrapped it, and its wrapped bytes,
and wipes the key when it is disposed.

| keyring | where | wraps with |
|---|---|---|
| `VortexKeyring.FromKeys`, keys the application holds, the first one current | the core | AES-256-GCM, a random nonce per wrap, and the key id and context as associated data |
| `AwsKmsKeyring` | `Vorticity.ObjectStorage`, on the clients of [17-object-storage.md](17-object-storage.md) | KMS `GenerateDataKey` and `Decrypt`, the context passed as the encryption context |
| `AzureKeyVaultKeyring` | `Vorticity.ObjectStorage` | Key Vault `wrapKey` and `unwrapKey`, RSA-OAEP-256, or AES key wrap on a managed HSM. Neither takes a context, so the context is bound by the derivation alone, and the key id recorded names the key's version, which unwrapping needs |
| `GoogleCloudKmsKeyring` | `Vorticity.ObjectStorage` | Cloud KMS `encrypt` and `decrypt` with a symmetric key, the context passed as additional authenticated data |

Other key services, OVHcloud's or Scaleway's for instance, derive from the same abstract class.

The context is a dataset's random id, minted when an encrypted dataset is created (see
[datasets](#63-datasets)), or nothing for a single file. A key service records it in its audit log,
so it never carries a secret, and the descriptor carries it in clear, since a reader must hand it back
to unwrap the key before it can read anything, as the AWS Encryption SDK keeps its encryption context
in clear in its message header.

Unwrapped data keys are cached by key id and wrapped bytes, in native memory, a bounded number of them
for a bounded time, and wiped when they leave the cache or the session is disposed. Keys handed to
`FromKeys` are copied into native memory the same way, and the caller's copy is the caller's.

### 4.4 Rotation, rekeying and shredding

A wrapping key rotates in its service, and AWS KMS, Key Vault and Cloud KMS keep its older versions
able to unwrap, so objects sealed before stay readable. A dataset changes its data key when asked,
by `RekeyAsync`: the commit that does it draws a new data key and is sealed under it, and every writer
then follows the latest commit. Older objects keep the key they were sealed with until a compaction
rewrites them, so a full rekey is a rekeying commit, a compaction of every object and a vacuum, whose
price is a rewrite of the dataset. An application that wants a data key to last a given time rekeys
on that schedule.

Destroying a wrapping key makes every object it served unreadable, on any store, including one under a
retention lock that refuses deletes until a date. That is the one erasure such a store allows. It
erases whatever the key served, so a dataset meant to be erased this way needs a wrapping key of its
own, and a process that still holds the data key in memory reads on until it lets the key go.
[Stores that cannot delete](15-compaction.md#5-stores-that-cannot-delete) names a key per data subject
as the answer for single rows, and the rows of many subjects share objects, so that remains outside
this design.

## 5. The cipher, from the platform

### 5.1 Why the platform's

The cipher is the platform's: AES-256-GCM through `AesGcm`, HKDF-SHA-256 through `HKDF`, SHA-256
through `SHA256.HashData`, entropy through `RandomNumberGenerator`, and constant-time comparison and
wiping through `CryptographicOperations`. They run in the operating system's library, OpenSSL on Linux,
CNG on Windows, and Apple's libraries on macOS, CryptoKit for AES-GCM.

A cipher written in this repository was weighed and set aside. It would allocate nothing per frame,
and neither do the platform's span methods. It would be slower on x86: .NET 11 exposes the AES round
instructions on 128-bit registers only, and not the vector AES instructions (VAES) that the native
libraries use where the processor has them, with which AES-256-GCM runs about twice as fast as with
AES-NI alone on recent x86 cores. And it would need its own constant-time fallback for processors without
AES instructions and its own review, and could never be a validated module, where the platform's
library is audited, constant time on the processors it supports, and validated on a host configured
for FIPS 140.

What stays here is what is specific to sealing: the format, the nonces, the inputs of each
derivation, the commitment check, the keyring and its cache, the policy, and the order in which a
block is decrypted, checked and handed on.

### 5.2 Using it well

- An `AesGcm` instance imports its key once into a native context and holds no lock, so an instance
  serves one thread at a time. On Linux it keeps one OpenSSL context per instance. An open sealed
  object keeps an instance per epoch key and per lane that reads it at once, created when first
  needed and disposed with the object, which frees the platform's copy of the key.
- Each frame is one call into the platform's library, a handful of native calls under it on OpenSSL: the
  nonce, the bytes, the end and the tag. Over 64 KiB that cost is small but not nil, and it is one of
  the inputs to the frame size (see [what it costs](#7-what-it-costs)).
- A frame is decrypted from the ciphertext buffer into its place in the plaintext block, never in
  place, since the tags sit between the frames and the block holds the plaintext end to end. When a
  tag fails, the platform clears the plaintext it wrote and throws `AuthenticationTagMismatchException`,
  and the reader wipes the whole block before it fails.
- `AesGcm.IsSupported` is checked when a session that holds a keyring is created, so a platform without
  it, a browser, refuses the session with a message rather than failing at the first file.
- The library's banned symbols forbid every AES API but `AesGcm`, so no unauthenticated mode can be
  written by mistake.
- Keys, salts and ids come from `RandomNumberGenerator` through one helper of the sealing code, and
  nonces from the frame index inside it. No API of this library takes a nonce, so a caller cannot
  reuse one.

### 5.3 What it must pass

- Known-answer vectors of the sealed format: files sealed with fixed keys, salts and ids, checked in byte
  for byte, written and read the same on Linux, Windows and macOS in CI. They hold the three platform
  libraries to one format, and catch any change in how this library uses them.
- The derivations, through the platform's `HKDF`, against the vectors of RFC 5869, to check the order of
  the arguments this library passes rather than the platform's arithmetic.
- A review by someone outside the project, of the format and of how it uses the cipher, before the
  encryption is called stable.

## 6. Transparency

### 6.1 The session's keys and policy

```csharp
VortexKeyring keyring = VortexKeyring.FromKeys(new VortexKey("app-2026-10", key));
await using VortexSession session = VortexSession.Create(o =>
{
    o.Keyring = keyring;
    o.EncryptFiles = true;
    o.RefusePlaintext = true;
});
```

| option | effect |
|---|---|
| `Keyring` | the keys the session seals and decrypts with. A sealed file opens whenever its key is in the ring, whatever the other two options say |
| `EncryptFiles` | every file the session writes is sealed: to a path, to a location, or to a caller's pipe |
| `RefusePlaintext` | a plain file or a plain dataset is refused at open, so a reader that expects sealed data cannot be handed plain bytes in their place |

Nothing else in the API changes. `OpenAsync`, `CreateWriter`, `OpenWriterAsync`, every scan, query and
sink behave on a sealed file as on a plain one. Indexing a file after its write is the one place where a
static entry point, `VortexFileIndexer`, works in the default session, so the session gains the two
calls that take a path, `AppendIndexesAsync` and `BuildFragmentAsync`, which index a sealed file
through its keys.

### 6.2 Files

A sealed file is read positionally and never mapped, so it goes through the session's segment cache and
bound on reads in flight, as a file read with `MapFiles = false` does. The cache then holds plaintext,
which the session's documentation says, and which a session that keeps no cache avoids.

A writer seals through a stage between itself and its sink. The stage is the pipe the writer writes
into: it hands out the frame it is filling, encrypts it into the sink's buffer once it holds `F` bytes
and appends the tag, so sealing adds the cipher's pass and no copy to the ones a plain write makes. At
completion, it seals the last frame and writes the trailer. Like the writer, the stage only moves
forward.

A sealed file whose last append was torn by a crash behaves as a plain one does. It ends with neither
magic, and its header still says it is sealed. The open walks back to the last whole trailer, one whose
layout reads under the header's descriptor, checks the commitments of its epochs, reads the version
that trailer describes, and reports the tear in `VortexFile.TornTail`, unless
`VortexOpenOptions.TornTail` refuses it. `VortexFileRepair` truncates the file to that trailer's end,
without the key, since finding the trailer needs none.

Indexes added after the write follow the same rules. Appended to a sealed file, the runs and the new
tail are an epoch, built in a scratch that holds the sealed bytes only and copied behind the file. A
fragment holds column values, so the fragment of a sealed file, or one built in a session that seals
what it writes, is sealed in the same format under the session's keys, and a reader opens it with its
own. A fragment a session cannot open is left out with its reason, as a fragment of another file is,
since an index is only a hint and never fails the open. A session that refuses plaintext leaves out a
plain fragment too, which could otherwise steer its pruning, except those an encrypted dataset reads
from its own sealed commits.

### 6.3 Datasets

A dataset is encrypted when it is created with `DatasetOptions.Encrypted`. Its first commit object is
sealed under a data key drawn for it, with a dataset id of 16 random bytes, minted at creation, as the
key context. From then on a handle knows the dataset is encrypted because its latest commit is sealed.
It seals every object it writes, data objects and commit objects, and refuses a dataset that mixes
sealed and plain objects, a plain commit after a sealed one included. Given to a handle of an existing
dataset, `Encrypted` refuses one that is plain. Nothing in the commit header changes, so the dataset's
format does not either, and a plain dataset becomes encrypted only by being copied into a new one. The
compaction loops' leases hold only a version number and are never read, so they stay plain.

Each object is bound to what names it:

| object | the reader checks | so that |
|---|---|---|
| data object | its object id against the uid its leaf entry records | an object swapped for another sealed object, of this dataset or another, fails |
| commit object | its binding against the SHA-256 of the dataset's id and the version its key names | a commit object copied under another version's key, or into another dataset, fails |
| page, fragment, deletion vector | nothing of its own | they are covered by the frames of the commit object they live in |

The dataset's id that these checks compare with is the key context of the latest commit a handle
opens first, as a handle trusts the latest version it lists. The object id and the binding are both
bound into the object's keys, so neither can be altered to pass the check.

A sealed file imported into an encrypted dataset keeps the binding it was written with: the import reads
its object id from its descriptor, and its entry records it. A plain file is refused.

An entry's content hash stays the XXH3-128 of the plaintext bytes, which is the identity of the content,
whatever key sealed it. The checksum sent to the store covers the sealed bytes, which are what travels,
and the sealing stage computes it as they leave it. Verification decrypts every object and checks its
frames, then its hash, and compares its two descriptors.

Opening a data object stays one request: the entry gives the length (see
[requests per operation](17-object-storage.md#5-requests-per-operation)), and the first read covers the
trailer and the Vortex tail.

### 6.4 Scratch

A group by that spills, and a sort that writes runs, put the data they hold on local disk. When the
session seals its files, its scratch is sealed too: frames of 64 KiB under AES-256-GCM, each written
once when it is full, with the frame index as its nonce, under a key drawn for that scratch file, which
no keyring wraps, held in native memory and never written anywhere. Nothing outside the process reads
the file, so it has no header and no trailer, and the frame being filled is served from memory. After
a crash the scratch is unreadable, which is what scratch should be. A spill reads its sections by
offset, which maps onto frames as any range does.

## 7. What it costs

| | sealed, against plain |
|---|---|
| requests to the store | the same: the descriptor and the trailer ride on reads already made |
| calls to the key service | one per data key and process, a dependent step when a dataset or a sealed file is first opened, and none with keys the application holds |
| bytes stored | 16 per frame, 0.02 % at 64 KiB, and two descriptors per object, a few hundred bytes |
| bytes read | each coalesced run widened to whole frames, at most one frame more at each end. A run of several megabytes on a store grows by a few percent. A local point read of 4 KiB decrypts one or two frames, 64 to 128 KiB |
| processor | one pass of AES-256-GCM in the operating system's library over every byte read or written, bytes that are already compressed, and one call into that library per frame |
| a local file | read positionally and decrypted into pooled blocks, where a plain one is mapped and read in place |
| allocations | none per batch and none per frame. A few cipher contexts per open object, created when first needed |

The frame size trades the amplification of small reads against the tags and the call per frame. At
4 KiB, a point read decrypts little, the file grows by 0.4 % and the calls into the platform's library
weigh most. At 1 MiB, the file grows by about 15 parts per million and a point read decrypts a megabyte.
64 KiB is the starting point, and the write option that sets it per file or per dataset stays, since the
right value depends on the reads.

The loss of the mapping is the largest local cost: a held mapping reads two to three times faster than
a positional read ([I/O](03-architecture.md#35-io)). It is the price of sealing a local file, and it is
measured with the cipher's own cost, on both instruction sets.

## 8. Mistakes this design rules out

DuckDB added database encryption in version 1.4.0, and CVE-2025-64429, published in November 2025,
recorded four flaws in it. Each one has its rule here:

| flaw | rule |
|---|---|
| keys and nonces drawn from a non-cryptographic generator | entropy only from the operating system, through `RandomNumberGenerator` |
| key memory cleared with a call the compiler could remove | keys in native memory, wiped with `CryptographicOperations.ZeroMemory`, which is not elided, and the platform's copies freed by disposing its `AesGcm` instances |
| a header edit that downgraded GCM to an unauthenticated mode | no suite field at all, a format version that fixes the suite, no unauthenticated mode, and the descriptor bound into every key |
| a failed call to the random generator left unchecked | a failure throws, and nothing falls back |

And the classic mistakes with GCM:

| mistake | rule |
|---|---|
| a nonce used twice under one key | a fresh key per object and per epoch, nonces from the frame index inside the sealing code, and no API of this library that takes a nonce |
| plaintext used before its tag is checked | every frame is checked before the block it lands in is handed on |
| a ciphertext that authenticates under two keys | key commitment, checked before the first frame |
| pages left unauthenticated for speed, as Parquet's `AES_GCM_CTR_V1` allows | no such mode |
| tags truncated below 128 bits | 128-bit tags only |
| an algorithm chosen by the file | a new suite is a new format version, which an older reader refuses with a `VortexUnsupportedException` of kind `Encryption` |

## 9. Errors

| case | exception |
|---|---|
| no key of the ring unwraps the data key | `VortexEncryptionException`, `NoKey`, naming the key id |
| a tag, a commitment, an epoch, an object id or a binding that fails, among them the platform's `AuthenticationTagMismatchException` | `VortexEncryptionException`, `Unauthenticated`. Tampering and corruption cannot be told apart, so they are reported alike |
| a plain file or dataset under `RefusePlaintext`, a plain dataset opened with `Encrypted`, or a dataset that mixes sealed and plain objects | `VortexEncryptionException`, `Refused` |
| a format version this build does not know | `VortexUnsupportedException` of kind `Encryption`, the kind the core already declares |

`VortexEncryptionException` derives from `VortexException`, as the core's other exceptions do.

## 10. How it is tested

- The sealed format's known-answer vectors on the three platforms, as
  [what it must pass](#53-what-it-must-pass) lists.
- Tampering, exhaustively on small objects: every byte flipped one at a time, a truncation at every
  offset, frames reordered or taken from another object, objects swapped in a dataset, the descriptor
  or the epochs altered, a plain object put in place of a sealed one. Each case must fail at the first
  read that uses the altered bytes, before any plaintext is used, and verification must find every one.
- Fuzzing: an envelope mutated at random, and in the fields its structure hangs on, fails with the
  format's own exceptions or reads back what was sealed, bit for bit, quickly. The walk that repairs
  a file without the key is fuzzed with it. A few thousand mutations run with every test run, and a
  longer campaign on demand.
- Transparency: every kind of query, from the rows and a filter to a group by, an ordered top-k and a
  walk of a key index, runs over a sealed file and over the plain file its plaintext is, at one and at
  many lanes, and over an encrypted dataset and a plain one of the same objects, and every answer is
  the same bits.
- Ratchets: the same requests and dependent steps for sealed and plain reads, held by the counting
  store, a single call to the key service per data key, and no allocation per batch on a sealed scan.
- Throughput: the platform's cipher per frame size, and a sealed scan against a plain one, each measured
  alone on arm64 and on x64, and on each operating system, since each runs another library.

## 11. The order of the work

1. The sealed format for single files, over the platform's `AesGcm` and `HKDF`: the writer's stage, the
   reader's layer, the open's wider read, repair, `vxdump decrypt`, the known-answer vectors on the
   three platforms, and the tampering suite. The cost of a call per frame is measured here, and sets the
   default frame size.
2. The session's keyring and policy.
3. Encrypted datasets: the shared data key, the bindings, commit objects read by their head, rekeying
   and verification.
4. Sealed scratch.
5. The key service keyrings, on the clients of the object storage package.
6. Appends to sealed files, as epochs.

## 12. Decisions to take

| decision | recommended | why |
|---|---|---|
| an envelope rather than the format's reserved fields | the envelope | it reads the same in every Vortex reader once decrypted, seals every object the same way, and hides the structures too |
| the cipher | the platform's `AesGcm` and `HKDF`, decided | faster where the processor has VAES, audited and constant time, validated on a host configured for it, and allocation-free through its span methods |
| the construction | the segments of Tink's streaming AEAD, with the AWS Encryption SDK's key commitment | known and reviewed constructions, adapted rather than invented |
| the default frame size | 64 KiB, to measure | small reads against the tags and the call per frame |
| a dataset's data key | one, shared by its writers, changed by a rekey | one call to the key service per process to read a dataset and none to write, while each object keeps keys of its own |
| how a dataset records that it is encrypted | by its sealed commits, with no field in the commit header | the dataset's format does not change, and a dataset that mixes sealed and plain objects is refused |
| scratch sealed whenever files are | yes | plaintext of sealed data should not reach a disk |
| appends to sealed files | readers know epochs from the start, writers later | no format break when appends come |
| FIPS 140 | the host's | the cipher runs in the operating system's library, which a host configured for FIPS provides as a validated module, to be checked on each platform |

## 13. What this rests on

Each claim is either read in this repository's code, taken from a source checked on 2026-10-10, or a
proposal of this design, to be measured.

| claim | where it comes from |
|---|---|
| the format's encryption fields are reserved and `EncryptionSpec` is empty | code: `spec/flatbuffers/footer.fbs`, vendored at Vortex 0.86.1 |
| the core already has a component kind `Encryption` for unsupported components | code: [error handling](03-architecture.md#5-error-handling) |
| a plaintext open reads the last 64 KiB of an object in a store and the last 8 KiB of a local file | code: `VortexOpenOptions.cs` (`DefaultInitialReadBytes`), `LocalFileSource.cs` (`TailReadSize`) |
| an append never rewrites a byte before the old end, a torn one leaves the previous version readable, and an abandoned one truncates back | [appending to a file](11-write-strategy.md#38-appending-to-a-file) |
| a commit object opens by its head, a data object's key and postscript carry its uid | [objects and keys](13-dataset.md#3-objects-and-keys), [identity and integrity](13-dataset.md#7-identity-and-integrity) |
| a held mapping reads two to three times faster than a positional read | [I/O](03-architecture.md#35-io), measured in this repository |
| a cipher written in C# on .NET 11 cannot use VAES: the runtime exposes AES-NI and VPCLMULQDQ on x86, AES and PMULL on arm64, and no vector AES | code: the `System.Runtime.Intrinsics` reference assembly of .NET 11 RC1 (`Aes`, `Pclmulqdq.V256`, `Pclmulqdq.V512`, `Arm.Aes`, and no `Aes.V256` or `Aes.V512`) |
| VAES and VPCLMULQDQ AES-256-GCM against the AES-NI code on 16 KiB messages: about +100 % on Zen 4 and up to +157 % on Sapphire and Emerald Rapids | source: Eric Biggers, "crypto: x86/aes-gcm - add VAES and AVX512 / AVX10 optimized AES-GCM", Linux kernel mailing list, May and June 2024, two years old but the code the kernel runs today |
| `AesGcm`: the tag size given to its constructor, `AuthenticationTagMismatchException` and the plaintext cleared when a tag fails, no support in a browser | source: .NET API reference, `AesGcm` and `AesGcm.Decrypt` |
| on Linux, `AesGcm` keeps one OpenSSL context per instance, imports its key once, makes a handful of native calls per operation, and holds no lock | code: dotnet/runtime, `AesGcm.OpenSsl.cs`, read on 2026-10-10 |
| a stream's key derived by HKDF from one key and a random salt, segments sealed under a nonce of their index and a last-segment flag, without associated data | source: Tink documentation, AES-GCM-HKDF streaming AEAD |
| GCM's deterministic nonce construction, and the limits on the invocations of a key | source: NIST SP 800-38D (2007), the standard in force |
| HKDF | source: RFC 5869 (2010), the standard in force |
| key commitment derived with the data key, and the encryption context kept in clear in the message header | source: AWS Encryption SDK developer guide, key commitment and message format |
| Parquet's modular encryption: a key per column, GCM per module, and `AES_GCM_CTR_V1`, which leaves pages unauthenticated | source: Apache Parquet format, `Encryption.md` |
| DuckDB's four encryption flaws, fixed in 1.4.2 | source: CVE-2025-64429, published 2025-11-12 |
| the cost of one call into the platform's library per frame, against the frame size | proposal, measured in the first phase |
| 64 KiB frames, a first read of two frames and the trailer | proposal, starting points to set by measurement |
| the cost of sealed scratch against the cost of the spill's disk | proposal, to measure |
