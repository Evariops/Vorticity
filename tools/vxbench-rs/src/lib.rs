//! Vortex's Rust reader and writer behind a C ABI, for the benchmarks to measure against: the
//! cdylib the in-process ratios load, and the binary the published report runs, from the same
//! functions.
//!
//! Every entry point takes a path and returns a count, or a negative error: no out-parameter, no
//! allocation handed across the boundary, no object lifetime to manage, so nothing in a
//! measurement is FFI bookkeeping.
//!
//! Built as upstream's benchmarks are, and run either on the single-threaded runtime, all the work
//! on the calling thread, or on a multi-threaded Tokio runtime of as many workers as our reader has
//! lanes: a ratio between a reader held to one core and one free to use them all measures a
//! threading model, not a decoder.
use std::collections::HashMap;
use std::ffi::CStr;
use std::future::Future;
use std::os::raw::c_char;
use std::panic::AssertUnwindSafe;
use std::panic::catch_unwind;
use std::sync::Arc;
use std::sync::Mutex;
use std::sync::OnceLock;
use std::sync::atomic::AtomicUsize;
use std::sync::atomic::Ordering;

use futures::Stream;
use futures::StreamExt;
use futures::pin_mut;
use vortex::VortexSessionDefault;
use vortex::array::ArrayRef;
use vortex::array::IntoArray;
use vortex::array::RecursiveCanonical;
use vortex::array::VortexSessionExecute;
use vortex::buffer::Buffer;
use vortex::scan::strict_sorted_buffer::StrictSortedBuffer;
use vortex::error::VortexResult;
use vortex::expr::Expression;
use vortex::expr::and;
use vortex::expr::eq;
use vortex::expr::get_item;
use vortex::expr::like;
use vortex::expr::lit;
use vortex::expr::lt;
use vortex::expr::gt_eq;
use vortex::expr::root;
use vortex::expr::select;
use vortex::file::OpenOptionsSessionExt;
use vortex::file::WriteOptionsSessionExt;
use vortex::io::runtime::single::block_on;
use vortex::io::session::RuntimeSessionExt;
use vortex::layout::scan::scan_builder::ScanBuilder;
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

/// The allocator upstream's benchmarks run with.
#[global_allocator]
static GLOBAL: mimalloc::MiMalloc = mimalloc::MiMalloc;

/// Workers of the multi-threaded runtime every call runs on, or 0 for the single-threaded one.
static THREADS: AtomicUsize = AtomicUsize::new(0);

/// The multi-threaded runtimes, one per worker count asked for, each built once: a runtime built per
/// call would charge every call for starting its threads.
static RUNTIMES: OnceLock<Mutex<HashMap<usize, Arc<tokio::runtime::Runtime>>>> = OnceLock::new();

/// Sets the runtime every later call runs on: 0 for the single-threaded one, the calling thread
/// doing all the work, or `threads` workers of a multi-threaded Tokio runtime.
///
/// # Safety
/// None; takes a count and returns 0, or a negative error for a negative count.
#[unsafe(no_mangle)]
pub extern "C" fn vxbench_set_threads(threads: i64) -> i64 {
    if threads < 0 {
        return ERR_BAD_PATH;
    }

    THREADS.store(threads as usize, Ordering::Relaxed);
    0
}

/// The multi-threaded runtime of `workers` workers.
fn runtime(workers: usize) -> VortexResult<Arc<tokio::runtime::Runtime>> {
    let mut runtimes = RUNTIMES
        .get_or_init(|| Mutex::new(HashMap::new()))
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    if let Some(runtime) = runtimes.get(&workers) {
        return Ok(Arc::clone(runtime));
    }

    let runtime = Arc::new(
        tokio::runtime::Builder::new_multi_thread()
            .worker_threads(workers)
            .enable_all()
            .build()?,
    );
    runtimes.insert(workers, Arc::clone(&runtime));
    Ok(runtime)
}

