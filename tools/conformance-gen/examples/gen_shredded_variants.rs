//! Variant columns shredded by the reference's own shredder, each beside the same rows unshredded:
//! the files the .NET reader checks it rebuilds the same variants from, so that it reads what Rust
//! writes (docs/design/21-variant-columns.md, "How a variant is stored").
//!
//! Each case is one or more chunks of JSON documents, each chunk with its shredding schema. The
//! documents become a Parquet variant column through the Arrow variant crate the reference uses,
//! `json_to_variant`, are shredded by `shred_variant`, and are written three times as a variant
//! column, with the same chunks: as they were, `<case>.plain.vortex`, shredded through the
//! reference's own pipeline, `<case>.shredded.vortex`, and shredded but written verbatim,
//! `<case>.parquet.vortex`. A reader that rebuilds the shredded rows must find the plain rows'
//! values.
//!
//! The reference's pipeline turns a shredded `vortex.parquet.variant` into its canonical form
//! before it compresses it, so `shredded` holds a `vortex.variant` whose second child is the
//! shredded tree: an object a struct by field name, a list a list, a primitive its typed column,
//! and a field that kept a residual a nested `vortex.variant`. `parquet` keeps the
//! `vortex.parquet.variant` with its `typed_value` in the Parquet layout, which the reference
//! reads but never writes on its own. After writing, the reference reads each shredded file back
//! and compares it with the plain one, row by row, and the run says where they differ.
//!
//! ```sh
//! cargo run --release --example gen_shredded_variants -- <out-dir>
//! ```

use std::path::Path;
use std::path::PathBuf;
use std::sync::Arc;

use arrow_array::Array;
use arrow_array::ArrayRef as ArrowArrayRef;
use arrow_array::ListArray;
use arrow_array::StringArray;
use arrow_array::StructArray as ArrowStructArray;
use arrow_array::cast::AsArray;
use arrow_schema::DataType;
use arrow_schema::Field;
use arrow_schema::Fields;
use parquet_variant_compute::VariantArray as ArrowVariantArray;
use parquet_variant_compute::json_to_variant;
use parquet_variant_compute::shred_variant;
use vortex::VortexSessionDefault;
use vortex::array::ArrayId;
use vortex::array::ArrayRef;
use vortex::array::session::ArraySessionExt;
use vortex::compressor::BtrBlocksCompressorBuilder;
use vortex::editions::ComponentKind;
use vortex::editions::EditionSessionExt;
use vortex::file::WriteStrategyBuilder;
use vortex::utils::aliases::hash_set::HashSet;
use vortex::array::IntoArray;
use vortex::array::VortexSessionExecute;
use vortex::array::arrays::ChunkedArray;
use vortex::array::arrays::StructArray;
use vortex::array::stream::ArrayStreamExt;
use vortex::array::validity::Validity;
use vortex::arrow::ArrowSessionExt;
use anyhow::Context;
use vortex::dtype::FieldNames;
use vortex::encodings::parquet_variant::ParquetVariant;
use vortex::file::OpenOptionsSessionExt;
use vortex::file::WriteOptionsSessionExt;
use vortex::io::runtime::Handle;
use vortex::io::runtime::single::block_on;
use vortex::io::session::RuntimeSessionExt;
use vortex::io::std_file::FileWrite;
use vortex::layout::LayoutStrategy;
use vortex::layout::layouts::chunked::writer::ChunkedLayoutStrategy;
use vortex::layout::layouts::flat::writer::FlatLayoutStrategy;
use vortex::session::VortexSession;

/// One case: its name and its chunks.
struct Case {
    name: &'static str,
    chunks: Vec<Chunk>,
}

/// One chunk: its documents, and the type its variant is shredded as.
struct Chunk {
    documents: Vec<&'static str>,
    /// How many rows the chunk holds: its documents over and over.
    rows: usize,
    shredding: DataType,
    /// Whether a shredded list's elements keep only their typed column. The reference turns a
    /// shredded column into its canonical form before writing it, and that conversion refuses an
    /// element that keeps a `value` column (vortex-parquet-variant 0.86.1 and 0.87.0 pair the
    /// rows' metadata with the elements' values, whose lengths differ). The standard lets an
    /// element whose values all fit its typed column hold no `value` at all, which it converts.
    typed_elements: bool,
}

