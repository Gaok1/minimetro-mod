# MiniMetroGA — vault do projeto

Mod do Mini Metro que otimiza a rede de metrô com um **algoritmo genético**, com UI
configurável, pausa do jogo e aplicação da solução encontrada.

> Este vault é a memória do projeto. O código conta *o quê*; aqui está o *porquê* —
> principalmente as armadilhas que custaram tempo.

## Comece por aqui

- [[Arquitetura do mod]] — como as peças se encaixam
- [[Build e desenvolvimento]] — compilar, rodar, depurar
- [[Linux - build nativo]] — rodar no build nativo de Linux (Steam, NixOS)
- [[UI e controles]] — atalhos e o que cada painel faz

## O problema central deste jogo

- [[Managed stripping - o problema central]] ← **leia antes de mexer em qualquer coisa**
- [[Carregamento - Doorstop sem BepInEx]] — por que não usamos BepInEx nem Harmony
- [[APIs ausentes - lista viva]] — o que já mordeu

## Domínio

- [[Dossiê - o problema de otimização]] ← **tudo que o jogo faz e que entra no problema**
- [[Núcleo nativo (Rust)]] — o AG e o avaliador em Rust, carregados por P/Invoke
- [[Validação do modelo]] — o fitness confere com o jogo? previsto × medido na partida
- [[Engenharia reversa - API do Mini Metro]] — classes e campos que o mod usa
- [[Algoritmo Genético - design]] — encoding, operadores, parâmetros
- [[Função de fitness]] — o simulador surrogate
- [[Modelo de demanda]] — a tabela de spawn do jogo, e o que mais foi extraído da base
- [[Aplicação da solução no jogo]] — como o genoma vira linhas de verdade

## Operação

- [[Troubleshooting]]
- [[Diário de decisões]]
- [[Roadmap]]

## Fatos rápidos

| | |
|---|---|
| Jogo | Mini Metro (Dinosaur Polo Club) |
| Engine | Unity **2022.3.62f2**, Mono — Windows **x86 (32-bit)**, Linux **x64** |
| Managed stripping | **sim, agressivo** |
| Carregador | Unity Doorstop 4 (`winhttp.dll` no Windows, `LD_PRELOAD` no Linux) |
| Patching | **nenhum** — reflexão pura |
| Pasta do mod | `<jogo>\MiniMetroGA` |
| Log | `<jogo>\MiniMetroGA\MiniMetroGA.log` (idem no Linux) |
