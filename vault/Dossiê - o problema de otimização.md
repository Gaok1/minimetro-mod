# Dossiê — o problema de otimização do Mini Metro

Tudo o que o jogo faz e que entra, ou deveria entrar, no problema que o AG
resolve. **Fonte primária: o próprio jogo.** O `Assembly-CSharp.dll` do build
Linux (Unity 2022.3.62f2) foi decompilado inteiro com `ilspycmd` 9.1 (1344
arquivos). Os dados que não estão no código (trens, cidades, rios, upgrades)
foram extraídos do `resources.assets` com os scripts de `tools/citydump/`. A
internet entra só como checagem cruzada, e onde ela diverge, vale o código.

Levantado em 2026-09-24. Os nomes `Classe.Membro` citados existem nesta versão e
dá para ir direto neles no código decompilado.

> [!summary] As 14 coisas que mais mudam a otimização
> 1. **O futuro da cidade já está sorteado.** Na largada, `CityPlanner.ScheduleStationSpawns`
>    cria **todas** as estações futuras (até 128) em `City.stations`, inativas, com
>    posição, forma e `Station.ActiveTime` definidos. Dá para otimizar olhando para
>    as estações que ainda vão nascer. (§5)
> 2. **A demanda não é "proporcional ao número de formas".** Ela vem de uma tabela
>    fixa origem→destino, com hora do dia e tipo de dia (útil × fim de semana).
>    Quadrado, pentágono e losango **não geram ninguém no fim de semana**. (§3)
> 3. **Depois que a última estação nasce, a demanda cresce sem teto:** +5% a cada
>    6 s de jogo (+117%/semana). É o que garante o fim de toda partida. (§3.5)
> 4. **No Clássico, `service` não afeta o spawn** (`DoesServiceAffectPeepSpawns`
>    só vale em Zen e Sandbox). A centralidade afeta (de 0,5 a 1,1). (§3.4)
> 5. **Game over = 45 s + 2 s de lotação acumulada**, e o relógio **só corre sem
>    trem parado na estação**. Ele regenera na mesma taxa quando a fila cai. (§4)
> 6. **Estourar uma estação dispara surtos nas outras quase cheias** ("breach",
>    `SpawnController`). Uma estação no limite contamina a rede. (§4.3)
> 7. **Embarque é serial:** 1 passageiro por pulso (≈0,42 s) **por trem**, não por
>    vagão. Vagão dá lugar, não dá velocidade. Interchange embarca todo mundo em
>    um pulso. (§6.3)
> 8. **Trem pula estação** em que ninguém sobe nem desce: sem frenagem, sem dwell. (§6.4)
> 9. **Cruzamento de linhas custa tempo:** 40% da velocidade num raio de 30
>    unidades de cada interseção de trilhos, ≈ +1,3 s por passagem. (§6.2)
> 10. **Túnel e ponte não reduzem velocidade.** Custam só o recurso, 1 por link que
>     cruza água, e a água é decidida pela costa exata (`ObstacleHull`). (§8)
> 11. **Passageiro roteia sozinho, com A* por tempo, a cada parada**, com penalidade
>     de baldeação 5 e custo de parada 3,5. O destino é a estação da forma **mais
>     próxima em tempo**, não em distância. (§7)
> 12. **Trilho é octilinear** (reta + diagonal a 45°), não euclidiano: o
>     comprimento é até 8% maior que a distância em linha reta. (§8.1)
> 13. **Upgrades são uma decisão semanal**, com limites: locomotivas por linha
>     `= 4 + (7 − linhasDaCidade)`, e a oferta de túnel é forçada quando o estoque
>     é 0. (§9)
> 14. **Estação pode mudar de forma no meio do jogo** (50% das especiais
>     WEDGE/STAR/CROSS substituem uma CIRCLE/TRIANGLE/SQUARE existente). O agendamento
>     é legível em `Station.ScheduledType`/`ScheduledTypeChangeTime`. (§5.3)

---

## 1. Unidades e relógio

| Grandeza | Valor | Onde |
|---|---|---|
| 1 dia | **20 s** de jogo | `Constants.DAY_LENGTH`, `Clock.DayLength` |
| 1 hora | 20/24 = **0,8333 s** | `Constants.HOUR_LENGTH` |
| 1 semana | 140 s | 7 dias |
| Semana começa | segunda, `Day % 7 == 0` | `Clock` (`dayTypes[0] = MONDAY`) |
| Dia útil / fim de semana | seg–sex / sáb–dom | `DayType.WEEKDAY = 31`, `WEEKEND = 96` |
| Pulso de passageiro | `PeepPulsePeriod · 5/6 s` ≈ **0,4167 s** (0,5 h, padrão) | `City.PeepPulsePeriod` (público; vem do `AudioLoadout` da cidade) |
| Distância | unidades do `CityLayer` (estação tem 36 de diâmetro) | `Constants.STATION_SIZE` |

Toda a simulação usa `Clock.Time`, que avança com `TimeInterval.ScaledDelta`. Por
isso a velocidade de jogo (pausa, acelerar) não muda nada do modelo: **tudo em
segundos de jogo**.

`Clock.Tension(scale) = 1 + (min(Semana, 24)·0,2 + [dia útil] DiaDaSemana·0,06) · scale`.

---

## 2. Objetivo do jogo, formalmente

- **Pontuação** = número de viagens entregues (`Game.IncreaseScore` → `Score++`, uma
  por passageiro que chega numa estação da sua forma, em `Station.AddPeep`).
- **Fim (Clássico/Extremo)**: a primeira estação com `StationTimer.Expired`
  (`City.OvercrowdedStation` em `Game`), ver §4.