/// Runs `body` on the runtime the thread count names: the single-threaded one, or the Tokio runtime
/// of that many workers, with the session bound to it.
fn drive<F, Fut>(session: VortexSession, body: F) -> VortexResult<i64>
where
    F: FnOnce(VortexSession) -> Fut,
    Fut: Future<Output = VortexResult<i64>>,
{
    match THREADS.load(Ordering::Relaxed) {
        0 => block_on(|handle| body(session.with_handle(handle))),
        workers => runtime(workers)?.block_on(body(session.with_tokio())),
    }
}

/// The scan's splits, each decoded to its canonical form on its own task, as row counts.
///
/// Decoding on the split's task rather than in the loop that drains the stream is what lets a
/// multi-threaded runtime decode on every core; on the single-threaded one it is the same work on
/// the one thread. A context per split, as upstream's Arrow conversion makes one per chunk.
fn decoded(
    scan: ScanBuilder<ArrayRef>,
    session: &VortexSession,
) -> VortexResult<impl Stream<Item = VortexResult<i64>> + Send + 'static> {
    let session = session.clone();
    scan.map(move |array: ArrayRef| {
        let mut ctx = session.create_execution_ctx();
        let rows = array.len() as i64;
        let _canonical: RecursiveCanonical = array.execute(&mut ctx)?;
        Ok(rows)
    })
    .into_stream()
}

/// The scan's splits, each decoded to its canonical form on its own task, as arrays: what a writer
/// is given to encode, the rows as our reader delivers them to ours.
fn canonical(scan: ScanBuilder<ArrayRef>, session: &VortexSession) -> ScanBuilder<ArrayRef> {
    let session = session.clone();
    scan.map(move |array: ArrayRef| {
        let mut ctx = session.create_execution_ctx();
        let canonical: RecursiveCanonical = array.execute(&mut ctx)?;
        Ok(canonical.0.into_array())
    })
}

/// The rows a stream of decoded splits counted.
async fn total(stream: impl Stream<Item = VortexResult<i64>>) -> VortexResult<i64> {
    pin_mut!(stream);
    let mut rows: i64 = 0;
    while let Some(split) = stream.next().await {
        rows += split?;
    }

    Ok(rows)
}

/// The empty call: the FFI floor, so it can be subtracted when it matters.
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
        drive(session, |session| async move {
            let file = session.open_options().open_path(path).await?;
            let stream = file.scan()?.into_array_stream()?;
            pin_mut!(stream);
            let mut rows: i64 = 0;
            while let Some(array) = stream.next().await {
                rows += array?.len() as i64;
            }

            Ok(rows)
        })
    })
}

/// Opens `path`, scans every batch and decodes it to its canonical form, returning the row count.
///
/// The like-for-like full scan: our reader has no representation but the canonical one, so it
/// decodes every column it delivers, where `vxbench_scan_all` hands back arrays in their stored
/// encodings and counts rows off their metadata.
///
/// `RecursiveCanonical` and not `Canonical`, which stops as soon as the root is canonical: a struct
/// is, so on a table it would decode none of the columns under it.
///
/// On the way, a constant array is expanded, as our reader expands it; were ours to keep constants
/// as they are, this call would charge the reference for work ours no longer does.
///
/// The decoded splits are dropped as they are counted: holding a million rows of them would
/// measure the allocator.
///
/// # Safety
/// `path` must be a valid NUL-terminated C string for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vxbench_scan_canonical(path: *const c_char) -> i64 {
    run(path, |session, path| {
        drive(session, |session| async move {
            let file = session.open_options().open_path(&path).await?;
            total(decoded(file.scan()?, &session)?).await
        })
    })
}

