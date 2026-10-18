# Rows whose keys are data: variant columns, shredded per object

Telemetry rows carry, beside a few columns every row has, attributes whose keys are data. One source
names them one way, another source another way, the same source changes them over time, and a log's
body may be a line of text or an object of any shape. This document proposes how a dataset stores
such rows so that a question on any attribute (a filter, a grouping, a count) runs as fast as on a
declared column, while every file stays a plain Vortex file that a Rust reader of the edition
`core2026.08.3` reads.

It is a proposal written on 2026-10-18 against `main` at `485e796b`, and nothing in it is built. Its
idea fits in one paragraph. An attribute bag is one column of the format's variant type. In each data
object, the writer looks at the paths its rows hold and writes those that pay as typed columns inside
the variant, as the format already allows under the name of a shredded variant. Rare keys and odd
types stay in a binary residual. Inside each time slice, rows are sorted by their columns as in [the
time-window design](20-time-window-aggregates.md), and by their shape among them, the set of shredded
paths they hold, so that every path's rows lie together. A path directory, beside the object's other
indexes, says where each path lies, and a key table counts the keys left in the residual. The format gives a shredded path the encodings of a column but
none of the pruning its layout gives a column, so block statistics and indexes per path, in the same
index directory, give it that. A question resolves each path in each object: a typed column is read
like any column, and a residual is decoded only on the rows whose shape holds the path.

Each claim says where it comes from:

