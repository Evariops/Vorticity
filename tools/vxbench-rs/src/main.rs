use std::env;
use std::ffi::CString;
use std::process::ExitCode;

/// One scenario per run, in its own process, so the report can measure wall time, peak resident
/// memory and processor time of a whole run rather than of a loop inside one. With `--repeat` the
/// scenario runs that many times in the process, a line per round: the first round is the cold one,
/// the rounds after it what a process that stays up pays.
///
/// Every scenario calls the same function the in-process harness calls, so the two ways of
/// measuring Rust cannot drift apart.
fn main() -> ExitCode {
    let mut args: Vec<String> = env::args().skip(1).collect();
    if args.is_empty() || args[0] == "--help" {
        eprintln!("usage: vxbench <scenario> <file.vortex> [argument]... [--threads <n>|all] [--repeat <n>]");
        eprintln!("  scan <file>                  read every column of every row");
        eprintln!("  project <file> <field>       read one column");
        eprintln!("  filter <file> <field> <lo> <width>   read rows where lo <= field < lo+width");
        eprintln!("  take <file> <count> <stride> read count rows, one every stride");
        eprintln!("  write <file>                 read it and encode it back out");
        eprintln!("  open <file>                  open it and read nothing");
        eprintln!("  rewrite <file> <out>         write it to <out> with the reference writer");
        eprintln!("--threads: a multi-threaded runtime of <n> workers, or of one per processor; without it,");
        eprintln!("the single-threaded runtime, all work on the calling thread.");
        eprintln!("--repeat: run the scenario <n> times and print `round=<i> rows=<n> work_us=<time>` for each.");
        eprintln!("prints `rows=<n> work_us=<action time inside the process> threads=<workers>` and exits 0,");
        eprintln!("or a reason and exits 1.");
        return ExitCode::from(2);
    }

    let threads = match take_threads(&mut args) {
        Ok(threads) => threads,
        Err(code) => return code,
    };

    let repeat = match take_repeat(&mut args) {
        Ok(repeat) => repeat,
        Err(code) => return code,
    };

    if vxbench::vxbench_set_threads(threads) != 0 {
        eprintln!("--threads: {threads} is not a thread count");
        return ExitCode::FAILURE;
    }

    let Some(scenario) = args.first().map(String::as_str) else {
        eprintln!("a scenario is required");
        return ExitCode::from(2);
    };

    let Some(path) = args.get(1) else {
        eprintln!("{scenario}: a file is required");
        return ExitCode::FAILURE;
    };

    let Ok(path) = CString::new(path.as_str()) else {
        eprintln!("{scenario}: the path holds a NUL byte");
        return ExitCode::FAILURE;
    };

    // Parsed before the clock starts, so a round times the call and nothing of its arguments.
    let action = match Action::parse(scenario, &args) {
        Ok(action) => action,
        Err(code) => return code,
    };

    // The action is timed from inside the process, once it is up: what the report compares is the
    // work, and the process start is a property of the binary that the parent times apart. The
    // rounds are printed once they are all done.
    let mut rounds: Vec<(i64, u128)> = Vec::with_capacity(repeat);
    for _ in 0..repeat {
        let started = std::time::Instant::now();
        let rows = action.run(&path);
        let work_us = started.elapsed().as_micros();
        if rows < 0 {
            eprintln!("{scenario}: the reader returned {rows}");
            return ExitCode::FAILURE;
        }

        rounds.push((rows, work_us));
    }

    if repeat > 1 {
        for (round, (rows, work_us)) in rounds.iter().enumerate() {
            println!("round={round} rows={rows} work_us={work_us}");
        }
    }

    let (rows, work_us) = rounds[rounds.len() - 1];
    let (cpu_ms, rss_bytes) = cost();
    println!("rows={rows} work_us={work_us} cpu_ms={cpu_ms} rss_bytes={rss_bytes} threads={threads}");
    ExitCode::SUCCESS
}

/// A scenario and its arguments, ready to call.
enum Action {
    Scan,
    Open,
    Write,
    Rewrite(CString),
    Project(CString),
    Filter(CString, i64, i64),
    Take(i64, i64),
}

