# Roadmap

## Validação pendente

- [x] A UI desenha. Confirmado por screenshot em 2026-08-20, com
      `output_log.txt` em zero exceções. Ver [[Diário de decisões]].
- [x] Aplicar constrói linhas de verdade (o log registrou várias aplicações
      bem-sucedidas numa partida do jogador).
- [ ] Rodar o AG numa partida real e conferir que `Snapshot.Capture` lê estações,
      linhas e orçamento corretamente (a aba **Rede** mostra tudo).
- [ ] Conferir se a rede aplicada bate com o preview da aba **Rede**.
- [ ] Testar vagões numa cidade que já ofereceu vagão (`Vagões > 0` no cartão de
      inventário). Até agora só houve teste com `0 / 0`.
- [ ] Aplicar uma solução e confirmar que as linhas saem como no preview.
- [x] Calibrar o modelo contra o jogo. Feito em [[Validação do modelo]]: demanda,
      vazão, comprimento de trilho, ciclo e lotação batem com o medido na partida.
- [ ] Calibrar o **risco de estação estourar** em regime: nas rodadas feitas quase
      não houve estouro para comparar. Precisa de janelas mais longas no fim de
      jogo (e, de preferência, apply incremental, para não medir o transiente).
- [ ] Testar em cidade com rio (Londres) para validar a detecção de travessia.

## Melhorias no algoritmo

- [x] **Modo incremental:** o `Applier` mexe só no que mudou (linha igual
      fica, estação nova entra por gesto com os trens rodando, linha que muda é
      apagada com `Mothball`, trem sobrando é movido) e o fitness cobra o custo
      de cada mexida (`change.rs`). Ver [[Aplicação da solução no jogo]].
      Conferido em jogo (2026-09-24): 9 de 9 edições por gesto (ponta e meio do
      trecho) sem refazer linha, 0 passageiro sumido, travessias do jogo = do
      modelo. A primeira rodada lia a rota na ordem da lista de links e achava
      que a inserção no meio tinha falhado; o `ExtractRoute` agora segue as
      conexões.
- [ ] Conferir em jogo o **movimento de trem/vagão entre linhas** e o
      **interchange posto sozinho** (código pronto, ainda sem rodada que passasse
      por eles).
- [ ] **Chegar a 1000 passageiros em Londres** no self-test (o recorde com a v3
      e a aplicação antiga foi 805, semana 7). Rodar
      `MINIMETROGA_SELFTEST_SECONDS=1800` com a aplicação incremental e o
      upgrade recomendado; se não chegar, olhar o que matou (log `SELFTEST`).
- [ ] Otimização multiobjetivo (NSGA-II): expor a fronteira de Pareto entre tempo
      de viagem e lotação em vez de uma soma ponderada.
- [x] Busca local (memético) no melhor indivíduo a cada N gerações. Feito no
      [[Núcleo nativo (Rust)]].
- [x] Levar em conta as estações futuras: elas já estão em `City.stations` desde
      a largada e o núcleo nativo as encaixa virtualmente.
- [x] **Recomendar o upgrade da semana** (`upgrade.rs` + `UpgradeAdvisor`): a
      rede otimizada com cada oferta, mesmo orçamento e semente, partindo da
      melhor rede sem upgrade. No bench (Londres, semana 6), descontado o
      controle: vagão −12%, interchange −9%, travessia ~0.
- [x] **Túnel/ponte acima do estoque = rede inviável** (reparo + custo 10⁶), e o
      `Applier` confere o que o jogo gastou contra o modelo.
- [x] Remover o AG em C# (o nativo é o único otimizador).
- [ ] Microssimulação discreta (as regras do [[Dossiê - o problema de otimização]]
      passo a passo) para validar o modelo analítico e reordenar os melhores.
- [ ] Build nativo para Windows (`i686-pc-windows-msvc`); sem ele o Windows fica
      sem otimizador (o AG em C# foi removido).
- [x] Modelar interchanges: capacidade da cidade (`InterchangeCapacity`) e
      embarque num pulso; o recomendador escolhe a estação.
- [ ] Ferries/`LinkType.Nautical` — hoje ignorados.

## Melhorias de UI

- [x] Aba de log dentro do painel.
- [x] Overlay de atalhos com feedback de tecla + progresso do AG (`UI/Hud.cs`).
- [x] Cartão de inventário lido direto do `AssetDatabase`.
- [ ] Overlay da solução **no mapa do jogo**, não só na miniatura do painel.
      Precisa converter `CityLayer` local → tela via Futile.
- [ ] Salvar/carregar presets de parâmetros em disco.
- [ ] Comparar A/B: fitness da rede atual vs. da proposta, lado a lado.
- [ ] Campo numérico para digitar parâmetro. Hoje só slider, porque
      `GUI.TextField` estoura neste build ([[APIs ausentes - lista viva]]) —
      precisaria de um editor de texto escrito na mão.

## Infra

- [x] Auditor de stripping (`tools/StripAudit`) — confere os `MemberRef` de uma
      assembly contra o que o jogo realmente carrega. Rodar sempre que entrar
      uma DLL nova em `lib/`. Desde 2026-09-24 confere também método de tipo
      genérico instanciado (`List<int>.GetRange` passava batido e estourou em
      jogo). Rodar com a IMGUI de `lib/` numa pasta só dela: o `netstandard.dll`
      de `lib/` define `List<T>` inteiro e esconde o que o jogo não tem.
- [x] Teste offline do avaliador: `mmopt-bench` (sintético ou o
      `problems/last.bin` gravado pelo mod).
- [x] Teste de ponta a ponta sem mão humana: `MINIMETROGA_SELFTEST=london`.
- [ ] Verificar o mod contra uma atualização do jogo: os nomes de campo usados por
      reflexão (`Main.controller`, `GameController.game`, `City.stations`) são o
      ponto frágil. Ver [[Engenharia reversa - API do Mini Metro]].
- [ ] Outros algoritmos além do AG (o pedido original diz "AG por enquanto"):
      simulated annealing e ACO reusariam o mesmo `Evaluator` e a mesma UI.
