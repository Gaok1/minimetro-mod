# Núcleo nativo (Rust)

O AG e o avaliador reescritos em Rust (`native/mmopt`) e carregados **dentro do
processo do jogo** por P/Invoke, do mesmo jeito que o próprio jogo chama a
`libminimetrox`. Não tem rede nem IPC: o mod serializa o problema num blob, o
núcleo roda o AG em threads próprias e o C# consulta o progresso a cada frame.

É o **único** otimizador: o AG em C# (`GaEngine`, `Evaluator`, `Mutator`,
`GenomeBuilder`) foi removido depois que o modelo dele ficou para trás (sem
sentido de trem, sem fila como distribuição, sem custo de mexer na rede, túnel
por amostragem da reta). Sem a biblioteca (Windows sem o build nativo, por
exemplo) o mod avisa e não otimiza.

O modelo segue o [[Dossiê - o problema de otimização]].

## Por que in-process, e não um servidor

| Opção | Custo por rodada | Risco |
|---|---|---|
| **P/Invoke (escolhida)** | um blob de ~10–50 KB, uma vez | ABI em C, tudo `IntPtr` |
| TCP/Unix socket | idem + serialização de volta | `System.Net` no mscorlib stripado? |
| Processo filho | idem + spawn | `Process` pode ter sido stripado |

O jogo já prova que P/Invoke com `IntPtr` + `GCHandle` pinado funciona neste
Mono stripado (`NativeExtensions`). É exatamente o padrão usado aqui.

## Arquitetura

```
C# (thread do Unity)                      Rust (libmmopt.so / mmopt.dll)
Snapshot.Capture ─► ProblemExport.Build ─► mm_create(blob)        -> handle
GaConfig ─► NativeEngine.ConfigVector  ─► mm_start(h, f64[])       -> thread "mmopt-ga"
UI todo frame                          ◄─ mm_status / mm_history
Aplicar                                ◄─ mm_best -> Genome do C# -> Applier
```

| Arquivo | Papel |
|---|---|
| `src/problem.rs` | formato do blob (espelho de `Core/ProblemExport.cs`), gerador sintético |
| `src/ctx.rs` | horizonte → cenários (dia útil × fim de semana, tensão, crescimento) → épocas |
| `src/geom.rs` | octilinear, formas do link, costa (`ObstacleHull`), custo de parada e de cruzamento |
| `src/eval.rs` | avaliador v3 (ver [[Validação do modelo]]) |
| `src/genome.rs`, `src/ops.rs` | encoding igual ao do C#, reparo (inclusive de travessia), 14 operadores |
| `src/change.rs` | custo de sair da rede atual para a do genoma (mesmo casamento do `Applier`) |
| `src/upgrade.rs` | recomendação do upgrade da semana |
| `src/ga.rs` | AG paralelo, cache, memético |
| `src/lib.rs` | ABI C (`mm_*`), todo ponto de entrada com `catch_unwind` |
| `src/bin/bench.rs` | `mmopt-bench`: roda fora do jogo |

## Avaliador v3

A v3 corrigiu o que a [[Validação do modelo]] achou na v2 (loop de mão única,
lotação pela média, fila atual virando taxa, demanda sem hora do dia) e
conferiu o modelo contra o jogo. O resumo do que o avaliador faz hoje:

- **Sentido de cada trem.** Linha aberta: todo trem vai e volta, headway =
  ciclo/T nos dois sentidos. Loop: cada trem roda num sentido só, e o genoma
  diz quantos vão contra a ordem da rota (`Route::rev`). O jogo, sozinho,
  alternaria (`Line.AddTrain`); o Applier põe cada trem no sentido pedido.
- **Dois nós por parada** (um por sentido) e **Dijkstra só sobre estações**:
  ao fechar uma estação, cada linha que embarca ali é percorrida direto,
  com poda de dominância por nó de trem. Mesmo caminho do grafo
  (estação, trem), 30% mais rápido.
- **Lotação pelo pior trecho de cada sentido:** fluxo × headway / lugares por
  trem.
