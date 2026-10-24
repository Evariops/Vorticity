# Rows whose keys are data: variant columns, shredded per object

Telemetry rows carry, beside a few columns every row has, attributes whose keys are data. One source
names them one way, another source another way, the same source changes them over time, and a log's
body may be a line of text or an object of any shape. This document is the record of the design that
stores such rows so that a question on any attribute (a filter, a grouping, a count) runs as fast as on
a declared column, while every file stays a plain Vortex file that a Rust reader of the edition
`core2026.08.3` reads.

It was written as a proposal on 2026-10-18 against `main` at `485e796b`, and built from it. Its idea
fits in one paragraph. An attribute bag is one column of the format's variant type. In each data
object, the writer looks at the paths its rows hold and writes those that pay as typed columns inside
the variant, as the format already allows under the name of a shredded variant. Rare keys and odd
types stay in a binary residual. A path directory, beside the object's other indexes, says where each
path lies and counts the keys left in the residual. The format gives a shredded path the encodings of
a column but none of the pruning its layout gives a column, so block statistics and indexes per path,
in the same index directory, give it that. A question resolves each path in each object: a typed
column is read like any column, and a residual is decoded only in the chunks that hold the path.

The pieces it built are described now where their components are ([what was built, and
where](#3-what-was-built-and-where-it-is-described)). This document keeps what those descriptions do
not: the need, the models weighed against each other, the examples, the worst cases, the decisions
and the alternatives.

Each claim says where it comes from:

| tag | meaning |
|---|---|
| (code) | read in the code or the vendored specification, linked to its lines |
| (measured) | measured, by the bench or test it names |
| (estimate) | arithmetic on the example |
| (hypothesis) | a design bet, with what would refute it |
| (source) | an external source, listed with its date in [sources](#9-sources) |

## 1. The need

### 1.1 The rows

An OpenTelemetry log record has a few columns every record has, and four parts whose keys are data:

```csharp
[VortexRecord]
public partial record struct LogRecord(
    DateTime At,
    string ServiceName,
    byte SeverityNumber,
    string? TraceId,
    Variant Resource,      // k8s.pod.name, host.name, cloud.region, …
    Variant Scope,         // the instrumentation scope's attributes
    Variant Attributes,    // http.route, http.response.status_code, user.id, db.query.text, …
    Variant Body);         // a line of text, or an object
```

Three rows from three sources show what varies:

```
web      Attributes {"http.route": "/orders/{id}", "http.response.status_code": 502, "user.id": "u-81"}
         Body       "upstream timed out after 30s"
database Attributes {"db.system": "postgresql", "db.query.text": "SELECT …", "db.response.rows": 17}
         Body       "slow query"
mobile   Attributes {"app.build": 4411, "feature.flags": ["new-cart", "dark"]}
         Body       {"event": "checkout", "cart": {"items": 3, "total": 52.10}, "device": {"os": "ios"}}
```

### 1.2 What varies

On a large deployment (estimates): 10³ to 10⁵ distinct keys a day, 10 to 50 keys per row, a key held
by anywhere from 0.01 % to every row, 10² to 10³ distinct shapes a minute, values of every type
(text, integers, floats, booleans, lists, nested objects), the same key holding an integer in one
source and text in another, and a few sources that put identifiers in their keys (`user.4711.score`),
which makes the set of keys grow with the data. OpenTelemetry's keys hold dots as part of the key:
`http.route` is one key, not `route` inside `http`.

### 1.3 The questions

Any filter and any grouping over any path, presence of a path, which keys are present and how often,
a row shown whole, and text search:

```csharp
from r in logs.Scan<LogRecord>()
where r.At >= from && r.At < to
   && r.Resource["k8s.namespace.name"].AsString() == "checkout"
   && r.Attributes["http.response.status_code"].AsInt64() >= 500
group r by r.Attributes["http.route"].AsString() into g
orderby g.Count() descending
select (g.Key, g.Count())
```

### 1.4 The targets

- A path an object shredded is read as a declared column is: the same kernels, the same bytes per
  value, the same pruning and the same indexes. The format gives a declared column
  its own bytes and its zone maps through the layout, and gives a path neither, so Vorticity adds both
  in its index directory.
- A value keeps its type class. `AsInt64()` converts nothing: it takes the path's integers where they
  are stored, and a question that asks a path for one type reads only that type's column. The integers
  of a path share one typed column at the widest width the path holds, so a value is rebuilt at that
  width, equal in value to the one written.
- A path an object did not shred costs the rows that hold it, never every row.
- A new key changes no schema.
- Ingestion pays nothing for shredding: appends write variants as they come, and shredding happens in
  a job that rewrites the rows anyway.
- Every file reads in Rust 0.85.0 and later, under the edition `core2026.08.3`, which carries a
  read-forever guarantee.

## 2. Terms

| term | what it is | not to be confused with |
|---|---|---|
| variant | a value of the format's `Variant` dtype: null, a primitive, a list or an object, in the Parquet variant binary encoding | a C# union, the `Union` dtype |
| attribute bag | a variant column whose values are objects of keys to values | a struct column, whose fields the schema declares |
| path | the keys from a variant's root to a value, each key whole: `["http.route"]` is one key holding a dot, `["cart"]["items"]` is two | a column path, which names struct fields of the schema |
| shredding | writing one path's values as a typed column inside the variant, as the format allows | flattening a variant into the schema's own columns |
| residual | the binary `value` that keeps what was not shredded | |
| shape | the set of paths the object shreds that a row holds, with their classes, and whether its residual holds anything | a schema |
| path directory | per object, every path and class of value its rows hold, with their counts, where they lie and their bounds | a dataset's schema |
| keys that are data | keys many and each rare, in an object of their own or at the top of a flat bag (`user.4711.score` beside `http.route`) | an attribute bag's keys, which repeat |
| column budget | how many shredded paths an object may hold, from what each costs against what it saves | the index budget |
| path map | per object, for each shredded key, where its buffers lie in each chunk and its part of the encoding tree, kept in the index directory | the path directory, which says whether and how a path is stored |
| type class | the types a question takes as one: the integers of every width, from `int8` to `int64`, are one class, text another | a dtype, which also fixes a width |
| block statistics | per shredded key and per block of 8 192 rows, the key's least and greatest value and count, kept in the index directory | a zone map, which the format keeps per column of the layout, never per path |

## 3. What was built, and where it is described

| piece | where it is described |
|---|---|
| reading both shredded forms, the reference's tree and the Parquet layout, and writing the reference's | [90-registry.md](90-registry.md#1-array-encodings), [01-scope.md](01-scope.md), [04-conformance.md](04-conformance.md) for the corpus the reference writes |
| the `Variant` type, paths, `As…`, `To…` and `Has` in questions, typed and on the tool path | [07-dotnet-mapping.md](07-dotnet-mapping.md), [14-public-api.md](14-public-api.md), [16-queries.md, variant paths](16-queries.md#36-variant-paths), [08-semantics.md](08-semantics.md) for a path absent or of another type |
| what an object shreds, the cost model, shapes, bodies, the validity of shredded fields, level 0's variants | [11-write-strategy.md, time slices and shredded variants](11-write-strategy.md#9-time-slices-and-shredded-variants), [90-registry.md, the writer's schemes](90-registry.md#5-the-writers-schemes-and-their-ids) |
| the path map, the path directory, block statistics and indexes on paths | [10-indexes.md](10-indexes.md#45-vorticityvariantpathsv1-where-each-shredded-key-of-a-variant-lies), and the three kinds after it |
| the synopses on paths, per column, to come | [22-on-the-fly-and-synopses.md](22-on-the-fly-and-synopses.md#5-the-synopses-all-per-column) |

The guide shows them from the caller's side ([records.md](../guide/records.md),
[untyped-files.md](../guide/untyped-files.md), [indexes.md](../guide/indexes.md),
[datasets.md](../guide/datasets.md)).

### 3.1 The models, compared

Seven ways to store an attribute bag, and what a question on one key pays, on rows of 20 attributes
whose values take about 5 bytes each once compressed (estimates):

| model | how it is stored | a filter or a group by on one key reads | statistics per key | Rust reads it |
|---|---|---|---|---|
| a map of strings, keys dictionary-encoded | per row, a list of keys and a list of values | every key of every row, then the matching values: about 120 bytes a row, 60 to 100 times one column | none: statistics mix every key | yes, `Map` and `vortex.map` |
| the same map, with chosen keys copied out as columns | the map, and a column per chosen key | one column for a chosen key, the whole map for the others | for the chosen keys only | yes |
| an unshredded variant | one binary per row | every row's binary, parsed | none | yes, `core2026.08.3` |
| an unshredded variant plus a list of each row's keys | the binary and a list of keys | the lists, to find the rows that hold the key, then their binaries | presence only | yes |
| numbered columns, assigned to keys per object | `slot_1` to `slot_1000`, and a mapping per object | one column, once the object's mapping is read | per object only: `slot_17` holds another key in the next object | yes, but the names mean nothing |
| one declared column per key | the dataset's own columns, added by schema evolution | one column | yes | yes |
| a variant shredded per object | typed columns inside the variant for the paths that pay, a residual for the rest | the buffers of one typed column, located by the path map and read alone, or the residual of the chunks that hold the key | yes, kept by Vorticity in its index directory, since the format gives a path none | yes, `core2026.08.3` |

The map translates directly: a Vortex `Map` whose keys are text in a dictionary and whose values are
text is what the relational mapping of OpenTelemetry stores (source), and Vorticity writes it
([90-registry.md](90-registry.md)). It answers every question, slowly: one key costs the whole bag, and
no statistic or zone map describes a key alone. The same mapping copies a handful of
Kubernetes keys out of the map into columns of their own, by name, which is the admission that the
map is not enough, and the opposite of a design that does not know its questions.

A list of each row's keys, next to an unshredded variant, says which rows hold a key, which a Bloom
filter over the lists then prunes. It answers presence and nothing else, and stores every key again
on every row. A shredded key's validity gives presence, and the path directory counts it per object.

Numbered columns bound the columns of an object as shredding does, but a column means a different key
in each object. Nothing that spans objects (the tree's summaries, the synopses beside the rows, a reader
that is not ours) can use them, and a file read alone tells nothing.

Declared columns are the fastest, and impossible here: every new key is a commit of the schema, the
schema only grows, and every object answers for keys it never holds.

A shredded variant has the speed of declared columns for the paths it shreds, the generality of the
binary for the rest, and the format's own name for both, so a Rust reader reads it whole.

## 4. The examples

On the example's volumes, thirty periods of a day. Every figure of these tables is an estimate,
written before anything was built.

| question | what answers it |
|---|---|
| a count per `Attributes["http.route"]` and per day, 30 days, the path shredded | its typed column, as for a declared column; once the synopses come, its counts by bucket of time |
| `ServiceName` under `Attributes["http.response.status_code"] >= 500`, per minute, the last hour | the two columns over the last hour, the path's typed one and the declared one |
| the rows holding `Attributes["feature.flag.new-cart"]`, per minute, the last day | the path's typed column, or the residual of the chunks that hold the key |
| `Attributes["feature.flag.new-cart"] == "on"`, per service and per day, 30 days, the key left in the residual of most objects | the residual of the chunks that hold it, and the partial learned for the next time |
| the average of `Attributes["http.server.request.duration"]` per route and per day | the typed columns of the two paths |
| the keys that are data most often held, such as `Attributes["user.4711.score"]` and its siblings, 30 days | the path directories' counts per period |
| the count per `Attributes["http.route"]` of the first row, cold | two round trips per object, in parallel: the path directory, then the typed column's chunks |
| `AsInt64() >= 500` on `http.response.status_code`, where one source sends the code as text | the typed column alone: that source's rows answer null, and are never read |
| the same with `ToInt64()`, which converts the text | the typed column, and the field's `value` on that source's rows, parsed |

The same questions run on generated logs of three sources, at a 2 500th of the example's rate, 24
periods of an hour, behind a store that counts and waits 30 ms a round trip (`vortex-queries --micro
questions`): every one reads its rows, a path's typed column where its object shreds it. A key the
application declares shredded (`DatasetOptions.ShreddedPaths`) is a column of every object whatever
share of the rows holds it. `ToInt64()` reads every class, and `AsDouble()` reads floats too, which the
double class leaves out, from the field's `value` of the rows that hold them.

## 5. The worst cases

| case | what it costs | what the design does | what remains |
|---|---|---|---|
| a path left in an object's residual | the residual of the chunks that hold it | the path directory says which chunks hold it, and postings find its rows | a parse per row that holds it |
| a path left in the residual, asked jointly with other columns per minute | the residual of every chunk of the window that holds it | shredding it, where the column budget allows | rows |
| a path held by 10 to 90 % of a chunk's rows | the typed column's slot for every row, null or not: 2.1 bytes a row for an integer, 2.0 for a double, 1.1 for a text, 0.55 for a boolean (measured), and the validity | the validity goes through the compressor, as runs where it has few (measured: a path held by half the rows costs no bit per row of validity), and the cost model weighs the null slots | the null slots, so a path held by half the rows can cost more shredded than whole |
| a chunk whose rows hold most keys of their object | the paths of every key in its tree and its path map | each chunk shreds only the keys its rows hold, within the object's column budget | most of the object's keys in each chunk, at a few hundred bytes each |
| a path whose chunks encode it differently | path map entries | a path's encoding chosen per chunk, its map one zstd frame per key, 32 to 82 bytes a chunk (measured) | none worth fixing the encoding per object |
| a path read with no open of its object | a round trip more per object, cold | each variant column's path directory is named in the table of contents | one round trip, cold only |
| keys that are data | one lookup: the blocks its postings name. A grouping by key: every entry of the path directories | the path directory, which counts the keys of the residual | a grouping over keys reads them all |
| a path whose type class changes from row to row | for a question on the class left in the field's `value`, that `value` in every chunk that holds the class | the dominant class is shredded per chunk, and the path map's counts skip the chunks that hold none of the class asked | half and half costs the parse of half the rows, for the class not shredded |
| a range on a shredded path whose values the rows scatter, such as a duration | the path's typed column in every chunk that holds it | block statistics kept where they prune, those that prune least dropped first under the index budget | the path's bytes, as for a declared column |
| rows not yet out of level 0, which appends write unshredded | every variant of those rows parsed, for any path: eight appends of three seconds hold 1.2 × 10⁶ rows, about 120 MB of attributes and 60 to 120 ms of one core to parse, spread over the lanes (estimate) | the compaction loop shreds them as they leave level 0, and each query node keeps a shredded copy of an object of level 0 until it leaves; a handle that shreds its declared keys at each append (`ShredOnAppend`) leaves objects read as they lie | a parse per node and per object of level 0, and more behind a lagging loop |
| an object at its column budget | the paths left over stay in the residual | the paths that pay least are the ones left | their rows' residual |
| a question over keys no chunk shreds | each chunk's segment, its residual parsed | the chunk gives its residual alone, the keys it shreds neither decoded nor put back, three times faster over log records | the residual's parse |
| many whole rows shown | a take per shredded column the rows hold | the path map names the columns each chunk holds | in proportion to the rows and their paths |
| text searched in every attribute at once | every text column of the object, and the residual | trigram indexes per column | inherent to a search with no key |
| a comparison across types (`"200"` against 200) | both representations of the path | the question names the conversion it means, `ToInt64()`, and `As…` never converts | rows of the type that is not shredded |
| the keys of thirteen months, when keys are data | every key of every object | the path directories, cached | output-bound |
| a path scanned over a long window | a request per chunk that holds it, 1.3 × 10⁵ over 30 days of the example (estimate) | the per-column synopses, once they come, answer most questions without a scan | the requests of the scan |
| a variant whose chunks carry no path map: an object another writer wrote, or one written before this design | each chunk's segment read whole | the next compaction that rewrites the object builds its path map | the whole segment, until then |
| many shredded paths over many chunks | path map entries, and trees, in proportion to paths times chunks | long variant chunks, per-chunk shredding of the keys a chunk holds, and the column budget | the paths past the budget stay in the residual |
| a question touching many paths of one chunk | as many buffer ranges per chunk | coalescing, which joins neighbouring ranges | close to the whole segment, which is then what it reads |

## 6. How it is held

| promise | gate |
|---|---|
| a variant reads back as it was written, whatever the shredding | a fuzzer of documents of every shape (nesting, lists, mixed types, keys that are data, keys with dots), written under random choices and read back equal, value for value, integers equal under their class (`ShreddedVariantWriteTests`) |
| the format's rules | tests of a missing key, a type mismatch, a residual disjoint from the shredded keys, and a key present in both, which is refused (`ShreddedVariantWriteTests`, and the two conflict cases of the Parquet suite) |
| Rust reads what we write, and we read what Rust writes | shredded variants in the cross-check, both ways (`verify_shredded`: our files against their twins written whole, value for value; `gen_shredded_variants`: the reference's shredder's files, read here) |
| the same answers shredded or not | every question of a matrix over paths, on the same rows written shredded and unshredded |
| the column budget | the bytes of the file and the tables of its largest encoding tree (`ShreddingChoiceTests`) |
| ingestion does not pay for shredding | appended objects carry no path map, and the append path takes no shredding (`ShreddedDatasetTests`) |
| shredding costs little where it happens | `--micro shredwrite`: 181 ns a row to count and choose, 310 more to write shredded, for six to eight attributes |
| a path read fetches only its bytes | no byte of another key read beyond the padding two ranges share, on a counting store (`VariantKeyReadTests`) |
| a range on a path skips what it skips on a declared column | five ranges keep the same live blocks on the key and on a column holding the same values (`VariantBlockStatsTests`) |
| an index on a path never matches across classes | a path holding 502 as an integer in some chunks and `"502"` as text in others: each class's question finds its own rows (`VariantPathIndexTests`) |
| the path map stays small | at most 90 bytes a key and a chunk (`VariantPathMapTests`) |
| nulls cost what they hold | a validity of two runs written as runs, and Rust reads it back (`ShreddedVariantWriteTests`) |
| a chunk shreds what it holds | each chunk's tree holds its rows' keys alone, and keyed reads serve the chunks that lack one (`VariantKeyReadTests`) |
| a key in the residual puts nothing back | a key no chunk shreds, read as stored, converted and under a filter, from the residuals alone, answers as on the rows written whole; beside a shredded key, the chunks are put back as before (`VariantKeyReadTests`) |
| keys that are data are counted | four hundred user keys among others, at every append and compaction, appended and written objects alike (`VariantDiscoveryTests`) |
| a path with dots stays one key | `http.route` and `http` holding `route` are two paths, in an index policy, the tool path and a plan |

## 7. Decisions

| | question | decided | the other way |
|---|---|---|---|
| 1 | how an attribute bag is stored | a variant shredded per object | a map, numbered columns, or declared columns ([the models, compared](#31-the-models-compared)) |
| 2 | which variant form | `vortex.variant` with its shredded child over a `vortex.parquet.variant` core, the form the reference converts every shredded variant to before writing | `vortex.parquet.variant` with a `typed_value`, the Parquet layout, which the reference reads but never stores |
| 3 | types | strict: `AsInt64()` takes the path's integers as stored and gives null for a text, so the typed column is enough. A conversion is a `To…` the question names | loose conversions by default, which read the field's `value` too |
| 4 | paths on the typed path | a path is a value, as a map's key is | paths only on the tool path |
| 5 | object bodies | stored once, shredded, rebuilt to show | kept as text as well |
| 6 | keys that are data | in the residual, counted by the path directory, which serves as the key table | a declared map column beside the variant, or a Bloom filter of the keys, which can neither list nor count them |
| 7 | the shape in the order | none since the synopses became per column: no slice is sorted by its rows' shape | over the shredded paths and a bit for the residual, placed by the object's first slice, as the first design did |
| 8 | the column budget | by the cost: the bytes a path's occurrences cost in the residual against what its column costs, null slots included | a fixed number of paths |
| 9 | paths left in the residual | counted by the path directory, parsed where a question reads them | counted by the synopsis tables of the first design, where their budget admitted them |
| 10 | the order inside a slice | the order the rows came in, until a cluster column orders them | by the runs each column adds, fixed per object, as the first design did |
| 11 | what the table of contents names of the paths | each variant column's path directory | the synopsis tables of the first design besides, about 50 bytes each |
| 12 | where shredding happens | in the jobs that already rewrite every row of a slice, level 0's exit, the fresh merges and the seal, with level 0's variants shredded in memory once per query node; the keys an application declares (`DatasetOptions.ShreddedPaths`) shredded by every job whatever their share of the rows, and at each append too by a handle that asks it (`ShredOnAppend`): 65 ms an append of 100 000 rows of eight keys against 23, for an object 5 % smaller (measured) | in every append, as other writers do, which puts its cost on every ingestion |
| 13 | reading one path | the path map, and only the buffers of the top-level key read | every chunk's segment read whole, every path's bytes with it |
| 14 | the variant column's chunks | long chunks of its own, about 10⁶ rows | the file's chunks, which multiply the trees |
| 15 | where what the format lacks for a path lives | the index directory, which Rust never reads, until an edition freezes a layout for paths | a layout of our own, which a strict Rust reader refuses to open ([where an index lives](10-indexes.md#2-where-an-index-lives)) |
| 16 | pruning on a path below the object | block statistics per shredded key, kept where they prune, those that prune least dropped first under the index budget | none below the object, or statistics for every path in every block |
| 17 | indexes on a path | entries that name a column, a path and a type class: a Bloom filter per key and class, and hashed postings for an identifier among the attributes | indexes on declared columns only, which leaves a needle in an attribute to every block that holds the path |
| 18 | the integers of one path | one typed column for every width, rebuilt at the widest, equal in value under the format's classes | a column per width, which splits a path between its typed column and its field's `value` |
| 19 | the validity of a shredded field | through the compressor, as runs where it has few, which Rust reads. `vortex.sparse` at nine nulls in ten, where each byte it saves costs about a third of a nanosecond of decode on integers, doubles and text alike (measured) | a plain bitmap, a bit per row and per field, or `vortex.sparse` chosen per chunk by that price, which makes about the same choices and needs the dense plan priced first |
| 20 | the shapes a variant chunk holds | each chunk shreds the keys its rows hold (measured) | chunks cut where the shape changes, or a budget that counts a chunk's shapes |
| 21 | a path's encoding across chunks | chosen per chunk, each key's region of the path map one zstd frame, 32 to 82 bytes a chunk (measured) | fixed per object, at a cost in compression |
| 22 | the variants of level 0 | shredded in memory once per query node and object, kept until it leaves level 0, a lock per object and its memory reserved before the copy; an object an append shredded read as it lies, with no copy | shredded in the append, from the previous object's choice of paths, for every handle |

## 8. Alternatives weighed

| alternative | what it offers | why it was not chosen |
|---|---|---|
| one shredding schema for the whole dataset, from the keys most rows hold | one layout to plan for | the keys of small sources fall under any global threshold, and the schema ages as sources change. Each object choosing alone adapts to both |
| frequent sets of keys per tile of 2¹⁰ to 2¹² records, records moved between neighbouring tiles, as JSON tiles do (source) | columns for what most records of a tile share | the same idea at a finer grain: a chunk shreds only the keys its rows hold, and the cost model replaces their threshold |
| a map of keys to values | simple, and Rust reads it | one key costs the whole bag, and no statistic describes a key ([the models, compared](#31-the-models-compared)) |
| a table of one row per key and value, sorted by key | a key's values together | every question over two keys joins rows back |
| numbered columns | a bounded number of columns | a column means another key in each object |
| declared columns added by schema evolution | the fastest reads | a commit per key, a schema that only grows, and objects that answer for keys they never hold |
| a layout of our own that makes each shredded path a column of the layout, with its zone map | the format's own pruning on paths, and a segment per path | a strict Rust reader fails to open a file whose layout it does not know, so every file with a variant would stop reading in Rust ([where an index lives](10-indexes.md#2-where-an-index-lives)). Proposing such a layout upstream is the way, and the index directory serves until an edition freezes one |
| a double class that takes floats too | `AsDouble()` answered from the typed column alone even in an object that holds a float | the Parquet rules keep a float out of a double's typed column, so a double class that took them would no longer be the reference's shredding. A float is read instead from the field's `value` of the rows that hold one |

## 9. Sources

Read on 2026-10-18. Older sources are marked as such, and a vendor's or a blogger's account is a lead,
not a proof.

- Apache Parquet, *Variant shredding*, the specification on the format's main branch, read again on
  2026-10-18: the layout of `metadata`, `value` and `typed_value`, the rules for missing keys,
  mismatched types and partially shredded objects, the reader's rebuild, statistics valid only against
  the shredded type ("When the corresponding value column is all nulls, all values must be the
  shredded typed_value field's type"), files free to shred differently, and one Parquet type per
  variant type, with no word on widening one.
  <https://github.com/apache/parquet-format/blob/master/VariantShredding.md>
- D. Durner, V. Leis, T. Neumann, *JSON tiles: fast analytics on semi-structured data*, SIGMOD 2021
  (older, best paper award): tiles of 2¹⁰ to 2¹² tuples, frequent sets of key paths paired with their
  types, columns for the frequent ones, the others in a binary form with a Bloom filter of their keys,
  records reordered between neighbouring tiles, statistics per tile.
  <https://db.cit.tum.de/~durner/papers/json-tiles-sigmod21.pdf>
- OpenTelemetry Collector contrib, the SQL table templates of a column store's exporter, for logs, JSON
  logs and traces: attributes as a map of low-cardinality keys to strings with Bloom filters on keys
  and values, or a JSON type with a list of keys, a few Kubernetes keys copied out as columns, rows
  ordered by a five-minute bucket, the service and the instant.
  <https://github.com/open-telemetry/opentelemetry-collector-contrib/blob/main/exporter/clickhouseexporter/internal/sqltemplates/logs_table.sql>,
  <https://github.com/open-telemetry/opentelemetry-collector-contrib/blob/main/exporter/clickhouseexporter/internal/sqltemplates/logs_json_table.sql>,
  <https://github.com/open-telemetry/opentelemetry-collector-contrib/blob/main/exporter/clickhouseexporter/internal/sqltemplates/traces_table.sql>
- S. Shah, *Experimenting with Apache Iceberg v3 with and without variant shredding*, 2026. A single
  laptop, Spark 4.1.1 and Iceberg 1.12, 1.8 million GitHub events over ten writes, about 300 inferred
  fields, an inference buffer of 10 000 rows: appends 34.6 % slower with shredding, from 22 972 to
  17 073 rows a second. The causes it names (inference, extraction, conversion, a Parquet column per
  field) are not isolated by a measure. A lead.
  <https://medium.com/@shahsoumil519/experimenting-with-apache-iceberg-v3-with-and-without-variant-shredding-impact-on-append-speed-cb4bcf958c61>
- A post on Iceberg v3 shredding on S3 Tables, 2026: a test it reports, writes about 2.7 times slower
  and storage 20 % larger, reads 34 % faster, by testers and data it does not name. A lead.
  <https://iceberglakehouse.com/posts/iceberg-v3-variant-shredding-s3-tables/>
- Apache Spark, pull request 52406, inference of a shredding schema per file: the writer buffers the
  first rows, keeps a field with a consistent type as a `value` and a `typed_value`, drops fields
  present in less than 10 % of the sampled rows, and caps the schema at 300 fields.
  <https://github.com/apache/spark/pull/52406>
- Vendor documentation that sums shredding up as 20 to 50 % slower writes for faster reads, without
  saying how it was measured. A lead. <https://docs.acceldata.io/odp/documentation/sql-features>
- The documentation of the reference's `vortex-parquet-variant` crate: a storage struct of a required
  `metadata`, an optional `value` and an optional `typed_value`, nested shredding through the same
  pair, and a list of paths and types that says what to shred.
  <https://docs.rs/vortex-parquet-variant/latest/vortex_parquet_variant/>
- duckdb-delta, pull request 390: a table format that forbids shredded files unless the table enables
  them, and a scan that does not push field projections into shredded columns. A lead on how readers
  differ. <https://github.com/duckdb/duckdb-delta/pull/390>
- paimon-rust, pull request 1069: a reader changed to extract shredded fields from their typed columns
  rather than decode each row's variant first. A lead on what a reader that rebuilds rows pays.
  <https://github.com/apache/paimon-rust/pull/1069>
- vortex-java, issue 445: the reference's default writer emits `vortex.parquet.variant` for variant
  columns under `core2026.08.3`, and another reader of the format did not read it yet. A lead on how
  unevenly the format's readers follow it. <https://github.com/dfa1/vortex-java/issues/445>
- Apache Parquet, *Variant binary encoding*, the specification on the format's main branch: every
  value carries its type, and types form equivalence classes, the integers from `int8` to `int64` and
  the decimals being one class, exact numerics, within which an expression behaves the same whatever
  the width. It says nothing of a writer changing a value's width, which this design does within the
  class. <https://github.com/apache/parquet-format/blob/master/VariantEncoding.md>
- Vortex, issue 7717, *Epic: Variant Type and Array*, last updated 2026-04-29 and marked as proposed:
  a dtype, a canonical array, Parquet's variant encoding read without a copy, and an expression that
  extracts a path. No layout for paths. <https://github.com/vortex-data/vortex/issues/7717>
- Vortex, issue 8119, *Variant write-path features*, opened 2026-05-27: shredding called an open
  research topic, with JSON tiles as its reference. <https://github.com/vortex-data/vortex/issues/8119>
- Vortex, pull request 8391, merged 2026-06-23: an expression that converts JSON text into Parquet
  variants, shredded, through the Arrow implementation of the encoding.
  <https://github.com/vortex-data/vortex/pull/8391>