/// Scans `path` canonically on a multi-threaded Tokio runtime of exactly `threads` workers,
/// whatever `vxbench_set_threads` said.
///
/// The lane axis: our reader's `WithDegreeOfParallelism` against the reference at the same number
/// of threads, pinned on both sides, because a ratio between an `n`-lane reader and a reference
/// free to use every core measures a threading model, not a decoder.
///
/// `threads = 1` is not the same measurement as `vxbench_scan_canonical` on the single-threaded
/// runtime: this one still hands the work to a worker and pays for the hand-off, where the
/// single-threaded runtime drives it on the calling thread.
///
/// # Safety
/// `path` must be a valid NUL-terminated C string for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vxbench_scan_canonical_threads(path: *const c_char, threads: i64) -> i64 {
    if threads <= 0 {
        return ERR_BAD_PATH;
    }

    run(path, move |session, path| {
        runtime(threads as usize)?.block_on(async move {
            let session = session.with_tokio();
            let file = session.open_options().open_path(&path).await?;
            total(decoded(file.scan()?, &session)?).await
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
        drive(session, |session| async move {
            let file = session.open_options().open_path(path).await?;
            // Bound against the file's own dtype, the way the reference's own tests do it: an
            // unbound expression is refused by the scan builder's signature.
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
        })
    })
}

/// Opens `path`, scans one field of every batch and canonicalizes it, returning the row count.
///
/// The like-for-like projected scan, for the same reason `vxbench_scan_canonical` is the
/// like-for-like full scan: `vxbench_scan_projected` counts rows off metadata and decodes nothing,
/// while our reader materializes every column it delivers.
///
/// # Safety
/// `path` and `field` must be valid NUL-terminated C strings for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vxbench_scan_projected_canonical(
    path: *const c_char,
    field: *const c_char,
) -> i64 {
    let Some(field) = (unsafe { text(field) }) else {
        return ERR_BAD_PATH;
    };

    run(path, move |session, path| {
        drive(session, |session| async move {
            let file = session.open_options().open_path(path).await?;
            let projection = select([field.as_str()], root())
                .optimize_recursive(file.dtype())
                .and_then(|expr| expr.bind(file.dtype()))?;
            total(decoded(file.scan()?.with_projection(projection), &session)?).await
        })
    })
}

/// Opens `path` and reads one batch, returning its row count: the time-to-first-batch axis.
///
/// # Safety
/// `path` must be a valid NUL-terminated C string for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vxbench_open_first_batch(path: *const c_char) -> i64 {
    run(path, |session, path| {
        drive(session, |session| async move {
            let file = session.open_options().open_path(path).await?;
            let stream = file.scan()?.into_array_stream()?;
            pin_mut!(stream);
            match stream.next().await {
                Some(array) => Ok(array?.len() as i64),
                None => Ok(0),
            }
        })
    })
}

/// Reads `path` and writes it back out with the default strategy, returning the rows written.
///
/// The read is inside the measurement on both sides, the same file through the same reader, and
/// `vxbench_scan_canonical` gives the figure to subtract when the read is a large share.
///
/// The output goes to an in-memory sink rather than to disk, as the .NET side's `DiscardSink` does:
/// a write benchmark that measures the filesystem measures the filesystem.
///
/// The writer is given each split decoded to its canonical form, which is what our reader hands
/// ours: re-encoding from the file's own encodings is not the work our side does. Given them as
/// stored, the reference's writer also refuses a numeric column stored as zstd, which it cannot
/// append to a builder.
///
/// # Safety
/// `path` must be a valid NUL-terminated C string for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vxbench_write(path: *const c_char) -> i64 {
    run(path, |session, path| {
        drive(session, |session| async move {
            let file = session.open_options().open_path(&path).await?;
            let rows = file.row_count() as i64;
            let stream = canonical(file.scan()?, &session).into_array_stream()?;
            session
                .write_options()
                .write(Vec::<u8>::new(), stream)
                .await?;
            Ok(rows)
        })
    })
}

/// Reads `path` and writes it to `destination` with the default strategy, returning the rows
/// written: the same rows in the reference writer's own bytes, so that the read scenarios also run
/// on a file whose encodings the reference chose.
///
/// The writer is given the rows decoded, as `vxbench_write` gives them, which is also how upstream
/// writes its benchmark files, from Arrow. Not a timing axis; the bytes land in memory and go to
/// disk once the write is done.
///
/// # Safety
/// `path` and `destination` must be valid NUL-terminated C strings for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vxbench_rewrite(path: *const c_char, destination: *const c_char) -> i64 {
    let Some(destination) = (unsafe { text(destination) }) else {
        return ERR_BAD_PATH;
    };

    run(path, move |session, path| {
        drive(session, |session| async move {
            let file = session.open_options().open_path(&path).await?;
            let rows = file.row_count() as i64;
            let stream = canonical(file.scan()?, &session).into_array_stream()?;
            let mut bytes = Vec::<u8>::new();
            session.write_options().write(&mut bytes, stream).await?;
            std::fs::write(&destination, &bytes)?;
            Ok(rows)
        })
    })
}