- **Fila como distribuição:** por sentido chegam λ × headway passageiros entre
  dois trens. A fila é Poisson com essa média variando (idade uniforme da
  leva), mais a parte fixa (fila atual). O custo é o excesso esperado acima da
  capacidade, `E[(fila − cap)+]`, que é o que dispara o timer.
- **Folga e quem não cabe no trecho que sai da estação**, não no pior trecho
  da linha (depois dele o trem já esvaziou).
- **O que sobra na plataforma cresce:** sem lugar no trem (utilização > 1) ou
  sem rota. Sem lugar acumula por `PersistDays` (7, até o próximo upgrade); sem
  rota, pelo resto da partida (4×). Foi isso que acabou com a rede partida em
  dois pedaços.
- **Fila atual é fila, não taxa:** a que tem rota escoa pela folga dos trens;
  a sem rota fica.
- **Demanda com hora marcada:** cada trecho do horizonte recebe exatamente os
  passageiros que o jogo solta nele (entrada na hora h, h+2, h+4…, com o
  sorteio `floor + Bernoulli`).

### O que a v2 já fazia (continua valendo)

Cada item sai do dossiê:

- **Distância octilinear**, não euclidiana (§8.1).
- **Túnel só quando as duas formas do link cruzam a costa exata**, a
  `ObstacleHull` lida por reflexão (§8.2). O C# amostrava 12 pontos na reta.
- **Cruzamento de trilhos custa tempo:** ≈ +1,33 s por passagem (§6.2).
- **Parada com a cinemática real:** ≈ 1,77 s de frenagem e aceleração, mais o
  dwell de **1 pulso por passageiro** (§6.3) e a chance de o trem **pular** a
  estação (§6.4). Em interchange e em Seul o lote embarca inteiro num pulso.
- **Escolha de rota igual ao A* do jogo:** o Dijkstra minimiza o custo percebido
  (espera + trecho + 3,5 por parada + 5 por baldeação), mas o fitness mede o
  **tempo real** do caminho escolhido (§7).
- **Demanda no horizonte:** um cenário por dia (útil/fim de semana, tensão da
  semana, crescimento pós-cidade-cheia), agrupados em épocas pelo conjunto de
  estações presentes (§3).
- **Estações futuras encaixadas virtualmente** (inserção mais barata), para a
  rede de hoje já absorver a de amanhã (§5).
- **Fila real por destino** e **timer de lotação** de cada plataforma: a fila
  entra como demanda do primeiro cenário, e o timer entra como urgência (§4).
- **Passageiro sem rota se acumula na plataforma** por todo o horizonte (mínimo
  3 dias) e conta como lotação: no clássico ninguém desiste. Sem isso o AG
  aceitava rede partida em dois pedaços (ver [[Diário de decisões]]).

### Túnel e ponte: rede inviável, não penalidade

`Link.ReserveCrossing` não deixa desenhar link sobre a água sem travessia no
estoque. Então a rede que passa do orçamento **não existe** e não pode ganhar
de nenhuma que existe:

- `Genome::fix_water` (dentro do reparo) corta links de água sorteados até
  caber: loop abre naquele link, ponta de linha perde a estação, trecho do meio
  parte a linha em duas (ou fica o pedaço maior, se não há linha sobrando);
- no avaliador, cada travessia acima do estoque custa `CROSSING_INFEASIBLE`
  (10⁶), acima de qualquer rede viável. O peso da UI
  ("túnel acima do orçamento") saiu;
- o `Applier` confere, depois de aplicar, as travessias que o jogo gastou contra
  as do modelo e loga a diferença.

"Precisa de túnel" continua sendo: as duas formas octilineares do link cruzam a
costa exata (`ObstacleHull`). Um link gasta 1 travessia, não importa quantas
vezes cruze.

### Custo de mexer na rede atual (`change.rs`)

O fitness cobra o que a mudança custa na partida, em passageiro·segundo
dividido pelo horizonte: linha igual 0; linha que só ganha estações 20 por
estação encaixada; linha apagada e redesenhada 300 + 25 por passageiro a bordo
(eles desembarcam e esperam de novo); trem tirado de linha que fica 60 + 25 por
passageiro dele. Pesa `ChangeWeight` (1). O casamento atual → genoma é o mesmo
do [[Aplicação da solução no jogo|Applier]]. Com isso o AG deixou de trocar a
rede inteira a cada rodada.