- Então o problema real não é "minimizar tempo médio de viagem". É:
  **maximizar a vazão entregue sujeito a nenhuma fila acumular 47 s de excesso**,
  num horizonte em que a demanda cresce sem parar. Tempo de viagem entra só
  porque passageiro a bordo é assento ocupado e fila parada.

---

## 3. Demanda de passageiros

### 3.1 A tabela (hardcoded em `StationDatabase.Load`)

Cada linha é um `PeepSpawnType(hora, tipoDeDia, formaDeDestino, passageirosPorDia)`.
Listado **como o jogo executa**, com os dois deslizes de copy-paste do original
(ver [[Modelo de demanda]]):

| Origem | Destino | /dia | Dia | Hora |
|---|---|---|---|---|
| CIRCLE | SQUARE | 1,0 | útil | 7 |
| CIRCLE | TRIANGLE | 1,0 | todos | 16 |
| CIRCLE | TRIANGLE | 0,75 | fim de sem. | 11 |
| CIRCLE | CROSS | 0,25 | todos | 10 |
| CIRCLE | WEDGE | 0,5 | fim de sem. | 14 |
| CIRCLE | STAR | 0,25 | fim de sem. | 9 |
| CIRCLE | DIAMOND | 0,25 | fim de sem. | 11 |
| CIRCLE | GEM | 0,2 + 0,2 | fim de sem. | 9 e 13 |
| CIRCLE | EGG | 0,4 | fim de sem. | 15 |
| CIRCLE | GEM | 0,1 | útil | 11 ← **deslize** (era do SQUARE) |
| TRIANGLE | CIRCLE | 1,0 | todos | 18 |
| TRIANGLE | CIRCLE | 0,75 | fim de sem. | 15 |
| TRIANGLE | STAR | 0,25 | fim de sem. | 15 |
| TRIANGLE | PENTAGON | 0,25 | fim de sem. | 9 |
| SQUARE | CIRCLE | 1,0 | útil | 17 |
| SQUARE | TRIANGLE | 0,5 | útil | 12 |
| SQUARE | DIAMOND | 0,25 | útil | 10 |
| SQUARE | PENTAGON | 0,25 | útil | 13 |
| PENTAGON | TRIANGLE | 0,25 | útil | 9 |
| PENTAGON | SQUARE | 0,25 | útil | 14 |
| DIAMOND | SQUARE | 0,5 | útil | 10 |
| STAR | TRIANGLE | 0,5 | útil | 12 |
| STAR | CIRCLE | 0,5 | fim de sem. | 15 |
| CROSS | CIRCLE | 0,25 + 0,25 | todos | 4 e 16 |
| WEDGE | CIRCLE | 0,25 + 0,25 | todos | 8 e 16 |
| WEDGE | CIRCLE | 0,5 | fim de sem. | 13 |
| WEDGE | SQUARE | 0,1 | útil | 14 ← **deslize** (era do GEM) |
| WEDGE | CIRCLE | 0,4 | fim de sem. | 20 ← **deslize** (era do GEM) |
| GEM | — | 0 | — | **GEM não gera ninguém** |
| EGG | CIRCLE | 2,0 | fim de sem. | 21 |

Soma por estação com centralidade 1, semana 0 e todas as formas de destino
presentes:

| Origem | Dia útil (/dia) | Fim de semana (/dia) |
|---|---|---|
| CIRCLE | 2,35 | 3,80 |
| TRIANGLE | 1,00 | 2,25 |
| SQUARE | 2,00 | **0** |
| PENTAGON | 0,50 | **0** |
| DIAMOND | 0,50 | **0** |
| STAR | 0,50 | 0,50 |
| CROSS | 0,50 | 0,50 |
| WEDGE | 0,60 | 1,40 |
| GEM | 0 | 0 |
| EGG | 0 | 2,00 |

Um círculo central num dia útil gera ≈ 2,35 / 20 s ≈ **1 passageiro a cada 8,5 s**.

### 3.2 Quando cada passageiro aparece (`StationSchedule.CreateSpawns` + `Station.UpdatePeepSpawns`)

- Na **virada de cada dia** a estação gera a lista do dia inteiro.
- Para cada entrada: `n = (int)x`, mais 1 com probabilidade `frac(x)`, onde
  `x = porDia · Tension(0,08) · escala`. O valor esperado é exatamente `x`.
- Os `n` passageiros saem nas horas `hora, hora+2, hora+4…` (1,67 s entre eles),
  então **a demanda chega em rajadas com hora marcada**: ~7 h (dia útil, círculo→quadrado),
  12–13 h (quadrado), 16–18 h (volta pra casa).
  Uma rede boa às 7 h pode estourar às 17 h.
- Entrada com hora 0 (ou já passada quando o dia vira) é descartada naquele dia.

### 3.3 Passageiros que o jogo descarta antes de aparecer (`Station.ScheduleAudioEvents`)

- A forma de destino não existe na cidade (`City.AnyStationsOfType`).
- A estação já tem **18** esperando (contando os agendados): teto duro de spawn.
- Só em Zen/Sandbox/FAQ: limite de transbordo (`MaxStationOverflow`).
- **Sufocados:** se a estação recebeu um "stifle" de um breach (§4.3), perde um spawn.

### 3.4 A escala de cada estação (`Station.PeepSpawnScale`)

```
escala = City.PeepSpawnScale · centralidade · [service, só Zen/Sandbox]
City.PeepSpawnScale = CityPlanner.PeepSpawnScale · CityDefinition.PassengerSpawnScale
```

