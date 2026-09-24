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

No meio de um trecho, cada estação tocada é presa no lado que "chega mais perto"
do toque (`LineBuilder.HandleStationTouchOver`); com o toque exatamente em cima
da estação, os dois lados empatam e ganha o lado do início do link. Por isso as
estações entram na ordem de `link.Start` para `link.End`.

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

> [!danger] O trem da linha nova **não é grátis**.
> `Line.AddLink`, ao abrir a linha, chama `AssetDatabase.ConsumeAsset`: o
> primeiro trem sai do **mesmo estoque**. Sem locomotiva livre, a linha nasce com
> `isWaitingForLocomotive = true` (e o jogo dá a ela a próxima que liberar).

## Conferências no fim

- **Travessias:** usadas no jogo (`total − livres` de `Crossing`) contra as do
  modelo (`Breakdown.CrossingsUsed`). Diferença = a detecção de água divergiu;
  vai para o log.
- **Passageiros:** plataformas + a bordo, antes e depois (o jogo está pausado).
  Com `Mothball` não pode sumir ninguém; o self-test soma o que sumir.

Ver também: [[Engenharia reversa - API do Mini Metro]], [[Função de fitness]].
