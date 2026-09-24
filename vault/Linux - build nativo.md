# Linux — build nativo

O mod roda no **build nativo de Linux** do Mini Metro (não Proton). O código C# é
o mesmo do Windows; o que muda é só o carregador e os caminhos. Confirmado em
2026-09-24: carrega, a IMGUI não-stripada entra e o `Player.log` fica com zero
exceções.

## O que é diferente do Windows

| | Windows | Linux |
|---|---|---|
| Executável | `MiniMetro.exe` | `Mini Metro` (com espaço) |
| Pasta de dados | `MiniMetro_Data` | `Mini Metro_Data` (com espaço) |
| Arquitetura | x86 (32-bit) | **x64** |
| Doorstop entra por | `winhttp.dll` (proxy DLL) | `LD_PRELOAD=libdoorstop.so` |
| Configuração | `doorstop_config.ini` | variáveis `DOORSTOP_*` (em `run.sh`) |
| Log do Unity | `<jogo>\output_log.txt` | `~/.config/unity3d/Dinosaur Polo Club/Mini Metro/Player.log` |
| Build | `build.ps1` | `build.sh` |

O stripping é **idêntico**. O `StripAudit` da IMGUI não-stripada contra o
`Mini Metro_Data/Managed` do Linux devolve exatamente as mesmas
[[APIs ausentes - lista viva|cinco minas]], e o mod compila contra as DLLs do
Linux com zero avisos. Então `Widgets`/`Theme` valem igual nas duas plataformas.

O mod em si é IL AnyCPU, então 32 × 64 bits não importa para ele — só para o
Doorstop, que tem que ser o `x64/libdoorstop.so`.

## Layout instalado

```
<jogo>/
  Mini Metro
  Mini Metro_Data/            <- intocado
  MiniMetroGA/
    run.sh                    <- lançador (faz o papel de winhttp.dll + ini)
    doorstop/libdoorstop.so   <- Unity Doorstop 4.5.0, x64
    lib/                      <- DOORSTOP_MONO_DLL_SEARCH_PATH_OVERRIDE
      UnityEngine.IMGUIModule.dll
      netstandard.dll
    bin/MiniMetroGA.dll
    MiniMetroGA.log
```

Diferente do Windows, o repositório **não precisa** morar dentro da pasta do
jogo: o `build.sh` instala tudo em `<jogo>/MiniMetroGA` a partir de onde o repo
estiver.

## Instalar

```bash
./build.sh                 # baixa Doorstop + lib/, compila, instala
```

Os três arquivos de terceiros são baixados das mesmas origens do setup de
Windows e conferidos por **sha256** (fixados no topo do `build.sh`):

| Arquivo | Origem |
|---|---|
| `libdoorstop.so` | `NeighTools/UnityDoorstop` v4.5.0, `doorstop_linux_release` |
| `UnityEngine.IMGUIModule.dll` | `unity.bepinex.dev/libraries/2022.3.62.zip` |
| `netstandard.dll` | `unity.bepinex.dev/corlibs/2022.3.62.zip` |

Depois, na Steam: **Mini Metro → Propriedades → Opções de inicialização**:

```
"/home/<você>/.local/share/Steam/steamapps/common/MiniMetro/MiniMetroGA/run.sh" %command%
```

## Por que o `run.sh` se reinsere na linha da Steam

Com `%command%`, a Steam chama algo como
`reaper SteamLaunch AppId=287980 -- [steam-runtime…] <jogo>/Mini Metro`.
Se o `LD_PRELOAD` já valer ali, ele contamina o `reaper` e o container da Steam
Linux Runtime ([UnityDoorstop#88](https://github.com/NeighTools/UnityDoorstop/issues/88)).
O `run.sh` então não exporta nada nesse primeiro estágio: ele reescreve a linha
colocando a si mesmo logo antes do executável do jogo e dá `exec`. Só no segundo
estágio, já dentro do container e imediatamente antes do jogo, ele exporta
`LD_PRELOAD` e `DOORSTOP_*`.

É a mesma ideia do `run.sh` oficial do Doorstop, reescrita sem depender do
comando `file` (que o NixOS não tem no host).

## `Assembly.Location` mente com o override

Com `DOORSTOP_MONO_DLL_SEARCH_PATH_OVERRIDE`, o Mono lê a `IMGUIModule` de `lib/`
mas `typeof(GUILayout).Assembly.Location` continua dizendo `Managed/`. Não dá para
confirmar o override pelo caminho. O `ModBootstrap.CheckImgui()` confere pelo
**conteúdo**: se `GUILayout.Label` existe, é a não-stripada (a do jogo só tem
`Width` e `Height`). O log mostra `IMGUIModule nao-stripada OK.`

## NixOS

- `dotnet` não precisa estar instalado: o `build.sh` se reexecuta em
  `nix shell nixpkgs#dotnet-sdk_8`.
- Para rodar fora da Steam: `steam-run "<jogo>/MiniMetroGA/run.sh"`.
- Sem `unzip` no sistema, o `build.sh` extrai com `python3`.

## Desligar / desinstalar

- Temporário: `MINIMETROGA_DISABLE=1` no ambiente, ou tirar a opção de
  inicialização na Steam.
- Desinstalar: apagar `<jogo>/MiniMetroGA/` e a opção de inicialização.
  Nada fora dessa pasta é tocado.

Ver também: [[Carregamento - Doorstop sem BepInEx]], [[Build e desenvolvimento]],
[[Troubleshooting]]
