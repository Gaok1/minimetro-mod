# Carregamento — Doorstop sem BepInEx

## O que ficou instalado no jogo

```
MiniMetro\
  winhttp.dll              <- Unity Doorstop 4 (veio do zip do BepInEx)
  .doorstop_version
  doorstop_config.ini
  MiniMetroGA\
    bin\MiniMetroGA.dll    <- o mod (target_assembly do Doorstop)
    lib\                   <- dll_search_path_override
      UnityEngine.IMGUIModule.dll   (não-stripada)
      netstandard.dll               (facade que a IMGUI acima exige)
    src\                   <- código
    vault\                 <- esta documentação
    build.ps1
    MiniMetroGA.log        <- log do mod
```

`doorstop_config.ini`, linhas que importam:

```ini
target_assembly = MiniMetroGA\bin\MiniMetroGA.dll
dll_search_path_override = MiniMetroGA\lib
redirect_output_log = true
```

> [!note] Linux
> No build nativo de Linux não há `winhttp.dll` nem `doorstop_config.ini`: o
> Doorstop entra por `LD_PRELOAD` e lê as mesmas opções de variáveis
> `DOORSTOP_*`, exportadas pelo `MiniMetroGA/run.sh`. O resto desta nota vale
> igual. Ver [[Linux - build nativo]].

## Por que não BepInEx

`BepInEx.Preloader` chama `System.Reflection.Module.GetPEKind` na primeira linha.
Esse método foi stripado. O preloader estoura e escreve um `preloader_*.log` na
raiz do jogo — e nada mais carrega.

Tentativa de contornar com a corlib não-stripada de `unity.bepinex.dev/corlibs`:
**piorou**. Aquela `mscorlib` é de outro flavor e faz P/Invoke em `System.Native`,
que não existe neste runtime. Resultado: todo o IO do jogo quebra
(`TypeInitializationException` em `Interop.Sys`, `System.Console`, `File`).

## Por que não Harmony

O cctor de `HarmonyLib.AccessTools` chama `AmbiguousMatchException(string, Exception)`.
Também stripado. `Harmony.PatchAll` estoura antes de patchar qualquer coisa.

Harmony também arrasta MonoMod + Mono.Cecil, que muito provavelmente esbarrariam
nos mesmos buracos. Não vale a pena insistir.

## O que fazemos no lugar

Três passos, zero patching:

1. **`Doorstop.Entrypoint.Start()`** — roda em `mono_jit_init`, cedo demais para
   tocar na API do Unity. Só registra um handler de `AppDomain.AssemblyLoad`.
2. Quando **`Assembly-CSharp`** aparece no domínio, a engine já está de pé:
   assinamos `SceneManager.sceneLoaded`.
3. No primeiro **`sceneLoaded`** (main thread, Unity completo) criamos um
   `GameObject` com o [[Arquitetura do mod|ModBehaviour]], que dá `Update` e `OnGUI`.

A instância de `Game` sai por **reflexão pura**, um `GetValue` por frame — ver
[[Engenharia reversa - API do Mini Metro]].

> [!note] `AssemblyLoadEventArgs.LoadedAssembly` perdeu o getter no stripping.
> Por isso o handler varre `AppDomain.CurrentDomain.GetAssemblies()` em vez de ler
> o argumento do evento.

## Se um dia precisar de patching de verdade

Opções, em ordem de dor crescente:

1. Reescrever a assembly do jogo offline com Mono.Cecil no build (fora do processo).
2. Adicionar a entrada no `MiniMetro_Data\RuntimeInitializeOnLoads.json`
   (hoje é `{"root":[]}`) e colocar a DLL em `Managed\`.
3. Portar só as partes do Harmony que interessam.

Até agora nada disso foi necessário: reflexão resolveu.