- **Centralidade** (fixa, calculada ao posicionar a estação):
  `c = clamp(√dist(estação, CityCentre), 9, 27)` e
  `centralidade = 1,1 − 0,6·(c − 9)/18`. Então é **1,1 até 81 unidades** do centro
  e **0,5 além de 729**. `CityCentre` é a média das estações iniciais. O valor é
  público em `Station.Centrality`, e o mod já lê.
- **`service`** (0,3–5,0): sobe +0,075 por embarque de quem nasceu ali e decai com
  `max(0, 0,03√fila − 0,0285)`/s. **No Clássico e no Extremo não entra no spawn**
  (`Game.DoesServiceAffectPeepSpawns` só vale em Zen/Sandbox). O `Snapshot` já
  respeita isso.
- **Primeira semana**: se `escala > 1`, ela é suavizada por `sin(dia/7)`.
- `PassengerSpawnScale` por cidade: São Paulo 1,7, Osaka 1,1, Seul 1,05,
  Mumbai 0,95, Xangai 0,8; o resto 1,0 (ver §11).

### 3.5 O multiplicador que acaba com a partida (`CityPlanner.PeepSpawnScale`)

```
PeepSpawnScale = 1 + 0,05 · boosts
               + [depois da última estação] 0,05 · (t − tÚltimaEstação) / 6
```

- **Depois que a última estação nasce**, a demanda cresce **+0,05 a cada 6 s**, ou
  seja +0,167/dia e **+1,17/semana**. Uma semana depois de a cidade "encher", todo
  mundo gera o dobro. Esse é o relógio de fim de jogo.
- Antes disso, cada janela de 6 h (5 s) sem conseguir posicionar estação depois do
  dia 5 já agenda um boost de +0,05 (`peepSpawnBoostTimes`).
- `lastStationSpawnTime` e `peepSpawnBoostTimes` são privados do `CityPlanner`, que
  por sua vez é privado de `City`. Dá para ler por reflexão.

`Tension(0,08)` cresce devagar: no máximo **1,40** (semana 24, sexta).

---

## 4. Estação, lotação e fim de jogo

### 4.1 Capacidade

- `StationCapacity` 6 (Paris/Mumbai 4, Seul 5); interchange 18 (12/15).
  `Station.PeepCapacity` já devolve o valor certo.
- Passageiros acima da capacidade continuam lá (até 18 visíveis). Eles só
  disparam o timer.

### 4.2 O timer (`Station.Update` + `StationTimer`)

- Quando `fila > capacidade`, nasce um `StationTimer(45 s, carência 2 s)`.
- O tempo **só desce se não houver trem parado na estação** (`!IsTrainAtStation`).
  Trem parado congela o relógio mesmo que ninguém caiba.
- Quando `fila ≤ capacidade`, ele **recarrega na mesma taxa** (1 s/s) e some ao
  encher de novo.
- `Expired` em −2 s: **47 s de excesso líquido sem trem = fim**.

Modelo certo para fitness: é uma **integral**, não um limiar. O que importa é
`∫ [fila > cap ∧ sem trem] dt − ∫ [fila ≤ cap] dt`, limitado em [0, 47].

### 4.3 Breach: a cascata (`SpawnController.HandleStationOverflow`)

Quando uma estação **começa** a lotar:

1. `k = ⌊ média(fila/cap das estações) / 0,19 ⌋ − (#estações já acima da capacidade)`.
2. Pega até `k` estações **quase cheias** (`fila ∈ [cap−1, cap]`, sem breach anterior),
   preferindo linhas diferentes da que lotou.
3. Cada uma recebe `cap − fila + 1` passageiros extras nas próximas 1–3 horas, o que
   **a joga acima da capacidade**. Para compensar, o mesmo número de spawns é
   "sufocado" em estações vizinhas ao longo de uma linha que passa por ela.

Consequência: estações que vivem perto do limite são um risco sistêmico, mesmo que
nunca estourem sozinhas. A fitness deve punir a **ocupação média alta**, não só
o estouro.

### 4.4 Outros fatos

- Passageiro **não desiste** no Clássico (`DoPeepsExpire` só Zen/Sandbox: 30–40 s).
- Estação sem linha e com fila mostra aviso depois de 8 s (só visual).
- Os upgrades de estação (`MAX_STATION_UPGRADES`, `LOAD_TIME_SCALE_PER_UPGRADE`,
  `RAILCAR_SPEED_BOOST`, …) são **código morto** nesta versão: nenhuma referência.

---

## 5. Surgimento de estações

### 5.1 Tudo é agendado na largada

`City` → `planner.ScheduleStationSpawns(game)` roda **uma vez** no começo (e de novo
só se mudar a área jogável/cenário). Ele percorre as agendas da cidade até que
nenhuma forma caiba mais, chamando `CityPlanner.CreateStation(hora, forma)`, que
chama `City.AddStation(tipo, hora, posição)`. As estações futuras ficam em
`City.stations` (array de **128**, privado) com `IsActive = false`,
`ActiveHour`/`ActiveTime` públicos. Elas **já aparecem na tela 1,333 s antes**
(20 s no Extremo) e ativam em `ActiveTime`.

**Para o otimizador isso é ouro:** dá para planejar a rede para as próximas N
estações em vez de reagir. Hoje o `GameHook.GetAllStations` descarta todas com
`!IsActive`.

Detalhes que alteram o futuro:

- Se no momento de ativar um trilho estiver sendo editado sobre o ponto,
  a ativação é empurrada 1 hora (`City.CanStationActivate`).
