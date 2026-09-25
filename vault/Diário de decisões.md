# Diário de decisões

Registro cronológico do que foi tentado, o que quebrou e por quê.
Adicione entradas novas no topo.

---

## 2026-09-25 — 1300 em Londres: a rede precisa assentar, e mexer tem que custar

Objetivo: a rodada longa do self-test (Londres, 1800 s) que ficou faltando.

| Rodada | Código | Resultado |
|---|---|---|
| 1 | commit anterior | game over, **450**, semana 4 |
| 2 | + espera assentar, ordem no trecho, 2ª volta da edição | travou na semana 1 (trem sem locomotiva) |
| 3 | + `SafeApply` | game over, **1064**, semana 7 (o recorde anterior era 805) |
| 4 | + frota tirada de linha com sobra, teto de 10 s na espera | game over, **727**, semana 5 |
| 5 | idem, **peso de mexer 10** | game over, **1300**, semana 8 (recorde) |

Linhas apagadas por partida (total / nas últimas 10 aplicações): rodada 3,
25 / 16; rodada 4, 23 / 17; rodada 5 (peso 10), 13 / 7. Com peso 1 a morte é
sempre um redesenho em massa no fim; com peso 10 foi capacidade (41 estações).
Uma partida por configuração ainda é pouco para mudar o padrão (continua 1).

- **Thrash da aplicação incremental.** Na rodada 1, das rodadas 49 a 57 cada
  aplicação redesenhou 2 a 4 linhas com até 6 trens "esperando o estoque". O
  `Line.Mothball` só devolve o trem quando ele termina o trecho, então a rodada
  seguinte (7 s depois) lia linhas sem trem, a "rede atual" valia 2.000 a 7.500 e
  o AG apagava mais. O recorde antigo (805) era do `Line.Remove`, que devolvia o
  trem na hora e sumia com os passageiros. `Applier.IsSettling` agora segura o
  self-test e o modo automático. Na rodada 3 só 2 de 20 aplicações até a semana 3
  apagaram linha (na rodada 1 foram 20 de 48).
- **Ordem das estações no meio de um trecho** saía trocada (`11-16-15-3` →
  `11-15-16-3`, duas vezes). O `LineBuilder` escolhe a ponta solta por
  `2 (S_j − s)·v − |v|²`, e com o dedo em cima da estação (`v = 0`) quem decidia
  era o arredondamento. O toque agora vai um pouco para o lado da estação
  anterior. Rodada 3: zero edições que não bateram.
- **Edição recusada ganha uma segunda volta**, depois das outras, antes de a linha
  ser apagada. A suspeita é o estoque de travessias (Londres tem 3, todas em uso),
  mas ainda não houve recusa desde então para confirmar; o log registra as
  travessias livres.
- **Trem sem locomotiva.** `Line.AddTrain` põe o trem na lista antes de
  `Train.Start` criar a locomotiva. Um `Start` que estoura no meio deixa um trem
  oco, e `Game.Update` (via `Train.DistanceToNextTrain`) passa a estourar em todo
  frame; nem `Line.RemoveTrain` o tira. O `TryApplyAsset` engolia a exceção.
  Agora todo `ApplyAsset` passa por `SafeApply`, que loga e tira o trem oco por
  reflexão. Na rodada 5 o log trouxe a causa: `TrackPosition.LinkDistance` anda
  de `FirstTrack` por `NextTrack`, e o `RefreshTracks` do mod só chamava
  `Link.Update`, que regenera a lista de trilhos; quem liga `NextTrack` é o
  `GenerateGeo` do `Link.LateUpdate`. Trem posto no mesmo frame de uma edição
  achava a corrente solta. O `RefreshTracks` agora chama os dois.
- **Pendência travada.** A rodada 3 morreu assim: um vagão devia ir da L4 para a
  L2 nova, mas a L2 ainda não tinha trem, o `MoveCar` falhou e o estoque de vagões
  era zero. A pendência esperou 60 s, a espera segurou a otimização, e na volta
  (35 estações, rede atual 3.766) três rodadas seguidas redesenharam 3 a 4 linhas.
  Agora a espera tem teto de 10 s, e o `Tick`, quando nada mais vai voltar ao
  estoque, tira trem ou vagão de linha que tem mais do que a rede pediu. É o que
  o jogo também faz errado: a locomotiva que volta do depósito vai para a
  primeira linha marcada `IsWaitingForLocomotive`, não para a que o genoma quer.
