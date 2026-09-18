// SPDX-License-Identifier: Apache-2.0
//
// Vortex's Rust reader behind a C ABI, so docs/05-benchmarks.md §2 can happen: both
// implementations measured IN ONE PROCESS, on the same bytes, with the same clock and the same
// page-cache state. Cross-process comparison is what makes a 1.4x ratio unreadable.
//
// WHY THIS IS NOT `vortex-ffi`. §2 originally named it, but `vortex-ffi` is `publish = false`
// upstream, so using it would mean a second git dependency; and it is a general-purpose C API with
// its own object model, whose per-call overhead would land inside the measurement. A shim built
// against the same crates.io pin the corpus was generated with (`vortex = "=0.86.1"`) measures the
// scan path and nothing else, which is the thing being compared.
//
// SINGLE-THREADED ON PURPOSE. `vortex::io::runtime::single::block_on` is the one-thread runtime,
// matching our own reader, which has no worker pool. §5 requires the thread count to be pinned on
// both sides - otherwise the ratio measures a threading-model difference rather than
// implementation quality.
//
// EVERY ENTRY POINT RETURNS A COUNT OR A NEGATIVE ERROR. No out-parameters, no allocation handed
// across the boundary, no object lifetime to manage: the whole surface is `path in, rows out`, so
// nothing in the harness can leak and nothing in the measurement is FFI bookkeeping.
use std::ffi::CStr;
use std::os::raw::c_char;
use std::panic::AssertUnwindSafe;
use std::panic::catch_unwind;
use std::sync::OnceLock;

use futures::StreamExt;
use futures::pin_mut;
use vortex::VortexSessionDefault;
use vortex::array::RecursiveCanonical;
use vortex::array::VortexSessionExecute;
use vortex::buffer::Buffer;
use vortex::scan::strict_sorted_buffer::StrictSortedBuffer;
use vortex::error::VortexResult;
use vortex::expr::and;
use vortex::expr::get_item;
use vortex::expr::lit;
use vortex::expr::lt;
use vortex::expr::gt_eq;
use vortex::expr::root;
use vortex::expr::select;
use vortex::file::OpenOptionsSessionExt;
use vortex::file::WriteOptionsSessionExt;
use vortex::io::runtime::BlockingRuntime;
use vortex::io::runtime::current::CurrentThreadRuntime;
use vortex::io::runtime::single::block_on;
use vortex::io::session::RuntimeSessionExt;
use vortex::array::stream::ArrayStreamExt;
use vortex::dtype::DType;
use vortex::scalar::DecimalValue;
use vortex::scalar::PValue;
use vortex::scalar::Scalar;
use vortex::scalar::ScalarValue;
use vortex::session::VortexSession;

/// The path was not valid UTF-8, or a null pointer.
const ERR_BAD_PATH: i64 = -1;

/// The reader returned an error.
const ERR_FAILED: i64 = -2;

/// The reader panicked. Reported rather than allowed to unwind across the ABI, which is UB.
const ERR_PANIC: i64 = -3;

/// The empty call: the FFI floor, so it can be subtracted when it matters (docs/05 §2).
///
/// # Safety
/// None; takes and returns nothing.
#[unsafe(no_mangle)]
pub extern "C" fn vxbench_noop() -> i64 {
    0
}

/// Opens `path` and scans every column of every batch, returning the row count.
///
/// # Safety
/// `path` must be a valid NUL-terminated C string for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vxbench_scan_all(path: *const c_char) -> i64 {
    run(path, |session, path| {
        block_on(|handle| {
            let session = session.with_handle(handle);
            async move {
                let file = session.open_options().open_path(path).await?;
                let stream = file.scan()?.into_array_stream()?;
                pin_mut!(stream);
                let mut rows: i64 = 0;
                while let Some(array) = stream.next().await {
                    rows += array?.len() as i64;
                }

                Ok(rows)
            }
        })
    })
}