- **Estação que nasce em cima de um trilho é inserida na linha automaticamente**
  (`City.ActivateStation` → `GetOverlappingLinks` → `line.InsertLink`). Linha que
  passa por onde vai nascer estação ganha uma parada extra de graça (ou à força).

### 5.2 Ritmo (`ScheduleDefinition`)

Cada cidade tem blocos `{numDays, randomness, types[]}`. As `types` do bloco são
espalhadas uniformemente no bloco (`numDays·24/len` horas entre elas), com
`randomness` misturando posição regular e sorteada. **O último bloco se repete**
até a cidade encher. Em Londres: 5 estações nos 7 primeiros dias, 9 nos 13
seguintes, depois 10 a cada 16 dias (0,62/dia). `SPECIAL` vira a próxima forma de
uma lista embaralhada por partida (Londres: PENTAGON, DIAMOND, STAR, CROSS, WEDGE,
uma de cada).

### 5.3 Onde e que forma (`CityArea`)

- A cidade é dividida em áreas (5–14) com grade de pontos candidatos, densidade e
  pesos por forma (`StationSpawnDefinition{peso, máximo, diaAtivo}`).
- Distância mínima entre estações: `180 · StationSeparationScale · densidadeDaÁrea`
  (DMZ), filtrada também pela área jogável no instante do spawn (a câmera abre
  com o tempo). O filtro é nativo (`NativeExtensions.filterPositions`), mas não
  precisa ser replicado, porque o resultado já está gravado nas estações futuras.
- **Troca de forma:** quando sai uma especial WEDGE/STAR/CROSS, há 50%
  (`SPECIAL_STATION_REPLACEMENT_CHANCE`) de ela **substituir** uma
  CIRCLE/TRIANGLE/SQUARE existente (de área com mais de uma daquela forma, sorteada
  por centralidade) via `Station.ScheduleTypeChange`. O novo tipo e a hora ficam em
  `Station.ScheduledType` e `ScheduledTypeChangeTime` (públicos).

---

## 6. Trens

### 6.1 Números (TextAssets `Trains/default` e `Trains/small`)

| Conjunto | Tipo | Velocidade | Aceleração | Desaceleração | Lugares/railcar |
|---|---|---|---|---|---|
| default | Locomotive | **130** | 55 | 110 | 6 |
| default | Shinkansen | 550 | 110 | 300 | 6 |
| default | Tram | 80 | 200 | 300 | 6 |
| small (Cairo, Mumbai) | Locomotive | 105 | 55 | 110 | **4** |
| small | Shinkansen | 240 | 80 | 200 | 4 |
| small | Tram | 60 | 300 | 300 | 4 |

(Unidades/s e unidades/s². O `Snapshot` antigo supunha 55 como velocidade; o valor
lido em runtime é que vale, e o mod já lê.)

Perfil: acelera até o topo, freia para parar (`Locomotive.UpdateSpeed`). Ir de
parado ao topo leva 2,36 s e 154 u; parar do topo leva 1,18 s e 77 u. **Cada parada
custa ≈ 1,77 s** a mais que passar direto (fora o dwell). Link com menos de 230 u
nem chega na velocidade máxima.

### 6.2 Cruzamentos de linha

`Link.UpdateCrossings` guarda cada interseção do link com **qualquer outro link
ativo de qualquer linha**. Links que saem da mesma estação na mesma direção não
contam. Dentro de ±30 u de cada interseção a velocidade máxima cai para **40%**
(`Train.UpdatePosition`), com frenagem antecipada.
Com os números do trem padrão, **cada cruzamento custa ≈ +1,33 s por passagem**.
Uma rede cheia de "X" é mensuravelmente mais lenta.

### 6.3 Parada e embarque (`Train.ScheduleAudioEvents`)

- Em cada **pulso** (≈0,42 s): primeiro desembarque, depois embarque.
  **1 passageiro por pulso por trem** no Clássico/Extremo. Em Zen/Sandbox é 1 por
  railcar (`DoCarriagesEmbarkInParallel`).
- **Interchange ou cidade com `IsEmbarkingQuick` (Seul)**: **todos** descem e sobem
  no mesmo pulso.
- Parte no pulso seguinte ao último embarque.
- Um trem que para com `k` desembarques e `m` embarques fica ≈ `(k+m+1) · 0,42 s`.
  Em hub movimentado isso domina o tempo de ciclo.
- **Espaçamento:** numa parada, se a linha tem mais de um trem e a distância até o
  próximo trem é menor que `0,8 · comprimentoDaLinha / trens`, o trem **segura**.
  O jogo regula o headway sozinho, então `headway ≈ ciclo/trens` é um modelo
  honesto.
- Terminal de linha aberta: o trem inverte (`link.Reverse()`), sem custo extra além
  da parada. Linha circular (loop): o trem segue em frente.
- Vagão novo vai para o trem com **menos** railcars da linha
  (`Line.AddCarriage`). Até 64 por linha: não amarra nada.

### 6.4 Pular estação (`Train.WillAnyPeepsTransferAtStation`)

O trem **só para** se alguém a bordo desce ali (destino ou baldeação), se tem
reserva, ou se há passageiro na plataforma cujo caminho usa **este trem para o
próximo trecho** e ainda há lugar. Senão ele passa direto, sem frear e sem dwell.
Estação a mais numa linha custa pouco se pouca gente usa.

---

## 7. Como o passageiro escolhe o caminho (A* do jogo)

Implementação: `AStar`, `AStarSearch`, `AStarNode`, `AStarConnection`.
A mesma coisa foi descrita pelos desenvolvedores num tópico da Steam (link no fim).