- **Interchange posto sozinho**, pela primeira vez em jogo: estações #11 (−4,1%)
  e #17 (−1,3%).
- **Travessia temporária.** Com 0 travessias livres, encaixar a estação 15 no
  trecho 0-19 foi recusado duas rodadas seguidas, e o redesenho da linha saiu
  igual (sem a 15). No arrasto de trecho o link antigo só devolve a travessia
  quando os trens saem dele. O `Applier` agora confere a linha desenhada e não
  apaga de novo, por 60 s, uma rota que o jogo acabou de recusar; o modelo ainda
  não sabe disso (ver [[Roadmap]]).
- **Largada avançada** (`MINIMETROGA_SELFTEST_START_WEEK`), pedida para não
  esperar 10 minutos até o fim de jogo: o relógio pula para a semana N e o jogo
  abre as estações, acumula os prêmios ("Locomotive x6" e seis escolhas na semana
  6) e ajusta a demanda. Londres na semana 6: 29 estações em ~40 s. Começando com
  a rede vazia, as duas partidas morreram em 3 a 3,5 min (432 e 534
  passageiros): é um teste de estresse mais duro que a partida normal.
- **Upgrade "+0,0%" não é bug.** No bench com o problema da semana 2: controle,
  linha e vagão dão exatamente a base (lotação de linha 0, pior linha a 51%; linha
  sem trem novo não serve), travessia −3,7%.
- **Núcleo para Windows:** `./build.sh --windows-dll` (mingw, `i686-pc-windows-gnu`);
  `build.ps1` compila `i686-pc-windows-msvc`. Detalhes em [[Núcleo nativo (Rust)]].

---

## 2026-09-24 — aplicar mexendo só no que mudou, upgrade recomendado, travessia inviável

Objetivo: o otimizador chegar a 1000 passageiros, escolher upgrade e descartar
rede que cruza rio sem túnel/ponte suficiente.

- **`Line.Remove` some com quem está a bordo.** `City.RemoveLine` →
  `Line.Release` → `Train.Release`: ninguém desembarca. O `Applier` antigo
  apagava a rede inteira assim a cada rodada. Agora apaga como o jogador
  (`Line.Mothball`: os trens terminam o trecho e desembarcam), e só o que muda.
- **Aplicação incremental** (ver [[Aplicação da solução no jogo]]): linha igual
  fica; linha que só ganha estações recebe as estações por gesto
  (`HandleTerminatorTouchBegan`, `HandleLinkTouchBegan`); o resto é apagado e
  redesenhado; trem sobrando é movido como o "arrastar o trem" do jogo.
- **Custo de mexer no fitness** (`change.rs`), em passageiro·segundo: sem ele o
  AG trocava a rede inteira a cada rodada (o log da rodada 3 da validação mostra
  topologias completamente diferentes de 7 em 7 s).
- **Travessia virou restrição.** Rede com mais links de água do que o estoque
  não existe no jogo; o reparo corta links de água e o avaliador põe 10⁶ por
  excesso. Primeira rodada em jogo: "travessias 1 (modelo 1)".
- **Orçamento de linhas estava errado:** `max(livres, total concedido)` mandou 7
  linhas para o AG em Londres com 4. Agora é vivas + livres.
- **Upgrade:** o núcleo otimiza a rede com cada oferta (mesmo orçamento e
  semente, partindo da melhor sem upgrade) e compara. Um "controle" (rodar de
  novo sem nada) mostrou que ~11% da melhora é só tempo extra de busca; a
  comparação vale entre as ofertas, não contra a base.
- **AG em C# removido.** O modelo dele tinha ficado para trás em tudo; manter
  dois motores era manter dois modelos.
- **StripAudit tinha um ponto cego:** pulava `MemberRef` de tipo genérico
  instanciado. `List<int>.GetRange` (que o mscorlib do jogo não tem) passou e
  estourou no primeiro teste. Corrigido; agora confere ~200 referências a mais.
