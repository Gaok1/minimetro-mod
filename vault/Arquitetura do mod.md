# Arquitetura do mod

## Fluxo geral

```
Doorstop (winhttp.dll)
   └─ Doorstop.Entrypoint.Start()          Entrypoint.cs
        └─ AssemblyLoad → sceneLoaded → ModBootstrap.EnsureHost()
             └─ GameObject "MiniMetroGA" + ModBehaviour
                  ├─ Update()  → GameHook.Refresh() → GaWindow.Tick()
                  │              → Applier.Tick() (frota pendente)
                  │              → UpgradeAdvisor.Tick() (segunda-feira)
                  └─ OnGUI()   → GaWindow.Draw()

GaWindow (main thread)
   ├─ [Otimizar] → Snapshot.Capture(game)  ──► dados puros
   │                  └─ EngineHost → NativeEngine → ProblemExport (blob)
   │                         └─ mm_start ──► núcleo Rust, threads próprias
   │                                          (AG + avaliador, native/mmopt)
   └─ [Aplicar]  → Applier.Apply(game, snapshot, melhorGenoma)
                        └─ mexe só no que mudou, pelo LineBuilder e ApplyAsset
```

## Arquivos

| Arquivo | Papel |
|---|---|
| `Entrypoint.cs` | entrada do Doorstop, bootstrap, `Log`, `ModInfo` |
| `ModBehaviour.cs` | o único `MonoBehaviour`; `Update` e `OnGUI` |
| `Core/GameHook.cs` | acha `Game`, `City.stations`, linhas vivas — por reflexão |
| `Core/Snapshot.cs` | congela o estado do jogo em arrays puros |
| `Core/Applier.cs` | genoma → rede real, mexendo só no que mudou (ver [[Aplicação da solução no jogo]]) |
| `Core/ProblemExport.cs` | snapshot → blob do núcleo nativo (espelho de `problem.rs`) |
| `Core/UpgradeAdvisor.cs` | upgrade da semana: recomenda, escolhe, põe o interchange |
| `Ga/GaConfig.cs` | todos os parâmetros (é o que a aba Parâmetros edita) |
| `Ga/Genome.cs` | encoding, `SameNetwork`, decomposição do custo |
| `Ga/NativeEngine.cs`, `Ga/EngineHost.cs` | ponte P/Invoke para o núcleo Rust ([[Núcleo nativo (Rust)]]) |
| `SelfTest.cs`, `Validation.cs` | jogo sozinho e validação do modelo |
| `UI/GaWindow.cs` | a janela IMGUI |
| `UI/Plot.cs` | gráfico de convergência e preview da rede, pixel a pixel |

## A regra de ouro das threads

O AG roda nas threads do núcleo nativo. **A API do Unity e do jogo não é
thread-safe.** Por isso o núcleo só recebe o blob montado do `Snapshot` (dados
puros). As referências vivas (`Station`, `Line`, `City`, `Game`) existem no
snapshot mas são de uso exclusivo do [[Aplicação da solução no jogo|Applier]] e
do `UpgradeAdvisor`, que rodam na main thread.

Progresso e melhor indivíduo saem do núcleo por `mm_status`/`mm_best`, que a UI
consulta a cada frame.

## Entrada do mouse

Enquanto o cursor está sobre o painel, o mod liga `game.IsLocked = true`.
Sem isso, clicar num botão da UI vira um traço de linha no mapa —
`Game.HandleSmartTouchBegan` retorna cedo quando `IsLocked`.

Ver também: [[Carregamento - Doorstop sem BepInEx]], [[Algoritmo Genético - design]]
