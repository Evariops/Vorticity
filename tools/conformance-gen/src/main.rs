//! Golden corpus generator for Vorticity conformance tests.
//!
//! docs/04-conformance.md states the problem this crate exists to solve: an implementation that
//! only tests against itself proves nothing, because a round trip through our own writer and
//! reader is self-consistent and can be uniformly wrong. Everything must be anchored to files
//! produced by the Rust reference implementation. This binary produces them, plus an
//! expected-value sidecar per file so that day-to-day .NET runs need no Rust toolchain.
//!
//! ```text
//! cargo run --release -j 6 --                       # the whole corpus
//! cargo run --release -j 6 -- --filter encodings/   # one dimension
//! cargo run --release -j 6 -- --list                # what would be produced
//! cargo run --release -j 6 -- --verify              # re-hash against the manifest
//! ```
//!
//! **Determinism is a requirement.** Every random value comes from [`util::Rng`] seeded from a
//! recorded master seed and the entry id, so a single file can be regenerated in isolation and
//! come out byte-identical. The writer itself is deterministic because it pre-populates the array
//! context rather than interning ids in completion order (`vortex-file-0.86.1/src/writer.rs:387`,
//! `NOTE(os)`); the manifest records that dependency explicitly.

mod emit;
mod encodings;
mod forged;
mod manifest;
mod schema;
mod sidecar;
mod util;

use std::collections::BTreeSet;
use std::path::Path;
use std::path::PathBuf;
use std::process::Command;

use anyhow::Context;
use anyhow::bail;
use futures_lite::FutureExt;
use vortex::VortexSessionDefault;
use vortex::editions::EditionSessionExt;
use vortex::io::runtime::single::block_on;
use vortex::io::runtime::Handle;
use vortex::io::session::RuntimeSessionExt;
use vortex::session::VortexSession;

use crate::emit::Entry;
use crate::manifest::Coverage;
use crate::manifest::Determinism;
use crate::manifest::FileRecord;
use crate::manifest::Generator;
use crate::manifest::Manifest;
use crate::manifest::SkipRecord;

/// Fixed and recorded. Changing it changes every random value in the corpus, so it is a corpus
/// version bump, not a tuning knob.
const MASTER_SEED: u64 = 0x564F_5254_4558_0001; // "VORTEX" + 0x0001

/// Prefix a child process uses to hand its `FileRecord` back to the parent on stdout.
const RECORD_PREFIX: &str = "VORTEX_CORPUS_RECORD ";

struct Args {
    out: PathBuf,
    filters: Vec<String>,
    seed: u64,
    list: bool,
    verify: bool,
    /// Delete corpus files the new manifest does not reference. Only meaningful on a full run.
    prune: bool,
    /// Child mode: produce exactly this entry and print its record. Used for the entries that
    /// need process-level environment switches.
    only: Option<String>,
}

fn default_out_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../tests/Vorticity.Conformance/corpus")
}

fn parse_args() -> anyhow::Result<Args> {
    let mut args = Args {
        out: default_out_dir(),
        filters: Vec::new(),
        seed: MASTER_SEED,
        list: false,
        verify: false,
        prune: false,
        only: None,
    };
    let mut argv = std::env::args().skip(1);
    while let Some(arg) = argv.next() {
        match arg.as_str() {
            "--out" => {
                args.out = PathBuf::from(argv.next().context("--out needs a path")?);
            }
            "--filter" => {
                args.filters
                    .push(argv.next().context("--filter needs a substring")?);
            }
            "--only" => {
                args.only = Some(argv.next().context("--only needs an entry id")?);
            }
            "--seed" => {
                let raw = argv.next().context("--seed needs a u64")?;
                args.seed = raw.parse().context("--seed must be a decimal u64")?;
            }
            "--list" => args.list = true,
            "--prune" => args.prune = true,
            "--verify" => args.verify = true,
            "-h" | "--help" => {
                print_help();
                std::process::exit(0);
            }
            other => bail!("unknown argument {other}; try --help"),
        }
    }
    Ok(args)
}