/// Opens `path`, scans every batch AND CANONICALIZES IT, returning the row count.
///
/// THIS IS THE LIKE-FOR-LIKE AXIS, and `vxbench_scan_all` is not.
///
/// `file.scan()` hands back arrays in whatever encoding the file holds: a `vortex.fsst` column
/// comes out as an FsstArray, and `array.len()` answers from its metadata without touching a code.
/// The .NET reader has no such state -- its `RecordBatch` is canonical by construction, because
/// `CanonicalArena` is the only representation it has -- so `vxbench_scan_all` compares a scan that
/// decompresses against one that does not, on every compressed encoding. That is not a small
/// correction: on the 1M-row axis it is most of what the per-encoding ratios were measuring.
///
/// `execute::<RecursiveCanonical>` AND NOT `execute::<Canonical>`, and the difference is the whole
/// axis on every tabular file. `Canonical` runs `execute_until::<AnyCanonical>`, which STOPS as
/// soon as the ROOT matches one of twelve kinds (`vortex-array-0.86.1/src/canonical.rs:616-629`,
/// `:1233-1251`) -- and `Struct`, `Map`, `ListView` and `Variant` are all in that list. Upstream
/// states the consequence outright: "canonicalization is shallow: children of canonical
/// struct/list arrays may still be encoded" (`src/lib.rs:37-38`). So on a file whose root is a
/// struct of encoded columns, or a variant over a constant, this call decoded NOTHING: it opened
/// the file, split it, and returned. Measured on the corpus's `variant` file, that was 85 us
/// against our 600 -- a ratio of 7,00 between a full decode and a file open.
///
/// `RecursiveCanonical` (`canonical.rs:788`, `:814-991`) is the one that "recursively execute[s]
/// the array until all of its children are canonical", which is what our reader does by
/// construction: `CanonicalArena` is the only representation it has, so every column of every batch
/// is materialized. That is the like-for-like.
///
/// A COMMENT HERE USED TO CLAIM this was "the same call its arrow conversion makes". It is not:
/// the arrow path is `into_arrow`, which executes each struct field in turn
/// (`vortex-arrow-0.86.1/src/executor/struct_.rs:152-166`). That claim is what made the shallow
/// call look defensible, so it is stated rather than quietly deleted.
///
/// ONE ASYMMETRY TO WATCH, and it is not live today: `Canonical` -- which this goes through first
/// -- "will fully expand constant arrays" (`canonical.rs:616-620`). So does our reader, today.
/// The day `ConstantForm` becomes our default, this call would charge Rust for an expansion we no
/// longer pay, and the bench would be wrong in the other direction.
///
/// The result is dropped rather than accumulated: the decode has already happened by then, and
/// holding a million rows of it would measure the allocator.
///
/// # Safety
/// `path` must be a valid NUL-terminated C string for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vxbench_scan_canonical(path: *const c_char) -> i64 {
    run(path, |session, path| {
        block_on(|handle| {
            let session = session.with_handle(handle);
            async move {
                let mut ctx = session.create_execution_ctx();
                let file = session.open_options().open_path(&path).await?;
                let stream = file.scan()?.into_array_stream()?;
                pin_mut!(stream);
                let mut rows: i64 = 0;
                while let Some(array) = stream.next().await {
                    let array = array?;
                    rows += array.len() as i64;
                    let _canonical: RecursiveCanonical = array.execute(&mut ctx)?;
                }

                Ok(rows)
            }
        })
    })
}

/// Scans `path` canonically on upstream's multi-threaded runtime with exactly `threads` workers.
///
/// THE CONTENTION FAMILY HAD NO NUMBER AT ALL (BENCH-AUDIT.md D2, PERF-AUDIT-v2.md §7). Our reader
/// has `WithDegreeOfParallelism` and nothing measured it; the reference side was single-threaded by
/// construction here, so a ratio at more than one lane could not exist. It can now, and the thread
/// count is PINNED on both sides -- docs/05 §5's rule -- because a ratio between an `n`-lane reader
/// and a reference free to use every core measures a threading model, not a decoder.
///
/// `threads = 1` is NOT the same measurement as `vxbench_scan_canonical`: this one still hands the
/// work to a worker pool and pays for the hand-off, where the single-thread runtime drives the
/// future on the calling thread. Comparing lanes against lanes is the point; comparing this at 1
/// against the single-threaded entry point prices the pool itself.
///
/// # Safety
/// `path` must be a valid NUL-terminated C string for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vxbench_scan_canonical_threads(path: *const c_char, threads: i64) -> i64 {
    if threads <= 0 {
        return ERR_BAD_PATH;
    }

    run(path, move |session, path| {
        let runtime = CurrentThreadRuntime::new();
        let pool = runtime.new_pool();
        pool.set_workers(threads as usize);
        let session = session.with_handle(runtime.handle());
        runtime.block_on(async move {
            let mut ctx = session.create_execution_ctx();
            let file = session.open_options().open_path(&path).await?;
            let stream = file.scan()?.into_array_stream()?;
            pin_mut!(stream);
            let mut rows: i64 = 0;
            while let Some(array) = stream.next().await {
                let array = array?;
                rows += array.len() as i64;
                let _canonical: RecursiveCanonical = array.execute(&mut ctx)?;
            }

            Ok(rows)
        })
    })
}

