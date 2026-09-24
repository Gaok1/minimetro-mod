// No Linux a biblioteca precisa de SONAME: o mod carrega pelo caminho completo
// (dlopen) e depois o DllImport("mmopt") do Mono pede "libmmopt.so" pelo nome;
// o glibc so reaproveita a ja carregada se o SONAME bater.
fn main() {
    let os = std::env::var("CARGO_CFG_TARGET_OS").unwrap_or_default();
    if os == "linux" {
        println!("cargo:rustc-cdylib-link-arg=-Wl,-soname,libmmopt.so");
    }
}