fn print_help() {
    println!(
        "conformance-gen — generates the golden Vortex corpus for Vorticity\n\
         \n\
         --out <dir>        output directory (default: tests/Vorticity.Conformance/corpus)\n\
         --filter <substr>  only entries whose id contains <substr>; repeatable\n\
         --seed <u64>       master PRNG seed (default: the recorded corpus seed)\n\
         --list             print the plan and exit\n\
         --verify           re-hash the existing corpus against manifest.json and exit\n\
         --prune            delete corpus files the new manifest does not reference\n\
         --only <id>        produce exactly one entry and print its record (internal)\n"
    );
}

fn main() -> anyhow::Result<()> {
    let args = parse_args()?;

    if args.verify {
        return verify(&args.out);
    }

    let (entries, static_skips) = emit::plan();
    let entries: Vec<Entry> = entries
        .into_iter()
        .filter(|e| {
            args.filters.is_empty() || args.filters.iter().any(|f| e.id.contains(f.as_str()))
        })
        .filter(|e| args.only.as_ref().is_none_or(|only| &e.id == only))
        .collect();

    if args.list {
        for e in &entries {
            println!(
                "{:<6} {:<58} {}{}",
                e.dimension,
                e.id,
                e.description,
                if e.env.is_empty() {
                    String::new()
                } else {
                    format!(
                        " [env: {}]",
                        e.env
                            .iter()
                            .map(|(k, v)| format!("{k}={v}"))
                            .collect::<Vec<_>>()
                            .join(" ")
                    )
                }
            );
        }
        println!("\n{} entries, {} static skips", entries.len(), static_skips.len());
        for s in &static_skips {
            println!("  SKIP {:<6} {}", s.dimension, s.id);
        }
        return Ok(());
    }

    if entries.is_empty() {
        bail!("no entries matched the filter");
    }

    std::fs::create_dir_all(&args.out)
        .with_context(|| format!("creating {}", args.out.display()))?;

    let child_mode = args.only.is_some();
    let (records, skips) = run(&args, &entries, child_mode)?;

    if child_mode {
        // The parent reads exactly one record line off our stdout.
        for record in &records {
            println!("{RECORD_PREFIX}{}", serde_json::to_string(record)?);
        }
        for skip in &skips {
            eprintln!("child skip: {} — {}", skip.id, skip.reason);
        }
        return Ok(());
    }

    let mut all_skips = static_skips;
    all_skips.extend(skips);
    all_skips.sort_by(|a, b| a.id.cmp(&b.id));

    let filtered = !args.filters.is_empty();
    write_manifest(&args, &records, &all_skips, filtered)?;
    write_sidecar_spec(&args.out)?;

    // The forged fixtures live next to the corpus, not in it: they are not reference-implementation
    // output, and a .NET test that globbed the corpus must not pick them up as golden files.
    let forged_dir = args
        .out
        .parent()
        .map(|p| p.join("forged"))
        .unwrap_or_else(|| args.out.join("../forged"));
    match forged::write_forged(&args.out, &forged_dir) {
        Ok(0) => {}
        Ok(n) => eprintln!("wrote {n} forged fixture(s) to {}", forged_dir.display()),
        Err(e) => eprintln!("WARNING: forged fixtures not written: {e:#}"),
    }

    let orphans = find_orphans(&args.out, &records)?;
    if args.prune && !filtered {
        for orphan in &orphans {
            std::fs::remove_file(args.out.join(orphan))
                .with_context(|| format!("removing {orphan}"))?;
        }
    }
    report(&args, &records, &all_skips, filtered, &orphans, args.prune && !filtered);
    Ok(())
}