/// Opens `path` and scans one field of every batch, returning the row count.
///
/// # Safety
/// `path` and `field` must be valid NUL-terminated C strings for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vxbench_scan_projected(path: *const c_char, field: *const c_char) -> i64 {
    let Some(field) = (unsafe { text(field) }) else {
        return ERR_BAD_PATH;
    };

    run(path, move |session, path| {
        let field = field.clone();
        block_on(|handle| {
            let session = session.with_handle(handle);
            async move {
                let file = session.open_options().open_path(path).await?;
                // Bound against the file's own dtype, the way the reference's own tests do it:
                // an unbound expression is refused by the scan builder's signature.
                let projection = select([field.as_str()], root())
                    .optimize_recursive(file.dtype())
                    .and_then(|expr| expr.bind(file.dtype()))?;
                let stream = file.scan()?.with_projection(projection).into_array_stream()?;
                pin_mut!(stream);
                let mut rows: i64 = 0;
                while let Some(array) = stream.next().await {
                    rows += array?.len() as i64;
                }

                Ok(rows)
            }
        })
    })
}

/// Opens `path` and reads ONE batch, returning its row count: the time-to-first-batch axis.
///
/// # Safety
/// `path` must be a valid NUL-terminated C string for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vxbench_open_first_batch(path: *const c_char) -> i64 {
    run(path, |session, path| {
        block_on(|handle| {
            let session = session.with_handle(handle);
            async move {
                let file = session.open_options().open_path(path).await?;
                let stream = file.scan()?.into_array_stream()?;
                pin_mut!(stream);
                match stream.next().await {
                    Some(array) => Ok(array?.len() as i64),
                    None => Ok(0),
                }
            }
        })
    })
}

/// Reads `path` and WRITES it back out with the default strategy, returning the rows written.
///
/// THE WRITE AXIS HAD NO REFERENCE AT ALL. `docs/05-benchmarks.md` compares reading against Vortex
/// Rust on five axes and writing against nothing, so every write-side change in this repository has
/// been measured against its own past and never against the implementation it is a port of. The
/// read is included in the measurement on BOTH sides -- it is the same file and the same reader, so
/// it is common-mode -- and `vxbench_scan_canonical` gives the caller the number to subtract when
/// the read is a large share.
///
/// The output goes to an in-memory sink rather than to disk, matching the .NET side's `DiscardSink`:
/// a write benchmark that measures the filesystem measures the filesystem.
///
/// # Safety
/// `path` must be a valid NUL-terminated C string for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vxbench_write(path: *const c_char) -> i64 {
    run(path, |session, path| {
        block_on(|handle| {
            let session = session.with_handle(handle);
            async move {
                let file = session.open_options().open_path(&path).await?;
                let rows = file.row_count() as i64;
                let stream = file.scan()?.into_array_stream()?;
                session
                    .write_options()
                    .write(Vec::<u8>::new(), stream)
                    .await?;
                Ok(rows)
            }
        })
    })
}

/// Takes `count` rows of `path`, one every `stride`, canonicalizing, and counts them.
///
/// THE TAKE AXIS HAD NO REFERENCE. docs/05's take figure was "0.32x of a full scan", which is a
/// ratio against ourselves and says nothing about whether the path is fast. The indices are a
/// stride rather than a list so the same call describes a scattered take of any density without
/// marshalling an array across the ABI -- the .NET side's `TakeBenchmarks` uses exactly this shape,
/// `i * 1024 + 511`.
///
/// # Safety
/// `path` must be a valid NUL-terminated C string for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vxbench_take(path: *const c_char, count: i64, stride: i64) -> i64 {
    if count < 0 || stride <= 0 {
        return ERR_BAD_PATH;
    }

    run(path, move |session, path| {
        block_on(|handle| {
            let session = session.with_handle(handle);
            async move {
                let mut ctx = session.create_execution_ctx();
                let file = session.open_options().open_path(&path).await?;
                let rows_in_file = file.row_count();
                let indices: Buffer<u64> = (0..count as u64)
                    .map(|i| (i * stride as u64) + (stride as u64 / 2))
                    .filter(|&row| row < rows_in_file)
                    .collect();
                let selection = StrictSortedBuffer::try_new(indices)?;
                let stream = file.scan()?.with_row_indices(selection).into_array_stream()?;
                pin_mut!(stream);
                let mut rows: i64 = 0;
                while let Some(array) = stream.next().await {
                    let array = array?;
                    rows += array.len() as i64;
                    let _canonical: RecursiveCanonical = array.execute(&mut ctx)?;
                }

                Ok(rows)
            }
        })
    })
}

