use std::env;
use std::ffi::CString;
use std::process::ExitCode;

/// One scenario per run, in its own process, so the report can measure wall time, peak resident
/// memory and processor time of a whole run rather than of a loop inside one.
///
/// Every scenario calls the same function the in-process harness calls, so the two ways of
/// measuring Rust cannot drift apart.
fn main() -> ExitCode {
    let args: Vec<String> = env::args().skip(1).collect();
    if args.is_empty() || args[0] == "--help" {
        eprintln!("usage: vxbench <scenario> <file.vortex> [argument]...");
        eprintln!("  scan <file>                  read every column of every row");
        eprintln!("  project <file> <field>       read one column");
        eprintln!("  filter <file> <field> <lo> <width>   read rows where lo <= field < lo+width");
        eprintln!("  take <file> <count> <stride> read count rows, one every stride");
        eprintln!("  write <file>                 read it and encode it back out");
        eprintln!("  open <file>                  open it and read nothing");
        eprintln!("prints `rows=<n>` and exits 0, or a reason and exits 1.");
        return ExitCode::from(2);
    }

    let scenario = args[0].as_str();
    let Some(path) = args.get(1) else {
        eprintln!("{scenario}: a file is required");
        return ExitCode::FAILURE;
    };

    let Ok(path) = CString::new(path.as_str()) else {
        eprintln!("{scenario}: the path holds a NUL byte");
        return ExitCode::FAILURE;
    };

    let rows = match scenario {
        "scan" => unsafe { vxbench::vxbench_scan_all(path.as_ptr()) },
        "open" => unsafe { vxbench::vxbench_open_only(path.as_ptr()) },
        "write" => unsafe { vxbench::vxbench_write(path.as_ptr()) },
        "project" => match field(&args, 2) {
            Ok(field) => unsafe { vxbench::vxbench_scan_projected(path.as_ptr(), field.as_ptr()) },
            Err(code) => return code,
        },
        "filter" => {
            let (Ok(field), Some(lo), Some(width)) = (field(&args, 2), number(&args, 3), number(&args, 4))
            else {
                eprintln!("filter: expected <field> <lo> <width>");
                return ExitCode::FAILURE;
            };

            unsafe { vxbench::vxbench_scan_filtered(path.as_ptr(), field.as_ptr(), lo, width) }
        }
        "take" => {
            let (Some(count), Some(stride)) = (number(&args, 2), number(&args, 3)) else {
                eprintln!("take: expected <count> <stride>");
                return ExitCode::FAILURE;
            };

            unsafe { vxbench::vxbench_take(path.as_ptr(), count, stride) }
        }
        other => {
            eprintln!("no scenario named '{other}'");
            return ExitCode::from(2);
        }
    };

    if rows < 0 {
        eprintln!("{scenario}: the reader returned {rows}");
        return ExitCode::FAILURE;
    }

    println!("rows={rows}");
    ExitCode::SUCCESS
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