/// Corpus files under `out` that the new manifest does not reference.
///
/// Renaming or dropping an entry otherwise leaves its old `.vortex` behind, and a stale file that
/// no manifest describes is worse than no file: a .NET test that globs the directory would read
/// it as if it were current.
fn find_orphans(out: &Path, records: &[FileRecord]) -> anyhow::Result<Vec<String>> {
    let known: BTreeSet<&str> = records
        .iter()
        .flat_map(|r| [r.path.as_str(), r.sidecar.as_str()])
        .collect();

    let mut orphans = Vec::new();
    let mut stack = vec![out.to_path_buf()];
    while let Some(dir) = stack.pop() {
        let Ok(entries) = std::fs::read_dir(&dir) else {
            continue;
        };
        for entry in entries {
            let path = entry?.path();
            if path.is_dir() {
                stack.push(path);
                continue;
            }
            let is_corpus_file = matches!(
                path.extension().and_then(|e| e.to_str()),
                Some("vortex") | Some("jsonl")
            );
            if !is_corpus_file {
                continue;
            }
            let relative = path
                .strip_prefix(out)
                .map(|p| p.to_string_lossy().replace('\\', "/"))
                .unwrap_or_default();
            if !known.contains(relative.as_str()) {
                orphans.push(relative);
            }
        }
    }
    orphans.sort();
    Ok(orphans)
}

/// Produce every entry. Failures become skips: one unsupported recipe must not lose the corpus.
fn run(
    args: &Args,
    entries: &[Entry],
    child_mode: bool,
) -> anyhow::Result<(Vec<FileRecord>, Vec<SkipRecord>)> {
    block_on(|handle: Handle| async move {
        let mut records: Vec<FileRecord> = Vec::new();
        let mut skips: Vec<SkipRecord> = Vec::new();
        let mut sessions: Vec<(String, VortexSession)> = Vec::new();

        for (index, entry) in entries.iter().enumerate() {
            // An entry with env switches needs its own process: every Vortex environment switch is
            // a `LazyLock` read once per process. In child mode we are already that process.
            if !entry.env.is_empty() && !child_mode {
                match spawn_child(args, entry) {
                    Ok(record) => {
                        eprintln!("[{:>4}/{}] {} (child)", index + 1, entries.len(), entry.id);
                        records.push(record);
                    }
                    Err(e) => skips.push(SkipRecord {
                        id: entry.id.clone(),
                        dimension: entry.dimension.to_string(),
                        description: entry.description.clone(),
                        reason: format!("child process failed: {e:#}"),
                    }),
                }
                continue;
            }

            let edition = entry.edition.unwrap_or(emit::DEFAULT_EDITION).to_string();
            let session = match sessions.iter().find(|(name, _)| name == &edition) {
                Some((_, session)) => session.clone(),
                None => {
                    let session = VortexSession::default().with_handle(handle.clone());
                    if let Some(name) = entry.edition {
                        let id = emit::EDITIONS
                            .iter()
                            .find(|(n, _)| *n == name)
                            .map(|(_, id)| *id)
                            .ok_or_else(|| anyhow::anyhow!("unknown edition {name}"))?;
                        session.enable_edition(id)?;
                    }
                    sessions.push((edition.clone(), session.clone()));
                    session
                }
            };

            let seed = util::entry_seed(args.seed, &entry.id);
            // Several 0.86.1 writer limitations surface as panics rather than errors (the file
            // statistics accumulator on a nullable top-level struct, for one), so a panic has to
            // become a skip like any other unsupported recipe.
            let produced = std::panic::AssertUnwindSafe(emit::produce(
                &session, entry, &args.out, seed,
            ))
            .catch_unwind()
            .await
            .unwrap_or_else(|payload| Err(anyhow::anyhow!("panicked: {}", panic_message(&payload))));

            match produced {
                Ok(record) => {
                    eprintln!(
                        "[{:>4}/{}] {} — {} rows, {} bytes, arrays {:?}",
                        index + 1,
                        entries.len(),
                        record.id,
                        record.row_count,
                        record.size_bytes,
                        record.array_ids
                    );
                    records.push(record);
                }
                Err(e) => {
                    eprintln!("[{:>4}/{}] {} — SKIPPED: {e:#}", index + 1, entries.len(), entry.id);
                    skips.push(SkipRecord {
                        id: entry.id.clone(),
                        dimension: entry.dimension.to_string(),
                        description: entry.description.clone(),
                        reason: format!("{e:#}"),
                    });
                }
            }
        }

        Ok::<_, anyhow::Error>((records, skips))
    })
}

