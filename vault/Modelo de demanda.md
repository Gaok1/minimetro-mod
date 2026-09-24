# Modelo de demanda

Como o Mini Metro decide **quem aparece, onde e indo para qual forma** — e como o
mod replica isso.

## A descoberta central: demanda é uma matriz origem → destino

O modelo antigo do mod supunha que o destino de um passageiro era sorteado entre as
formas presentes, proporcional a quantas estações de cada forma existiam. **O jogo
não faz nada disso.**

`StationDatabase.Load()` monta uma agenda por **forma de origem**. Cada entrada é um
`PeepSpawnType(hora, tipoDeDia, formaDeDestino, passageirosPorDia)`:

| Origem | Destino | /dia | Dia | Hora |
|---|---|---|---|---|
| CIRCLE | SQUARE | 1.0 | útil | 7 |
| CIRCLE | TRIANGLE | 1.0 | qualquer | 16 |
| CIRCLE | TRIANGLE | 0.75 | fim de semana | 11 |
| CIRCLE | CROSS | 0.25 | qualquer | 10 |
| CIRCLE | WEDGE / STAR / DIAMOND / GEM / EGG | 0.2–0.5 | fim de semana | — |
| TRIANGLE | CIRCLE | 1.0 | qualquer | 18 |
| SQUARE | CIRCLE | 1.0 | útil | 17 |
| SQUARE | TRIANGLE | 0.5 | útil | 12 |
| DIAMOND | SQUARE | 0.5 | útil | 10 |
| PENTAGON | TRIANGLE / SQUARE | 0.25 | útil | — |
| EGG | CIRCLE | **2.0** | fim de semana | 21 |

Consequências que mudam decisão de rede:

- **O fluxo é assimétrico.** Losango alimenta quadrado, mas quadrado nunca manda
  ninguém para losango. Uma linha que só liga losangos entre si não transporta nada.
- **EGG é uma bomba de fim de semana**: 2.0/dia, o dobro de qualquer outra entrada,
  e sempre para círculo.
- **O tipo de dia liga e desliga metade da tabela.** Uma rede ótima na quarta pode
  ser péssima no sábado.
- Formas de destino inexistentes na cidade são descartadas antes de virar passageiro
  (`Station.Update` checa `City.AnyStationsOfType`), então só entram no modelo as
  formas presentes.

## Os deslizes do original (que replicamos de propósito)

`StationDatabase.Load()` tem dois bugs de copy-paste:

```csharp
list3.Add(...PENTAGON, 0.25f);                    // agenda do SQUARE
list.Add(new PeepSpawnType(11, WEEKDAY, GEM, 0.1f));   // <- cai no CIRCLE
schedules.Add(StationType.SQUARE, new StationSchedule(list3));

List<PeepSpawnType> peepSpawnTypes = new List<PeepSpawnType>();   // vazia
list8.Add(...SQUARE, 0.1f);                       // <- vai pro WEDGE
list8.Add(...CIRCLE, 0.4f);                       // <- vai pro WEDGE
schedules.Add(StationType.GEM, new StationSchedule(peepSpawnTypes));
```

Ou seja: **estação GEM não gera passageiro nenhum**, e WEDGE gera duas entradas a
mais do que a tabela sugere.

> [!important] Modelamos o comportamento, não a intenção.
> O fitness precisa prever o jogo que está rodando na máquina do jogador. Se a
> réplica "consertasse" a agenda do GEM, o AG passaria a otimizar para uma demanda
> que não existe.

De qualquer forma, o caminho preferido é **ler a tabela do jogo por reflexão**
(`StationDatabase.schedules` → `StationSchedule.peepSpawnTypes`), o que mantém o
modelo certo mesmo se o jogo for atualizado. A réplica hardcoded em
`SpawnModel.BuiltIn()` é só o plano B.

## Quanto, e quando

O volume sai de `StationSchedule.CreateSpawns` × `Station.PeepSpawnScale`:

```
passageiros/dia = agenda(forma, tipoDeDia)
                × Tension(0.08)                        // Clock
                × PeepSpawnScale(cidade) × centralidade × serviço
                × PEEP_SPAWN_SCALE
```

- **`centrality`** (`Station.Centrality`, público): 1.1 no centro → 0.5 na periferia.
  Estação central gera **mais que o dobro** de uma de borda. Interpolado entre
  `StationMinCentrality = 9` e `StationMaxCentrality = 27`.
- **`service`** (`Station.Service`, público, 0.3 a 5.0): sobe quando trens coletam
  passageiros ali, cai por decaimento proporcional a `√(fila)`. **Atender bem uma
  estação faz ela gerar mais** — o jogo recompensa e pune ao mesmo tempo.
- **`Tension`**: `1 + (min(semana,24)·0.2 + diaDaSemana·0.06) · 0.08`. Cresce ao
  longo da partida e ao longo da semana.
- Na primeira semana há uma suavização: `scale·sin(dia/7) + (1−sin(dia/7))`.

**`Clock.DayLength = 20f`** — um dia do jogo dura 20 segundos reais. É essa constante
que converte "passageiros por dia" em **passageiros por segundo**, e é o que torna a
comparação com a capacidade dos trens honesta. Ver [[Função de fitness]].

## Material rodante: também não precisa ser chutado

`City.Definition.TrainDefinition` (público) dá os números reais:

- `TrainDefinition.Capacity` → passageiros por *railcar*
- `GetLocomotiveDefinition(AssetType.Locomotive).Speed` → velocidade real
- também `Acceleration` / `Deceleration`, que o mod ignora (o modelo usa velocidade
  média, não perfil de aceleração)

Locomotiva e vagão são ambos `Railcar` e têm a **mesma** capacidade
(`Railcar.Capacity => train.Definition.Capacity`). Então:

```
assentos(linha) = (locomotivas + vagões) × RailcarCapacity
```

`Line.AddCarriage` distribui o vagão para o trem com **menos railcars** da linha, o
que valida modelar vagão como um número por linha em vez de por trem.

## Outros achados da base

- **Ponte é túnel.** `AssetType.Bridge` não é um recurso separado: `AssetButton` e
  `AssetCircle` trocam só o ícone quando `CityDefinition.CrossingStyle == Bridge`.
  O orçamento consumido é sempre `AssetType.Crossing`, um por `Link` que cruze.
- **Limite de trens é por linha**, não global: `City.GetMaxLocomotivesPerLine(index)`.
  Cidades UGC definem linha a linha; `Game.MaxLocomotivesPerRoute` só coincide por
  padrão. O `Snapshot` guarda o array `MaxLocosAt`.
- **Limite de vagões por linha é 64** (`Constants.MAX_CARRIAGES_PER_ROUTE`) — muito
  acima do que qualquer estoque real permite, então na prática não amarra nada.
  `GaConfig.MaxCarsPerLoco` é uma rédea *nossa*, para o espaço de busca, não uma
  regra do jogo.
- **Interchange** só muda a capacidade da estação para
  `CityDefinition.InterchangeCapacity`. Como o mod já lê `Station.PeepCapacity`
  direto, isso entra sozinho.
- **`SpawnController.HandleStationOverflow`** é sobre *nascimento de estações novas*
  e sobre "breaches" (surtos de passageiros em estação quase cheia), não sobre spawn
  normal. O mod não modela isso: é um evento raro e reativo.

Ver também: [[Engenharia reversa - API do Mini Metro]], [[Função de fitness]]