impl Action {
    fn parse(scenario: &str, args: &[String]) -> Result<Action, ExitCode> {
        match scenario {
            "scan" => Ok(Action::Scan),
            "open" => Ok(Action::Open),
            "write" => Ok(Action::Write),
            "rewrite" => field(args, 2).map(Action::Rewrite),
            "project" => field(args, 2).map(Action::Project),
            "filter" => {
                let (Ok(field), Some(lo), Some(width)) = (field(args, 2), number(args, 3), number(args, 4))
                else {
                    eprintln!("filter: expected <field> <lo> <width>");
                    return Err(ExitCode::FAILURE);
                };

                Ok(Action::Filter(field, lo, width))
            }
            "take" => {
                let (Some(count), Some(stride)) = (number(args, 2), number(args, 3)) else {
                    eprintln!("take: expected <count> <stride>");
                    return Err(ExitCode::FAILURE);
                };

                Ok(Action::Take(count, stride))
            }
            other => {
                eprintln!("no scenario named '{other}'");
                Err(ExitCode::from(2))
            }
        }
    }

    fn run(&self, path: &CString) -> i64 {
        unsafe {
            match self {
                // The canonicalizing scans, not the counting ones: our reader materializes every
                // column it delivers, so a scan that counts rows off metadata is not the same work.
                Action::Scan => vxbench::vxbench_scan_canonical(path.as_ptr()),
                Action::Open => vxbench::vxbench_open_only(path.as_ptr()),
                Action::Write => vxbench::vxbench_write(path.as_ptr()),
                Action::Rewrite(destination) => vxbench::vxbench_rewrite(path.as_ptr(), destination.as_ptr()),
                Action::Project(field) => vxbench::vxbench_scan_projected_canonical(path.as_ptr(), field.as_ptr()),
                Action::Filter(field, lo, width) => {
                    vxbench::vxbench_scan_filtered(path.as_ptr(), field.as_ptr(), *lo, *width)
                }
                Action::Take(count, stride) => vxbench::vxbench_take(path.as_ptr(), *count, *stride),
            }
        }
    }
}

/// Takes `--threads <n>` or `--threads all` out of the arguments: the workers of the multi-threaded
/// runtime, one per processor for `all`, or 0 for the single-threaded runtime when it is absent.
fn take_threads(args: &mut Vec<String>) -> Result<i64, ExitCode> {
    let Some(at) = args.iter().position(|arg| arg == "--threads") else {
        return Ok(0);
    };

    let Some(value) = args.get(at + 1).cloned() else {
        eprintln!("--threads: expected a count or `all`");
        return Err(ExitCode::FAILURE);
    };

    args.drain(at..at + 2);
    if value == "all" {
        return Ok(std::thread::available_parallelism().map_or(1, |n| n.get()) as i64);
    }

    match value.parse::<i64>() {
        Ok(threads) if threads > 0 => Ok(threads),
        _ => {
            eprintln!("--threads: '{value}' is not a positive count");
            Err(ExitCode::FAILURE)
        }
    }
}

/// Takes `--repeat <n>` out of the arguments: how many times the scenario runs, 1 when it is absent.
fn take_repeat(args: &mut Vec<String>) -> Result<usize, ExitCode> {
    let Some(at) = args.iter().position(|arg| arg == "--repeat") else {
        return Ok(1);
    };

    let Some(value) = args.get(at + 1).cloned() else {
        eprintln!("--repeat: expected a count");
        return Err(ExitCode::FAILURE);
    };

    args.drain(at..at + 2);
    match value.parse::<usize>() {
        Ok(repeat) if repeat > 0 => Ok(repeat),
        _ => {
            eprintln!("--repeat: '{value}' is not a positive count");
            Err(ExitCode::FAILURE)
        }
    }
}

/// The processor time and the peak resident set of this process, the two figures the report pairs
/// with the wall clock its parent keeps. `ru_maxrss` is bytes on macOS and kilobytes elsewhere.
fn cost() -> (i64, i64) {
    let mut usage = std::mem::MaybeUninit::<libc::rusage>::zeroed();
    if unsafe { libc::getrusage(libc::RUSAGE_SELF, usage.as_mut_ptr()) } != 0 {
        return (-1, -1);
    }

    let usage = unsafe { usage.assume_init() };
    let micros = |t: libc::timeval| t.tv_sec as i64 * 1_000_000 + t.tv_usec as i64;
    let cpu_ms = (micros(usage.ru_utime) + micros(usage.ru_stime)) / 1_000;
    let rss_bytes = if cfg!(target_os = "macos") {
        usage.ru_maxrss as i64
    } else {
        usage.ru_maxrss as i64 * 1024
    };

    (cpu_ms, rss_bytes)
}

fn field(args: &[String], at: usize) -> Result<CString, ExitCode> {
    match args.get(at) {
        Some(name) => CString::new(name.as_str()).map_err(|_| ExitCode::FAILURE),
        None => Err(ExitCode::FAILURE),
    }
}

fn number(args: &[String], at: usize) -> Option<i64> {
    args.get(at).and_then(|value| value.parse().ok())
}