/// Best-effort human form of a panic payload.
fn panic_message(payload: &Box<dyn std::any::Any + Send>) -> String {
    if let Some(s) = payload.downcast_ref::<&str>() {
        (*s).to_string()
    } else if let Some(s) = payload.downcast_ref::<String>() {
        s.clone()
    } else {
        "<non-string panic payload>".to_string()
    }
}

/// Re-run this binary with the entry's environment switches set, and read its record back.
fn spawn_child(args: &Args, entry: &Entry) -> anyhow::Result<FileRecord> {
    let exe = std::env::current_exe().context("locating the generator binary")?;
    let mut cmd = Command::new(exe);
    cmd.arg("--out")
        .arg(&args.out)
        .arg("--seed")
        .arg(args.seed.to_string())
        .arg("--only")
        .arg(&entry.id);
    for (key, value) in &entry.env {
        cmd.env(key, value);
    }
    let output = cmd.output().context("spawning the generator child")?;
    if !output.status.success() {
        bail!(
            "exited with {}: {}",
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        );
    }
    let stdout = String::from_utf8(output.stdout).context("child stdout was not UTF-8")?;
    let line = stdout
        .lines()
        .find_map(|l| l.strip_prefix(RECORD_PREFIX))
        .ok_or_else(|| {
            anyhow::anyhow!(
                "child produced no record; stderr: {}",
                String::from_utf8_lossy(&output.stderr).trim()
            )
        })?;
    serde_json::from_str(line).context("parsing the child's record")
}

// -------------------------------------------------------------------------------------------
// Manifest and reporting
// -------------------------------------------------------------------------------------------

fn write_manifest(
    args: &Args,
    records: &[FileRecord],
    skips: &[SkipRecord],
    filtered: bool,
) -> anyhow::Result<()> {
    let mut records = records.to_vec();
    records.sort_by(|a, b| a.id.cmp(&b.id));

    let mut notes = vec![
        "Two runs of this generator produce byte-identical files: the writer pre-populates the \
         array context with every id the enabled editions permit rather than interning ids in \
         serialization-completion order, which is otherwise nondeterministic across runs \
         (vortex-file-0.86.1/src/writer.rs:387, NOTE(os))."
            .to_string(),
        "User metadata segments are sorted by key before being written \
         (vortex-file-0.86.1/src/footer/serializer.rs:116), so the HashMap they are collected in \
         does not leak its iteration order into the bytes."
            .to_string(),
        "Entries carrying environment switches are produced by a child process, because every \
         Vortex environment switch is a LazyLock read once per process. Their files depend on \
         that environment, which is recorded per file in `env`."
            .to_string(),
    ];
    if filtered {
        notes.push(
            "THIS MANIFEST IS PARTIAL: the run was filtered, so `coverage` describes only the \
             entries that were regenerated."
                .to_string(),
        );
    }

    let coverage: Coverage = manifest::summarize(&records);
    let manifest = Manifest {
        format: manifest::MANIFEST_FORMAT,
        generator: Generator {
            name: env!("CARGO_PKG_NAME"),
            version: env!("CARGO_PKG_VERSION"),
            source: "tools/conformance-gen",
            vortex_version: manifest::VORTEX_VERSION,
            default_edition: emit::DEFAULT_EDITION.to_string(),
        },
        determinism: Determinism {
            prng: util::PRNG_NAME,
            master_seed: args.seed,
            per_entry_seed: "splitmix64(master_seed ^ fnv1a64(entry_id))",
            notes,
        },
        coverage,
        caveats: manifest::CAVEATS.to_vec(),
        files: records,
        skipped: skips.to_vec(),
    };

    let path = args.out.join("manifest.json");
    let json = serde_json::to_string_pretty(&manifest)?;
    std::fs::write(&path, json).with_context(|| format!("writing {}", path.display()))?;
    eprintln!("wrote {}", path.display());
    Ok(())
}

/// The sidecar grammar, written into the corpus directory so it travels with the published CI
/// artifact.
///
/// The format was documented only in `tools/conformance-gen/README.md` and in the `sidecar` module
/// doc, neither of which is inside the artifact the .NET tests consume without a Rust toolchain.
/// The in-header `legend` describes value shapes; this describes the line grammar.
const SIDECAR_SPEC: &str = r####"# Sidecar format — `vortex-conformance-sidecar/2`

