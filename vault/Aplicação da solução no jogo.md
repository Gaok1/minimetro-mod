# Aplicação da solução no jogo

Traduzir o melhor genoma em linhas de verdade é onde um mod normalmente quebra o
save. A decisão que evita isso:

> [!important] Não mexemos nas estruturas internas na mão.
> Dirigimos o `LineBuilder` e o `ApplyAsset` do jogo exatamente como o dedo do
> jogador faria.

E, desde a v3 do avaliador, **mexemos só no que mudou** (`Core/Applier.cs`).

## Por que incremental

Até a v2 o `Applier` apagava a rede inteira com `Line.Remove()` e redesenhava.
Dois problemas, os dois achados no decompilado e na [[Validação do modelo]]:

- `Line.Remove` → `City.RemoveLine` → `Line.Release` → `Train.Release`: os
  passageiros a bordo **somem** (ninguém desembarca). Cada reconstrução jogava
  fora dezenas de passageiros que iam virar ponto — e, de quebra, aliviava as
  estações de um jeito que o jogador não consegue.
- Os trens recomeçam de um ponto só; no fim de jogo o AG reconstruía a cada
  rodada e o game over veio logo depois de três reconstruções em 15 s.

## O que o Applier faz

Casa cada linha atual com uma rota do genoma (mesma regra de
`native/mmopt/src/change.rs`, que cobra no fitness o custo de cada mexida):

| Caso | O que acontece no jogo | Custo no fitness |
|---|---|---|
| Rota **igual** (a menos de sentido e rotação do loop) | nada | 0 |
| Rota atual é **subsequência** da nova (só ganha estações, mesmo tipo de linha) | estica a ponta pelo terminal e encaixa no meio arrastando o trecho; trens seguem rodando | 20 pax·s por estação |
| Qualquer outra mudança | `Line.Mothball` (como o jogador apaga: os trens terminam o trecho e **desembarcam** na próxima estação) e a linha nova é desenhada | 300 pax·s + 25 por passageiro a bordo |
| Trem tirado de linha que fica | o "arrastar o trem" do jogo: desembarca antes de mudar | 60 pax·s + 25 por passageiro dele |

O custo em passageiro·segundo vira fitness dividido pelo horizonte (mesma
unidade do custo de viagem). `GaConfig.ChangeWeight` multiplica (0 = o AG troca
a rede inteira por qualquer ganho).

Ordem: (1) apaga as linhas que não ficam — libera linha e travessia antes de
desenhar; (2) edita as que só ganham estações; (3) desenha as novas; (4) frota.
Depois de cada edição a rota é **relida**; se não bateu com o genoma, a linha é
apagada e redesenhada (e o log diz por quê).

Linha nova também é relida depois do desenho. Se o jogo recusar uma rota (na
edição ou no desenho), ela fica marcada por 60 s: o AG não sabe da recusa e pede
a mesma rota na rodada seguinte, e apagar e redesenhar de novo só mandaria trem
para o depósito e voltaria com a mesma linha. Nesse intervalo a linha fica como
está.

A edição recusada ganha **uma segunda volta** antes de a linha ser apagada, depois
das outras edições. O estoque de travessias é da rede inteira: em Londres (3
túneis, todos em uso) a linha 2 precisava de um túnel que só a edição da linha 4
liberava. O total final cabia, a ordem não, e a linha 2 era apagada e redesenhada
à toa (com o trem indo para o depósito). O log registra quantas travessias
estavam livres na recusa.

## Os gestos

Linha nova, como sempre:

```csharp
lb.HandleStationTouchBegan(primeira, ToGlobal(primeira.Position));
foreach (var st in resto) {
    lb.HandleTouchMove(ToGlobal(st.Position));
    lb.HandleStationTouchOver(st);
}
if (circular) lb.HandleStationTouchOver(primeira);
lb.HandleTouchEnded();
```

Esticar a ponta (linha aberta):

```csharp
lb.HandleTerminatorTouchBegan(terminal, pontaLocal);   // terminal ancorado na ponta
foreach (var st in novas) { lb.HandleTouchMove(...); lb.HandleStationTouchOver(st); }
lb.HandleTouchEnded();
```

Encaixar no meio de um trecho A→B:

```csharp
lb.HandleLinkTouchBegan(link, meioLocal);               // coordenada LOCAL
foreach (var st in novas_de_A_para_B) { lb.HandleTouchMove(...); lb.HandleStationTouchOver(st); }
lb.HandleTouchEnded();                                  // CommitMiddleEdit
```

No meio de um trecho o jogo tem **duas pontas soltas**, uma saindo de cada
estação do trecho (`links[0]` de `link.Start`, `links[1]` de `link.End`), e
`LineBuilder.HandleStationTouchOver` prende a estação tocada `s` na ponta `j` que
maximiza

```
|S_j − s|² − |S_j − último toque|²  =  2 (S_j − s)·v − |v|²,   v = último toque − s
```

Com o dedo exatamente em cima da estação (`v = 0`) os dois lados empatam e quem
decide é o arredondamento de `GlobalToLocal`: duas estações no mesmo trecho
entravam na ordem trocada (`11-16-15-3` virava `11-15-16-3`; duas vezes numa
partida de Londres). O `Applier` agora põe o último toque um pouco para o lado
de `S_0 − S_1` (5% da distância, no mínimo 1 unidade): a estação sempre prende na
ponta que vem da anterior, qualquer que seja a geometria, e as estações entram na
ordem de `link.Start` para `link.End`.

