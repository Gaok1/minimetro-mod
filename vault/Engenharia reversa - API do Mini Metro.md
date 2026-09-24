# Engenharia reversa — API do Mini Metro

O `Assembly-CSharp.dll` **não é ofuscado**. Nomes de classe, método e campo estão
inteiros. Isso é o que torna o mod viável.

Para navegar:

```bash
dotnet tool install -g ilspycmd
ilspycmd -p -o ./src "<jogo>/MiniMetro_Data/Managed/Assembly-CSharp.dll"
```

São ~900 classes. As que importam:

## Chegando na partida

Não existe singleton público de `Game`. O caminho:

```
Main.Instance                      // public static Main
  └─ private IController controller
       └─ GameController
            └─ private Game game
```

Ambos os campos são privados → `GetField(..., NonPublic | Instance)`.
Implementado em `GameHook.Refresh()`.

## `Game`

| Membro | Uso no mod |
|---|---|
| `bool IsPaused { get; set; }` | o botão de pausa |
| `bool IsLocked { get; set; }` | bloqueia a entrada do jogo enquanto o mouse está na UI |
| `City City` | tudo do estado |
| `LineBuilder LineBuilder` | construir linhas ([[Aplicação da solução no jogo]]) |
| `AssetDatabase AssetDatabase` | orçamento de trens, vagões, túneis |
| `int Score`, `int Week`, `GameMode Mode` | cabeçalho da UI |
| `int MaxLocomotivesPerRoute` | limite por linha |

## `City`

| Membro | Observação |
|---|---|
| `private Station[] stations` | **privado**, indexado por id, com buracos (`null`) |
| `int LineCount`, `Line GetLine(int)` | públicos |
| `Line AddLine(...)`, `void RemoveLine(Line)` | públicos |
| `public List<Obstacle> obstacles` | campo público; rios/água |
| `int MaxLineCount` | limite da cidade |
| `CityLayer CityLayer` | `LocalToGlobal` / `GlobalToLocal` |

Não há acessor público que devolva todas as estações — só `GetStationsOfType`.
Por isso `GameHook.GetAllStations` lê o campo privado por reflexão.

## `Station`

`Position` (local da `CityLayer`), `Type` (`StationType`, enum `[Flags]`),
`Id`, `PeepCount`, `PeepCapacity`, `IsInterchange`, `IsActive`, `IsGhost`,
`IsDisabled`, `Lines`, `Neighbours`, `Centrality`, `Service`.

`Centrality` e `Service` são os dois multiplicadores de geração de passageiros, e
ambos são **públicos** — ver [[Modelo de demanda]]. `PeepSpawnScale` (privado) é só
o produto dos dois com a escala da cidade.

`StationType`: `CIRCLE=1, TRIANGLE=2, SQUARE=4, CROSS=8, DIAMOND=0x10, EGG=0x20,
GEM=0x40, PENTAGON=0x80, STAR=0x100, WEDGE=0x200`.

## `Line` e `Link`

- `Line` é uma lista de `Link`; indexador `line[i]`.
- Cada `Link` tem `Start`/`End` (`LinkStop`), e `LinkStop.Station`.
- Percorrer os links dá a sequência de estações (`Snapshot.ExtractRoute`).
- `Line.IsLooping`, `Line.Index`, `Line.IsMothballed`.
- `Line.Remove()` → `City.RemoveLine` → `Release()`, que devolve o asset da linha.
- `Line.ApplyAsset(AssetType, ...)` adiciona trem/vagão **e já cuida** de checar
  disponibilidade e consumir do `AssetDatabase`. Retorna `false` se não deu.

## `AssetDatabase`

`GetTotalAssets(AssetType)`, `GetAvailableAssets(AssetType)`,
`AvailableLocomotiveAsset`.

`AssetType`: `Line, Locomotive, Shinkansen, Tram, Ferry, Carriage, Crossing,
Interchange, Bridge`. `Crossing` é o túnel.

> [!info] `Bridge` não é um recurso separado.
> É o mesmo `Crossing` com outro ícone, escolhido por
> `CityDefinition.CrossingStyle`. `AssetButton` e `AssetCircle` fazem a troca só na
> apresentação; quem é consumido e liberado (`Link.ReserveCrossing` /
> `ReleaseCrossing`) é sempre `AssetType.Crossing`, um por `Link` que cruze água.

## `StationDatabase`, `StationSchedule`, `PeepSpawnType`

A tabela de geração de passageiros, **hardcoded** em `StationDatabase.Load()`.
`StationDatabase.Instance` é público; `schedules` e `peepSpawnTypes` são privados
(o mod lê por reflexão). É a fonte da matriz origem→destino — ver
[[Modelo de demanda]], que também lista os dois bugs de copy-paste do original.

## `TrainDefinition` / `LocomotiveDefinition`

`City.Definition.TrainDefinition` é público e carrega os números reais:
`Capacity` (por railcar), e por tipo de locomotiva `Speed`, `Acceleration`,
`Deceleration`, `Capacity`. Carregado de `Trains/default` e `Trains/small` (JSON de
asset, não código).

## `Clock`

`Day`, `DayOfTheWeek`, `Week`, `Hour`, `DayType`, e `Tension(scale)` — todos
públicos. **`DayLength => 20f`**: um dia do jogo dura 20 segundos reais. É a
constante que converte a agenda de spawn (passageiros/dia) para passageiros/segundo.

> [!danger] O trem da linha nova **não é de graça**.
>
> `Line.AddLink`, quando `LinkModify == NEW_LINE`, faz:
>
> ```csharp
> assetType = game.AssetDatabase.SpecificAvailableLocomotiveAsset(...);
> if (assetType != AssetType.None && ...) {
>     game?.AssetDatabase.ConsumeAsset(assetType);   // <-- sai do estoque
>     ...
> } else if (flag) {
>     isWaitingForLocomotive = true;                 // <-- linha nasce parada
> }
> ```
>
> Logo o orçamento de trens é `locomotivasNoInventario`, **e ponto**. Somar um
> brinde por linha conta cada linha duas vezes, e o AG passa a propor uma rede a
> mais do que dá para operar. Ver [[Diário de decisões]].
>
> `GetTotalAssets` = tudo que a cidade já concedeu (disponível + em uso).
> Como aplicar apaga as linhas antes — e `RemoveTrain` chama `ReleaseAsset` para o
> trem e para cada vagão — o orçamento pós-limpeza é exatamente o `Total`.

`Line.ActiveTrainCount` conta os trens não-mothballed de uma linha. É por ele que
o `Applier` descobre quantos trens a linha realmente ganhou, em vez de supor.

## `Obstacle`

`Contains(Vector2)`, `IsDecoration`, `GetCrossings(...)`.
O mod amostra 12 pontos ao longo do segmento e usa `Contains` para decidir se a
aresta gasta um túnel/ponte. Não é exato como o `LinkFootprint` do jogo (que usa
os tracks em L), mas erra pouco para efeito de fitness.

Ver também: [[Aplicação da solução no jogo]]