/// Scans `path` under `field >= lo AND field < lo + width`, canonicalizing, and counts the rows.
///
/// THE FILTER AXIS HAD NO REFERENCE EITHER. `bench/BRANCHING.md` priced the comparison kernel's
/// remedy at 6.0x and `FilterSelectivityBenchmarks` measured the whole path at four selectivities,
/// but nothing said whether 230 microseconds for a 1% band was good, bad or indifferent -- the
/// reference had no filter entry point to ask.
///
/// The predicate is a BAND rather than a single comparison, because that is what the .NET side's
/// selectivity benchmark uses and what a zone map can actually prune: a half-open interval on one
/// i64 field. `lo` and `width` are the caller's, so the same call serves every selectivity.
///
/// # Safety
/// `path` and `field` must be valid NUL-terminated C strings for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vxbench_scan_filtered(
    path: *const c_char,
    field: *const c_char,
    lo: i64,
    width: i64,
) -> i64 {
    let Some(field) = (unsafe { text(field) }) else {
        return ERR_BAD_PATH;
    };

    run(path, move |session, path| {
        let field = field.clone();
        block_on(|handle| {
            let session = session.with_handle(handle);
            async move {
                let mut ctx = session.create_execution_ctx();
                let file = session.open_options().open_path(&path).await?;
                let predicate = and(
                    gt_eq(get_item(field.as_str(), root()), lit(lo)),
                    lt(get_item(field.as_str(), root()), lit(lo + width)),
                );
                let filter = predicate
                    .optimize_recursive(file.dtype())
                    .and_then(|expr| expr.bind(file.dtype()))?;
                let stream = file.scan()?.with_filter(filter).into_array_stream()?;
                pin_mut!(stream);
                let mut rows: i64 = 0;
                while let Some(array) = stream.next().await {
                    let array = array?;
                    rows += array.len() as i64;
                    let _canonical: RecursiveCanonical = array.execute(&mut ctx)?;
                }

                Ok(rows)
            }
        })
    })
}

/// Scans `path` and folds every decoded VALUE into one 64-bit checksum.
///
/// THE PRECONDITION `--ffi-check` NEVER HAD. It compared row counts, and a row count is not
/// evidence of a decode: upstream's lazy scan answers `len()` from metadata without materializing
/// a byte, which is how a "0.96x" ratio came to compare a decode against an absence of one. Same
/// rows, same order, same bytes is what this says instead.
///
/// WHAT IT DOES NOT PROVE, and a comment here used to claim it did: that
/// `vxbench_scan_canonical` canonicalizes. This walks `execute_scalar` row by row, which is a
/// DIFFERENT PATH -- it reaches every value whatever the scan did or did not decode, so it stayed
/// green for as long as that entry point was stopping at a canonical root without touching a
/// child. The two are checked separately, and this one checks values.
///
/// THE ENCODING IS A CONTRACT WITH THE .NET SIDE, byte for byte
/// (bench/Vorticity.Benchmarks/Checksum.cs):
///
///   null 0x00 · bool 0x01 + byte · signed 0x02 + width + LE bytes · unsigned 0x03 + width + LE
///   bytes · float 0x04 + width + LE bits · utf8/binary 0x05 + u32 LE length + bytes · struct 0x06
///   + u32 field count + fields in order · list 0x07 + u32 count + elements · decimal 0x08 + width
///   + LE bits · extension 0x09 + the storage value
///
/// It is a checksum of VALUES and not of buffers, because a buffer is where the two
/// implementations are allowed to differ: an Arrow view's buffer index and offset, a validity
/// bitmap that is absent here and all-ones there, the bytes under a null. None of that is data.
///
/// FNV-1a, because the mixing does not have to be good -- it has to be identical.
///
/// # Safety
/// `path` must be a valid NUL-terminated C string for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vxbench_scan_checksum(path: *const c_char) -> i64 {
    run(path, |session, path| {
        block_on(|handle| {
            let session = session.with_handle(handle);
            async move {
                let mut ctx = session.create_execution_ctx();
                let file = session.open_options().open_path(&path).await?;
                let array = file.scan()?.into_array_stream()?.read_all().await?;
                let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
                let dtype = array.dtype().clone();
                for row in 0..array.len() {
                    let scalar: Scalar = array.execute_scalar(row, &mut ctx)?;
                    hash_value(&dtype, scalar.value(), &mut hash);
                }

                Ok(hash as i64)
            }
        })
    })
}