One `.jsonl` per `.vortex`, paired by filename (`x.vortex` <-> `x.jsonl`) **and** by the
`entry_id` / `path` / `sha256` fields of the header line. A loader should hash the `.vortex` and
compare it against the header before trusting anything else in the file: a sidecar regenerated
against a different file is otherwise undetectable from inside.

Every line is one JSON object. **Dispatch on the top-level `kind`.** `kind` is also the
discriminator inside dtype trees, so scanning a line's text for `"kind":"..."` instead of parsing
it will mis-dispatch. With a real JSON parser this is a non-issue.

## Line order

| # | `kind` | count | what it carries |
|---|---|---|---|
| 1 | `header` | 1 | format, entry id, path, sha256 of the `.vortex`, dtype Display, row count, legend |
| 2 | `dtype` | 1 | the file dtype as a parsed tree |
| 3 | `layout` | 1 | the layout tree; each `vortex.flat` node carries its `array_tree` |
| 4 | `metadata` | 1 | user metadata segments in stored order, payloads included |
| 5 | `file_stats` | 1 | `{present:false}`, or per-field statistics with precision |
| 6 | `zone_map` | 0..n | one per `vortex.zoned` layout, in depth-first order |
| 7 | `rows` | 0..n | up to 128 values each; row index = `from` + position in `v` |
| 8 | `null_counts` | 1 | per field path, rows null there or under a null ancestor |

A zero-row file has no `rows` lines and an empty `null_counts.by_path`.

## Value encodings

Read the header's `legend`; it is normative and travels with each file. The rules that catch the
most readers:

* `null` is JSON `null` and **nothing else ever is**. An empty string, an empty list and an empty
  map are all distinct from null and all encode as themselves.
* Integers are decimal **strings**, because JSON numbers are `f64` in most parsers and `u64::MAX`
  does not survive one.
* Floats are `{bits, dec}`, plus `special` when the value is not finite. **`bits` is normative**:
  it is the hex of the raw IEEE bytes, big-endian, so a wrong NaN payload or a `-0.0` read as
  `+0.0` fails the comparison. `dec` spells the infinities `Infinity` / `-Infinity` / `NaN`, which
  both Rust and .NET parse.
* Utf8 is `{b64, len, char_count}` — base64 of the UTF-8 **bytes**, the **byte** length, and the
  Unicode scalar count. They differ on every non-ASCII value, which is the point.
* Binary is `{b64, len}`. Base64 rather than a JSON string because a JSON string cannot carry
  invalid UTF-8, and silently repairing it would hide the bug the corpus exists to find.
* Decimals are `{unscaled, storage}` with `storage` in `i8|i16|i32|i64|i128|i256`.

## `null_counts` paths

`.`-joined field names from the root; the root itself is the empty string `""`. A row counts as
null at a path when the value there is null **or** when an ancestor struct is null — the number a
reader gets by materializing that leaf column. Note that a struct field may itself be named with a
`.` or be empty (`types/struct_field_names` does exactly that), so these paths are ambiguous for
that file by construction; use the `dtype` tree and index-based access there.

## `layout` -> `array_tree`

A layout node with `encoding_id == "vortex.flat"` carries `array_tree`: the **array** encoding tree
serialized inside that leaf, with child order preserved. Each node is
`{id, nchildren, nbuffers, metadata_len, metadata_b64, children}`.

`nchildren` is load-bearing on its own. For `fastlanes.bitpacked` and `vortex.alp` the patch shape
*is* the child count — no patches, patches without chunk offsets, patches with them — and all three
spell the same encoding id. `metadata_b64` is the raw encoding metadata protobuf; `vortex.bool`'s
bit offset lives in there and is the only thing that distinguishes one bool array from another.

## `zone_map` -> `aggregate_details`

Zone maps are the pruning input, and a bound is not a value. Each aggregate carries a `precision`:

* `exact` — `vortex.min()`, `vortex.max()`, `vortex.null_count()`, `vortex.nan_count()`.
* `bound` — `vortex.bounded_min(n)`, `vortex.bounded_max(n)`. The stored value is a truncated
  bound, not an extreme. A bounded partial is a struct `{bound, unknown}`; when `unknown` is
  `true` there is **no** bound at all, and treating the null `bound` as "no data" prunes wrongly.

## Caveats

`manifest.json`'s `caveats` array lists reference-implementation behaviours that look like corpus
bugs and are not — the float `sum` on a column containing an infinity, above all. A reader that
"corrects" one of them will disagree with every real Vortex file. Read it before filing one.
"####;

/// Write [`SIDECAR_SPEC`] into the corpus directory.
fn write_sidecar_spec(out: &Path) -> anyhow::Result<()> {
    let path = out.join("SIDECAR.md");
    std::fs::write(&path, SIDECAR_SPEC)
        .with_context(|| format!("writing {}", path.display()))?;
    Ok(())
}

fn report(
    args: &Args,
    records: &[FileRecord],
    skips: &[SkipRecord],
    filtered: bool,
    orphans: &[String],
    pruned: bool,
) {
    let coverage = manifest::summarize(records);
    println!("\n=== corpus ===");
    println!("  out:    {}", args.out.display());
    println!("  files:  {}", coverage.files);
    println!("  bytes:  {}", coverage.total_bytes);
    println!("  rows:   {}", coverage.total_rows);
    println!("  skips:  {}", skips.len());

    println!("\n=== coverage ===");
    // The gate of docs/04-conformance.md §3: every 1.0-scope component must have a corpus file.
    let mut gate_failed = Vec::new();
    for (label, set) in [
        ("arrays", &coverage.arrays),
        ("layouts", &coverage.layouts),
        ("ext dtypes", &coverage.extension_dtypes),
        ("aggregates", &coverage.aggregates),
    ] {
        println!(
            "  {label:<11} {}/{} components in 1.0 scope covered",
            set.covered.len(),
            set.claimed
        );
        if !set.missing.is_empty() {
            println!("      MISSING: {}", set.missing.join(", "));
            gate_failed.push((label, set.missing.clone()));
        }
    }
    println!(
        "  {:<11} {}/{} covered (docs/90-registry.md defers these to 1.1; not gating)",
        "deferred",
        coverage.deferred_arrays.covered.len(),
        coverage.deferred_arrays.claimed
    );
    if !coverage.deferred_arrays.missing.is_empty() {
        println!(
            "      not present: {}",
            coverage.deferred_arrays.missing.join(", ")
        );
    }
    if !coverage.unclaimed_observed.is_empty() {
        println!("  observed but listed nowhere in docs/90-registry.md:");
        for (label, ids) in [
            ("arrays", &coverage.unclaimed_observed.arrays),
            ("layouts", &coverage.unclaimed_observed.layouts),
            ("aggregates", &coverage.unclaimed_observed.aggregates),
            ("ext dtypes", &coverage.unclaimed_observed.extension_dtypes),
        ] {
            if !ids.is_empty() {
                println!("      {label:<11} {}", ids.join(", "));
            }
        }
    }
    // A missing component is only a build failure if nothing explains it. docs/04-conformance.md
    // §3 fails the build on a "claimed-but-untested" component; a component with a SkipRecord
    // naming the upstream limitation is documented, not untested-by-omission.
    let mut documented = Vec::new();
    let mut undocumented = Vec::new();
    for (label, missing) in &gate_failed {
        for id in missing {
            match skips.iter().find(|s| s.description.contains(id.as_str())) {
                Some(skip) => documented.push((*label, id.clone(), skip.id.clone())),
                None => undocumented.push((*label, id.clone())),
            }
        }
    }
    if gate_failed.is_empty() {
        println!("\n  COVERAGE GATE: pass — every 1.0-scope component has a corpus file.");
    } else if undocumented.is_empty() {
        println!("\n  COVERAGE GATE: pass with documented gaps");
        for (label, id, skip) in &documented {
            println!("      {label}: {id} — see skipped/{skip}");
        }
    } else {
        println!("\n  COVERAGE GATE: FAIL");
        for (label, id) in &undocumented {
            println!("      {label}: {id} — claimed for 1.0, no corpus file, no SkipRecord");
        }
        println!("      Produce it, or record why 0.86.1 cannot, in emit::static_skips.");
    }

    // The shape gate: properties an id set cannot express. A miss here is a real hole — every one
    // of these was silently absent from the corpus before it existed — so it fails the run the
    // same way a missing component does, unless the run was filtered.
    println!("\n=== shapes ===");
    let mut shape_misses = Vec::new();
    for check in &coverage.shapes {
        println!(
            "  [{}] {:<38} {}",
            if check.satisfied { "x" } else { " " },
            check.name,
            check.requirement
        );
        if !check.satisfied {
            shape_misses.push(check.name);
        }
    }
    if shape_misses.is_empty() {
        println!("\n  SHAPE GATE: pass");
    } else if filtered {
        println!("\n  SHAPE GATE: not evaluated (the run was filtered)");
    } else {
        println!("\n  SHAPE GATE: FAIL — {}", shape_misses.join(", "));
    }

    let unforced: Vec<&FileRecord> = records
        .iter()
        .filter(|r| !r.missing_expected_array_ids.is_empty())
        .collect();
    if !unforced.is_empty() {
        println!("\n=== forcing recipes that did NOT produce their encoding ===");
        for r in unforced {
            println!("  {}: missing {}", r.id, r.missing_expected_array_ids.join(", "));
        }
    }

    if !skips.is_empty() {
        println!("\n=== skipped ===");
        for s in skips {
            println!("  {:<6} {}", s.dimension, s.id);
        }
        println!("  (reasons are in manifest.json)");
    }

    if !orphans.is_empty() {
        println!(
            "\n=== {} corpus files not referenced by this manifest ===",
            orphans.len()
        );
        for orphan in orphans.iter().take(20) {
            println!("  {orphan}{}", if pruned { " (deleted)" } else { "" });
        }
        if orphans.len() > 20 {
            println!("  ... and {} more", orphans.len() - 20);
        }
        if !pruned {
            println!("  re-run with --prune to delete them");
        }
    }

    if filtered {
        println!("\nNOTE: the run was filtered, so manifest.json covers only the regenerated subset.");
    }
}

