#!/usr/bin/env bash
# Compila o MiniMetroGA e instala no jogo (Linux). Equivalente do build.ps1.
#
#     ./build.sh                 compila e instala
#     ./build.sh --run           compila, instala e abre o jogo
#     ./build.sh --run --tail    idem, e segue o log
#     ./build.sh --setup         so baixa/instala Doorstop + lib/ (sem compilar)
#     ./build.sh --windows-dll   so compila o nucleo nativo para o Windows (x86)
#
# Opcoes:
#     --game-dir <dir>           pasta do jogo (default: Steam em ~/.local/share)
#     -c <Release|Debug>         configuracao (default: Release)
#     --no-native                nao compila o nucleo Rust (sem ele o otimizador fica desligado)
#
# Requisitos: .NET SDK (8+) no PATH; cargo/rustc para o nucleo nativo (opcional).
# No NixOS, o que faltar vem de `nix shell nixpkgs#dotnet-sdk_8 nixpkgs#cargo ...`.

set -euo pipefail

REPO="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")" && pwd -P)"
GAME_DIR="${MINIMETRO_DIR:-$HOME/.local/share/Steam/steamapps/common/MiniMetro}"
CONFIG=Release
RUN=0
TAIL=0
SETUP_ONLY=0
NATIVE=1
WINDOWS_DLL=0
ORIG_ARGS=("$@")

while [ $# -gt 0 ]; do
    case "$1" in
        --run) RUN=1 ;;
        --tail) TAIL=1 ;;
        --setup) SETUP_ONLY=1 ;;
        --no-native) NATIVE=0 ;;
        --windows-dll) WINDOWS_DLL=1 ;;
        --game-dir) GAME_DIR="$2"; shift ;;
        -c|--configuration) CONFIG="$2"; shift ;;
        -h|--help) sed -n '2,18p' "$0"; exit 0 ;;
        *) echo "opcao desconhecida: $1" 1>&2; exit 1 ;;
    esac
    shift
done

say()  { printf '\033[36m%s\033[0m\n' "$*"; }
ok()   { printf '\033[32m%s\033[0m\n' "$*"; }
warn() { printf '\033[33m%s\033[0m\n' "$*"; }
die()  { printf '\033[31m%s\033[0m\n' "$*" 1>&2; exit 1; }

# Nucleo nativo para o jogo de Windows (x86, 32 bits), compilado daqui com
# mingw: i686-pc-windows-gnu. Sai em native/mmopt/target/i686-pc-windows-gnu/
# release/mmopt.dll, que o csproj instala no Windows se nao houver o build MSVC.
# No NixOS o gcc do mingw usa o modelo de threads mcf: o libgcc_eh pede a
# mcfgthread e o Rust pede -lpthread, entao as duas entram estaticas (a DLL so
# depende de DLLs do sistema).
build_windows_dll() {
    local dir="$REPO/native/mmopt" target=i686-pc-windows-gnu
    command -v rustup >/dev/null 2>&1 && rustup target add "$target" >/dev/null
    if [ -z "${MINIMETROGA_IN_MINGW:-}" ] && ! command -v i686-w64-mingw32-gcc >/dev/null 2>&1; then
        command -v nix >/dev/null 2>&1 || die "i686-w64-mingw32-gcc nao encontrado (instale o mingw-w64)."
        say "entrando no mingw32 do nixpkgs..."
        local pth mcf
        pth="$(nix build --no-link --print-out-paths nixpkgs#pkgsCross.mingw32.windows.pthreads)"
        mcf="$(nix build --no-link --print-out-paths nixpkgs#pkgsCross.mingw32.windows.mcfgthreads)"
        MINIMETROGA_IN_MINGW=1 \
        CARGO_TARGET_I686_PC_WINDOWS_GNU_RUSTFLAGS="-L native=$pth/lib -C link-arg=$mcf/lib/libmcfgthread.a -C link-arg=-lntdll -C link-arg=-lkernel32" \
            exec nix shell nixpkgs#pkgsCross.mingw32.buildPackages.gcc -c "$0" --windows-dll
    fi
    say "compilando o nucleo nativo para Windows x86 ($target)..."
    (cd "$dir" && CARGO_TARGET_I686_PC_WINDOWS_GNU_LINKER=i686-w64-mingw32-gcc \
        cargo build --release --lib --target "$target") || die "falha ao compilar a DLL do Windows."
    ok "nucleo nativo (Windows x86): $dir/target/$target/release/mmopt.dll"
}

if [ $WINDOWS_DLL -eq 1 ]; then
    command -v cargo >/dev/null 2>&1 || die "cargo nao encontrado."
    build_windows_dll
    exit 0
fi

GAME_DIR="$(cd "$GAME_DIR" && pwd -P)"
MOD_DIR="$GAME_DIR/MiniMetroGA"
LOG_FILE="$MOD_DIR/MiniMetroGA.log"

# Artefatos de terceiros, fixados por versao e sha256. Mesmas origens do setup
# de Windows (ver vault/Carregamento - Doorstop sem BepInEx.md).
DOORSTOP_URL="https://github.com/NeighTools/UnityDoorstop/releases/download/v4.5.0/doorstop_linux_release_4.5.0.zip"
DOORSTOP_SHA="07ec6ee28c7d200c000ba9db6dfecec466a6ed449194a37decb719bf5321aee0"   # x64/libdoorstop.so
UNITYLIBS_URL="https://unity.bepinex.dev/libraries/2022.3.62.zip"
IMGUI_SHA="881b8feb0c5cf9906a38b17dfa1aeccfd7ad99bc31d82cddf0286a0faed3b8cb"
CORLIBS_URL="https://unity.bepinex.dev/corlibs/2022.3.62.zip"
NETSTD_SHA="bf0b7eac9010b75413e4ec1a07a3453bc6a68eeb8e916fbed7c1f2777841f7c7"

