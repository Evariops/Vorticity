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
use vortex::array::Canonical;
use vortex::array::VortexSessionExecute;
use vortex::error::VortexResult;
use vortex::expr::root;
use vortex::expr::select;
use vortex::file::OpenOptionsSessionExt;
use vortex::io::runtime::single::block_on;
use vortex::io::session::RuntimeSessionExt;
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
/// `execute::<Canonical>` is upstream's own canonicalization, the same call its arrow conversion
/// and its own `to_canonical` make. The result is dropped rather than accumulated: the decode has
/// already happened by then, and holding a million rows of it would measure the allocator.
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
                    let _canonical: Canonical = array.execute(&mut ctx)?;
                }

                Ok(rows)
            }
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
fn run<F>(path: *const c_char, body: F) -> i64
where
    F: FnOnce(VortexSession, String) -> VortexResult<i64>,
{
    let Some(path) = (unsafe { text(path) }) else {
        return ERR_BAD_PATH;
    };

    let session = SESSION.get_or_init(VortexSession::default).clone();
    match catch_unwind(AssertUnwindSafe(move || body(session, path))) {
        Ok(Ok(rows)) => rows,
        Ok(Err(_)) => ERR_FAILED,
        Err(_) => ERR_PANIC,
    }
}

unsafe fn text(pointer: *const c_char) -> Option<String> {
    if pointer.is_null() {
        return None;
    }

    unsafe { CStr::from_ptr(pointer) }.to_str().ok().map(str::to_owned)
}