- **`ExtractRoute` lia a linha na ordem da lista de links**, que depois de uma
  edição no meio não é a do trajeto (o link novo entra no fim). Agora segue as
  conexões a partir de `Line.FirstActiveLink`.

Ainda falta: a rodada longa que mostra os 1000 passageiros (ver [[Roadmap]]).

## 2026-09-24 — O fitness confere com o jogo? (avaliador v3 e validação)

**A pergunta era se o modelo é fiel.** Em vez de construir um simulador
(caro, e ele mesmo precisaria ser validado), o gabarito virou o próprio jogo:
o self-test congela uma rede em janelas e mede o que o modelo previu. Ver
[[Validação do modelo]].

**Achados, relendo o código decompilado com o avaliador do lado:**

- `Line.AddTrain` **alterna o sentido** de cada trem novo em loop. Loop com 1
  trem é mão única; o modelo dava os dois sentidos. Virou variável do genoma
  (`rev`), e o Applier põe cada trem no sentido pedido (`Line.ApplyAsset`
  aceita posição e sentido).
- Lotação medida pela **média da linha** escondia o trecho que estoura.
- A **fila atual** entrava como taxa diluída no resto do dia: a demanda de
  hoje saía 2–3x maior (Londres: 9,2 pax/s em vez de 1,7).
- **Demanda média do dia** num trecho que não é um dia inteiro erra: o jogo
  solta cada entrada nas horas h, h+2…

**Tropeço: corrigir a lotação quebrou o incentivo.** Com o pior trecho, a
penalidade de trem cheio explodiu e o AG passou a deixar estações fora da rede
para não lotar os trens. No jogo quem não embarca continua na plataforma. A
solução foi pôr tudo na moeda do game over: passageiros acima da capacidade
na plataforma, com o excedente (sem lugar ou sem rota) crescendo no tempo.

**Tropeço: a medição foi estragada por fora.** Na primeira rodada o painel
estava com o modo automático ligado e aplicava redes no meio das janelas. O
self-test agora desliga atalhos e modo automático. E o contador de spawns do
jogo (`City.NewPeepCount`) zera no meio do dia: nascidos = entregues + fila +
a bordo.

**AG:** orçamento de tempo em vez de número fixo de gerações, e filho
repetido é mutado de novo (antes ~90% dos filhos eram redes já avaliadas).
Dijkstra só sobre estações, andando cada linha direto: 30% mais rápido com
resultado idêntico (diferença relativa máxima de 4e-10 em 128 redes).

---

## 2026-09-24 — Dossiê do jogo e núcleo nativo em Rust

**Primeiro o jogo, depois o código.** O `Assembly-CSharp` inteiro foi decompilado
e os dados de `resources.assets` foram extraídos (trens em JSON, cidades em
binário), para não chutar nada. O resultado está em
[[Dossiê - o problema de otimização]]. Os achados que mudaram o modelo:

- As **estações futuras já existem** na largada, inativas em `City.stations`.
- A demanda **depende do dia**: quadrado, pentágono e losango não geram ninguém no
  fim de semana.
- **No clássico `service` não afeta o spawn.**
- O fim de jogo é uma **integral** de 47 s, e existe **breach em cascata**.
- **Embarque é 1 passageiro por pulso por trem.** Trem **pula** estação vazia.
- **Cruzamento de trilhos** custa ~1,3 s. Túnel **não** reduz velocidade.
- Túnel só quando **as duas formas octilineares** cruzam a `ObstacleHull`.

**Por que Rust in-process, e não servidor.** O próprio jogo chama uma lib nativa
por P/Invoke com `IntPtr` + `GCHandle` pinado, então esse caminho já está provado
neste Mono stripado. Rede (`System.Net`) e processo filho (`Process`) dependem de
partes do BCL que podem não ter sobrevivido. Ver [[Núcleo nativo (Rust)]].

**Tropeços:**

1. O AG nativo aceitava **rede partida em dois componentes**. O custo de "sem
   rota" era linear e pequeno (0,25 passageiro/dia por par), e ficava barato não
   ligar. No jogo esse passageiro nunca sai da plataforma. Solução: o passageiro
   sem rota agora se acumula na fila da origem por todo o horizonte (mínimo 3
   dias) e conta como lotação. Também entrou o operador `link_components`.