/// Takes `count` rows of `path`, one every `stride`, canonicalizing, and counts them.
///
/// The indices are a stride rather than a list, so that one call describes a scattered take of any
/// density without marshalling an array across the ABI.
///
/// # Safety
/// `path` must be a valid NUL-terminated C string for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vxbench_take(path: *const c_char, count: i64, stride: i64) -> i64 {
    if count < 0 || stride <= 0 {
        return ERR_BAD_PATH;
    }

    run(path, move |session, path| {
        drive(session, |session| async move {
            let file = session.open_options().open_path(&path).await?;
            let rows_in_file = file.row_count();
            let indices: Buffer<u64> = (0..count as u64)
                .map(|i| (i * stride as u64) + (stride as u64 / 2))
                .filter(|&row| row < rows_in_file)
                .collect();
            let selection = StrictSortedBuffer::try_new(indices)?;
            total(decoded(file.scan()?.with_row_indices(selection), &session)?).await
        })
    })
}

/// Scans `path` under `field >= lo AND field < lo + width`, canonicalizing, and counts the rows.
///
/// The predicate is a band rather than a single comparison, because that is what the .NET side's
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
        drive(session, |session| async move {
            let file = session.open_options().open_path(&path).await?;
            let predicate = and(
                gt_eq(get_item(field.as_str(), root()), lit(lo)),
                lt(get_item(field.as_str(), root()), lit(lo + width)),
            );
            let filter = predicate
                .optimize_recursive(file.dtype())
                .and_then(|expr| expr.bind(file.dtype()))?;
            total(decoded(file.scan()?.with_filter(filter), &session)?).await
        })
    })
}

/// Scans `path` keeping the rows whose `field` equals `value`, and returns how many survived.
///
/// `eq` rather than a band, because equality is the predicate a text encoding can answer without
/// decompressing: on a dictionary by comparing its values, on FSST by compressing the needle with
/// the column's own table. A band would measure ordering, which text encodings do not accelerate.
///
/// # Safety
/// `path`, `field` and `value` must be valid NUL-terminated C strings for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vxbench_scan_filtered_eq_utf8(
    path: *const c_char,
    field: *const c_char,
    value: *const c_char,
) -> i64 {
    let (Some(field), Some(value)) = (unsafe { text(field) }, unsafe { text(value) }) else {
        return ERR_BAD_PATH;
    };

    filtered_utf8(path, field, move |column| eq(column, lit(value.as_str())))
}

/// Scans `path` keeping the rows whose `field` starts with `prefix`, and returns how many survived.
///
/// SQL LIKE with a trailing `%` is how this side expresses a prefix; the .NET side has a dedicated
/// `StartsWith`. Each uses its own natural form on purpose -- the axis compares what a caller
/// would actually write, not a shape imposed on one side to resemble the other -- and the harness
/// holds them to the same surviving row count before it times anything.
///
/// `prefix` must not contain `%` or `_`. Both are LIKE wildcards here and neither is a wildcard to
/// `StartsWith`, so a prefix carrying one would quietly make the two sides ask different questions.
/// The caller picks the prefixes; escaping is not added for a needle nobody needs.
///
/// # Safety
/// `path`, `field` and `prefix` must be valid NUL-terminated C strings for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vxbench_scan_filtered_prefix_utf8(
    path: *const c_char,
    field: *const c_char,
    prefix: *const c_char,
) -> i64 {
    let (Some(field), Some(prefix)) = (unsafe { text(field) }, unsafe { text(prefix) }) else {
        return ERR_BAD_PATH;
    };

    if prefix.contains('%') || prefix.contains('_') {
        return ERR_BAD_PATH;
    }

    let pattern = format!("{prefix}%");
    filtered_utf8(path, field, move |column| like(column, lit(pattern.as_str())))
}