[ -x "$GAME_DIR/Mini Metro" ] || [ -f "$GAME_DIR/MiniMetro.exe" ] \
    || die "Mini Metro nao encontrado em $GAME_DIR (use --game-dir)."

echo "Jogo : $GAME_DIR"
echo "Mod  : $MOD_DIR"

# Extrai um membro de um zip e confere o sha256. Usa python3 porque nem toda
# distro (NixOS, por exemplo) tem unzip.
fetch_member() {
    local url="$1" member="$2" dest="$3" sha="$4"
    if [ -f "$dest" ] && echo "$sha  $dest" | sha256sum -c --status; then
        return 0
    fi
    local tmp; tmp="$(mktemp -d)"
    say "baixando $(basename "$dest") de $url"
    curl -fsSL -o "$tmp/a.zip" "$url"
    python3 - "$tmp/a.zip" "$member" "$dest" <<'EOF'
import sys, zipfile
z = zipfile.ZipFile(sys.argv[1])
open(sys.argv[3], "wb").write(z.read(sys.argv[2]))
EOF
    rm -rf "$tmp"
    echo "$sha  $dest" | sha256sum -c --status \
        || die "sha256 de $dest nao confere; o arquivo remoto mudou."
}

setup() {
    mkdir -p "$MOD_DIR/lib" "$MOD_DIR/doorstop" "$MOD_DIR/bin"
    fetch_member "$DOORSTOP_URL"  "x64/libdoorstop.so"          "$MOD_DIR/doorstop/libdoorstop.so"     "$DOORSTOP_SHA"
    fetch_member "$UNITYLIBS_URL" "UnityEngine.IMGUIModule.dll" "$MOD_DIR/lib/UnityEngine.IMGUIModule.dll" "$IMGUI_SHA"
    fetch_member "$CORLIBS_URL"   "netstandard.dll"             "$MOD_DIR/lib/netstandard.dll"         "$NETSTD_SHA"
    install -m 755 "$REPO/linux/run.sh" "$MOD_DIR/run.sh"
    ok "Doorstop + lib/ + run.sh instalados em $MOD_DIR"
}

ensure_tools() {
    local missing=()
    command -v dotnet >/dev/null 2>&1 || missing+=(nixpkgs#dotnet-sdk_8)
    if [ $NATIVE -eq 1 ] && ! command -v cargo >/dev/null 2>&1; then
        missing+=(nixpkgs#cargo nixpkgs#rustc nixpkgs#gcc)
    fi
    [ ${#missing[@]} -eq 0 ] && return 0
    if command -v nix >/dev/null 2>&1 && [ -z "${MINIMETROGA_IN_NIX:-}" ]; then
        warn "faltando no PATH (${missing[*]}); reexecutando dentro de nix shell"
        export MINIMETROGA_IN_NIX=1
        exec nix shell "${missing[@]}" -c "$0" "${ORIG_ARGS[@]}"
    fi
    command -v dotnet >/dev/null 2>&1 || die "dotnet nao encontrado. Instale o .NET SDK 8+."
    if [ $NATIVE -eq 1 ] && ! command -v cargo >/dev/null 2>&1; then
        warn "cargo nao encontrado: sem nucleo nativo o otimizador fica desligado."
        NATIVE=0
    fi
}

# Nucleo nativo (native/mmopt). O csproj copia a libmmopt.so para o bin do mod.
build_native() {
    [ $NATIVE -eq 1 ] || return 0
    say "compilando o nucleo nativo (Rust)..."
    (cd "$REPO/native/mmopt" && cargo build --release) || die "falha ao compilar o nucleo nativo (use --no-native para pular)."
    ok "nucleo nativo: $REPO/native/mmopt/target/release/libmmopt.so"
}

game_running() { pgrep -f "$GAME_DIR/Mini Metro" >/dev/null 2>&1; }

setup
[ $SETUP_ONLY -eq 1 ] && exit 0

ensure_tools
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# No Linux o jogo nao trava o arquivo como no Windows, mas o Mono ja carregou a
# DLL antiga: a nova so entra reabrindo o jogo.
if game_running; then
    if [ $RUN -eq 1 ]; then
        warn "Mini Metro esta aberto; fechando..."
        pkill -f "$GAME_DIR/Mini Metro" || true
        sleep 1
    else
        warn "Mini Metro esta aberto: a DLL nova so vale depois de reabrir o jogo."
    fi
fi

build_native

dotnet build "$REPO/src/MiniMetroGA/MiniMetroGA.csproj" -c "$CONFIG" -v m -nologo \
    -p:GameDir="$GAME_DIR"
ok "OK."

if [ $RUN -eq 1 ]; then
    rm -f "$LOG_FILE"
    say "Abrindo o jogo..."
    if command -v steam >/dev/null 2>&1 && [ -z "${MINIMETROGA_NO_STEAM:-}" ]; then
        # Pela Steam, para respeitar a Steam Linux Runtime. Exige a opcao de
        # inicializacao configurada (ver README).
        steam -applaunch 287980 >/dev/null 2>&1 &
    else
        nohup "$MOD_DIR/run.sh" >/dev/null 2>&1 &
    fi

    if [ $TAIL -eq 1 ]; then
        say "Seguindo $LOG_FILE (Ctrl+C para sair)..."
        while [ ! -f "$LOG_FILE" ]; do sleep 0.3; done
        tail -f "$LOG_FILE"
    fi
fi