/// Folds one byte in.
fn hash_byte(byte: u8, hash: &mut u64) {
    *hash = (*hash ^ byte as u64).wrapping_mul(0x100_0000_01b3);
}

/// Folds a run of bytes in.
fn hash_bytes(bytes: &[u8], hash: &mut u64) {
    for byte in bytes {
        hash_byte(*byte, hash);
    }
}

/// Folds a u32 count in, little-endian, as the .NET side writes it.
fn hash_count(count: usize, hash: &mut u64) {
    hash_bytes(&(count as u32).to_le_bytes(), hash);
}

/// Folds one scalar in, by the encoding above.
///
/// THE DTYPE IS CARRIED because a `Tuple` is a struct, a list and a fixed-size list on this side,
/// and the .NET side tags a struct 0x06 and a list 0x07 -- the value alone cannot say which. It is
/// the same reason `sidecar.rs` walks a dtype beside its value.
fn hash_value(dtype: &DType, value: Option<&ScalarValue>, hash: &mut u64) {
    let Some(value) = value else {
        hash_byte(0x00, hash);
        return;
    };

    match (dtype, value) {
        (DType::Null, _) => hash_byte(0x00, hash),
        (DType::Bool(_), ScalarValue::Bool(b)) => {
            hash_byte(0x01, hash);
            hash_byte(u8::from(*b), hash);
        }
        (DType::Primitive(_, _), ScalarValue::Primitive(p)) => hash_primitive(p, hash),
        (DType::Utf8(_), ScalarValue::Utf8(s)) => {
            hash_byte(0x05, hash);
            hash_count(s.as_str().len(), hash);
            hash_bytes(s.as_str().as_bytes(), hash);
        }
        (DType::Binary(_), ScalarValue::Binary(b)) => {
            hash_byte(0x05, hash);
            hash_count(b.as_slice().len(), hash);
            hash_bytes(b.as_slice(), hash);
        }
        (DType::Decimal(_, _), ScalarValue::Decimal(d)) => hash_decimal(d, hash),
        (DType::Struct(fields, _), ScalarValue::Tuple(children)) => {
            hash_byte(0x06, hash);
            hash_count(children.len(), hash);
            for (index, child) in children.iter().enumerate() {
                match fields.field_by_index(index) {
                    Some(child_dtype) => hash_value(&child_dtype, child.as_ref(), hash),
                    None => hash_byte(0xFE, hash),
                }
            }
        }
        (DType::List(element, _), ScalarValue::Tuple(children))
        | (DType::FixedSizeList(element, _, _), ScalarValue::Tuple(children)) => {
            hash_byte(0x07, hash);
            hash_count(children.len(), hash);
            for child in children.iter() {
                hash_value(element, child.as_ref(), hash);
            }
        }
        (DType::Extension(ext), v) => {
            hash_byte(0x09, hash);
            hash_value(ext.storage_dtype(), Some(v), hash);
        }
        (dtype, value) => {
            // LOUD RATHER THAN SILENT. A shape neither side has an encoding for would otherwise
            // fold into something plausible and the check would pass for no reason at all.
            hash_byte(0xFF, hash);
            hash_bytes(format!("{dtype} {value:?}").as_bytes(), hash);
        }
    }
}

/// Folds a primitive in: tag, width, then the value's own little-endian bytes.
fn hash_primitive(value: &PValue, hash: &mut u64) {
    let (tag, bytes): (u8, Vec<u8>) = match value {
        PValue::U8(v) => (0x03, v.to_le_bytes().to_vec()),
        PValue::U16(v) => (0x03, v.to_le_bytes().to_vec()),
        PValue::U32(v) => (0x03, v.to_le_bytes().to_vec()),
        PValue::U64(v) => (0x03, v.to_le_bytes().to_vec()),
        PValue::I8(v) => (0x02, v.to_le_bytes().to_vec()),
        PValue::I16(v) => (0x02, v.to_le_bytes().to_vec()),
        PValue::I32(v) => (0x02, v.to_le_bytes().to_vec()),
        PValue::I64(v) => (0x02, v.to_le_bytes().to_vec()),
        PValue::F16(v) => (0x04, v.to_bits().to_le_bytes().to_vec()),
        PValue::F32(v) => (0x04, v.to_bits().to_le_bytes().to_vec()),
        PValue::F64(v) => (0x04, v.to_bits().to_le_bytes().to_vec()),
    };
    hash_byte(tag, hash);
    hash_byte(bytes.len() as u8, hash);
    hash_bytes(&bytes, hash);
}

