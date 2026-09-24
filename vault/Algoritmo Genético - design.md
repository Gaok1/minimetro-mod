# Algoritmo Genético — design

> [!note] Histórico.
> Esta nota descreve o AG em C# original (`GaEngine`, `GenomeBuilder`,
> `Mutator`), removido em 2026-09-24. O AG de hoje é o do [[Núcleo nativo (Rust)]]:
> mesmo encoding e mesmos operadores de base, mais sentido de trem em loop,
> reparo de travessia, custo de mexer na rede e recomendação de upgrade.

## Encoding

Um indivíduo é **uma rede de metrô inteira**:

```csharp
List<List<int>> Routes;  // por linha, sequência ordenada de índices de estação
List<bool>      Loops;   // linha circular?
List<int>       Locos;   // trens por linha, mínimo 1 (nenhum é grátis)
List<int>       Cars;    // vagões por linha (o jogo espalha entre os trens da linha)
```

Índices apontam para o `Snapshot`, não para objetos do jogo.

Regras que o `Genome.Repair` garante depois de todo crossover/mutação:

- sem estação repetida dentro da mesma rota (mas a mesma estação **pode** estar em
  várias rotas — é assim que baldeação nasce)
- `MinStationsPerLine ≤ |rota| ≤ MaxStationsPerLine`
- `|Routes| ≤ linhas disponíveis`
- loop só com 3+ estações
- frota dentro do orçamento (`NormalizeFleet`)

## População inicial

Aleatório puro em roteamento é desperdício: quase todo indivíduo nasce absurdo e o
AG gasta centenas de gerações chegando onde uma heurística gulosa chega de graça.

`GenomeBuilder.RandomConstructive`:
1. escolhe *k* sementes espalhadas (k-means++ simplificado)
2. cada estação vai para a semente mais próxima → clusters
3. dentro do cluster, monta a rota por **vizinho mais próximo**
4. distribui frota

Além disso, se `SeedFromCurrentNetwork` estiver ligado, 10% da população começa
como **cópia da rede que o jogador já construiu** (com mutações leves). Assim o AG
nunca devolve algo pior que o status quo sem motivo.

## Operadores

Seleção: **torneio** de tamanho *k*.
Elitismo: os *E* melhores passam intactos (e não são reavaliados).
Crossover: **em nível de rota** — cada slot de linha do filho vem do pai A ou do B.
Rotas são unidades semânticas; trocar rotas inteiras preserva estrutura muito
melhor do que qualquer crossover posicional sobre a lista achatada.

Mutação — 10 operadores, sorteados uniformemente (`Mutator.cs`):

| # | Operador | Ideia |
|---|---|---|
| 0 | `InsertBestPosition` | insere estação na posição de menor custo adicional |
| 1 | `InsertRandom` | insere em posição aleatória (diversidade) |
| 2 | `RemoveStation` | tira uma parada |
| 3 | `SwapWithinRoute` | troca duas paradas de lugar |
| 4 | `TwoOpt` | inverte um trecho (clássico de TSP) |
| 5 | `MoveBetweenRoutes` | move estação de uma linha para outra |
| 6 | `ToggleLoop` | abre/fecha o circuito |
| 7 | `ShiftLocomotive` | tira um trem da linha folgada e põe na apertada |
| 8 | `ShiftCarriage` | idem, vagão |
| 9 | `AddOrDropRoute` | divide a maior rota ou funde a menor |

O operador 9 é o que deixa o AG **decidir quantas linhas usar**, em vez de fixar.

Os operadores 7 e 8 são **meio gulosos**: metade das vezes escolhem origem e destino
por *demanda por assento* (`RouteDemand / assentos`), metade sorteiam. Guloso puro
viraria heurística disfarçada de mutação e mataria a exploração; sorteio puro é o
que eles eram antes, e desperdiçava a informação de demanda que o `Snapshot` já tem.

## Distribuição de frota

`GenomeBuilder.DistributeFleet` dá 1 locomotiva por linha (nenhuma é grátis — ver
[[Aplicação da solução no jogo]]) e distribui a sobra para a rota com **maior
demanda por assento**, com 1 chance em 4 de escolher uma qualquer para a população
inicial não virar clone.

`Genome.NormalizeFleet`, ao estourar orçamento, corta a unidade de quem tem **menor**
demanda por assento. Antes cortava da última rota da lista — que era só a ordem em
que o crossover deixou as coisas, sem relação nenhuma com por onde as linhas passam.

Nada disso substitui o fitness: é só sair de um ponto de partida decente. A pressão
seletiva de verdade vem da realimentação de lotação em [[Função de fitness]].

## Anti-estagnação

Se passarem `StagnationRestart` gerações sem melhora, 80% da população é
substituída por indivíduos construtivos novos, mantendo os melhores. Barato e
evita convergência prematura, que é o modo de falha típico aqui.

## Paralelismo

`Parallel` não existe neste build ([[APIs ausentes - lista viva]]), então
`GaEngine.EvaluateAll` fatia a população entre `min(ProcessorCount, 8)` threads.
Cada worker tem o **próprio `Evaluator`**, porque o `Evaluator` guarda buffers
mutáveis (grafo, heap, distâncias). Zero estado compartilhado, zero lock.

A thread do AG também trabalha em vez de só esperar.

## Parâmetros

Todos em `GaConfig.cs` e editáveis na aba **Parâmetros** ([[UI e controles]]).
A config é **clonada** no início de cada run, então mexer nos sliders enquanto
roda não corrompe a execução em andamento.

Ver também: [[Função de fitness]]