2. `unsafe` no C# para converter float em bits: o compilador emitiu
   `SecurityPermissionAttribute`/`UnverifiableCodeAttribute`, tipos que o mscorlib
   stripado não tem (o StripAudit pegou). Solução: struct com
   `StructLayout(Explicit)`.
3. `UI.Screen` dentro do namespace `MiniMetroGA` resolvia para `MiniMetroGA.UI`.
   Solução: `global::UI.Screen`.
4. O primeiro self-test (o mod jogando Londres sozinho) congelou o placar na
   semana 1: o jogo para na tela de upgrade e espera o jogador. O self-test agora
   escolhe o upgrade por reflexão (`NewAssetScreen.OnAsset`).
5. O modo automático reaplicava a mesma rede a cada rodada, e aplicar apaga e
   reconstrói tudo. Agora só aplica se a rede mudou (`Genome.SameNetwork`) e se o
   ganho medido sobre a rede atual é ≥ 3% (`AutoApplyMinGain`).
6. Linha em loop quebrava o `Applier` com `NullReferenceException` em
   `Station.GetAmbiguousSections`. A causa: `Line.Start` roda a dica "estação
   ambígua" (`TipSystem.HandleLineEditEnded`), que percorre os trilhos de todos os
   links. Como montamos a linha inteira num frame, o link recém-criado ainda não
   tem trilho (a geração é lazy, no `Link.Update`). A exceção deixava a rede pela
   metade e o placar parava. Forçar `Link.Update` antes não basta: o link nasce e o
   `Start` roda dentro da mesma chamada do `LineBuilder`. A solução foi desligar só
   essa dica durante o `Apply`, direto no `ProfileData` em memória (sem
   `Profile.Save`), e religar no fim. Ela só roda enquanto o perfil ainda "precisa"
   da dica, por isso aparecia em perfil novo. Cada linha também ganhou `try/catch`
   próprio, para uma recusa do jogo não derrubar as outras.

---

## 2026-09-24 — Suporte ao build nativo de Linux

O jogo na máquina Linux é o build **nativo** (`Mini Metro` + `UnityPlayer.so`,
x64), não Proton. Então `winhttp.dll` não serve e tudo que dependia dele precisou
de um par Linux — sem remover nada do Windows.

**O que se confirmou igual:** Unity 2022.3.62f2, stripping idêntico (StripAudit dá
as mesmas cinco minas), e o mod compila contra as DLLs do Linux com zero avisos.
Nenhuma linha da lógica do mod precisou mudar.

**O que foi adicionado:**