- **Grafo:** nó = (estação, **trem específico**). Aresta = o próximo trecho que aquele
  trem vai fazer. `City.UpdateConnectivity` monta a cadeia de conexões de cada trem
  a partir da posição atual dele, percorrendo a linha inteira (ida e volta).
- **Custo = tempo estimado de chegada**, calculado por trem
  (`Train.UpdateConnectionEstimates`): tempo até o trem chegar, mais o tempo de
  parada estimado (`PATH_STATION_LOADING_COST = 3,5 s`, ou 0/0,875/1,75 s nas duas
  próximas paradas conforme a fila) e `comprimentoDoLink / velocidade`.
  Se o trem passa antes de o passageiro poder pegar, soma um ciclo (`EstimatedCycleTime`).
- **Baldeação:** +**5** (`PATH_ROUTE_CHANGE_COST`) de penalidade por troca de trem.
- **Lotação:** só no primeiro trecho. Se o trem que está chegando não tem lugar
  (livres + desembarques − reservas ≤ 0), o custo vira o **próximo ciclo** dele.
  Passageiro **reserva** lugar no trem escolhido.
- **Heurística:** distância euclidiana até a estação mais próxima da forma-alvo,
  dividida pela maior velocidade de trem que serve a estação.
- **Objetivo:** o primeiro nó retirado cuja forma é a do passageiro. Ou seja, ele vai
  para a estação daquela forma **mais rápida de alcançar** agora, não para uma
  fixa. Não revisita estação.
- **Replaneja a cada parada**, tanto de quem está na plataforma quanto de quem está
  a bordo, então muda de ideia se a rede mudou.
- Sem caminho: fica esperando (e conta para a lotação).

Implicação: o **Dijkstra estação×linha** do `Evaluator` é uma aproximação boa da
média desse A*, desde que use tempo e não distância, penalidade 5 por baldeação,
e espera ≈ headway/2 no embarque.

---

## 8. Geometria, rios e travessias

### 8.1 Trilho octilinear

`LinkBuilder.ConfigureLink`: um link liga duas estações com **um segmento reto e um
diagonal** (múltiplos de 45°). Se Δx, Δy ou |Δx|=|Δy| alinham, é reto.
Comprimento:

```
L = |dx| + |dy| − (2 − √2) · min(|dx|, |dy|)       ( ≥ distância euclidiana, até +8,2% )
```

(Mais cantos arredondados de raio 24, que encurtam um pouco.) Cada estação tem 8
direções e até **3 links por direção** (`MAX_LINKS_PER_DIRECTION`).

Um link inclinado tem duas formas possíveis (diagonal primeiro ou reta primeiro)
e variações de plataforma. `LinkBuilder.ConfigureLink` ordena os candidatos
(`LinkConfiguration.CompareTo`) por: plataforma livre → **sem túnel** → **menos
cruzamentos de linha** → alinhamento com o trecho anterior. Consequência para o
modelo: **o link só gasta túnel se as duas formas cruzam água**, e entre as que
não cruzam o jogo escolhe a que cruza menos trilhos.

### 8.2 Água

- A decisão "este link cruza água?" é `City.HasCrossingSection(footprint)` →
  `ObstacleHull.DoesCross`: algum dos **segmentos do footprint octilinear** cruza
  algum **segmento de costa** da `ObstacleHull` (lista plana de `LineSegment`, com
  uma árvore de bounding boxes por cima). É pré-assada no arquivo da cidade e fica
  em `CityDefinition.ObstacleHull` (campo privado `obstacleHull`, com `lines` dentro).
- **1 recurso de travessia por link que cruza** (`Link.ReserveCrossing`), não importa
  quantas vezes cruze. Ponte = túnel (`AssetType.Crossing`; `Bridge` é só o ícone,
  por `CrossingStyle`).
- **Não há penalidade de velocidade na água** (`TUNNEL_SPEED_SCALE = 1`; a
  desaceleração de §6.2 é só para cruzamento de trilhos).
- Trecho submerso mínimo de 40 u (`MIN_TUNNEL_LENGTH`), o que só ajusta a
  geometria.
- O mod hoje amostra 12 pontos na **reta** entre estações contra `Obstacle.Contains`.
  Isso erra em dois pontos: o trilho real é octilinear e pode contornar (ou pegar)
  uma margem que a reta não pega; e a fonte de verdade é a hull, não os polígonos
  visuais. O critério certo: **as duas formas octilineares cruzam a hull** (§8.1).
- Balsas (`Ferry`, `LinkType.Nautical`) existem no código, mas **nenhuma cidade
  oficial desta versão oferece Ferry** nas listas de upgrade.

---

## 9. Recursos e upgrades

### 9.1 Estoque

- `AssetDatabase`: `GetTotalAssets` (concedido) e `GetAvailableAssets` (livre).
- **Locomotiva por linha:** `Game.MaxLocomotivesPerRoute = 4 + (7 − MaxLineCount)`
  (Lisboa e São Petersburgo com 5 linhas → 6 trens por linha). A cidade pode
  sobrescrever linha a linha com `City.GetMaxLocomotivesPerLine`.
- A linha nova **consome** uma locomotiva do estoque (ver
  [[Engenharia reversa - API do Mini Metro]]).

### 9.2 Toda segunda-feira (`Game`, `NewAssetScreen`)

1. `pendingAssetCount++` na hora 0 do dia 0 de cada semana.
2. **Grupo 0 (locomotiva):** ganha `count` do tipo (Hong Kong 2; Osaka escolhe entre
   2 locomotivas ou 1 Shinkansen), limitado por
   `Σ máxPorLinha − locomotivasTotais`. Se todas as linhas estão no máximo, **não vem
   locomotiva**.