## AG

Mesmos operadores do C# antigo, mais:

- `flip_direction`: muda quantos trens do loop rodam em cada sentido (com 1
  trem, escolhe a mão);
- `add_spare_unit`: trem ou vagão parado no estoque vai para uma linha, sem
  mexer no resto da frota (antes só a redistribuição inteira usava o estoque, e
  ela tira trem de linha que está rodando);
- **semente "a jogada padrão"**: a rede atual com as estações novas encaixadas
  onde custa menos e o estoque distribuído;
- **orçamento de tempo** (`TimeBudgetMs`, default 3 s; o self-test usa 1,5 s)
  além do número de gerações;
- **filho repetido é mutado de novo** antes de ser avaliado: com a população
  convergida, ~90% dos filhos eram redes já vistas (cache). Agora quase toda
  avaliação é rede nova (55–75 mil em 3 s em Londres com 28 estações, contra 4,3
  mil úteis na configuração antiga);

- `connect_orphan`: liga uma estação que ninguém atende, na inserção mais barata
  global;
- `link_components`: une componentes desconexos pelo par de estações mais
  próximo (cria a baldeação);
- escolha de estação para inserir prefere órfãs;
- **cache** de avaliação por hash do genoma (elites e clones não reavaliam);
- **busca local** (memético) no melhor indivíduo a cada `LocalSearchEvery`
  gerações;
- avaliação paralela com threads com escopo, um `Evaluator` por thread e zero
  alocação por avaliação depois de aquecido.

## Números medidos

No **mesmo estado real** (Londres gravado pelo mod no self-test, 28 estações
ativas + 30 futuras, rio com 48 segmentos), 250 gerações × 120 indivíduos:

| | C# (Mono do jogo, 8 threads) | Rust, 1 thread | Rust, 12 threads |
|---|---|---|---|
| Tempo total | 2731 ms | 750 ms | **250 ms** |
| CPU por avaliação | ~750 µs | **44 µs** | 44 µs |

Isso tudo com o modelo nativo fazendo **mais** trabalho por avaliação (cenários
por dia, épocas com estações futuras, dois passes, cruzamentos, dwell por
passageiro). O cache ainda evita ~85% das avaliações depois que a população
converge.

**Self-test em jogo** (Londres, clássico, 2x, 7 min): 65 rodadas, 46 redes
aplicadas, 20 mantidas (iguais à atual ou com ganho < 3%), 10 upgrades
escolhidos sozinhos. Chegou à **semana 5 com 28 estações e placar 290** até o game
over. Zero erro no log do mod e zero exceção no `Player.log`. Cada rodada levou de
10 a 270 ms.

## Números da v3

Londres, 28 estações ativas + 30 futuras, i7-9750H (6 núcleos):

| | v2 | v3 |
|---|---|---|
| µs por rede, 1 thread (redes aleatórias) | 44 | 109 |
| Avaliações úteis em 3 s (12 threads) | ~4 mil (o resto era cache) | 55–75 mil |

A v3 faz mais por rede (dois sentidos, fila como Poisson misturada, fila atual,
demanda por hora). O Dijkstra só sobre estações tirou 30%; a fila Poisson
devolveu parte (é 30% do tempo, com atalhos para estação longe ou muito acima
da capacidade). O teto é o hardware: 6 núcleos físicos, e hyperthreading não
ajuda (6 threads ≈ 12 threads).

## ABI (`mm_*`)

