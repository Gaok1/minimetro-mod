# Função de fitness

O `Evaluator` **não roda o jogo**. Ele constrói um modelo próprio da rede e mede.
Rodar o simulador real por indivíduo seria inviável: são dezenas de milhares de
avaliações por otimização.

## O grafo estação × linha

O truque central é não modelar só estações, mas o par **(estação, linha)**:

```
nó  s                 → plataforma da estação s (fora do trem)
nó  N + l*N + s       → dentro do trem da linha l, na estação s
```

Arestas:

| De → Para | Peso |
|---|---|
| plataforma(s) → dentro(s,l) | `headway_l / 2 + BoardingPenalty` |
| dentro(s,l) → plataforma(s) | `TransferPenalty` |
| dentro(a,l) → dentro(b,l) | `dist(a,b)/TrainSpeed + DwellTime` |

Com esse grafo, **baldeação sai de graça**: trocar de linha obriga a passar por
uma aresta de desembarque + uma de embarque, e o custo já inclui a espera da
linha nova. Não precisa contar transferências à mão.

`headway_l = tempoDeCiclo_l / trens_l`, onde o ciclo é ida-e-volta se a linha não
for circular.

## Demanda

Para cada estação de origem `u`, uma **Dijkstra** sobre esse grafo dá o tempo até
todo o resto. Depois, para cada forma de destino `T`:

```
custo += DemandRate[u][T] · min{ d(u→v) : forma(v) == T }
```

O `min` é o que reproduz o jogo: o passageiro vai para a estação **mais próxima**
que tenha a forma que ele quer, não para uma específica.

`DemandRate` vem da [[Modelo de demanda|tabela de spawn do próprio jogo]], em
**passageiros por segundo**:

```
DemandRate[u][T] = agenda(forma(u) → T, tipoDeDia)   // passageiros/dia
                 · tensão(semana, diaDaSemana)
                 · escalaDaCidade · centralidade(u) · serviço(u)
                 / DayLength                          // 20 s por dia
                 · (1 + CrowdWeight · esperando(u))   // viés nosso, não do jogo
                 · DemandScale                        // 1.0 = o que o jogo faz
```

> [!important] A unidade não é decorativa.
> Enquanto a demanda estava numa unidade arbitrária, `aBordo[l]` e a contagem de
> assentos eram grandezas incomparáveis, `util` dava sempre ≪ 1 e a penalidade de
> lotação **nunca disparava** — vagão não tinha gradiente nenhum. Ver
> [[Diário de decisões]].

Se não houver caminho: `UnreachablePenalty`.

## Lotação (Little), e a realimentação

Reconstruindo o caminho ótimo (ponteiros de predecessor da Dijkstra), o fluxo é
creditado:

- tempo a bordo × demanda → **passageiros médios no trem** da linha
- tempo de espera × demanda → **passageiros médios na plataforma**

Daí:

```
util_linha    = aBordo[l] / ((trens + vagões) · RailcarCapacity)
util_estação  = esperando[s] / capacidade[s]
penalidade   += Σ max(0, util − 1)²
```

O quadrado é de propósito: uma linha 200% lotada é muito pior que duas a 100%.

`RailcarCapacity` sai de `City.Definition.TrainDefinition.Capacity` — locomotiva e
vagão são ambos *railcars* e carregam o mesmo tanto.

**O segundo passe.** Uma penalidade somada no fim não muda o *roteamento*: no grafo,
uma linha lotada continuava tão atraente quanto uma vazia. Então o `Evaluator` roda
o roteamento duas vezes:

1. passe 1 com espera nominal → mede `aBordo[l]`
2. `crowd[l] = clamp(aBordo[l] / assentos[l], 1, MaxCrowdingMultiplier)`
3. passe 2 com `esperaDeEmbarque · crowd[l]`

A leitura física é direta: numa linha com o dobro de passageiros do que cabe, você
deixa um trem passar cheio e espera o próximo. Isso é o que faz um vagão a mais
valer alguma coisa **na linha que precisa dele**, e não em qualquer uma.

Custa ~2× por avaliação. `CapacityFeedback = false` desliga — mas aí os vagões
voltam a não influenciar tempo de viagem nenhum.

## Custo total (minimizar)

```
J = tempoDeViagem
  + UnreachablePenalty     · paresSemRota
  + CongestionWeight       · Σ max(0, utilLinha − 1)²
  + StationLoadWeight      · Σ max(0, utilEstação − 1)²
  + TrackLengthWeight      · comprimentoTotalDeTrilho
  + 10⁶                    · túneisAcimaDoEstoque     (rede inviável)
  + UnservedStationPenalty · estaçõesForaDaRede
  + ChangeWeight           · custoDeMexerNaRedeAtual / horizonte
```

> [!note] O modelo atual é o do [[Núcleo nativo (Rust)]] (avaliador v3, fila como
> distribuição e crescimento na plataforma; ver [[Validação do modelo]]). As
> seções acima descrevem a origem do fitness; os termos de lotação foram
> trocados pela fila acima da capacidade.

A aba **Fitness** da UI mostra essa decomposição em barras, então dá para ver de
onde o custo está vindo e ajustar o peso certo.

## Custo computacional

Nós ≈ `N · (L+1)` — numa cidade cheia, 40 estações × 8 linhas ≈ 360 nós, ~1200
arestas. Uma Dijkstra é microssegundos. São `N` Dijkstras por avaliação.

Otimizações que importam:
- adjacência em lista ligada sobre arrays, **zero alocação por avaliação**
- heap binário indexado próprio
- buffers reusados entre avaliações (por isso um `Evaluator` por thread)

## Calibração

Velocidade, capacidade e demanda **não são mais chutes**: saem de
`City.Definition.TrainDefinition` e da tabela de spawn do jogo. `DemandScale = 1.0`
significa "o que o jogo faz".

O que sobrou de knob honesto:

| Knob | Quando mexer |
|---|---|
| `DemandScale` | se `util` de linha parecer irreal frente ao jogo |
| `TrainSpeedOverride` | só para testar hipótese; `0` usa a real |
| `DwellTime`, `TransferPenalty`, `BoardingPenalty` | não têm equivalente direto no jogo, continuam estimados |
| `MaxCrowdingMultiplier` | teto da espera extra por lotação |

O cartão **Demanda** na aba Controle mostra a taxa total em passageiros/dia, de onde
veio a tabela e as maiores geradoras. Se esse número não bater com o que se vê no
mapa, o problema está no modelo — não no AG.

Ver também: [[Modelo de demanda]], [[Algoritmo Genético - design]], [[Roadmap]]