3. **Grupo 1:** o jogo sorteia **2 ofertas** (sem repetir, por peso) entre as
   elegíveis, e o jogador escolhe 1:
   - elegível se `AnyLeft` (máximo não atingido), `semana ≥ Week − 1`, e para `Line`
     se não passa de `LineCount`;
   - **travessia forçada:** se há travessias sobrando para dar e o estoque livre é
     **0**, 70% de chance (95% se alguma estação está sem linha) de uma das ofertas
     ser Crossing; se o estoque é **1**, 40%.

Então o otimizador também pode **recomendar qual upgrade pegar**: o AG avalia a
melhor rede com +1 linha, +1 vagão, +2 túneis ou +1 interchange e compara.

---

## 10. Modos (flags de `Game`)

| Flag | Clássico | Extremo | Zen | Sandbox |
|---|---|---|---|---|
| Estação estoura (game over) | sim | sim | não | não |
| `service` afeta spawn | **não** | **não** | sim | sim |
| Passageiro desiste (30–40 s) | não | não | sim | sim |
| Embarque paralelo por vagão | não | não | sim | sim |
| Pode remover trilho/mover recurso | sim | **não** | sim | sim |
| Preview de estação | 1,33 s | **20 s** | 1,33 s | — |
| Boost de spawn (`PassengerSpawnBoost`) | 0,05 | 0,05 | 0,01 | sem crescimento |

No Extremo a rede é **incremental por obrigação**: aplicar "apaga tudo e refaz"
não é permitido pelo jogo.

---

## 11. Cidades (extraído dos binários de `Resources/Cities`)

Colunas: capacidade de estação/interchange, `PassengerSpawnScale`,
`StationSeparationScale` (menor = estações mais densas), recursos iniciais
(Linhas/Trens/Travessias/Vagões), ritmo de estações/dia (primeiro bloco → bloco
que se repete) e o grupo 1 de upgrades semanais (`w` = peso no sorteio).