Detalhes que custaram leitura do decompilado:

- `HandleStationTouchBegan` espera **coordenadas globais**; `HandleLinkTouchBegan`
  e `HandleTerminatorTouchBegan` esperam **locais** (é o que `Line.HandleTouchBegan`
  e `Terminator.HandleTouchBegan` passam). `Station.Position` é local.
- `HandleStationTouchOver` retorna cedo se `focus == station`.
- Ele pode **fechar a linha sozinho** (ao voltar na primeira estação). Por isso o
  laço checa `lb.IsBuilding` a cada passo.
- Link apagado (`Mothball`) devolve a travessia na hora; a linha também.

## Frota

- `Line.ApplyAsset` checa disponibilidade e consome do `AssetDatabase`.
- Trem que sobra numa linha vai para onde falta como o jogador faria: marca a
  locomotiva `MOTHBALLED` e chama `destino.ApplyAsset(tipo, pos, sentido,
  locomotivaAntiga)`. O jogo cria o trem novo **pendente**, ligado ao antigo, que
  termina o trecho, desembarca e só então libera o novo. Vagão igual.
- Vagões são acertados antes dos trens (trem movido leva os vagões junto), e
  para mover escolhe-se o trem sem vagão e mais vazio.
- O que falta e o estoque ainda não tem (a locomotiva de uma linha apagada só
  volta quando o trem chega na estação) fica **pendente**: `Applier.Tick`, todo
  frame, completa assim que liberar (até 60 s).
- Loop: cada trem entra no sentido que o genoma pediu (ver [[Núcleo nativo (Rust)]]).

> [!danger] Trem sem locomotiva.
> `Line.AddTrain` põe o trem na lista da linha **antes** de `Train.Start` criar a
> locomotiva. Se o `Start` estoura no meio, sobra um trem oco, e
> `Line.ActiveTrainCount`, `Train.DistanceToNextTrain` e o `Game.Update` passam a
> estourar em todo frame; nem `Line.RemoveTrain` consegue tirá-lo (ele lê a
> locomotiva). Aconteceu em Londres ao pôr um trem numa linha logo depois de
> encaixar uma estação no meio de um trecho: o `Start` estourou em
> `TrackPosition.LinkDistance`, que anda de `FirstTrack` por `NextTrack`.
> `Link.Update` só regenera a lista de trilhos; quem liga `NextTrack` e soma o
> comprimento é o `GenerateGeo` do `Link.LateUpdate`, que o jogo roda no fim do
> frame. O `RefreshTracks` do `Applier` agora chama `Update` em todos os links e
> depois `LateUpdate` (a solda olha os vizinhos). Por garantia, todo `ApplyAsset`
> passa por `SafeApply`: a exceção vai para o log e o trem oco sai da lista por
> reflexão (`Line.trains` + `Link.RemoveTrain`); a pendência tenta de novo meio
> segundo depois.

> [!danger] O trem da linha nova **não é grátis**.
> `Line.AddLink`, ao abrir a linha, chama `AssetDatabase.ConsumeAsset`: o
> primeiro trem sai do **mesmo estoque**. Sem locomotiva livre, a linha nasce com
> `isWaitingForLocomotive = true` (e o jogo dá a ela a próxima que liberar).

## Esperar a rede assentar

Linha apagada com `Mothball` só devolve o trem ao estoque quando ele termina o
trecho e desembarca; até lá a linha fica na cidade com `IsMothballed`, e a linha
nova espera trem (`Applier` pendente). Otimizar nesse meio-tempo lê uma rede de
passagem: linhas sem trem e frota que não está nem no estoque nem em linha viva.
O AG "conserta" isso apagando mais linhas, e cada rodada deixa mais trem parado.
Foi assim que Londres morreu na semana 4 em 2026-09-25 (de 7 em 7 s, duas a quatro
linhas redesenhadas e até 6 trens esperando o estoque).

`Applier.IsSettling` (linha apagada com trem rodando, ou pendência com menos de
10 s) segura o self-test e o modo automático do painel até a rede assentar; o
log registra quanto tempo a frota pendente levou para entrar (em Londres, 1 a
5 s reais).

A pendência não pode depender só do estoque. A locomotiva que volta do depósito
vai para a primeira linha que o jogo marcou `IsWaitingForLocomotive`, não para a
que o genoma quer, e o vagão destinado a uma linha que ainda não tem trem não tem
onde entrar e fica na linha de origem. Foi o que matou a rodada 3 de Londres
(pendência de 60 s, depois três rodadas de redesenho). Quando nenhuma linha
apagada tem mais trem rodando (o estoque não vai crescer), `Applier.Tick` tira
trem ou vagão das linhas que têm mais do que a última rede pediu.

## Conferências no fim

- **Travessias:** usadas no jogo (`total − livres` de `Crossing`) contra as do
  modelo (`Breakdown.CrossingsUsed`). Diferença = a detecção de água divergiu;
  vai para o log.
- **Passageiros:** plataformas + a bordo, antes e depois (o jogo está pausado).
  Com `Mothball` não pode sumir ninguém; o self-test soma o que sumir.

Ver também: [[Engenharia reversa - API do Mini Metro]], [[Função de fitness]].
