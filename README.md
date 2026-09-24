# MiniMetroGA

Mod do **Mini Metro** que otimiza a rede de metrô com um **algoritmo genético**.

- pausa o jogo, roda o AG numa thread separada e mostra a convergência ao vivo
- UI configurável: todos os parâmetros do AG e todos os pesos do fitness em slider
- overlay com os atalhos, que pisca a tecla apertada e mostra o AG progredindo
  mesmo com o painel fechado
- aplica a melhor solução **mexendo só no que mudou**, com os gestos do próprio
  jogo: linha igual fica, estação nova é encaixada com os trens rodando, linha
  que muda de verdade é apagada como o jogador apaga (ninguém some do trem) e
  trem sobrando vai para onde falta
- o fitness cobra o custo de mexer na rede atual, então o AG não troca a rede
  inteira por 1% de ganho
- **túnel/ponte**: rede que precisa de mais travessias do que o estoque tem é
  descartada (o jogo não deixa desenhar), e o que o jogo gastou é conferido
  contra o modelo a cada aplicação
- **upgrade da semana**: na segunda-feira o núcleo otimiza a rede com cada
  oferta (linha, vagão, travessia, interchange...) e recomenda a melhor; com
  "escolher sozinho" ligado, escolhe e põe o interchange na estação indicada
- modo automático: reotimiza e aplica sozinho a cada N segundos
- **núcleo nativo em Rust** (`native/mmopt`), carregado dentro do jogo por
  P/Invoke: o AG paralelo com um modelo calibrado pelas regras reais do jogo
  (estações futuras, dia útil × fim de semana, parada/embarque, sentido dos
  trens de loop, fila como distribuição, cruzamentos, costa exata) e conferido
  contra o próprio jogo (`vault/Validação do modelo.md`). É o único otimizador:
  sem a biblioteca, o mod avisa e não otimiza.

## Atalhos

| Tecla | Ação |
|---|---|
| F8 | abre/fecha o painel |
| F9 | pausa/retoma o jogo |
| F10 | roda o AG |
| F11 | aplica a melhor solução |

## Estrutura

```
MiniMetroGA/
  bin/          MiniMetroGA.dll (o mod) + libmmopt.so / mmopt.dll (núcleo nativo)
  lib/          UnityEngine.IMGUIModule.dll não-stripada + netstandard.dll
  src/          código-fonte C#
  native/mmopt/ núcleo nativo em Rust (AG + avaliador) e o mmopt-bench
  problems/     last.bin: o último problema enviado ao núcleo (para o bench)
  tools/        StripAudit (audita uma DLL contra a BCL stripada do jogo,
                inclusive métodos de tipo genérico como List<int>),
                citydump (extrai trens/cidades/rios do resources.assets) e
                validate (compara previsto x medido no jogo)
  vault/        vault Obsidian com a documentação  ← comece por "00 - Índice"
  build.ps1     build/instalação no Windows
  build.sh      build/instalação no Linux
  linux/run.sh  lançador Linux (Doorstop via LD_PRELOAD)
  MiniMetroGA.log
```

## Compilar

```powershell
.\build.ps1            # compila e instala
.\build.ps1 -Run -Tail # compila, abre o jogo e segue o log
```

Precisa apenas do .NET SDK no PATH.

### Linux (build nativo da Steam)

```bash
./build.sh                 # baixa Doorstop + lib/, compila e instala em <jogo>/MiniMetroGA
./build.sh --run --tail    # idem, abre o jogo e segue o log
```

Depois, na Steam, em **Mini Metro → Propriedades → Opções de inicialização**:

```
"/home/<você>/.local/share/Steam/steamapps/common/MiniMetro/MiniMetroGA/run.sh" %command%
```

Se o jogo estiver em outro lugar: `./build.sh --game-dir <pasta>`. O `build.sh`
também compila o núcleo nativo com `cargo` (`--no-native` pula). No NixOS o
`dotnet` e o `cargo` vêm sozinhos via `nix shell`. Detalhes em
`vault/Linux - build nativo.md` e `vault/Núcleo nativo (Rust).md`.

### Núcleo nativo fora do jogo

```bash
cd native/mmopt
cargo run --release --bin mmopt-bench -- synth --active 40 --future 8 --week 6
cargo run --release --bin mmopt-bench -- file "<jogo>/MiniMetroGA/problems/last.bin"
```

Recomendação de upgrade num problema gravado (base, controle e cada oferta):

```bash
cargo run --release --bin mmopt-bench -- upgrade "<jogo>/MiniMetroGA/problems/last.bin"
```

Teste de ponta a ponta sem mão humana (o mod abre Londres e joga sozinho, como
o modo automático: pausa, otimiza, aplica, escolhe o upgrade):

```bash
MINIMETROGA_SELFTEST=london MINIMETROGA_SELFTEST_SECONDS=1800 \
MINIMETROGA_SELFTEST_QUIT=1 <jogo>/MiniMetroGA/run.sh
```

No NixOS, o executável da Steam precisa de `steam-run` na frente do `run.sh`.

Validação do modelo contra o jogo (congela a rede em janelas e mede o que o
modelo previu; ver `vault/Validação do modelo.md`):

```bash
MINIMETROGA_SELFTEST=london MINIMETROGA_SELFTEST_MODE=validate \
MINIMETROGA_VALIDATE_FROM_DAY=8 MINIMETROGA_SELFTEST_QUIT=1 <jogo>/MiniMetroGA/run.sh
python3 tools/validate/analyze.py "<jogo>/MiniMetroGA/validation.jsonl"
```

## Documentação

Abra a pasta `vault/` no Obsidian (ou leia os `.md` direto). Comece por
`00 - Índice.md`. Tudo que o jogo faz e que entra no problema de otimização
(spawn, rios, trens, pathfinding, upgrades, dados de cada cidade) está em
`Dossiê - o problema de otimização.md`, levantado do código decompilado, e o
quanto o modelo do AG acerta está em `Validação do modelo.md`. Se for mexer no código, leia antes
`Managed stripping - o problema central.md` — este build do jogo tem metade da
BCL removida e isso muda como tudo tem que ser escrito.

## Desinstalar

Windows: apague `winhttp.dll`, `.doorstop_version`, `doorstop_config.ini` e esta pasta.
Nada dentro de `MiniMetro_Data/` foi modificado.

Linux: apague `<jogo>/MiniMetroGA/` e a opção de inicialização na Steam.
