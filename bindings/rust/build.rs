use std::path::PathBuf;

fn main() {
    let crate_dir = PathBuf::from(std::env::var_os("CARGO_MANIFEST_DIR").unwrap());
    let core_dir = crate_dir.join("../../native/canto-core");
    let sherpa_dir = crate_dir.join("../../third_party/sherpa-onnx");
    let runtime_dir = sherpa_dir.join("runtime/windows-x64");
    let runtime_definition = format!("\"{}\"", runtime_dir.display());

    let mut build = cc::Build::new();
    build
        .cpp(true)
        .file(core_dir.join("src/canto_core.cpp"))
        .include(core_dir.join("include"))
        .include(sherpa_dir.join("include"))
        .define("CANTO_CORE_BUILD", None)
        .define("CANTO_ENABLE_TEST_BACKEND", Some("1"))
        .define("CANTO_ENABLE_SHERPA_ONNX", Some("1"))
        .define("CANTO_SHERPA_RUNTIME_DIR", Some(runtime_definition.as_str()));

    if build.get_compiler().is_like_msvc() {
        build.flag_if_supported("/std:c++20").flag_if_supported("/EHsc");
    } else {
        build.flag_if_supported("-std=c++20");
    }
    build.compile("canto_core_static");

    println!("cargo:rerun-if-changed={}", core_dir.join("src/canto_core.cpp").display());
    println!("cargo:rerun-if-changed={}", core_dir.join("include/canto_core.h").display());
    println!("cargo:rerun-if-changed={}", sherpa_dir.join("include/sherpa-onnx/c-api/c-api.h").display());
    println!("cargo:rerun-if-changed={}", runtime_dir.display());
}