| tag | meaning |
|---|---|
| (code) | read in the code or the vendored specification at `485e796b`, linked to its lines |
| (estimate) | arithmetic on the example, to be replaced by a measure |
| (hypothesis) | a design bet, with what would refute it |
| (source) | an external source, listed with its date in [sources](#15-sources) |

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
  value, the same pruning, the same indexes and the same synopsis. The format gives a declared column
  its own bytes and its zone maps through the layout, and gives a path neither, so Vorticity adds both
  in its index directory ([what the format gives a path, and what Vorticity
  adds](#510-what-the-format-gives-a-path-and-what-vorticity-adds)).
- A value keeps its type class. `AsInt64()` converts nothing: it takes the path's integers where they
  are stored, and a question that asks a path for one type reads only that type's column. The integers
  of a path share one typed column at the widest width the path holds, so a value is rebuilt at that
  width, equal in value to the one written ([how a variant is stored](#52-how-a-variant-is-stored)).
- A path an object did not shred costs the rows that hold it, never every row.
- A new key changes no schema.
- Ingestion pays nothing for shredding: appends write variants as they come, and shredding happens in
  a job that rewrites the rows anyway ([writing, and what it costs](#57-writing-and-what-it-costs)).
- Every file reads in Rust 0.85.0 and later, under the edition `core2026.08.3`, which carries a
  read-forever guarantee.

## 2. Terms

| term | what it is | not to be confused with |
|---|---|---|
| variant | a value of the format's `Variant` dtype: null, a primitive, a list or an object, in the Parquet variant binary encoding | a C# union, the `Union` dtype |
| attribute bag | a variant column whose values are objects of keys to values | a struct column, whose fields the schema declares |
| path | the keys from a variant's root to a value, each key whole: `["http.route"]` is one key holding a dot, `["cart"]["items"]` is two | a column path, which names struct fields of the schema |
| shredding | writing one path's values as a typed column inside the variant, as the format allows (`typed_value`) | flattening a variant into the schema's own columns |
| residual | the binary `value` that keeps what was not shredded | |
| shape | the set of paths the object shreds that a row holds, with their types, and whether its residual holds anything | a schema |
| path directory | per object, every path that pays its entry, with its type, its row count and where it lies | a dataset's schema |
| key table | per object and bucket, every key the residual holds, with its count and postings to its blocks | the path directory, which lists only the paths that pay their entry |
| keys that are data | keys many and each rare, in an object of their own or at the top of a flat bag (`user.4711.score` beside `http.route`) | an attribute bag's keys, which repeat |
| column budget | how many shredded paths an object may hold, from what their nodes cost in each chunk's encoding tree and their entries in the path map | the synopsis's byte budget |
| encoding tree | the description of an array's encodings and of where its buffers lie, written at the end of the array's segment, and which a flat layout may also carry in the footer, inlined | the layout tree, which describes columns and chunks |
| selective buffer read | the read of only some buffers of a segment, located beforehand, rather than of the whole segment | a selective decode, which decodes some rows of what was read |
| path map | per object, for each shredded path, its part of the encoding tree and where its buffers lie in each chunk that holds it, kept in the index directory | the path directory, which says whether and how a path is stored |
| type class | the types a question takes as one: the integers of every width, from `int8` to `int64`, are one class, text another | a dtype, which also fixes a width |
| block statistics | per shredded path and per block of 8 192 rows, the path's minimum, maximum and null count, kept in the index directory | a zone map, which the format keeps per column of the layout, never per path |

## 3. The models, compared

Seven ways to store an attribute bag, and what a question on one key pays, on rows of 20 attributes
whose values take about 5 bytes each once compressed (estimates):

| model | how it is stored | a filter or a group by on one key reads | statistics and synopsis per key | Rust reads it |
|---|---|---|---|---|
| a map of strings, keys dictionary-encoded | per row, a list of keys and a list of values | every key of every row, then the matching values: about 120 bytes a row, 60 to 100 times one column | none: statistics mix every key | yes, `Map` and `vortex.map` |
| the same map, with chosen keys copied out as columns | the map, and a column per chosen key | one column for a chosen key, the whole map for the others | for the chosen keys only | yes |
| an unshredded variant | one binary per row | every row's binary, parsed | none | yes, `core2026.08.3` |
| an unshredded variant plus a list of each row's keys | the binary and a list of keys | the lists, to find the rows that hold the key, then their binaries | presence only | yes |
| numbered columns, assigned to keys per object | `slot_1` to `slot_1000`, and a mapping per object | one column, once the object's mapping is read | per object only: `slot_17` holds another key in the next object | yes, but the names mean nothing |
| one declared column per key | the dataset's own columns, added by schema evolution | one column | yes | yes |
| a variant shredded per object, rows sorted by shape | typed columns inside the variant for the paths that pay, a residual for the rest | the buffers of one typed column, located by the path map and read alone, or the residual of the rows whose shape holds the key | yes, kept by Vorticity in its index directory, since the format gives a path none | yes, `core2026.08.3` |

The map translates directly: a Vortex `Map` whose keys are text in a dictionary and whose values are
text is what the relational mapping of OpenTelemetry stores (source), and Vorticity writes it
([90-registry.md](90-registry.md)). It answers every question, slowly: one key costs the whole bag, and
no statistic, zone map or synopsis describes a key alone. The same mapping copies a handful of
Kubernetes keys out of the map into columns of their own, by name, which is the admission that the
map is not enough, and the opposite of a design that does not know its questions.

A list of each row's keys, next to an unshredded variant, says which rows hold a key, which a Bloom
filter over the lists then prunes. It answers presence and nothing else, and stores every key again
on every row. [Shapes](#55-shapes) give presence from one code per row, sorted into runs.

Numbered columns bound the columns of an object as shredding does, but a column means a different key
in each object. Nothing that spans objects (the tree's summaries, the synopsis's coarse tables, a
reader that is not ours) can use them, and a file read alone tells nothing.

Declared columns are the fastest, and impossible here: every new key is a commit of the schema, the
schema only grows, and every object answers for keys it never holds.

A shredded variant has the speed of declared columns for the paths it shreds, the generality of the
binary for the rest, and the format's own name for both, so a Rust reader reads it whole.

## 4. What the format and Vorticity have today

| what | where |
|---|---|
| the dtypes `Variant` (tag 11), `Union` (12) and `Map` (13) | [02-format.md](02-format.md#4-dtype-dtypefbs--dtypeproto) |
| the arrays `vortex.variant` and `vortex.parquet.variant`, added by the edition `core2026.08.3`, read by Rust 0.85.0 and later, frozen with a read-forever guarantee | (code: [core2026.08.3.toml](../../spec/editions/core2026.08.3.toml)) |
| `vortex.parquet.variant`: a `metadata` child, a `value` child, and an optional `typed_value` child holding shredded paths | (code: [VariantMetadata.cs](../../src/Vorticity/Vortex/Arrays/Metadata/VariantMetadata.cs#L89-L105)) |
| `vortex.variant`: a variant over its storage, with an optional shredded child | (code: [VariantMetadata.cs](../../src/Vorticity/Vortex/Arrays/Metadata/VariantMetadata.cs#L7-L21)) |
| Vorticity writes the unshredded form, and reads both shredded forms, the reference's tree and the Parquet layout, by mapping each onto the Parquet groups its Parquet reader rebuilds rows from | (code: [VariantDecoder.cs](../../src/Vorticity/Vortex/Arrays/Decoders/Compressed/VariantDecoder.cs), `ShreddedTree`) |
| no .NET type maps a variant column | [07-dotnet-mapping.md](07-dotnet-mapping.md) |
| maps are written under `core2026.08.3`, and refused below it | [90-registry.md](90-registry.md) |
| a column added after creation is a commit of the schema | [13-dataset.md](13-dataset.md#13-schema-evolution) |
| the reference writes a variant column as one `vortex.flat` segment per chunk, the whole array in it | (code: the corpus's `parquet_variant.vortex` under `vxdump --layout`) |
| the shredded type lies in each array node's metadata, so each chunk may shred its own way | (code: [VariantMetadata.cs](../../src/Vorticity/Vortex/Arrays/Metadata/VariantMetadata.cs#L89-L105)) |
| a flat layout may carry its encoding tree inlined in its metadata, which readers accept since 0.84. Vorticity reads such a tree, and skips the segment when the tree declares no buffer, but never writes one, and reads a segment whole | [02-format.md](02-format.md#6-layouts-layoutfbs), (code: [FlatLayoutReader.cs:11-19](../../src/Vorticity/Vortex/Layouts/FlatLayoutReader.cs#L11-L19)) |
| the edition `core2026.08.3` adds the two variant arrays and no layout, so a shredded path is a child of the variant's array, never a column of the layout | (code: [core2026.08.3.toml:13-22](../../spec/editions/core2026.08.3.toml#L13-L22)) |
| a zone map aggregates one column of the layout, with the edition's aggregates: minimum, maximum, their bounded forms, NaN count and null count | (code: [core2026.08.3.toml:77-84](../../spec/editions/core2026.08.3.toml#L77-L84)) |
| an index entry names a column, and a reader ignores the entry unless that column's dtype is one its kind supports, so no index covers a variant's paths | [10-indexes.md](10-indexes.md#3-the-directory-and-the-runs) |
| a filter on a dictionary or on run ends is answered once per value or per run, and spread over the rows | (code: [EncodedAnswers.cs:8-17](../../src/Vorticity/Compute/EncodedAnswers.cs#L8-L17)) |
| the reference shreds when it converts JSON to variants. Its plan for variants is still a proposal, and names no layout for paths | (source) |
| our writer keeps a validity bitmap plain, a bit per row, and writes `vortex.sparse` only for a chunk of nulls on nine rows in ten or more | [90-registry.md](90-registry.md#5-the-writers-schemes-and-their-ids) |
| index policies name a nested column by a path whose fields are separated by dots | (code: [IndexPolicy.cs:63](../../src/Vorticity/Vortex/Indexes/IndexPolicy.cs#L63)) |
| Parquet's shredding maps each variant type to one Parquet type, says nothing of widening one, and holds a column's statistics valid only when every value has the shredded type | (source) |

## 5. The model

### 5.1 Declaring and asking

An attribute bag is a member of type `Variant`, and the dataset's schema says only that: the column is
a variant. A question walks into it by path:

| member | gives |
|---|---|
| `v["http.route"]` | the value under one whole key, dots included |
| `v["cart"]["items"]`, `v[3]` | a nested key, an element of a list |
| `AsString()`, `AsInt64()`, `AsDouble()`, `AsBool()`, `AsTimestamp()` | the value as it is stored, when the path holds that type, and null otherwise. `AsInt64()` takes an integer of any width |
| `ToInt64()`, `ToDouble()` and the other `To…` | a value converted from another type, the text `"200"` read as 200, and null when it does not convert |
| `Has("http.route")` | whether the path is present: true or false, never unknown |

`As…` converts nothing. Every value of a variant carries its type, in the binary as in a shredded
column, so `"502"` and `502` are two different values that were written differently. `AsInt64()` takes
the path's integers where they are stored: in a shredded path's typed column, whose values are stored,
encoded and compared as a declared column's are, or in the residual, decoded. The names follow [the
API's rules](14-public-api.md#11-names): `As` is the same data in another form, and `To` makes a new
value the caller owns, which a conversion does. A conversion reads every representation of the path,
and the format itself warns that comparisons across types cannot be pruned on (source), so a question
pays for one only when it names it.

A path that is absent, or holds another type, gives null, so a filter on it is unknown and keeps no
row, under the three-valued logic every filter follows ([08-semantics.md](08-semantics.md#3-three-valued-logic)).
The tool path takes the same paths in its text filters, where the literal's type says which `As…` is
meant: `Attributes['http.response.status_code'] >= 500` asks for integers.

The member is checked by the compiler, and a path is a value, as a map's key or a list's index is. A
path names data, not the schema. Whether the typed path accepts it is one of the [decisions to
take](#13-decisions-to-take).

A path is a list of keys, never a string with dots: `http.route` is one key. Index policies name a
nested column by a path whose fields are separated by dots (code:
[IndexPolicy.cs:63](../../src/Vorticity/Vortex/Indexes/IndexPolicy.cs#L63)), so wherever a path of a
variant becomes text, in an index policy, the tool path's filters, a plan or a metric, it is written
as its column, its keys and its type class apart, each key quoted whole.

### 5.2 How a variant is stored

As the reference stores it: a `vortex.variant` whose core storage is a `vortex.parquet.variant` of the
rows' metadata and residual values, and whose second child, the shredded tree, holds the typed columns.
The reference turns any shredded `vortex.parquet.variant` into that form before its writer compresses
it (code: vortex-parquet-variant 0.86.1, `vtable.rs:276-288` and `array.rs:182-296`, unchanged in
0.87.0), so that is the form its files hold and the form ours write.

| child | holds |
|---|---|
| core storage, `metadata` | per row, the dictionary of the keys its values use. Rows of one shape share one, so a dictionary encoding stores it once per shape |
| core storage, `value` | the residual: the variant binary of what was not shredded |
| shredded tree | for an object, a struct with one field per shredded key. A key whose values all have the shredded type is that typed column. A key that also holds values of other types is a nested `vortex.variant`, whose core storage keeps those values and whose tree is the typed column. For a list, a list of the same. For a primitive, a typed column |

The tree is the Parquet layout (source) with its wrappers taken off. A Parquet group of a `value` and a
`typed_value` becomes its typed column when it has no `value`, and a nested variant when it has both
(code). The format's rules hold as written. A missing key is null in both its columns. A value of
another type than the shredded one stays whole in the field's values. A key that is shredded never
appears in the residual. A reader rebuilds the object as the union of both, its keys in order. Each
chunk's tree type lies in its node's metadata, so different chunks, and different objects, may shred
differently, which is what lets each object choose.

The specification maps each variant type to one Parquet type, says nothing of widening one, and keeps
a column's statistics valid only when every value has the shredded type (source). The integers of
every width that this design writes in one typed column ([what an object shreds](#53-what-an-object-shreds))
are therefore rebuilt at the column's width: equal in value under the format's equivalence classes,
not in width. OpenTelemetry sends every integer as 64 bits, so its attributes keep their width. A JSON
body parsed by the size of each number does not.

A cross-check has Rust write shredded objects, nested objects, lists, primitives and a column of three
chunks shredded three ways, beside the same rows unshredded, and Vorticity reads each back equal to its
unshredded twin, value for value (code: `tools/conformance-gen/examples/gen_shredded_variants.rs`,
`ShreddedVortexVariantTests`). Three findings bound what a writer of ours may do:

- The reference converts a list whose elements keep values of their own wrongly: it pairs the rows'
  metadata with the elements' values, whose lengths differ, and refuses the write. A list whose
  elements all fit their typed column keeps no element values, and is written and read. A writer of
  ours shreds a list only that way, or leaves it in the residual.
- The reference's default writer concatenates chunks under 1 MiB before compressing them, and refuses
  to concatenate chunks shredded differently. Chunks kept apart, as the layouts of ours keep them, are
  written and read by both.
- The reference reads a shredded key that a row lacks as a key holding null, where the format, the
  unshredded row and Vorticity leave it out. Its reads of a shredded file also differ from its reads of
  the unshredded one in the width of integers and the order of keys, which its scalar equality counts
  and the format's equivalence classes do not.

### 5.3 What an object shreds

Each object chooses, whenever a job writes its slices ([20-time-window-aggregates.md](20-time-window-aggregates.md#64-how-a-writer-sorts-a-slice)),
from the counts of every path and type its rows hold.

1. A path is worth shredding when reading it as a typed column saves more than it costs. It saves, on
   every question that touches it, the residual bytes and the parsing of the rows that hold it. It
   costs its `typed_value` and its `value` in the encoding tree of every chunk that shreds it, and
   their entries in the path map (item 3). The costs are measured as everywhere in the time-window
   design.
2. Rows are sorted by shape ([shapes](#55-shapes)), so a path held by one source's rows lies in a few
   contiguous ranges, and its column is null everywhere else. Those nulls cost almost nothing only if
   the writer encodes them. Ours keeps a validity bitmap plain, a bit per row, and takes
   `vortex.sparse` only for a chunk of nulls on nine rows in ten or more
   ([90-registry.md](90-registry.md#5-the-writers-schemes-and-their-ids)). A path held by 10 to 90 % of
   a chunk's rows would pay a bit per row for its typed column, and as much for its field's `value`:
   about 12 to 25 bytes a row over a hundred such paths, against attributes of about a hundred bytes a
   row (estimate). The writer therefore encodes the validity of a shredded field as runs or sparse,
   chosen by their cost rather than at nine in ten, which the format allows since validity is a child
   array, once the cross-check shows a Rust reader reads an encoded validity there. A rare path is then
   cheap to shred, and what bounds shredding is the column budget, not a frequency threshold.
3. A shredded path is a child of the variant's array, not a column of the layout: each chunk of the
   variant is one flat segment, and every path it shreds adds a few nodes to that chunk's encoding tree
   and a field to its shredded dtype, four to six nodes a path (estimate). What a path costs is
   therefore counted per chunk, and the column budget bounds the sum: the nodes of every chunk's tree,
   kept far below the 10⁶ tables a traversal may visit ([limits.md](../guide/limits.md#the-fixed-caps)),
   and the bytes of the path map ([reading one path without the others](#59-reading-one-path-without-the-others)).
   Two things keep it small. The variant column is written in long chunks of its own
   (`ColumnChunkTargetBytes`, [blocks-and-chunks.md](../guide/blocks-and-chunks.md)), about 10⁶ rows
   each, so a day of the example holds about 4 300 of them rather than one per megabyte. And a chunk
   shreds only the paths of the shapes it holds. Rows sorted by shape do not keep those few by
   themselves: the shape comes where its runs put it, after the columns of fewest values, so a chunk of
   10⁶ rows, a third of a slice in sort order, crosses many of their runs, and each may hold every
   shape of the slice, 10² to 10³ a minute. A chunk sees most of its slice's shapes, and its paths
   with them (estimate). Three ways keep a chunk's paths few, each to measure: the shape first in the
   order, at the cost of runs in the shallow columns, chunks of the variant cut where the shape
   changes, or a column budget that counts the shapes a chunk really holds. Past the budget, the paths
   that pay the least stay in the residual.
4. A path holding several type classes shreds the class most of its rows hold, every width of it in
   one typed column wide enough for all: a path whose sources send 502 as an `int16` and 70 000 as an
   `int32` is one integer column, not a column and a residual. The format's equivalence classes allow
   it, since an `int8` of 1 behaves as an `int64` of 1 (source). The other classes stay in the field's
   `value`, as the format requires, and the path map counts them per chunk.
5. Keys that are data, many and each rare, stay in the residual, whether they sit in an object of
   their own or at the top of a flat bag, as OpenTelemetry's do (`user.4711.score` beside
   `http.route`). The key table counts them, with postings to their blocks, so a lookup of one key
   reads only the blocks that hold it ([the path directory](#54-the-path-directory)). Shredding them
   would cost a column per key for keys nobody asks twice.
6. A list whose elements share a type is shredded as a list.

### 5.4 The path directory

An index kind in the object's index directory, invisible to Rust: for every path and type the object's
rows hold, its row count, its minimum, maximum and null count in the object, where it lies (a typed
column, a field's `value`, the residual), its place in the synopsis, and for a path in the residual,
postings to its blocks. When keys are too many, as when keys are data, the directory keeps the paths
that pay their entry, and the key table counts every key the residual holds, per bucket, with
postings to its blocks, at the size the synopsis's budget decides. A Bloom filter of the other keys,
as JSON tiles keep for the keys they did not extract (source), would find a key but could neither list
nor count it. The directory and the key table are part of the object's synopsis part, so the
[synopsis cache](20-time-window-aggregates.md#11-the-synopsis-cache) keeps them. Below the object, a path is pruned by its block statistics and found by its indexes ([what
the format gives a path, and what Vorticity adds](#510-what-the-format-gives-a-path-and-what-vorticity-adds)).

### 5.5 Shapes

A row's shape is the set of paths the object shreds that the row holds, with their types, and one bit
that says whether its residual holds anything. Keys left in the residual do not enter it. An
OpenTelemetry bag is flat, so a source that puts identifiers in its keys would otherwise give every
row a shape of its own, and push the shape out of the order for every source of the slice. The shape
is therefore computed once the object has chosen its paths. Inside a slice, the shapes form a
dictionary, and the shape is one more synopsis column: the writer places it in the object's order by
what it measures, like the dataset's own columns and the shredded paths whose values repeat ([the
order inside a slice](#62-the-order-inside-a-slice)). With few shapes it comes early, and with shapes
almost as many as rows it is left out.

Five things follow:

1. Every path's rows lie together, one range per shape that holds it inside each run of the columns
   before the shape. A shredded column is runs of nulls and dense ranges, and the columns of other
   sources cost nothing in these ranges.
2. A question on a path reads only the ranges of the shapes that hold it, for the typed column and for
   the residual alike.
3. `Has` on a shredded path is a set of shapes, decided once per run of shapes. On a key left in the
   residual, it reads the key table.
4. Which shredded paths a window holds, and how often, comes from the counts of shapes, without a row,
   and the key table counts the others.
5. Rows that look alike lie together, which helps every column compress.

JSON tiles reorder records between neighbouring tiles so that records of the same structure share a
tile, for the same reasons, and report that it also helps run-length encoding (source). Sorting a slice
by shape is the same idea at the grain of the slice, with a cost model in place of their frequency
threshold.

The shape is derived from the rows and stored nowhere in them. Its dictionary and its runs lie in the
synopsis part, in the index directory, so a Rust reader sees only the data.

### 5.6 Bodies

A body is a variant: a line of text is a string, and an object is shredded like attributes. A body
that is text keeps its text, and the trigram indexes serve its search
([10-indexes.md](10-indexes.md#42-vorticitybloomngram3v1-trigrams-for-text-predicates)). An object body
is stored once, shredded, and showing it rebuilds it from its columns and residual, keys in order,
which may differ from the bytes the source sent in spacing and in the order of keys. An application
that must show the exact bytes keeps them as text as well, which stores them twice. Parsing a text body
that holds JSON into an object is the application's choice, at ingestion.

### 5.7 Writing, and what it costs

Other writers that shred report a slower ingestion. One measure on a laptop, Spark 4.1.1 writing to an
Iceberg 1.12 table, finds appends 34.6 % slower with shredding, from 22 972 to 17 073 rows a second,
over about 300 shredded fields (source). Vendors announce 20 to 50 % (source). Four things in those
writers explain it, although no measure isolates them:

| where they pay | what it costs them |
|---|---|
| the writer of every append shreds | every row pays it on the way in |
| the writer buffers the first rows of each file to infer which fields to shred, drops the fields under 10 % of that sample, and caps them at 300 columns (source) | a pass over a sample, and a choice fixed per file from it |
| the variant is first built as a binary, then taken apart into typed columns, and the rest built again as a residual binary | two passes over every value, and a second binary |
| every shredded field is a column chunk of its own, which stores a definition level for every row, whether the row holds the field or not | work in rows times fields, whatever the fields' sparsity |

The design here pays none of these on the way in, and less at its exit:

1. Appends write variants unshredded, as those writers do when they do not shred: the cost of
   ingestion does not move.
2. Shredding happens in the jobs that already read, sort and write again every row of a slice: level
   0's exit, each fresh merge and the seal ([how a writer sorts a
   slice](20-time-window-aggregates.md#64-how-a-writer-sorts-a-slice)). It adds work to jobs that run
   anyway, in the background, and never to an append.
3. That job decodes each value once and writes it where it goes, the typed column of its path or the
   residual. No binary is built to be taken apart, and a residual is built only for the rows that keep
   keys in it.
4. A path costs in proportion to the rows that hold it. Rows are sorted by shape, so a path's absence
   is a run of nulls or the fill of a sparse array per range of rows (`vortex.sparse`,
   [90-registry.md](90-registry.md)), not a value per row, once the writer encodes validity that way
   ([what an object shreds](#53-what-an-object-shreds)).
5. The choice comes from the slice's exact counts, which the sort computes anyway: no sample, no
   second pass.
6. The shredded type lies in each array node's metadata (code:
   [VariantMetadata.cs](../../src/Vorticity/Vortex/Arrays/Metadata/VariantMetadata.cs)), so each
   chunk carries its own shredding while the column's dtype stays `Variant`. An object whose chunks
   shred differently is legal for the format, and the reference writes and reads one when its chunks
   are kept apart ([how a variant is stored](#52-how-a-variant-is-stored)).

What shredding adds per job that writes a slice (estimate, to measure): decoding an attribute and
routing it takes 10 to 20 ns, so 0.2 to 0.4 µs for a row of 20 attributes, or 10 to 20 ms of one lane
per second of rows at 5 × 10⁴ rows a second, one to two per cent of a core, beside the sort of the
slice, and three or four times that over a row's life, as many times as it is rewritten. Building the
path map adds a few bytes per path and per chunk to what the writer already knows when it lays the
buffers out. The memory is one slice's values, about 1.8 GB decoded for twenty attributes over a
minute of the example, in memory or in a mapped file of the local scratch, reserved through the
governor (estimate). When compaction runs on the machines that ingest, that share of a core is what
ingestion shares with it.

Rows not yet out of level 0 cost the other side. Every question that touches a path parses their
variants: eight appends of three seconds, 1.2 × 10⁶ rows, 60 to 120 ms of one core (estimate). At
hundreds of questions a second over the last minutes, that is six to twelve cores parsing the same
immutable objects. A query node therefore keeps in memory the decoded column of each object of level
0, path and type class its questions asked for, until the object leaves level 0: parsed once per node,
not once per question. Shredding in the append, from the previous object's choice of paths, is the
other way, which costs the append ([decisions to take](#13-decisions-to-take)).

### 5.8 Compatibility

- A dataset with a variant column targets the edition `core2026.08.3` or later, and a variant column
  below it fails the write, as it does today.
- Rust 0.85.0 and later reads every object, shredded variants included, and the cross-check proves it
  value for value, under the one difference in how Rust reads a shredded key a row lacks ([how a
  variant is stored](#52-how-a-variant-is-stored)).
- Everything Vorticity adds (the path directory, the path map, the shapes) lies in the index
  directory, which Rust never reads.
- An encoding tree inlined in a flat layout is read by every reader since 0.84, and Rust 0.85.0, the
  edition's first reader, is past it. A reader that ignores the tree, or the path map, reads each
  segment whole and gets the same values.
- A dataset whose objects shred differently is legal for the format, and the dataset's schema says only
  `Variant`.
- A shredded field's validity is written as runs or sparse only once the cross-check shows that Rust
  reads an encoded validity in that place. Until then the writer keeps it plain, at a bit per row.
- A Rust reader reads every path, and none of what makes a path fast in Vorticity. It reads each
  chunk's segment whole and prunes nothing on a path, since the format gives a path no zone map. If an
  edition freezes a layout for paths, the block statistics move there, as the indexes are to move to
  `vortex.indexed` once it freezes ([the road upstream](10-indexes.md#8-compatibility-and-the-road-upstream)).

### 5.9 Reading one path without the others

The reference writes a variant column as a `vortex.flat` layout, one segment per chunk holding the
whole array: its `metadata`, its `value` and, once shredded, its `typed_value`. The corpus's
`parquet_variant.vortex` shows it under `vxdump --layout`: one `vortex.flat` segment of
`vortex.parquet.variant(vortex.varbin, vortex.varbin)` (code). Read whole, such a segment makes a
question on one path fetch the bytes of every path of the chunk: on the example, about 100 MB per chunk
of a million rows, where the path's own buffers take a few megabytes (estimate). That would be the
hidden price of shredding at query time, and the design removes it.

A reader can fetch only the buffers of one path once it knows where they lie in the segment. That is
written in the chunk's encoding tree, and three places can hold the tree, each with its own cost:

| where the tree is read from | what a path read costs | what it costs elsewhere |
|---|---|---|
| the end of the chunk's segment, where every writer puts it | a dependent read of the tree, then one of the path's buffers, per chunk | nothing. The tree grows with the paths the chunk shreds: a few hundred paths make about 60 KB (estimate) |
| the flat layout's metadata, inlined (`array_encoding_tree`), which readers accept since 0.84 ([02-format.md](02-format.md#6-layouts-layoutfbs)) and Vorticity reads (code: [FlatLayoutReader.cs:11-19](../../src/Vorticity/Vortex/Layouts/FlatLayoutReader.cs#L11-L19)) | one read of the path's buffers per chunk, the footer being read with the object | the footer holds every chunk's tree. Over the 4 300 variant chunks of a day, at 60 KB each, that is about 250 MB of footer (estimate), which the column budget refuses |
| the path map, in the index directory | one read of the path's entry per object, cached with the synopsis part, then one read of its buffers per chunk | a few dozen bytes per path and per chunk that holds it, about 200 KB for a path present in every chunk of a day (estimate) |

So the writer builds the path map for every object it shreds, and inlines the trees only in an object
small enough for its footer to hold them, which the cost model decides against the column budget. In
both cases it lays each path's buffers out together, which the tree's depth-first order already does.
The path map stores a path's part of the tree once while its chunks encode it alike, and per chunk only
what differs (where the buffers lie, their lengths, a dictionary's size), which is what keeps its
entries to a few dozen bytes. Chunks encode a path alike only if the writer makes them: each column
chooses its encoding once per chunk ([blocks-and-chunks.md](../guide/blocks-and-chunks.md)), so
dictionaries, run ends, frames of reference and widths differ, and an entry may grow to a few hundred
bytes, back to the order of the inlined trees set aside above. The writer therefore fixes a path's
encoding once per object, at a cost in compression the write bench measures, unless the path map,
measured on real mixes first, stays small without it (hypothesis).

Reading a path then goes as follows:

1. The path directory says the object shreds the path, and in which chunks, which the shapes those
   chunks hold decide.
2. The path map gives, per chunk, the path's part of the tree and its buffers' ranges.
3. The reader fetches those ranges only, coalesced across neighbouring chunks as every read is, and
   decodes the path's arrays as a column, with the dtype the directory gives.
4. A path the object left in the residual is read from the residual's buffers, in the chunks whose
   shapes hold it.

A question never rebuilds a variant to read one path. It decodes the path's arrays as a column, and
rebuilds a variant only to show a row ([showing rows](#74-showing-rows)). A reader that rebuilt every
row before extracting the field would pay back everything shredding saved, which is what another
engine's reader had to remove and what a scan that does not push field projections into shredded
columns still pays (source).

A Rust reader knows nothing of the path map. It reads each segment whole, as it reads every flat
segment, and gets the same values. A path read in Vorticity costs the bytes of the path, as a declared
column's does, plus one small read per object that the cache keeps. In requests it costs one per chunk
that holds the path, since its buffers in two neighbouring chunks lie a segment apart: about 4 300 a
day of the example, 1.3 × 10⁵ over 30 days, for a scan of the path over the window (estimate). Its
value counts and coarse tables answer most questions without such a scan.

### 5.10 What the format gives a path, and what Vorticity adds

A declared column of a Vortex file owes its speed to two stages. Its arrays give the encodings, and
the kernels that answer on them: a filter on a dictionary or on run ends is answered once per value or
per run, never per row (code: [EncodedAnswers.cs:8-17](../../src/Vorticity/Compute/EncodedAnswers.cs#L8-L17)).
Its layout gives what lets a question skip rows and read few bytes: a zone map per block of 8 192
rows, segments of its own in each chunk, read without the other columns, and a dictionary that the
`vortex.dict` layout can share between chunks.

A shredded path gets the first stage whole, since its typed column is an array like any other, under
the same encodings. It gets nothing of the second, because it is not a column of the layout: it is a
child of the variant's array, inside each chunk's one flat segment. The edition `core2026.08.3` added
the two variant arrays and no layout (code: [core2026.08.3.toml:13-22](../../spec/editions/core2026.08.3.toml#L13-L22)),
and a zone map aggregates a column of the layout (code: [core2026.08.3.toml:77-84](../../spec/editions/core2026.08.3.toml#L77-L84)),
so over a variant column it can at most count the variant's nulls, never give a path's minimum. The
reference's plan for variants names no layout for paths either (source).

On the example, a chunk of the variant column holds 10⁶ rows. Its segment weighs about 100 MB, the
typed column of `http.response.status_code` about 1 MB, and that column's minimum and maximum per block
of 8 192 rows a few kilobytes (estimate). With the format alone, `>= 500` would read a hundred times
the path's bytes, and skip nothing below the object. Vorticity therefore gives a path, in its index
directory, what the layout gives a column:

| what makes a column fast | a declared column | a shredded path, in the format | what Vorticity adds for a path |
|---|---|---|---|
| encodings, and kernels that answer on them | yes | yes, the same arrays | nothing |
| its bytes, read without the other columns' | its own segments | none: one flat segment per chunk holds every path and the residual | the path map ([reading one path without the others](#59-reading-one-path-without-the-others)) |
| a minimum, a maximum and a null count per block | its zone map | none | its block statistics |
| a dictionary shared between chunks | the `vortex.dict` layout can share one | one per chunk | nothing: a chunk of 10⁶ rows amortizes its own |
| Bloom filters, postings, sorted runs, trigrams | Vorticity's indexes, named by the column | none: an entry must name a column whose dtype its kind supports | the same indexes, named by the column, the path and the type class |
| rows sorted by its values | in the slice's order | in the slice's order, compared in its type | nothing more than [the order inside a slice](#62-the-order-inside-a-slice) |

The block statistics are an index kind of the index directory. For each shredded path and each block
of the directory's grain, 8 192 rows counted from row 0 as zones are, they hold the path's minimum,
maximum and null count. A block whose statistics refute a predicate clears its bit of the block mask,
as a zone map's would ([skipping indexes](10-indexes.md#1-terms)). They are sparse: a block where the
path is absent has no entry, and rows sorted by shape keep a path's blocks few and contiguous. They are
runs of ordinary arrays, like every index ([the directory and the runs](10-indexes.md#3-the-directory-and-the-runs)),
so they compress like data. A path that every row holds, at about a byte a value, weighs 4.3 GB a day,
and its block statistics about 12 MB before compression, three tenths of a per cent of the path
(estimate).

A path that is a synopsis column needs none, since its runs give its rows exactly. For the other
shredded paths, the writer keeps the block statistics where they can prune, which it measures on the
object's own values. A path the order groups, such as `Resource["k8s.pod.name"]` once rows are sorted
by service, spans a narrow part of its range in each block, and keeps them. A path whose values scatter, such as a
duration, spans its whole range in every block, so its statistics could refute no range, and the
writer drops them. A declared column's zone map would prune nothing there either. Under the index
budget, the statistics that prune least are dropped first ([the policy](10-indexes.md#6-the-policy)),
with no threshold of their own.

An index entry gains a variant path and a type class beside its column, and a reader checks that the
class's dtype is one the kind supports, as it checks a column's today. An object shreds a path its own
way in each chunk, so one path may hold integers in one chunk and text in another, with the same
digits. Each class has its own entry, covering the blocks where it lies, so `AsInt64() == 502` and
`AsString() == "502"` probe different entries and never find each other's rows. The directory's reader
skips a field it does not know (code: [IndexDirectory.cs:175](../../src/Vorticity/Vortex/Indexes/IndexDirectory.cs#L175)),
so a Vorticity older than this design sees an entry naming a variant column, whose dtype no kind
supports, and ignores that entry alone. `IndexPolicy.Auto` treats every path an object shreds, in each
of its type classes, as a top-level column of its own, with the same verdicts and under the same
budget.

The path map counts, per chunk, the values of the path that its field's `value` holds, per type class.
There are none in most chunks, since the writer puts the path's dominant class in the typed column, so
`AsInt64()` reads a field's `value` only in a chunk that shredded the path as text and holds a few
integers.

## 6. The synopsis on variant columns

Variant columns take every primitive of [the time-window design](20-time-window-aggregates.md):
periods, sorted slices, runs, the synopsis part and its cache, and the planner. Two choices are made
per object, and they are independent. Shredding decides how a path is stored, under the column budget.
The synopsis decides how it is counted, under its byte budget. The writer parses every variant of a
slice when it sorts the slice, so it can count a path it leaves in the residual.

### 6.1 What each path gets

| the path, in an object | runs at the slice's grain | value counts and coarse tables | run statistics | a filter on it reads |
|---|---|---|---|---|
| shredded, values that repeat | yes, as a synopsis column: sorted and run-end encoded | yes | | its runs |
| shredded, numeric values that rarely repeat | | | yes, as a measure column | its typed column, through its block statistics where they prune |
| shredded, other values that rarely repeat, such as an identifier | | | | its typed column, through the Bloom filters and postings of the path and its type class |
| in the residual, values that repeat | | yes, where the budget admits them, counted from the parsed rows | | the residual of its shapes |
| in the residual, numeric values that rarely repeat | | | yes, where the budget admits them, folded from the parsed rows | the residual of its shapes |
| keys that are data | | how many rows hold each key, per bucket, in the key table, at the size the budget admits | | postings to its blocks |

A path benefits from the synopsis whether or not its object shredded it. What only shredding gives is
runs at the slice's grain: the counts of the path jointly with every column before it in the order,
minute by minute, stored as the order of the rows. A path left in the residual is counted only by the
tables the budget admitted for it, per their buckets and per the combinations they hold, and a
question finer than those tables reads the residual of its shapes.

### 6.2 The order inside a slice

The shape and every shredded path whose values repeat take their places in the object's order beside
the dataset's own columns. The time-window design orders columns by their numbers of distinct values
([20-time-window-aggregates.md](20-time-window-aggregates.md#62-the-order-inside-a-slice)), which is
right for dense columns. A shredded path is sparse: null outside the shapes that hold it, so it splits
only the runs of those shapes. The order is therefore measured by the runs each column adds over the
object's rows, which for dense columns comes back to the ascending number of distinct values
(hypothesis: a greedy order by runs added comes within a few per cent of the best order on mixed
sources, refuted by a bench of real mixes of logs).

Measured exactly, that greedy order costs about a pass over the rows per candidate and per place, of
the order of candidates² × rows for hundreds of candidate paths. It is measured on a sample, or with
sketches of the combinations each candidate would add, and paths of disjoint shapes, which split no
run of each other, are ordered apart (hypothesis, its cost and its runs measured against the exact
greedy order on real mixes).

A minute of mixed sources might be ordered by `SeverityNumber`, `Resource["cloud.region"]`,
`Attributes["http.request.method"]`, the shape, `ServiceName`, `Attributes["http.response.status_code"]`,
`Attributes["http.route"]`, `Resource["k8s.pod.name"]`, then the instant. The shape comes where its
runs put it, here after three columns of few values, so that every path's rows lie together inside
each run of those three.

Coarse tables merge the runs of their bucket's slices as sorted lists, which needs the slices of an
object to share their order, and shapes drift from slice to slice more than declared columns do. The
order is therefore fixed per object, for every dataset, as the time-window design decided ([decisions
to take](20-time-window-aggregates.md#18-decisions-to-take)).

### 6.3 Typed questions

A question asks a path for one type: `AsInt64()`, `AsString()`. Under strict types, a row whose value
has another type answers null, which is exactly what the typed column holds for it, so the typed
column is enough. The writer puts every value of the shredded type class into the typed column,
integers of every width alike, a rule the path directory records, so that no matching value hides in
the field's `value`. Where a chunk shredded the path as another class, the path map's counts say
whether that chunk's field `value` holds any value of the class asked. A file another writer shredded
is pruned by its typed column's statistics only as the format allows (source). A conversion,
`ToInt64()` reading the text `"200"` as 200, also reads the field's `value` on the rows of other types,
whose number the directory gives.

### 6.4 Finding a path's synopsis

The tree of the dataset summarizes declared columns, not paths. The path directory carries, per path,
the object's minimum, maximum and null count, so it prunes an object for a path as the tree's
summaries prune it for a column. The directory lies in the synopsis part, and the leaf entry's table of
contents names its region, with the regions of the paths the object's rows hold most. A question on one
of those paths reads its runs or tables in one round trip per object, cold. A question on any other
path reads the directory first, then the path's regions: two round trips per object cold, none warm,
since the cache keeps the directory. How many paths the table of contents names is the cost model's to
decide, against the bytes it adds to every leaf entry.

### 6.5 Examples

Thirty periods of a day, warm unless said. Every figure is an estimate.

| question | what answers it |
|---|---|
| a count per `Attributes["http.route"]` and per day, 30 days, the path shredded | its value counts by period, as for a declared column: 30 × 3 × 10⁴ entries |
| `ServiceName` under `Attributes["http.response.status_code"] >= 500`, per minute, the last hour | the runs of 60 slices, both being synopsis columns |
| the rows holding `Attributes["feature.flag.new-cart"]`, per minute, the last day | the runs of the shape in 1 440 slices, summed over the shapes that hold the key |
| `Attributes["feature.flag.new-cart"] == "on"`, per service and per day, 30 days, the key left in the residual of most objects | the coarse tables that count it, where the budget admitted them, and elsewhere the residual of the shapes that hold it |
| the average of `Attributes["http.server.request.duration"]` per route and per day | the states of the coarse tables by period, or the run statistics |
| the keys that are data most often held, such as `Attributes["user.4711.score"]` and its siblings, 30 days | the key tables' counts per period |
| the count per `Attributes["http.route"]` of the first row, cold, the path not among those the tables of contents name | two round trips per object, in parallel: the directory, then the value counts |

## 7. Answering questions over paths

### 7.1 Resolving a path, object by object

The planner reads the path directory of each object of the window, from its synopsis part, and finds
the path in one of four places:

| the path, in this object | what the question reads |
|---|---|
| a typed column of the type asked | the column, as any column: its blocks pruned by its runs or its block statistics, and its buffers located by the path map ([reading one path without the others](#59-reading-one-path-without-the-others)). The field's `value` holds only other classes, so only a conversion reads it |
| the residual | the residual of the rows whose shape holds the path, and no other |
| absent | nothing: a filter on it refutes the object before any read of data, and a grouping gives its null group the object's rows |
| a typed column of another type | the field's `value` only, since no typed value can be of the type asked, and only in the chunks whose path map counts values of the class asked |

### 7.2 Filters and groupings

A filter on a shredded path takes the steps a filter on a declared column takes, with the block
statistics and the indexes of [what Vorticity adds](#510-what-the-format-gives-a-path-and-what-vorticity-adds)
where the layout would have served. Take `Attributes["http.response.status_code"].AsInt64() >= 500`
over a day:

1. The path directory of each object gives the path's smallest and largest integer: an object whose
   largest is 499 is refuted before any read.
2. Where the path is a synopsis column, its values are sorted inside each run of the columns before it
   in the slice's order. A binary search finds where 500 starts in each such run, and a count is the
   sum of the lengths of the runs past it, read from the synopsis part, which the cache keeps, without
   a byte of data ([walking the runs](20-time-window-aggregates.md#92-walking-the-runs)).
3. Where rows must be read, to show them or for a measure the synopsis does not hold, the runs give the
   matching rows exactly. A path that is no synopsis column skips, through its block statistics, the
   blocks whose largest value is under 500.
4. The blocks left are read through the path map, the path's buffers only, and the comparison runs on
   the typed column: once per value on a dictionary, once per run on run ends, and on the decoded
   integers otherwise (code: [EncodedAnswers.cs:8-17](../../src/Vorticity/Compute/EncodedAnswers.cs#L8-L17),
   [ComparisonKernels.cs:19-27](../../src/Vorticity/Compute/ComparisonKernels.cs#L19-L27)).
5. The rows whose path holds text are in the field's `value`, and `AsInt64()` never reads them. Only a
   chunk that shredded the path as text reads its field's `value`, and only if its path map counts
   integers there.

`IN (500, 502, 503)` takes the same steps, with a search per value in the runs, a set of codes on a
dictionary, and, for values that few rows hold, the Bloom filters or postings of the path's integer
class, which the dataset's index policy builds as for any column ([columns known only at run
time](20-time-window-aggregates.md#13-columns-known-only-at-run-time)). `NOT IN` keeps the rows whose
path holds an integer outside the list. A row without the path, or whose path holds text, answers
unknown and is dropped, as SQL drops a null under `NOT IN`, by the three-valued logic every filter
follows. A question that wants those rows too says so, with
`|| r.Attributes["http.response.status_code"].AsInt64() == null`, which is the exclusion of [excluding
a rare value](20-time-window-aggregates.md#94-excluding-a-rare-value), answered as the whole minus the
part.

A grouping by a shredded path groups its dictionary codes or its runs. Across objects, groups merge by
value, whatever column held them. What the synopsis adds, for shredded paths and for the others, is in
[the synopsis on variant columns](#6-the-synopsis-on-variant-columns).

### 7.3 Presence and discovery

`Has(path)` on a shredded path is a set of shapes, so a count of rows holding it, per slice, is a sum
of runs of shapes. On a key left in the residual, it reads the key table. The keys a window holds,
with their counts, are the union of its objects' path directories and key tables, read from their
synopsis parts.

### 7.4 Showing rows

A row shown whole is rebuilt from the typed columns of its shape's paths and from its residual,
through a take of those rows. A page of 200 rows reads the columns of their shapes, not every shredded
column of the object.

### 7.5 Examples

On the example's volumes, with periods of a day ([20-time-window-aggregates.md](20-time-window-aggregates.md#5-objects-that-cover-periods-sized-by-volume)).
Every figure is an estimate.

| question | what it reads |
|---|---|
| a count per `http.route` over 30 days, the path shredded in every object | the value counts of the path, as for a declared column: 30 objects, one round trip each cold |
| the same, the path shredded in 28 objects and left in the residual of 2 where it is rare | the value counts in 28 objects, and in the other 2 the residual of the few ranges of the shapes that hold it |
| rows holding `feature.flags`, per hour, over a day | the counts of shapes per slice: no column, no row |
| `Attributes["user.4711.score"]`, a key that is data | the key table's postings of that key, then the residual of the blocks that hold it |
| the keys present over the last day, with their counts | the path directories of the day's objects |
| `AsInt64() >= 500` on `http.response.status_code`, where one source sends the code as text | the typed column alone: that source's rows answer null, and are never read |
| the same with `ToInt64()`, which converts the text | the typed column, and the field's `value` on that source's rows, parsed |
| the rows of `AsInt64() >= 500` on that path, the path no synopsis column | the chunks where its block statistics leave a block, and in each the path's buffers alone: about 1 MB rather than the segment's 100 MB |

## 8. The worst cases

| case | what it costs | what the design does | what remains |
|---|---|---|---|
| a path left in an object's residual | the residual of the rows of the shapes that hold it, for a question its tables do not answer | the synopsis counts it where its budget admits, shapes keep its rows together, and postings find them | a parse per row that holds it, for questions finer than its tables |
| a path left in the residual, asked jointly with other columns per minute | the residual of its shapes in every slice of the window, since it has no runs | shredding it, where the column budget allows | rows |
| shapes almost as many as rows among the shredded paths, so the shape is left out of the order | the residual of every row the object holds, for a path left there | keys left in the residual stay out of the shape, and the cost model shreds more where shapes scatter | every row's residual, in that object |
| a path held by 10 to 90 % of a chunk's rows | a bit per row of validity for its typed column and for its field's `value`, 12 to 25 bytes a row over a hundred such paths (estimate) | validity encoded as runs or sparse, once Rust reads it there | a bit per row until then |
| a chunk that crosses most shapes of its slice | the paths of every shape in its tree and its path map | the shape first, chunks cut where the shape changes, or a column budget that counts a chunk's shapes, as measured | the paths past the budget stay in the residual |
| a path whose chunks encode it differently | path map entries of a few hundred bytes | a path's encoding fixed per object | some compression, which the write bench measures |
| sparse paths placed early in the order | more runs for every column after them | the order by the runs each column adds | the overhead the bench measures |
| a path the table of contents does not name | a round trip more per object, cold | the paths the rows hold most are named | one round trip, cold only |
| slices of one object ordered differently | a sort when coarse tables merge them | one order per object | none |
| keys that are data | one lookup: the blocks its postings name. A grouping by key: every entry of the key tables | the key table, its counts and its postings | a grouping over keys reads them all |
| a path whose type class changes from row to row | for a question on the class left in the field's `value`, that `value` in every chunk that holds the class | the dominant class is shredded per chunk, and the path map's counts skip the chunks that hold none of the class asked | half and half costs the parse of half the rows, for the class not shredded |
| a range on a shredded path whose values the order scatters, such as a duration | the path's typed column in every chunk that holds it | run statistics and sketches answer thresholds on their grid ([columns nothing summarizes](20-time-window-aggregates.md#145-columns-nothing-summarizes)), and block statistics are kept where they prune, those that prune least dropped first under the budget | the path's bytes, as for a declared column |
| rows not yet out of level 0, which appends write unshredded | every variant of those rows parsed, for any path: eight appends of three seconds hold 1.2 × 10⁶ rows, about 120 MB of attributes and 60 to 120 ms of one core to parse, spread over the lanes (estimate) | the compaction loop shreds them as they leave level 0 ([scale](20-time-window-aggregates.md#146-scale)), and each query node keeps the columns it decoded until the object leaves level 0 | a parse per node and per object of level 0, and more behind a lagging loop |
| an object at its column budget | the paths left over stay in the residual | the paths that pay least are the ones left | their rows' residual |
| many whole rows shown | a take per shredded column of each row's shape | rows of one shape read the same columns | in proportion to the rows and their paths |
| text searched in every attribute at once | every text column of every shape, and the residual | trigram indexes per column | inherent to a search with no key |
| a comparison across types (`"200"` against 200) | both representations of the path | the question names the conversion it means, `ToInt64()`, and `As…` never converts | rows of the type that is not shredded |
| the keys of thirteen months, when keys are data | every key of every object | the directories and key tables, cached | output-bound |
| a path scanned over a long window | a request per chunk that holds it, 1.3 × 10⁵ over 30 days of the example (estimate) | its value counts and coarse tables answer most questions without a scan | the requests of the scan |
| a variant whose chunks carry neither a path map nor an inlined tree: an object another writer wrote, or one written before this design | each chunk's tree read from the end of its segment first, one dependent read more per chunk, or the segment whole | the next compaction that rewrites the object builds its path map | one read more per chunk, until then |
| many shredded paths over many chunks | path map entries, and trees, in proportion to paths times chunks | long variant chunks, per-chunk shredding of the shapes a chunk holds, and the column budget | the paths past the budget stay in the residual |
| a question touching many paths of one chunk | as many buffer ranges per chunk | coalescing, which joins neighbouring ranges | close to the whole segment, which is then what it reads |

## 9. What changes in Vorticity

| item | today | to build |
|---|---|---|
| reading shredded variants | refused by name (code) | the rebuild of the Parquet rules, and a path's typed column read alone |
| writing shredded variants | refused, `VariantMetadata.Write` throws (code) | the writer of `typed_value`, from the object's choices, with the integers of every width in one typed column, each path encoded alike in all its chunks, and its validity as runs or sparse once Rust reads it there |
| variants in records | no .NET type maps them ([07-dotnet-mapping.md](07-dotnet-mapping.md)) | a `Variant` member, paths, `As…`, `To…` and `Has` as symbols, and the same on the tool path, where a literal's type picks the `As…` |
| the choice of paths | none | the cost model and the column budget, per object |
| the path directory | none | an index kind of the index directory, with each path's minimum, maximum and null count in the object |
| the key table | none | an index kind of the synopsis part: per bucket, every key the residual holds, its count and postings to its blocks |
| shapes | none | the shape's dictionary and runs in the synopsis part, over the shredded paths and a bit for the residual, and the shape placed in the object's order |
| the variants of level 0 | parsed by every question | the decoded columns a query node asked for, kept in memory per object, path and type class until the object leaves level 0 |
| a path written as text | a nested column's path separated by dots (code) | a path of a variant written as its column, its keys and its type class apart |
| the order inside a slice | by numbers of distinct values, in the time-window design | by the runs each column adds |
| the synopsis on paths | none | shredded paths as synopsis and measure columns, and value counts, coarse tables and run statistics for paths left in the residual, counted from the parsed rows |
| the table of contents | the synopsis part's columns and tables, in the time-window design | also the path directory, and the paths the object's rows hold most |
| the indexes on paths | none: an entry must name a column whose dtype its kind supports, which a variant is not ([10-indexes.md](10-indexes.md#3-the-directory-and-the-runs)) | entries that name a column, a path and a type class, one per class a path holds, and `IndexPolicy.Auto` over every path an object shreds |
| the block statistics of a path | none: the format's zone maps describe columns of the layout | an index kind of the index directory: per shredded path and per block, its minimum, maximum and null count, kept where they prune |
| the path map | none | an index kind of the index directory: per shredded path, its part of the encoding tree once, and per chunk where its buffers lie and how many of its values each type class keeps in the field's `value`, built again by every job that writes the object's slices |
| the encoding tree of a flat segment | read when inlined, never written (code) | inlined by the writer when the object's footer can hold every chunk's tree |
| the variant column's chunks | the file's chunks, sized by the widest column | long chunks of its own, about 10⁶ rows, each shredding only the paths of the shapes it holds |
| reading part of a flat segment | the segment is read whole | only the buffers of the children a question decodes, located by the path map or by an inlined tree |
| extracting one path | no shredded variant is read | the path's arrays decoded as a column, never a variant rebuilt to extract a field |

## 10. Order of work

| phase | delivers |
|---|---|
| 1 | reading both shredded forms, proven on files Rust writes |
| 2 | variants in the query API, paths, `As…`, `To…` and `Has`, on unshredded data first: right before fast |
| 3 | writing shredded variants in the jobs that write slices, from a fixed choice, then from the cost model, with their path maps, each path encoded alike per object, the cross-check of an encoded validity, and the reader's fetch of one path's buffers |
| 4 | the path directory, the key table, and the block statistics of each shredded path |
| 5 | shapes over the shredded paths, and the order by runs added, measured per object, with the sorted slices of the time-window design |
| 6 | the synopsis on paths, shredded or not, the table of contents, the indexes on paths, one entry per type class, and the memo of level 0's variants |

## 11. How it is held

| promise | gate |
|---|---|
| a variant reads back as it was written, whatever the shredding | a fuzzer of documents of every shape (nesting, lists, mixed types, keys that are data, keys with dots), written under random choices and read back equal, value for value, integers equal under their class |
| the format's rules | tests of a missing key, a type mismatch, a residual disjoint from the shredded keys, and a key present in both, which is refused |
| Rust reads what we write, and we read what Rust writes | shredded variants in the cross-check, both ways |
| the same answers shredded or not | every question of a matrix over paths, on the same rows written shredded and unshredded |
| a shredded path is as fast as a declared column | a ratchet on the same question over a path and over a declared column holding the same values |
| the column budget | footer bytes and layout nodes as ratchets, on a bench of 10⁴ paths |
| ingestion does not pay for shredding | the append throughput of rows with variant columns is the same with shredding on and off, since appends never shred (a ratchet on the write bench) |
| shredding costs little where it happens | the time of a level 0 exit with shredding against the same exit without it, recorded on the write bench and held as a ratchet |
| a path read fetches only its bytes | the bytes a question on one shredded path reads, against the bytes of that path's buffers plus its path map entry, on a counting store |
| a path read rebuilds no variant | an allocation ratchet on a question over one shredded path, which equals the same question over a declared column |
| `As…` converts nothing and reads one class | on a path holding integers and text in the same chunks, the bytes `AsInt64()` reads: the typed column and none of the field's `value`, on a counting store |
| a range on a path skips what it skips on a declared column | the blocks and bytes a range question reads over a shredded path that is no synopsis column, against the same question over a declared column holding the same values in the same order: the same blocks, on a counting store |
| an index on a path never matches across classes | a path holding 502 as an integer in some chunks and `"502"` as text in others, probed by `AsInt64() == 502` and by `AsString() == "502"`: each finds its own rows and no other |
| the integers of a path share one column | a path written as `int8`, `int16`, `int32` and `int64` across its rows reads back from one typed column, each value equal to the one written under the format's equivalence classes, in Vorticity and in Rust |
| the path map and the trees stay within the column budget | their bytes, and the nodes of every tree, as ratchets on a bench of 10⁴ paths over a day of chunks |
| the synopsis counts a path exactly, shredded or not | at every version of the fuzzer, every value count, coarse table and run of a path equals the count of the parsed live rows that hold it |
| the order pays for itself | the runs of every synopsis column under the measured order, against the order by numbers of distinct values, on real mixes of logs, and the cost of measuring it on a sample against the exact greedy order |
| nulls cost what they hold | a path held by half of a chunk's rows costs no bit per row once its validity is encoded, on the write bench, and Rust reads it back in the cross-check |
| a chunk shreds what it holds | the paths each chunk shreds, against the shapes it holds, on real mixes, and the bytes of the path map with a path's encoding fixed per object |
| keys that are data are counted | at every version of the fuzzer, the key table's counts equal the rows that hold each key, and discovery lists every key of the window |
| level 0 is parsed once per node | the parses of a level 0 object's path, counted on a query node under a load of questions: one per object, path and type class |
| a path with dots stays one key | `http.route` and `http` holding `route` are two paths, in an index policy, the tool path and a plan |

## 12. Documentation to change, and what to clean up

| item | documents |
|---|---|
| reading and writing shredded variants | [90-registry.md](90-registry.md) (the two forms read and written), [01-scope.md](01-scope.md) (the refusal by name goes), [04-conformance.md](04-conformance.md) (the corpus and the cross-check) |
| variants in queries | [07-dotnet-mapping.md](07-dotnet-mapping.md), [14-public-api.md](14-public-api.md) (paths on the typed path, `As…` and `To…` under its naming rules), [16-queries.md](16-queries.md), [08-semantics.md](08-semantics.md) (a path absent, or of another type, under three-valued logic), the guide's `records.md` and `untyped-files.md`, a guide page on attribute bags with its sample |
| the path directory, the key table, shapes | [10-indexes.md](10-indexes.md), [20-time-window-aggregates.md](20-time-window-aggregates.md) (the order inside a slice) |
| the validity of shredded fields | [90-registry.md](90-registry.md) (validity encoded as runs or sparse, chosen by cost), [11-write-strategy.md](11-write-strategy.md) |
| paths written as text | [14-public-api.md](14-public-api.md) and [10-indexes.md](10-indexes.md) (a variant's path beside a nested column's dotted path) |
| block statistics and indexes on paths | [10-indexes.md](10-indexes.md) (the new kind, entries that name a path and a type class, `Auto` over paths), the guide's `indexes.md` |
| every item | `PublicSurface.txt`, the XML documentation, `bench/README.md` |

To clean up: the refusal of shredded variants, its messages and its tests, once both forms read.

## 13. Decisions to take

| | question | proposed | the other way |
|---|---|---|---|
| 1 | how an attribute bag is stored | a variant shredded per object | a map, numbered columns, or declared columns ([the models, compared](#3-the-models-compared)) |
| 2 | which variant form | `vortex.variant` with its shredded child over a `vortex.parquet.variant` core, the form the reference converts every shredded variant to before writing, decided on 2026-10-18 from its code | `vortex.parquet.variant` with a `typed_value`, the Parquet layout, which the reference reads but never stores |
| 3 | types | strict: `AsInt64()` takes the path's integers as stored and gives null for a text, so the typed column is enough. A conversion is a `To…` the question names | loose conversions by default, which read the field's `value` too |
| 4 | paths on the typed path | a path is a value, as a map's key is | paths only on the tool path |
| 5 | object bodies | stored once, shredded, rebuilt to show | kept as text as well |
| 6 | keys that are data | residual, counted by the key table with postings per key | a declared map column beside the variant, or a Bloom filter of the keys, which can neither list nor count them |
| 7 | the shape in the order | over the shredded paths and a bit for the residual, placed by the runs it adds, like any synopsis column | over every key a row holds, which gives each row of a source with data in its keys a shape of its own, or always first, or never |
| 8 | the column budget | from the footer's cost | a fixed number of paths |
| 9 | paths left in the residual | counted by the synopsis where its budget admits them | counted only once shredded |
| 10 | the order inside a slice | measured by the runs each column adds, on a sample or with sketches, and fixed per object, decided on 2026-10-18 | by numbers of distinct values, per slice, as for declared columns alone |
| 11 | the paths the table of contents names | those the cost model weighs against the bytes of every leaf entry | none, every path through the directory |
| 12 | where shredding happens | in the jobs that already rewrite every row of a slice, level 0's exit, the fresh merges and the seal, with level 0's variants parsed once per query node | in the append, as other writers do, which puts its cost on ingestion |
| 13 | reading one path | the path map, and only the path's buffers read, with the trees inlined in an object whose footer can hold them | every chunk's segment read whole, every path's bytes with it, or every tree inlined, which a day of chunks makes hundreds of megabytes of footer |
| 14 | the variant column's chunks | long chunks of its own, about 10⁶ rows, sized so that the trees and the path map stay within the budget | the file's chunks, which multiply the trees |
| 15 | where what the format lacks for a path lives | the index directory, which Rust never reads, until an edition freezes a layout for paths | a layout of our own, which a strict Rust reader refuses to open ([where an index lives](10-indexes.md#2-where-an-index-lives)) |
| 16 | pruning on a path below the object | block statistics per shredded path, kept where they prune, those that prune least dropped first under the index budget | none below the object, or statistics for every path in every block |
| 17 | indexes on a path | entries that name a column, a path and a type class, built by the policy as for any column | indexes on declared columns only, which leaves a needle in an attribute to every block that holds the path |
| 18 | the integers of one path | one typed column for every width, rebuilt at the widest, equal in value under the format's classes | a column per width, which splits a path between its typed column and its field's `value` |
| 19 | the validity of a shredded field | runs or sparse, chosen by their cost, once Rust reads it there | a plain bitmap, a bit per row and per field |
| 20 | the shapes a variant chunk holds | measured first: the shape first in the order, chunks cut where the shape changes, or a budget that counts a chunk's shapes | chunks of 10⁶ rows whatever their shapes |
| 21 | a path's encoding across chunks | fixed per object, so the path map stays small | chosen per chunk, which the path map pays for |
| 22 | the variants of level 0 | parsed once per query node and object, kept in memory until it leaves level 0 | shredded in the append, from the previous object's choice of paths |

## 14. Alternatives weighed

| alternative | what it offers | why it was not chosen |
|---|---|---|
| one shredding schema for the whole dataset, from the keys most rows hold | one layout to plan for | the keys of small sources fall under any global threshold, and the schema ages as sources change. Each object choosing alone adapts to both |
| frequent sets of keys per tile of 2¹⁰ to 2¹² records, records moved between neighbouring tiles, as JSON tiles do (source) | columns for what most records of a tile share | the same idea at a finer grain. Sorting a slice by shape groups the same records, and the cost model replaces their threshold |
| a map of keys to values | simple, and Rust reads it | one key costs the whole bag, and no statistic describes a key ([the models, compared](#3-the-models-compared)) |
| a table of one row per key and value, sorted by key | a key's values together | every question over two keys joins rows back |
| numbered columns | a bounded number of columns | a column means another key in each object |
| declared columns added by schema evolution | the fastest reads | a commit per key, a schema that only grows, and objects that answer for keys they never hold |
| a layout of our own that makes each shredded path a column of the layout, with its zone map | the format's own pruning on paths, and a segment per path | a strict Rust reader fails to open a file whose layout it does not know, so every file with a variant would stop reading in Rust ([where an index lives](10-indexes.md#2-where-an-index-lives)). Proposing such a layout upstream is the way, and the index directory serves until an edition freezes one |

## 15. Sources

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