/// The rows of a block the reference's writer cuts a column into. A chunk of exactly this many
/// rows reaches the layout alone; smaller ones are concatenated first, and the reference refuses
/// to concatenate chunks shredded differently.
const ROW_BLOCK: usize = 8192;

impl Chunk {
    fn new(documents: Vec<&'static str>, shredding: DataType) -> Self {
        let rows = documents.len();
        Self { documents, rows, shredding, typed_elements: false }
    }

    fn block(documents: Vec<&'static str>, shredding: DataType) -> Self {
        Self { rows: ROW_BLOCK, ..Self::new(documents, shredding) }
    }
}

fn object_shredding() -> DataType {
    DataType::Struct(Fields::from(vec![
        Field::new("route", DataType::Utf8, true),
        Field::new("status", DataType::Int64, true),
    ]))
}

fn cases() -> Vec<Case> {
    vec![
        // An object, two keys shredded as an integer and a string, a third left in the residual,
        // a key of another type than its shredded one, a missing key, a null row, a non-object row.
        Case {
            name: "object",
            chunks: vec![Chunk::new(
                vec![
                    r#"{"status": 200, "route": "/api/users", "user": "u1"}"#,
                    r#"{"status": 502, "route": "/api/orders"}"#,
                    r#"{"status": "late", "route": "/health", "extra": [1, 2, 3]}"#,
                    r#"{"route": "/api/users"}"#,
                    r#"null"#,
                    r#"42"#,
                    r#"{"status": 404, "route": null, "user": {"id": 7, "name": "x"}}"#,
                ],
                object_shredding(),
            )],
        },
        // Nested: an object shredded inside an object.
        Case {
            name: "nested",
            chunks: vec![Chunk::new(
                vec![
                    r#"{"http": {"method": "GET", "status": 200}, "service": "api"}"#,
                    r#"{"http": {"method": "POST"}, "service": "auth"}"#,
                    r#"{"http": "not an object", "service": "api"}"#,
                    r#"{"service": "web", "http": {"status": 500, "client": "curl"}}"#,
                ],
                DataType::Struct(Fields::from(vec![
                    Field::new(
                        "http",
                        DataType::Struct(Fields::from(vec![
                            Field::new("method", DataType::Utf8, true),
                            Field::new("status", DataType::Int64, true),
                        ])),
                        true,
                    ),
                    Field::new("service", DataType::Utf8, true),
                ])),
            )],
        },
        // A list of integers shredded as a list, beside rows of another type. Every element is
        // an integer, so the elements keep their typed column only.
        Case {
            name: "list",
            chunks: vec![Chunk {
                typed_elements: true,
                ..Chunk::new(
                    vec![
                        r#"[1, 2, 3]"#,
                        r#"[]"#,
                        r#"[10, 11, 12]"#,
                        r#""a string""#,
                        r#"{"not": "a list"}"#,
                        r#"[7]"#,
                    ],
                    DataType::List(Arc::new(Field::new("element", DataType::Int64, true))),
                )
            }],
        },
        // A primitive shredded whole: every row a number, one row of another type.
        Case {
            name: "primitive",
            chunks: vec![Chunk::new(
                vec![r#"1.5"#, r#"2.25"#, r#""text""#, r#"-3.0"#, r#"null"#],
                DataType::Float64,
            )],
        },
        // A key with a dot beside an object holding the key after the dot: two paths, the dotted
        // key shredded as one field and the object as another.
        Case {
            name: "dotted",
            chunks: vec![Chunk::new(
                vec![
                    r#"{"http.route": "/a", "http": {"route": "/b"}}"#,
                    r#"{"http.route": "/c"}"#,
                    r#"{"http": {"route": "/d", "method": "GET"}}"#,
                ],
                DataType::Struct(Fields::from(vec![
                    Field::new("http.route", DataType::Utf8, true),
                    Field::new(
                        "http",
                        DataType::Struct(Fields::from(vec![Field::new("route", DataType::Utf8, true)])),
                        true,
                    ),
                ])),
            )],
        },
        // One column, three chunks of a block each shredded three ways: the same keys as an
        // object, as another object, and not shredded at all.
        Case {
            name: "chunks",
            chunks: vec![
                Chunk::block(
                    vec![
                        r#"{"status": 200, "route": "/a", "user": "u1"}"#,
                        r#"{"status": 500, "route": "/b"}"#,
                    ],
                    object_shredding(),
                ),
                Chunk::block(
                    vec![
                        r#"{"status": 201, "route": "/c", "user": "u2"}"#,
                        r#"{"user": "u3", "route": "/d"}"#,
                    ],
                    DataType::Struct(Fields::from(vec![Field::new("user", DataType::Utf8, true)])),
                ),
                Chunk::block(vec![r#"{"status": 404, "route": "/e"}"#, r#""plain""#], DataType::Null),
            ],
        },
    ]
}

fn main() -> anyhow::Result<()> {
    let out = PathBuf::from(
        std::env::args()
            .nth(1)
            .unwrap_or_else(|| "../../tests/Vorticity.Tests/Data/shredded-variants".to_string()),
    );
    std::fs::create_dir_all(&out)?;
    block_on(|handle: Handle| async move {
        let session = VortexSession::default().with_handle(handle.clone());
        for case in cases() {
            let mut plain = Vec::new();
            let mut shredded = Vec::new();
            for chunk in &case.chunks {
                let documents: Vec<&str> = chunk.documents.iter().copied().cycle().take(chunk.rows).collect();
                let json: ArrowArrayRef = Arc::new(StringArray::from(documents));
                let variant = json_to_variant(&json)?;
                plain.push(ParquetVariant::from_arrow_variant(&variant, &session.arrow())?);
                let shred = if chunk.shredding == DataType::Null {
                    variant
                } else {
                    let shred = shred_variant(&variant, &chunk.shredding)?;
                    if chunk.typed_elements { without_element_values(&shred)? } else { shred }
                };
                shredded.push(ParquetVariant::from_arrow_variant(&shred, &session.arrow())?);
            }

            let plain_path = out.join(format!("{}.plain.vortex", case.name));
            let shredded_path = out.join(format!("{}.shredded.vortex", case.name));
            let parquet_path = out.join(format!("{}.parquet.vortex", case.name));
            write(&session, plain, &plain_path, Strategy::Default).await?;
            write(&session, shredded.clone(), &shredded_path, Strategy::Default)
                .await
                .with_context(|| format!("{}: the reference writing the shredded chunks", case.name))?;
            write(&session, shredded, &parquet_path, Strategy::Verbatim)
                .await
                .with_context(|| format!("{}: the reference writing the Parquet layout verbatim", case.name))?;
            let rows: usize = case.chunks.iter().map(|c| c.rows).sum();
            for path in [&shredded_path, &parquet_path] {
                let differences = compare(&session, &plain_path, path)
                    .await
                    .with_context(|| format!("{}: the reference reading {} back", case.name, path.display()))?;
                println!(
                    "{}: {} rows in {} chunks; the reference reads {} of {} back",
                    case.name,
                    rows,
                    case.chunks.len(),
                    if differences.is_empty() { "every row".to_string() } else { differences.join("; ") },
                    path.file_name().map(|n| n.to_string_lossy()).unwrap_or_default(),
                );
            }
        }

        anyhow::Ok(())
    })
}

/// The shredded variant with its list elements' `value` column dropped, which must hold no value:
/// each element is then its typed column alone.
fn without_element_values(variant: &ArrowVariantArray) -> anyhow::Result<ArrowVariantArray> {
    let storage = variant.inner();
    let mut fields = Vec::new();
    let mut columns: Vec<ArrowArrayRef> = Vec::new();
    for (field, column) in storage.fields().iter().zip(storage.columns()) {
        if field.name() != "typed_value" {
            fields.push(Arc::clone(field));
            columns.push(Arc::clone(column));
            continue;
        }

        let list = column.as_list::<i32>();
        let elements = list.values().as_struct();
        let value = elements
            .column_by_name("value")
            .ok_or_else(|| anyhow::anyhow!("a shredded element has no value column"))?;
        anyhow::ensure!(
            value.null_count() == value.len(),
            "a shredded element holds a value its typed column does not"
        );
        let typed = elements
            .column_by_name("typed_value")
            .ok_or_else(|| anyhow::anyhow!("a shredded element has no typed_value column"))?;
        let typed_field = Arc::new(Field::new("typed_value", typed.data_type().clone(), true));
        let group = ArrowStructArray::try_new(
            Fields::from(vec![Arc::clone(&typed_field)]),
            vec![Arc::clone(typed)],
            elements.nulls().cloned(),
        )?;
        let element = Arc::new(Field::new("element", group.data_type().clone(), false));
        let list = ListArray::try_new(
            Arc::clone(&element),
            list.offsets().clone(),
            Arc::new(group),
            list.nulls().cloned(),
        )?;
        fields.push(Arc::new(Field::new("typed_value", list.data_type().clone(), true)));
        columns.push(Arc::new(list));
    }

    let storage = ArrowStructArray::try_new(Fields::from(fields), columns, storage.nulls().cloned())?;
    Ok(ArrowVariantArray::try_new(&storage)?)
}

/// How a file is written.
enum Strategy {
    /// The reference's own pipeline, which turns a shredded `vortex.parquet.variant` into its
    /// canonical `vortex.variant` before compressing it.
    Default,
    /// Each chunk as it is, in a flat layout: the shredded `vortex.parquet.variant` keeps its
    /// `typed_value`, the form the reference reads but never writes on its own.
    Verbatim,
}

/// Writes the chunks as the one column, `v`, of a table: a table per chunk, which the write stream
/// hands the layout one at a time.
async fn write(session: &VortexSession, chunks: Vec<ArrayRef>, path: &Path, strategy: Strategy) -> anyhow::Result<()> {
    let tables = chunks
        .into_iter()
        .map(|column| {
            let rows = column.len();
            Ok(StructArray::try_new(FieldNames::from(["v"]), vec![column], rows, Validity::NonNullable)?
                .into_array())
        })
        .collect::<anyhow::Result<Vec<ArrayRef>>>()?;
    let dtype = tables[0].dtype().clone();
    let table = ChunkedArray::try_new(tables, dtype)?.into_array();
    let sink = FileWrite::create(path, session.handle()).await?;
    let strategy: Arc<dyn LayoutStrategy> = match strategy {
        Strategy::Default => WriteStrategyBuilder::default()
            // The 1 MiB coalescing target would concatenate the chunks before compressing them,
            // which the reference refuses for chunks shredded differently.
            .with_data_block_target_bytes(None)
            // A strategy passed by hand opts out of the editions' encodings, so they are put back.
            .with_btrblocks_builder(
                BtrBlocksCompressorBuilder::default().retain_allowed_encodings(&allowed_encodings(session)),
            )
            .build(),
        Strategy::Verbatim => {
            let flat: Arc<dyn LayoutStrategy> = Arc::new(FlatLayoutStrategy::default());
            Arc::new(ChunkedLayoutStrategy::new(flat))
        }
    };
    session
        .write_options()
        .with_strategy(strategy)
        .write(sink, table.to_array_stream())
        .await?;
    Ok(())
}

/// The array encodings the session's editions allow, as the corpus emitter computes them.
fn allowed_encodings(session: &VortexSession) -> HashSet<ArrayId> {
    let arrays = session.arrays();
    session
        .enabled_component_ids(ComponentKind::Array)
        .iter()
        .filter_map(|serialized_id| arrays.registry().get(serialized_id))
        .map(|plugin| plugin.id())
        .collect()
}

/// The rows the reference reads differently from the two files, by its own scalar equality.
async fn compare(session: &VortexSession, plain: &Path, shredded: &Path) -> anyhow::Result<Vec<String>> {
    let plain = read_all(session, plain).await?;
    let shredded = read_all(session, shredded).await?;
    let mut ctx = session.create_execution_ctx();
    let mut differences = Vec::new();
    for row in 0..plain.len() {
        let want = plain.execute_scalar(row, &mut ctx)?;
        match shredded.execute_scalar(row, &mut ctx) {
            Ok(got) if got == want => {}
            Ok(got) => differences.push(format!("row {row} is {got} shredded and {want} plain")),
            Err(error) => differences.push(format!("row {row} fails: {error}")),
        }
    }

    Ok(differences)
}

async fn read_all(session: &VortexSession, path: &Path) -> anyhow::Result<ArrayRef> {
    let file = session.open_options().open_path(path).await?;
    Ok(file.scan()?.into_array_stream()?.read_all().await?)
}