- `linux/run.sh` → instalado como `<jogo>/MiniMetroGA/run.sh`. Faz o papel de
  `winhttp.dll` + `doorstop_config.ini`: `LD_PRELOAD` + `DOORSTOP_*`. Quando
  chamado pela Steam com `%command%`, se reinsere logo antes do executável do jogo
  para o `LD_PRELOAD` não contaminar o reaper/container (UnityDoorstop#88).
- `build.sh` → par do `build.ps1`. Baixa Doorstop e `lib/` conferindo sha256,
  compila e instala. No NixOS se reexecuta em `nix shell` se faltar `dotnet`.
- `.csproj` → `GameDir` default por SO, e `GameDataDir` detecta
  `MiniMetro_Data` × `Mini Metro_Data`. Caminhos com `/`, que o MSBuild aceita nos
  dois sistemas.
- `ModBootstrap.CheckImgui()` → loga se a IMGUI carregada é a não-stripada.

**Armadilha encontrada:** com o override do Doorstop, `Assembly.Location` da
IMGUIModule continua apontando para `Managed/`, mesmo o Mono tendo lido a de
`lib/` (o `GUILayout.Label` existe em runtime, e a do jogo só tem `Width`/`Height`).
Verificar override de assembly **pelo conteúdo**, nunca pelo caminho.

Ver [[Linux - build nativo]].

---

## 2026-08-20 — Vagão sem gradiente: a unidade da demanda era fictícia

**Sintoma (relatado pelo jogador):** *"o otimizador atual tava jogando vagão em
qualquer trem parece, sem considerar onde a linha passava e a demanda"*.

Estava certo, e a causa não era o operador de mutação — era **aritmética de
unidades**.

A demanda era `SpawnWeight · ShapeProb · DemandScale`, com `DemandScale = 0.030`
escolhido no olho. `aBordo[l]` saía dessa unidade fictícia; `assentos` saía de
`(trens+vagões)·6`, em passageiros de verdade. As duas grandezas não eram
comparáveis. Na prática `util = aBordo/assentos` dava sempre ≪ 1, a penalidade de
lotação **nunca disparava**, e como a lotação era o *único* lugar onde vagão
aparecia no fitness, **vagão não tinha gradiente nenhum**. O AG distribuía vagão ao
acaso porque, para ele, todas as distribuições valiam exatamente o mesmo.

Detalhe incômodo: a própria nota [[Função de fitness]] já dizia *"se as penalidades
de lotação parecerem irrelevantes, mexa em `DemandScale`"*. A suspeita estava
escrita; faltou ir conferir.

**Correção em três camadas:**

1. **Unidade real.** A demanda passou a vir da tabela de spawn do jogo, em
   passageiros/segundo (`Clock.DayLength = 20 s` faz a conversão de dia para
   segundo). `DemandScale = 1.0` agora significa "o que o jogo faz". Capacidade e
   velocidade também deixaram de ser chute: saem de
   `City.Definition.TrainDefinition`.
2. **Realimentação de lotação.** Somar uma penalidade no fim não muda o
   *roteamento* — no grafo, linha lotada continuava tão atraente quanto linha
   vazia. Agora o roteamento roda duas vezes, e no segundo passe a espera de
   embarque é multiplicada por `aBordo/assentos`. Assim um vagão a mais reduz o
   tempo de viagem de gente de verdade, na linha certa.
3. **Heurísticas coerentes.** `DistributeFleet` e `ShiftCarriage` passaram a olhar
   demanda por assento em vez de sortear, e `NormalizeFleet` corta a unidade
   excedente de quem tem folga — não da última rota da lista, que era só a ordem em
   que o crossover deixou as coisas.

**Lição, irmã da anterior:** um número que não tem unidade física não tem como
estar certo nem errado — ele simplesmente não participa. O `Max(1, ...)` do bug
passado *escondia* uma inconsistência; a escala arbitrária deste aqui **desligava**
um termo inteiro do fitness sem nenhum sinal de erro. Vale a regra: toda grandeza
que vai ser comparada com outra precisa nascer na mesma unidade, e essa unidade
precisa estar escrita no código.

De quebra, ao revisar os pontos de orçamento achei um `s.LinesAvailable` que tinha
escapado da correção anterior, em `Mutator.AddOrDropRoute`.

---

## 2026-08-20 — Linhas nascendo sem trem: três bugs empilhados

**Sintoma (relatado pelo jogador):** o AG propunha redes em que uma das linhas
ficava sem nenhum trem. *"parece q ele n sabe que a linha tem Xs carros. ai uma
das linhas ele esgotou e n tem carro. mas isso n é uma solução valida"*.

Diagnóstico exato, e eram três problemas somados:

**1. O orçamento contava cada linha duas vezes.**
`Genome.NormalizeFleet` fazia `locoBudget = n + snap.Locomotives`, apoiado na
crença de que a linha nova ganha um trem de brinde. O decompilado desmente:
`Line.AddLink` chama `AssetDatabase.ConsumeAsset(assetType)` — o primeiro trem sai
do mesmo estoque, e só existe se houver um disponível. Com 3 linhas e 3 trens o AG
achava que tinha 6 e distribuía 2/2/2; na aplicação o estoque acabava na segunda
linha e a terceira nascia parada.

**2. O fitness era cego para o problema.**
`Evaluator` tinha `int trains = Math.Max(1, g.Locos[l])`. Esse `Max(1, ...)` fazia
uma linha com zero trens ser avaliada como se tivesse um — então a solução
impossível pontuava igual a uma possível, e o AG não tinha como aprender a evitá-la.
**Piso defensivo em número que representa um recurso escasso é bug esperando
acontecer**: ele não conserta o dado, só esconde a inconsistência de quem poderia
corrigi-la.

**3. O applier assumia o brinde** e pedia só `Locos[r] - 1` locomotivas.

**Correções:**

- `Snapshot.LineBudget = min(LinesAvailable, Locomotives)` — o teto real de linhas.
  Usado por `Repair`, `GenomeBuilder` e `Evaluator` no lugar de `LinesAvailable`.
- `NormalizeFleet`: orçamento = `Locomotives`, e corta rotas excedentes quando há
  mais rota do que trem (uma rota que nasceria parada não é uma rota).
- `Evaluator`: linha com `Locos[l] <= 0` é **ignorada** — não atende estação, não
  gera aresta, não consome trilho. O custo passa a refletir a verdade.
- `Applier`: lê `line.ActiveTrainCount` depois de construir e completa até
  `Locos[r]`, em vez de supor. Se ainda assim sobrar linha sem trem, conta em
  `LinesWithoutTrain` e a mensagem grita.
- UI: cartão de inventário mostra **Linhas operáveis** e avisa quando há mais
  linhas que trens.

Lição geral: o `Snapshot` tem que carregar o orçamento **já reduzido à forma que o
AG precisa**, não os números crus do jogo. Cada lugar que refazia a conta na mão
era uma chance de refazer diferente — e foram três.

---

## 2026-08-20 — A UI não aparecia: IMGUI de fábrica × CoreModule stripado

**Sintoma:** o painel não desenhava nada. O log do mod estava limpo — porque a
exceção acontecia dentro do `OnGUI`, e o `output_log.txt` do jogo tinha **6,4 MB**
dela:

```
MissingMethodException: Method not found: UnityEngine.Vector4 UnityEngine.Vector4.get_one()
  at MiniMetroGA.UI.GaWindow.DrawControl (Game game)
GUI Error: You are pushing more GUIClips than you are popping.
```

**A classe de falha nova.** Até aqui o problema era sempre "o jogo não tem a API".
Este é o inverso: a `IMGUIModule` que trouxemos de fábrica chama membros que o
stripping removeu do `CoreModule` **do jogo**. O compilador não pega, porque o
código quebrado não é o nosso.

**Resposta: auditar em vez de adivinhar.** Escrevemos `tools/StripAudit`
(`System.Reflection.Metadata`, ~100 linhas): lê todos os `MemberRef` de uma
assembly e confere contra o que existe nas assemblies que o jogo carrega.
Resultado, em um comando — exatamente 5 membros:

```
UnityEngine.Rect::set_center
UnityEngine.RectOffset::Remove
UnityEngine.TouchScreenKeyboard::get_isRequiredToForceOpen
UnityEngine.Vector4::get_one
UnityEngine.Vector4::op_Multiply
```

Um `grep` na IMGUI decompilada mostrou onde cada um mora, e o estrago foi maior
que o esperado: três estão dentro de `SliderHandler`, ou seja **slider, scrollbar
e `BeginScrollView` são todos inutilizáveis**. E `Vector4.one` está na sobrecarga
para a qual até `GUI.DrawTexture(rect, tex)` delega — o desenho de textura inteiro
estava fora.

**Decisão: widgets próprios.** `UI/Widgets.cs` e `UI/Theme.cs` implementam slider,
checkbox, abas, barras e rolagem na mão, sobre o subconjunto de IMGUI que sobrou
(`GUI.Label`, `GUI.Box`, `GUILayout.*`, `GUILayoutUtility.GetRect`,
`Event.current`). `Theme.Blit()` é o único caminho de desenho de textura, e chama
a sobrecarga longa de `GUI.DrawTexture`, que já recebe os `Vector4` prontos.

Efeito colateral bom: dava para trazer a `CoreModule` não-stripada e resolver tudo,
mas isso ampliaria a superfície de risco justamente na assembly mais central do
jogo. Widget próprio custou ~300 linhas e ficou mais bonito que o skin padrão.

Depois disso, duas armadilhas clássicas de IMGUI ainda derrubaram o layout
(`GetRect` mentindo no evento `Layout` e `hotControl` posicional). Ambas
documentadas em [[UI e controles]].

Verificação: `output_log.txt` caiu de 6,4 MB de exceções para 11 KB e zero
exceções, e o print confirmou o painel inteiro desenhado.

---

## 2026-08-20 — Loader próprio em vez de BepInEx + Harmony

**Tentativa 1: BepInEx 5.4.23.5 x86.**
Instalado, Doorstop rodou, mas o preloader estourou:

```
MissingMethodException: Method not found:
void System.Reflection.Module.GetPEKind(PortableExecutableKinds&, ImageFileMachine&)
```

`mscorlib` do jogo está stripado.

**Tentativa 2: corlib não-stripada** de `unity.bepinex.dev/corlibs/2022.3.62.zip`,
em `dll_search_path_override`. Pior: aquela `mscorlib` é de outro flavor e faz
P/Invoke em `System.Native`, inexistente aqui. Todo o IO do jogo quebrou
(`Interop.Sys`, `System.Console`, `File.Exists`). **Revertido.**

**Tentativa 3: só Harmony, sem BepInEx.** Também não:

```
TypeInitializationException: HarmonyLib.AccessTools
 ---> MissingMethodException: AmbiguousMatchException..ctor(string, Exception)
```

**Decisão: zero patching.** O que precisávamos era (a) um `MonoBehaviour` para
`OnGUI` e (b) a instância de `Game`. Ambos dão para conseguir sem patch:

- bootstrap via `SceneManager.sceneLoaded` (sobreviveu ao stripping)
- `Game` via reflexão em `Main.controller` → `GameController.game`

Resultado: o mod não depende de **nenhuma** biblioteca de terceiros.
Ver [[Carregamento - Doorstop sem BepInEx]].

---

## 2026-08-20 — IMGUI não-stripada, e só ela

O jogo não usa IMGUI, então o stripping arrancou quase tudo: `GUILayout` ficou com
`Width()` e `Height()`, e mais nada. Sem `Label`, `Button`, `Window`.

Alternativas consideradas:

1. **Hand-roll com o que sobrou** (`GUI.Label`, `GUI.Box`, `Event.current`) —
   viável, mas botões e sliders manuais, e feio.
2. **Usar Futile**, a UI do próprio jogo — garantido, mas muito mais código.
3. **Trazer a IMGUIModule não-stripada** — testado e funcionou.

Escolhida a 3. Só `UnityEngine.IMGUIModule.dll` + `netstandard.dll` em
`MiniMetroGA\lib`. O jogo carregou 3138 frames limpo, sem erro de assembly.

Lição: o override de assembly funciona **cirurgicamente**. Trocar o conjunto
inteiro (como o zip de `libraries/` faz) quebra tudo.

---

## 2026-08-20 — Compilar contra a BCL do jogo

Depois de perder tempo com `File.AppendAllText` falhando em silêncio, mudamos o
`.csproj` para `NoStdLib` + referências ao `mscorlib`/`System`/`System.Core`
**do jogo**. Na hora o compilador acusou `List<T>.GetRange` e o ctor de
`RectOffset`, que teriam virado bug de runtime.

Foi a mudança de maior retorno do projeto. Ver
[[Managed stripping - o problema central]].

---

## 2026-08-20 — Dirigir o LineBuilder em vez de montar links na mão

`Line.AddLink`/`AppendLink` são públicos e tentadores, mas replicar as regras do
jogo (túnel, terminador, travessia, orçamento) seria reimplementar o jogo.

`LineBuilder.HandleStationTouchBegan/Over/TouchEnded` é o mesmo caminho que o dedo
do jogador percorre — todas as validações vêm de graça, e uma jogada ilegal é
recusada em vez de corromper o estado. Ver [[Aplicação da solução no jogo]].

---

## 2026-08-20 — Fitness: grafo estação × linha

Alternativas para modelar baldeação:

1. grafo só de estações + contador de trocas de linha → não modela espera
2. simular o jogo de verdade → caro demais para 50k avaliações
3. **grafo (estação, linha) com arestas de embarque/desembarque** ← escolhida

A 3 embute o custo de baldeação nas arestas, então uma única Dijkstra por origem
já dá tempo de viagem *e* número de trocas, sem contabilidade extra.
Ver [[Função de fitness]].