/// Folds a decimal in: its storage width, then its bits.
fn hash_decimal(value: &DecimalValue, hash: &mut u64) {
    let bytes: Vec<u8> = match value {
        DecimalValue::I8(v) => v.to_le_bytes().to_vec(),
        DecimalValue::I16(v) => v.to_le_bytes().to_vec(),
        DecimalValue::I32(v) => v.to_le_bytes().to_vec(),
        DecimalValue::I64(v) => v.to_le_bytes().to_vec(),
        DecimalValue::I128(v) => v.to_le_bytes().to_vec(),
        DecimalValue::I256(v) => v.to_le_bytes().to_vec(),
    };
    hash_byte(0x08, hash);
    hash_byte(bytes.len() as u8, hash);
    hash_bytes(&bytes, hash);
}

/// Opens `path` and reads its row count from the footer, touching no data segment.
///
/// # Safety
/// `path` must be a valid NUL-terminated C string for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vxbench_open_only(path: *const c_char) -> i64 {
    run(path, |session, path| {
        block_on(|handle| {
            let session = session.with_handle(handle);
            async move {
                let file = session.open_options().open_path(path).await?;
                Ok(file.row_count() as i64)
            }
        })
    })
}

/// The session, built ONCE.
///
/// This is a fairness fix, not an optimization, and it moved a number: `VortexSession::default()`
/// registers every edition and initializes the arrow and parquet-variant integrations, which the
/// first version of this shim paid on every call. Nothing on the .NET side does that per open -
/// `EncodingRegistry` is static and initialized once - so charging Rust for it measured a harness
/// decision rather than the reader. Rebuilding it per call cost ~100 us, which is more than the
/// entire open-latency axis.
///
/// The FILE is still opened from scratch on every call, which is what the .NET side does and what
/// keeps the segment cache cold on both sides.
static SESSION: OnceLock<VortexSession> = OnceLock::new();

/// Opens `path` and returns how many BATCHES a full scan produces.
///
/// Not a timing axis. It exists because "time to first batch" only compares like with like if the
/// two implementations agree on what a batch is, and they do not have to: the split strategy is a
/// writer/reader choice, not a format rule.
///
/// # Safety
/// `path` must be a valid NUL-terminated C string for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vxbench_batch_count(path: *const c_char) -> i64 {
    run(path, |session, path| {
        block_on(|handle| {
            let session = session.with_handle(handle);
            async move {
                let file = session.open_options().open_path(path).await?;
                let stream = file.scan()?.into_array_stream()?;
                pin_mut!(stream);
                let mut batches: i64 = 0;
                while let Some(array) = stream.next().await {
                    array?;
                    batches += 1;
                }

                Ok(batches)
            }
        })
    })
}

/// Decodes the C string, runs the body on the shared session, and turns every failure mode -
/// including a panic, which must never unwind across the ABI - into a negative return.
///
/// THE ERROR IS PRINTED BEFORE IT IS FLATTENED, and BENCH-AUDIT.md B14 is why. An i64 can carry
/// "it failed" across the ABI and nothing more, so discarding the `VortexError` left the .NET side
/// with a message it had invented -- "the Rust reader returned an error" -- and no way to learn
/// which call, which encoding, or what the reference actually refused. One `eprintln!` is the
/// difference between a diagnosable reference and an opaque one; a panic's payload is printed by
/// the default hook already.
fn run<F>(path: *const c_char, body: F) -> i64
where
    F: FnOnce(VortexSession, String) -> VortexResult<i64>,
{
    let Some(path) = (unsafe { text(path) }) else {
        return ERR_BAD_PATH;
    };

    let session = SESSION.get_or_init(VortexSession::default).clone();
    let reported = path.clone();
    match catch_unwind(AssertUnwindSafe(move || body(session, path))) {
        Ok(Ok(rows)) => rows,
        Ok(Err(error)) => {
            eprintln!("vxbench: {reported}: {error:?}");
            ERR_FAILED
        }
        Err(_) => ERR_PANIC,
    }
}

unsafe fn text(pointer: *const c_char) -> Option<String> {
    if pointer.is_null() {
        return None;
    }

    unsafe { CStr::from_ptr(pointer) }.to_str().ok().map(str::to_owned)
}
