# A list's elements in the form the file holds them

A reader of a list column gets its elements decoded, whatever form the file holds them in. This document proposes that a reader who asks for columns in their encoded form get a list's elements that way too: a dictionary's codes and its distinct values, where today it gets every element's bytes gathered.

It was written as a proposal on 2026-11-23 against `main` at `c9fdd706`. Nothing in it is built, and nothing in it is scheduled: it waits for the measurement of section 5 and the maintainer's decisions of section 7. Each claim is marked *(read)* when it was read in the code, *(measured)* when it was run, and *(proposal)* when it is this document's.

## 1. What a reader of a list gets today

A chunk of a list column chooses the encoding of its elements as a column chooses its own ([the writer's choice](11-write-strategy.md#34-choose-exact-verdicts-then-bounded-trials)). Elements that repeat, such as the frames of a profile's stack, come out as a dictionary of the chunk's distinct values and a code per element. On a file of 200 000 rows whose list holds 8 to 20 names drawn from 30, the 25 chunks of the list are all written `vortex.list(vortex.dict(fastlanes.for(fastlanes.bitpacked), vortex.zstd), …)`, as `vxdump --layout` shows *(measured)*.

The scan keeps a column in its encoded form only where its consumer reads that form ([encoded forms](14-public-api.md#54-encoded-views-and-the-selection)). The decode context grants the form to a column's root, passes it on to an extension's storage and to a struct's field, and to nothing else (`ArrayDecodeContext.KeepsEncoding`, `ExtensionDecoder`, `StructDecoder`) *(read)*. A list's decoder decodes its elements as a child with no grant (`ListDecoder.cs`, `ListViewDecoder.cs`) *(read)*, so the dictionary decoder gathers each element's bytes out of the values (`DictDecoder.Gather`) *(read)*. On the same file, read with `Scan<T>()`, a top-level text column arrives 25 times of 25 as `Dictionary`, and the list's elements 25 times of 25 as `Canonical` *(measured)*.

A consumer that builds something per distinct element, a tree of stack frames for instance, then hashes each element's bytes to find it again, where the file had already numbered them. It can do that today without allocating a string: `elements[i]` is the element's UTF-8 as a span of the batch ([text columns](../guide/text-columns.md)).

## 2. What changes

A list column that the consumer reads encoded gets its elements in their encoded form too: `Elements.Encoding` is `Dictionary`, `RunEnd` or `Constant` as the chunk holds them, and `Elements.AsDictionary()` gives the chunk's codes, one per element, and its distinct values *(proposal)*. A dictionary is the chunk's: two batches cut from one chunk share it, and the next chunk has its own, so a consumer that keeps codes across batches translates them at each new dictionary, once per distinct value, as it does for a top-level dictionary column.

Only the sink whose consumer reads `Column<T>.Encoding` asks for it: the enumeration of `Scan<T>` (`Scan.Typed.cs`, `GetAsyncEnumerator`), its `Columns<T>` and the untyped `VortexColumn` it hands out *(proposal)*. The scan spec gets a flag of its own, `KeepListElements`, beside `KeepEncodings`. The group-by engine, which keeps encodings too (`AggregationEngine.cs`, `KeepEncodings = true`) *(read)*, does not set it, and its list paths keep reading canonical elements.

The grant reaches the elements by the same rule as a struct's field: the list's decoder grants its elements child when it is granted itself and the scan asks for list elements, and decodes it with `KeepEncodingInNext` before its offsets and sizes *(proposal)*.

## 3. What reads a list's elements between the decode and the consumer

A batch passes through the scan's own kernels before the consumer sees it. Each that reads a list's elements must either keep their form or decode them, never misread them.

| reader | what it does with a list's elements | under this proposal |
|---|---|---|
| the filter of rows (`CanonicalFilter.FilterListView`) | keeps the elements node and gathers the rows' offsets and sizes *(read)* | keeps the encoded elements: nothing to change |
| the cut into batches (`CanonicalSlice.SliceListView`) | the same *(read)* | nothing to change |
| a list predicate, `Contains` (`ListKernels`) | slices the window of elements the rows name, then compares it with `ComparisonKernels.Compare` *(read)* | a slice of a dictionary keeps its codes (`CanonicalSlice`), and the comparison answers a dictionary by its values, then its codes (`EncodedAnswers`) *(read)*: the predicate reads each distinct value once, where it compared every element |
| the merge of small batches (`CanonicalConcat.ConcatListView`) | concatenates the batches' elements; a chunk in an encoded form is decoded first (`ConcatDecoding`) *(read)* | correct, and the form is lost where batches are merged. Section 7 asks whether to merge the dictionaries instead |
| `Column<ReadOnlyMemory<T>>` and `ListColumn` | a row's range, and `Elements` as a column of the elements node (`ColumnExtensions`, `ListColumn.Elements`) *(read)* | `Column<T>` and `VortexColumn` already read a dictionary node: their accessors decode it once per batch (`EncodedForms.Canonical`, `VortexColumn.Require`) *(read)* |
| the record path (`ToRecordsAsync`) and the group-by engine | read canonical elements' bytes (`ColumnData`, `ColumnAccess`, `CompositeKeys`, `GroupOrder`) *(read)* | never granted: unchanged |
| the writer, a merge and a compaction | read decoded batches (`ArrayBlobWriter`, `ColumnWriter`, `ChunkCompactor`) *(read)* | never granted: unchanged |

The code reads a list's elements node in 50 places *(read)*. The rule that keeps the change safe: a reader on the column-batch path either keeps the elements' form or resolves them with `EncodedForms.Canonical` before it reads their buffers, and every other reader runs only on paths that never set the flag. A test enumerates the readers of `ElementsIndex` on the column-batch path, as the residue tests read the sources, so that a new one cannot slip in without the resolution *(proposal)*.

## 4. What it costs

Nothing when no consumer asks: the flag is off on every path but one. A consumer that asks and then reads the elements' values anyway pays the gather once per batch, when it first reads them, as it pays for a top-level dictionary column today. The batch holds the codes and the values instead of the gathered bytes, which is less memory for elements that repeat.

## 5. What to measure before building it

The gain is the gather skipped, a copy of every element's bytes, and the hash a consumer pays per element to find it again. The consumer that asked for this, a tree of profile stacks, can already avoid the strings: a table of its own from the UTF-8 span of each element to a number. The measure compares the two on real profiles, the frames of an hour of samples, 13 to 33 frames a row:

1. Today's path: `Scan<T>()`, each frame read as `elements[i]` and looked up in the consumer's own table, in nanoseconds per row.
2. A prototype of this proposal on a branch: the frames read as codes, each chunk's dictionary translated once into the consumer's numbering.

If the second does not take a third off the first, the proposal is not worth its 50 readers to watch, and is withdrawn.

## 6. How it would be tested

- A list of repeated text read by `Scan<T>`: `Elements.Encoding` is `Dictionary` on every batch whose chunk holds a dictionary, and the codes and the values give back every element.
- The same file read with a filter on another column, with `Contains` on the list, and with batches small enough to be merged: the rows are those of a canonical read.
- The record path and a group by over the same file are unchanged.
- The gather's allocation is gone from a read that asks for codes, in the allocation ratchets.

## 7. Decisions for the maintainer

1. Whether to build it at all, once section 5 is measured.
2. The merge of small batches: decode the elements, which loses their form where batches are merged, or merge the batches' dictionaries, which costs a hash per distinct value per batch and keeps it.
3. Whether the group-by engine should later read list elements encoded too, for list predicates and chosen rows: a gain of its own, which this proposal leaves out.