| Função | O que faz |
|---|---|
| `mm_version()` | versão do ABI (3). O C# recusa se não bater |
| `mm_create(blob, len, err, cap)` | parseia o problema → handle (0 = erro em `err`) |
| `mm_start(h, cfg, n)` | inicia o AG com o vetor de configuração |
| `mm_status(h, out, n)` | estado, geração, melhor/média/pior, ms, avaliações, cache, threads, µs/aval, fitness da rede atual |
| `mm_best(h, out, cap, bd, n)` | melhor genoma + decomposição do custo (19 valores; o fitness é o 15º, o custo de mexer o 16º) |
| `mm_history(h, best, avg, cap)` | curva de convergência |
| `mm_evaluate(h, genome, len, cfg, n, bd, n)` | avalia um genoma qualquer |
| `mm_explain(h, genome, len, cfg, n, out, cap)` | o que o modelo prevê para a rede (por linha e por estação), para comparar com o jogo |
| `mm_upgrade_start(h, cfg, n, opts, nopt)` | recomendação de upgrade, assíncrona: `opts` = pares (AssetType, quantidade) |
| `mm_upgrade_result(h, out, cap)` | `[linhas, (tipo, qtd, fitness, estação)*]`, a base (tipo 0) primeiro |
| `mm_stop`, `mm_destroy`, `mm_error` | ciclo de vida |

O vetor de configuração segue a ordem de `Config::from_vector` (índice novo
sempre no fim; o que faltar fica no default).

## Carregamento

`NativeEngine.Preload` abre a biblioteca pelo **caminho completo**: `dlopen` no
Linux, `LoadLibraryW` no Windows. Só depois o `DllImport("mmopt")` resolve:

- no Linux a `.so` tem **SONAME `libmmopt.so`** (`build.rs`), então o glibc reusa a
  que já está carregada. O `run.sh` também põe `MiniMetroGA/bin` no
  `LD_LIBRARY_PATH`, como segunda garantia;
- no Windows o `LoadLibrary` casa pelo nome do módulo.

> [!warning] Nada de `unsafe` no C#.
> Código `unsafe` faz o compilador emitir `SecurityPermissionAttribute` e
> `UnverifiableCodeAttribute`, que o mscorlib stripado não tem (o StripAudit
> acusou). O `float → bits` do exportador usa um struct com
> `StructLayout(Explicit)`, que é pseudo-atributo.

## Build

- **Linux:** `./build.sh` compila o núcleo (`cargo build --release`) e o csproj
  copia a `libmmopt.so` para `MiniMetroGA/bin`. No NixOS, se faltar
  `cargo`/`dotnet`, o script se reexecuta em `nix shell`. `--no-native` pula o
  núcleo.
- **Windows:** o jogo é 32-bit, então o alvo é `i686-pc-windows-msvc`
  (`cargo build --release --target i686-pc-windows-msvc`); o csproj copia
  `mmopt.dll` se existir. Sem ela o otimizador fica desligado.

## Rodar fora do jogo

Com `DumpProblem` ligado, cada rodada grava `MiniMetroGA/problems/last.bin`:

```bash
cd native/mmopt
cargo run --release --bin mmopt-bench -- file "<jogo>/MiniMetroGA/problems/last.bin"
cargo run --release --bin mmopt-bench -- synth --active 40 --future 8 --week 6
```

## Teste de ponta a ponta

`MINIMETROGA_SELFTEST=london` no ambiente faz o mod abrir Londres sozinho e
**jogar** como o modo automático do painel: a cada
`MINIMETROGA_SELFTEST_EVERY` s (default 6) pausa, otimiza, mexe na rede e
despausa, em velocidade 2x, até `MINIMETROGA_SELFTEST_SECONDS` (default 180) ou
o game over (`MINIMETROGA_SELFTEST_PAUSE=0` não pausa). Na segunda-feira o
`UpgradeAdvisor` escolhe o upgrade pela recomendação e põe o interchange.
`MINIMETROGA_SELFTEST_QUIT=1` fecha o jogo no fim. Tudo vai para o log com o
prefixo `SELFTEST`.

Com `MINIMETROGA_SELFTEST_MODE=validate`, de tempos em tempos ele aplica a
melhor rede, **congela** e mede no jogo o que o modelo previu (ver
[[Validação do modelo]]). Enquanto o self-test roda, os atalhos F9–F11 e o
modo automático do painel ficam desligados: aplicar uma rede no meio da janela
estragaria a medição.

Ver também: [[Dossiê - o problema de otimização]], [[Função de fitness]],
[[Algoritmo Genético - design]]