/// `--verify`: re-hash the corpus against `manifest.json` without regenerating anything.
fn verify(out: &Path) -> anyhow::Result<()> {
    #[derive(serde::Deserialize)]
    struct Partial {
        files: Vec<FileRecord>,
    }

    let path = out.join("manifest.json");
    let raw = std::fs::read_to_string(&path)
        .with_context(|| format!("reading {}", path.display()))?;
    let parsed: Partial = serde_json::from_str(&raw).context("parsing manifest.json")?;

    let mut bad = Vec::new();
    let mut missing = Vec::new();
    let mut seen = BTreeSet::new();

    for record in &parsed.files {
        let file = out.join(&record.path);
        seen.insert(record.path.clone());
        seen.insert(record.sidecar.clone());
        if !file.exists() {
            missing.push(record.path.clone());
            continue;
        }
        let actual = util::sha256_file(&file)?;
        if actual != record.sha256 {
            bad.push(format!(
                "{}: manifest {} but on disk {}",
                record.path, record.sha256, actual
            ));
        }
        let sidecar = out.join(&record.sidecar);
        if !sidecar.exists() {
            missing.push(record.sidecar.clone());
        } else {
            let actual = util::sha256_file(&sidecar)?;
            if actual != record.sidecar_sha256 {
                bad.push(format!(
                    "{}: manifest {} but on disk {}",
                    record.sidecar, record.sidecar_sha256, actual
                ));
            }
        }
    }

    println!("verified {} files against {}", parsed.files.len(), path.display());
    if !missing.is_empty() {
        println!("MISSING ({}):", missing.len());
        for m in &missing {
            println!("  {m}");
        }
    }
    if !bad.is_empty() {
        println!("HASH MISMATCH ({}):", bad.len());
        for b in &bad {
            println!("  {b}");
        }
    }
    if missing.is_empty() && bad.is_empty() {
        println!("all hashes match");
        Ok(())
    } else {
        bail!("corpus does not match its manifest")
    }
}
