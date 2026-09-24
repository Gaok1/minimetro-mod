#!/usr/bin/env bash
# Abre o Mini Metro (build nativo de Linux) com o MiniMetroGA carregado.
#
# Equivalente Linux do winhttp.dll + doorstop_config.ini do Windows: no Linux o
# Unity Doorstop entra por LD_PRELOAD e le a configuracao de variaveis de
# ambiente, entao este script faz o papel dos dois.
#
# Uso:
#   1. Direto:            "<jogo>/MiniMetroGA/run.sh" [args do jogo]
#   2. Pela Steam:        Propriedades -> Opcoes de inicializacao:
#                         "<jogo>/MiniMetroGA/run.sh" %command%
#
# Para abrir o jogo sem o mod, sem mexer na Steam: MINIMETROGA_DISABLE=1.

set -e

MOD_DIR="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")" && pwd -P)"
GAME_DIR="$(dirname "$MOD_DIR")"

# Chamado pela Steam com %command%: a linha de comando e algo como
#   reaper SteamLaunch AppId=287980 -- [steam-runtime ...] <jogo>/Mini Metro
# O LD_PRELOAD nao pode valer para o reaper nem para o container da Steam
# Linux Runtime (UnityDoorstop#88), so para o jogo. Entao reinserimos este
# script logo antes do executavel do jogo e deixamos a Steam seguir.
if [ "${MINIMETROGA_STAGE:-}" != "game" ]; then
    for a in "$@"; do
        if [ "$a" = "SteamLaunch" ]; then
            args=()
            injected=0
            for b in "$@"; do
                if [ $injected -eq 0 ] && [ "${b#"$GAME_DIR"/}" != "$b" ]; then
                    args+=("$MOD_DIR/run.sh" "--minimetroga-game")
                    injected=1
                fi
                args+=("$b")
            done
            if [ $injected -eq 0 ]; then
                echo "MiniMetroGA: executavel do jogo nao encontrado na linha da Steam:" 1>&2
                echo "  $*" 1>&2
                exit 1
            fi
            exec "${args[@]}"
        fi
    done
fi

if [ "${1:-}" = "--minimetroga-game" ]; then
    shift
fi

# Sem argumentos: abre o executavel nativo direto.
if [ $# -eq 0 ]; then
    set -- "$GAME_DIR/Mini Metro"
fi

if [ "${MINIMETROGA_DISABLE:-0}" = "1" ]; then
    exec "$@"
fi

DOORSTOP_LIB="$MOD_DIR/doorstop/libdoorstop.so"
TARGET="$MOD_DIR/bin/MiniMetroGA.dll"

for f in "$DOORSTOP_LIB" "$TARGET"; do
    if [ ! -f "$f" ]; then
        echo "MiniMetroGA: faltando $f (rode ./build.sh no repositorio)." 1>&2
        echo "MiniMetroGA: abrindo o jogo sem o mod." 1>&2
        exec "$@"
    fi
done

export MINIMETROGA_STAGE=game

# Mesmas chaves do doorstop_config.ini do Windows
export DOORSTOP_ENABLED=1
export DOORSTOP_TARGET_ASSEMBLY="$TARGET"
export DOORSTOP_MONO_DLL_SEARCH_PATH_OVERRIDE="$MOD_DIR/lib"
export DOORSTOP_IGNORE_DISABLED_ENV=0
export DOORSTOP_MONO_DEBUG_ENABLED="${DOORSTOP_MONO_DEBUG_ENABLED:-0}"
export DOORSTOP_MONO_DEBUG_ADDRESS="${DOORSTOP_MONO_DEBUG_ADDRESS:-127.0.0.1:10000}"
export DOORSTOP_MONO_DEBUG_SUSPEND="${DOORSTOP_MONO_DEBUG_SUSPEND:-0}"

# O launchscript original do jogo faz isto; repetimos para o caso de a Steam
# chamar o binario direto.
# bin/ tem a libmmopt.so (nucleo nativo); o mod tambem a carrega pelo caminho
# completo, isto e so a rede de seguranca para o DllImport do Mono.
export LD_LIBRARY_PATH="$GAME_DIR/Mini Metro_Data/Plugins/:$MOD_DIR/doorstop:$MOD_DIR/bin:${LD_LIBRARY_PATH:-}"
export LD_PRELOAD="$DOORSTOP_LIB${LD_PRELOAD:+:$LD_PRELOAD}"

cd "$GAME_DIR"
exec "$@"