| Cidade | Linhas | Trens | Cap. est./interc. | Spawn pax | Sep. | Travessia | Inicial (L/T/Túnel/Vag) | Estações/dia (1º bloco → regime) | Upgrades semanais | Áreas | Obstáculos |
|---|---|---|---|---|---|---|---|---|---|---|---|
| addisababa | 6 | default | 6/18 | 1.00 | 0.85 | Tunnel | 3/3/2/0 | 0.71 → 0.62 | Locomotive×1; Line (máx 3) w1.0, Crossing w0.4, Carriage w1.0 | 10 | 12 |
| auckland | 7 | default | 6/18 | 1.00 | 1.00 | Tunnel | 3/3/3/0 | 0.71 → 0.62 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.4, Carriage w1.0 | 7 | 2 |
| barcelona | 7 | default | 6/18 | 1.00 | 1.00 | Tunnel | 3/3/2/0 | 0.71 → 0.62 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.4, Carriage w1.0 | 6 | 3 |
| berlin | 7 | default | 6/18 | 1.00 | 1.00 | Tunnel | 3/3/2/0 | 0.71 → 0.62 | Locomotive×1; Line (máx 4) w1.0, Crossing w0.4, Carriage w1.0 | 7 | 3 |
| boston | 7 | default | 6/18 | 1.00 | 1.00 | Tunnel | 3/3/3/0 | 0.71 → 0.57 | Locomotive×1; Line (máx 4) w0.9, Crossing×3 w0.5, Carriage w0.8, Interchange (máx 2) sem≥3 w0.4 | 10 | 1 |
| budapest | 7 | default | 6/18 | 1.00 | 1.00 | Tunnel | 3/3/2/0 | 0.62 → 0.53 | Locomotive×1; Line (máx 4) w1.0, Crossing w0.4, Carriage w1.0 | 10 | 4 |
| cairo | 6 | small | 6/18 | 1.00 | 1.00 | Tunnel | 3/3/3/0 | 0.71 → 0.69 | Locomotive×1; Line (máx 3) w1.0, Crossing×2 w0.8, Carriage w0.6 | 9 | 10 |
| canberra | 7 | default | 6/18 | 1.00 | 1.00 | Tunnel | 7/7/1/0 | 0.71 → 0.69 | Locomotive×1; Crossing×2 w0.4, Carriage w1.0 | 0 | 1 |
| chicago | 7 | default | 6/18 | 1.00 | 1.00 | Bridge | 3/3/3/0 | 0.71 → 0.62 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.4, Carriage w1.0, Interchange (máx 2) sem≥3 w0.5 | 8 | 2 |
| chongqing | 7 | default | 6/18 | 1.00 | 1.00 | Bridge | 3/3/3/0 | 0.43 → 0.44 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.4, Carriage w1.0, Interchange (máx 1) sem≥4 w0.4 | 8 | 1 |
| guangzhou | 7 | default | 6/18 | 1.00 | 1.00 | Tunnel | 3/3/4/0 | 1.00 → 0.64 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.5, Carriage w1.0, Interchange (máx 3) sem≥3 w0.6 | 9 | 6 |
| hongkong | 7 | default | 6/18 | 1.00 | 1.00 | Tunnel | 3/3/5/0 | 1.29 → 0.86 | Locomotive×2; Line (máx 4) w1.0, Crossing×2 w0.4, Carriage w1.0 | 5 | 9 |
| istanbul | 7 | default | 6/18 | 1.00 | 1.00 | Tunnel | 3/3/2/0 | 0.86 → 0.62 | Locomotive×1; Line (máx 4) w1.0, Carriage w1.0, Crossing×2 w0.4 | 8 | 1 |
| lagos | 7 | default | 6/18 | 1.00 | 1.00 | Tunnel | 3/3/5/0 | 0.71 → 0.62 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.4, Carriage w1.0, Interchange (máx 2) sem≥5 w0.5 | 6 | 7 |
| lisbon | 5 | default | 6/18 | 1.00 | 1.00 | Tunnel | 2/2/2/0 | 0.45 → 0.53 | Locomotive×1; Line (máx 5) w0.9, Crossing×2 w0.6, Carriage (máx 3) w0.6, Interchange sem≥3 w0.4 | 6 | 13 |
| london | 7 | default | 6/18 | 1.00 | 1.00 | Tunnel | 3/3/3/0 | 0.71 → 0.62 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.4, Carriage w1.0, Interchange (máx 2) sem≥3 w0.5 | 8 | 3 |
| london1960 | 7 | default | 6/18 | 1.00 | 1.00 | Tunnel | 3/3/3/0 | 0.71 → 0.62 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.4, Carriage w1.0, Interchange (máx 2) sem≥3 w0.5 | 8 | 26 |
| melbourne | 6 | default | 6/18 | 1.00 | 1.00 | Bridge | 3/3 (Tram)/3/0 | 0.57 → 0.50 | Tram×1; Line (máx 3) w1.0, Crossing×2 w0.4, Carriage w1.0 | 6 | 5 |
| montreal | 7 | default | 6/18 | 1.00 | 1.00 | Tunnel | 3/3/5/0 | 0.57 → 0.56 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.4, Carriage w1.0 | 13 | 40 |
| mumbai | 6 | small | 4/12 | 0.95 | 0.71 | Bridge | 3/3/2/3 | 0.71 → 0.56 | Locomotive×1; Line (máx 3) w1.0, Crossing w0.4, Carriage×2 w1.0 | 5 | 2 |
| nanjing | 7 | default | 6/18 | 1.00 | 1.00 | Tunnel | 3/3/3/0 | 1.00 → 0.62 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.5, Carriage w1.0, Interchange (máx 1) sem≥3 w0.2 | 12 | 6 |
| nyc | 7 | default | 6/18 | 1.00 | 1.00 | Bridge | 3/3/3/0 | 0.71 → 0.62 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.4, Carriage w1.0, Interchange (máx 2) sem≥4 w0.5 | 7 | 17 |
| nyc1972 | 7 | default | 6/18 | 1.00 | 1.00 | Bridge | 3/3/3/0 | 0.71 → 0.62 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.4, Carriage w1.0, Interchange (máx 2) sem≥4 w0.5 | 6 | 8 |
| osaka | 7 | default | 6/18 | 1.10 | 1.00 | Tunnel | 3/3/4/0 | 0.86 → 0.67 | Locomotive×2, Shinkansen×1; Line (máx 4) w1.0, Crossing×2 w0.4, Carriage w1.0, Interchange (máx 2) sem≥3 w0.4 | 9 | 21 |
| paris | 7 | default | 4/12 | 1.00 | 0.71 | Tunnel | 3/3/4/0 | 0.71 → 0.62 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.4, Carriage w1.0, Interchange (máx 1) sem≥2 w0.4 | 10 | 3 |
| paris1937 | 7 | default | 4/12 | 1.00 | 0.71 | Tunnel | 3/3/4/0 | 0.71 → 0.62 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.4, Carriage w1.0, Interchange (máx 1) sem≥2 w0.4 | 10 | 8 |
| sanfrancisco | 6 | default | 6/18 | 1.00 | 1.00 | Tunnel | 3/3/4/0 | 0.71 → 0.62 | Locomotive×1; Line (máx 3) w1.0, Crossing×2 w0.4, Carriage w1.0 | 8 | 2 |
| santiago | 7 | default | 6/18 | 1.00 | 1.00 | Tunnel | 3/3/1/0 | 0.71 → 0.56 | Locomotive×1; Line (máx 4) w1.0, Crossing w0.4, Carriage w1.0 | 7 | 2 |
| saopaulo | 6 | default | 6/18 | 1.70 | 1.60 | Tunnel | 3/3/4/0 | 0.43 → 0.44 | Locomotive×1; Line (máx 3) w1.0, Crossing×2 w0.4, Carriage w1.0 | 8 | 2 |
| seoul | 7 | default | 5/15 | 1.05 | 0.86 | Tunnel | 3/3/3/0 | 0.71 → 0.71 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.4, Carriage w1.0 | 14 | 27 |
| shanghai | 7 | default | 6/18 | 0.80 | 1.00 | Tunnel | 3/3/3/0 | 1.29 → 0.79 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.4, Carriage w1.0, Interchange (máx 3) sem≥3 w0.5 | 10 | 1 |
| singapore | 7 | default | 6/18 | 1.00 | 1.00 | Bridge | 3/3/4/0 | 0.71 → 0.56 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.4, Carriage w1.0 | 9 | 6 |
| stockholm | 7 | default | 6/18 | 1.00 | 1.00 | Tunnel | 3/3/5/0 | 0.71 → 0.56 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.4, Carriage w1.0, Interchange (máx 1) sem≥4 w0.5 | 10 | 7 |
| stpetersburg | 5 | default | 6/18 | 1.00 | 1.00 | Tunnel | 3/3/5/0 | 0.83 → 0.44 | Locomotive×1; Line (máx 2) w1.0, Crossing×2 w0.4, Carriage w1.0 | 7 | 4 |
| tashkent | 6 | default | 6/18 | 1.00 | 1.00 | Tunnel | 3/3/3/0 | 0.57 → 0.41 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.4, Carriage w0.7, Interchange (máx 2) sem≥3 w0.5 | 9 | 2 |
| tokyo | 7 | default | 6/18 | 1.00 | 1.00 | Tunnel | 3/3 (Shinkansen)/3/0 | 0.55 → 0.65 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.6, Carriage w0.8, Interchange (máx 2) sem≥3 w0.5, Shinkansen (máx 3) sem≥2 w0.1 | 8 | 17 |
| warsaw | 6 | default | 6/18 | 1.00 | 1.00 | Tunnel | 3/3/3/0 | 0.67 → 0.65 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.6, Carriage w0.8, Interchange (máx 2) sem≥2 w0.4 | 5 | 13 |
| washingtondc | 6 | default | 6/18 | 1.00 | 1.00 | Tunnel | 3/3/3/0 | 0.71 → 0.56 | Locomotive×1; Line (máx 4) w1.0, Crossing×2 w0.4, Carriage w1.0, Interchange (máx 2) sem≥3 w0.5 | 8 | 3 |

