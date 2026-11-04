//! Hands the library the version of the `vortex` crate it is built against, read from
//! `Cargo.lock`, so that `vxbench_vortex_version` states the reference the bench's pages name with
//! no copy of the pin to keep in step.

use std::fs;

fn main() {
    println!("cargo:rerun-if-changed=Cargo.lock");
    let lock = fs::read_to_string("Cargo.lock").expect("Cargo.lock beside Cargo.toml");
    let version = lock
        .split("[[package]]")
        .find_map(|package| {
            let mut fields = package.lines().map(str::trim);
            if fields.clone().find_map(|line| line.strip_prefix("name = "))? != "\"vortex\"" {
                return None;
            }
            fields
                .find_map(|line| line.strip_prefix("version = "))
                .map(|version| version.trim_matches('"').to_owned())
        })
        .expect("a vortex package in Cargo.lock");
    println!("cargo:rustc-env=VORTEX_CRATE_VERSION={version}");
}