/// Opens `path`, filters it by the predicate `build` puts on `field`, and counts the rows kept.
///
/// The body the two string axes share: everything but the predicate is the band filter's, down to
/// the recursive canonicalization that makes the count evidence of a decode rather than of a
/// metadata read.
///
/// An empty `field` addresses the root array instead of a column of it. The single-encoding files
/// have a bare array at their root, so without this there is no way to put a predicate on one at
/// all -- and those are the only files that carry a given encoding with nothing else mixed in.
fn filtered_utf8<F>(path: *const c_char, field: String, build: F) -> i64
where
    F: Fn(Expression) -> Expression + Send + 'static,
{
    run(path, move |session, path| {
        drive(session, |session| async move {
            let file = session.open_options().open_path(&path).await?;
            let column = if field.is_empty() {
                root()
            } else {
                get_item(field.as_str(), root())
            };
            let predicate = build(column);
            let filter = predicate
                .optimize_recursive(file.dtype())
                .and_then(|expr| expr.bind(file.dtype()))?;
            total(decoded(file.scan()?.with_filter(filter), &session)?).await
        })
    })
}

/// Scans `path` and folds every decoded value into one 64-bit checksum: the same rows, in the same
/// order, with the same values, where a row count says nothing of whether anything was decoded.
///
/// It does not prove that `vxbench_scan_canonical` decodes: `execute_scalar` reaches every value
/// whatever a scan decoded, so the two are checked apart.
///
/// The encoding is a contract with `bench/Vorticity.Benchmarks/Checksum.cs`, byte for byte:
///
///   null 0x00 · bool 0x01 + byte · signed 0x02 + width + LE bytes · unsigned 0x03 + width + LE
///   bytes · float 0x04 + width + LE bits · utf8/binary 0x05 + u32 LE length + bytes · struct 0x06
///   + u32 field count + fields in order · list 0x07 + u32 count + elements · decimal 0x08 + width
///   + LE bits · extension 0x09 + the storage value
///
/// A checksum of values and not of buffers, because buffers are where two implementations may
/// differ: an Arrow view's buffer index and offset, a validity bitmap absent here and all-ones
/// there, the bytes under a null.
///
/// FNV-1a: the mixing has to be identical on both sides, not good.
///
/// # Safety
/// `path` must be a valid NUL-terminated C string for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vxbench_scan_checksum(path: *const c_char) -> i64 {
    run(path, |session, path| {
        drive(session, |session| async move {
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
/// The dtype is carried because a `Tuple` is a struct, a list and a fixed-size list on this side,
/// and the .NET side tags a struct 0x06 and a list 0x07: the value alone cannot say which.
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
            // A shape neither side has an encoding for is folded in whole, rather than into
            // something plausible that would let the check pass for no reason.
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
        drive(session, |session| async move {
            let file = session.open_options().open_path(path).await?;
            Ok(file.row_count() as i64)
        })
    })
}

/// The session, built once: `VortexSession::default()` registers every edition and initializes the
/// arrow and parquet-variant integrations, which the .NET side also does once per process, its
/// `EncodingRegistry` being static. Built per call, it would charge the reference for a harness
/// decision.
///
/// The file is still opened from scratch on every call, as the .NET side opens it, which keeps the
/// segment cache cold on both sides.
static SESSION: OnceLock<VortexSession> = OnceLock::new();

/// Opens `path` and returns how many batches a full scan produces.
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
        drive(session, |session| async move {
            let file = session.open_options().open_path(path).await?;
            let stream = file.scan()?.into_array_stream()?;
            pin_mut!(stream);
            let mut batches: i64 = 0;
            while let Some(array) = stream.next().await {
                array?;
                batches += 1;
            }

            Ok(batches)
        })
    })
}

/// Decodes the C string, runs the body on the shared session, and turns every failure mode -
/// including a panic, which must never unwind across the ABI - into a negative return.
///
/// The error is printed before it is flattened: an i64 carries that the call failed and nothing
/// more, and the message is what says which call, which encoding and what the reference refused. A
/// panic's payload is printed by the default hook.
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