Destaques: **Seul** embarca instantâneo (`IsEmbarkingQuick`) e tem capacidade 5;
**Paris/Mumbai** estação 4; **Cairo/Mumbai** trem pequeno (4 lugares, 105 u/s);
**São Paulo** gera 70% mais por estação, com estações bem mais espaçadas;
**Tóquio** começa com um Shinkansen (550 u/s); **Melbourne** é só bonde.

Reproduzir: `tools/citydump/find_cities.py` + `parse_city.py`.

---

## 12. O que o `Evaluator` atual acerta e o que falta

| Tema | Hoje | Jogo | Impacto |
|---|---|---|---|
| Tabela de demanda | lida do jogo ✔ | — | — |
| Centralidade, tensão, `service` só em Zen | ✔ | — | — |
| Tipo de dia | só o de **hoje** | alterna útil/fim de semana | rede some com o fim de semana |
| Hora do dia | média diária | rajadas com hora marcada | pico 2–3× a média |
| Estações futuras | ignoradas | todas conhecidas | planejar em vez de reagir |
| Crescimento pós-cidade-cheia | ignorado | +117%/semana | horizonte |
| Distância | euclidiana | octilinear (+até 8%) | pequeno, grátis de corrigir |
| Cruzamento de linhas | ignorado | +1,3 s por passagem | médio |
| Parada | `DwellTime` fixo | 1,77 s + 0,42 s·pax, e pula se vazia | médio/alto em hub |
| Embarque em interchange | só capacidade | instantâneo | alto (decide onde pôr) |
| Lotação | penalidade `max(0, util−1)²` | timer integral 47 s + breach | **é o objetivo real** |
| Travessia | 12 amostras na reta | hull exata no footprint octilinear | erro de orçamento de túnel |
| Troca de forma agendada | ignorada | legível | média |
| Upgrade da semana | não decide | 2 ofertas, escolhe 1 | recomendação nova |

## 13. O que ler em runtime (lista para o código)

| Dado | Caminho | Acesso |
|---|---|---|
| Estações futuras | `City.stations[]` (inclui `!IsActive`) + `Station.ActiveTime` | privado (array), público (tempo) |
| Troca de forma | `Station.ScheduledType`, `ScheduledTypeChangeTime` | público |
| Costa exata | `CityDefinition.obstacleHull.lines` (`LineSegment[]`) | privado |
| Pulso de embarque | `City.PeepPulsePeriod` | público |
| Boost de spawn | `City.planner.lastStationSpawnTime`, `peepSpawnBoostTimes` | privado |
| Modo e flags | `Game.Mode`, `DoesServiceAffectPeepSpawns`, `MaxLocomotivesPerRoute` | público |
| Trem | `City.Definition.TrainDefinition` + `GetLocomotiveDefinition` | público |
| Embarque rápido | `CityDefinition.IsEmbarkingQuick` | público |
| Relógio de lotação | `Station.ExpiryTimerCompletion` | público |

## 14. A confirmar em runtime (não dá para ter certeza só lendo)

- O valor exato de `PeepPulsePeriod` por cidade (0,5 h é o padrão do `AudioLoadout`;
  cidades podem trocar via módulo de áudio).
- O comprimento real dos links (`Link.Length`) contra a fórmula octilinear, para
  calibrar os cantos arredondados.
- Quantas estações futuras cada partida realmente agenda (depende do sorteio e da
  área jogável, que depende da proporção da tela quando a área não é limitada).

## Fontes

- Código decompilado: `Assembly-CSharp.dll` (este build, Linux), classes citadas
  acima.
- Dados: `Mini Metro_Data/resources.assets` (TextAssets `Trains/*`, `Cities/*`).
- Desenvolvedores sobre o pathfinding (A*, reservas, preferência por rota direta):
  [Steam — How do passengers choose which station they go to?](https://steamcommunity.com/app/287980/discussions/0/627456486607812743/)
- Sobre trens pularem estações:
  [Steam — Trains just ignoring passengers](https://steamcommunity.com/app/287980/discussions/0/2552901289725324391/)
- Histórico do timer (20 s → 40 s no alpha13; hoje 45 + 2 s no código):
  [Dinosaur Polo Club — alpha13](https://dinopoloclub.com/2014/06/03/mini-metro-alpha13/)
- Capacidade 6/18 e timer, em linhas gerais:
  [Mini Metro Wiki — Station](https://mini-metro.fandom.com/wiki/Station)

Ver também: [[Modelo de demanda]], [[Função de fitness]],
[[Engenharia reversa - API do Mini Metro]], [[Algoritmo Genético - design]]
