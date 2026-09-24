# Validação do modelo

A pergunta: **o fitness do AG descreve o jogo?** O avaliador é analítico
(Dijkstra + fórmulas de fila, ~75 µs por rede), não uma simulação. Então é
preciso conferir contra o gabarito, que é o próprio jogo: o mod mede na
partida o que o modelo prevê para a mesma rede e compara.

Ver também: [[Núcleo nativo (Rust)]], [[Dossiê - o problema de otimização]].

## Como mede

`MINIMETROGA_SELFTEST_MODE=validate` (ver `SelfTest.cs` e `Validation.cs`):

1. O self-test joga normalmente (otimiza e aplica a cada 6 s, em 2x).
2. De tempos em tempos abre uma **janela**: aplica a melhor rede e a
   **congela** por `MINIMETROGA_VALIDATE_WINDOW` s de jogo (30).
3. Na hora de aplicar, pede ao núcleo a previsão para aquela rede, naquele
   estado (`mm_explain`), com o horizonte igual à janela e sem encaixar
   estação futura (no jogo ninguém as liga durante a janela).
4. Durante a janela, mede todo frame:
   - **por linha:** ciclo de cada trem (intervalo entre duas passagens pelo
     mesmo link no mesmo sentido), passageiros a bordo, tempo com o trem
     lotado, e quantos trens rodam em cada sentido;
   - **por estação** (as que existiam no início): fila média, fração do tempo
     acima da capacidade, pico;
   - **total:** passageiros nascidos (contador do jogo `City.NewPeepCount`) e
     entregues (placar).
5. Grava previsto e medido lado a lado em `MiniMetroGA/validation.jsonl` e o
   problema da janela em `problems/val_N.bin` (dá para reabrir no
   `mmopt-bench`).

Se alguém aplicar outra rede no meio (F11, modo automático do painel), a
janela é descartada. Por isso o self-test desliga os atalhos e o modo
automático enquanto roda.

```bash
MINIMETROGA_SELFTEST=london MINIMETROGA_SELFTEST_MODE=validate \
MINIMETROGA_VALIDATE_FROM_DAY=8 MINIMETROGA_SELFTEST_QUIT=1 <jogo>/MiniMetroGA/run.sh
python3 tools/validate/analyze.py <jogo>/MiniMetroGA/validation.jsonl
```

## O que a comparação achou (e foi corrigido)

A leitura do código decompilado e as rodadas de validação apontaram estes erros
no avaliador (1–5 na v2, 6–7 achados já na v3 com os dados do jogo):

| # | Erro na v2 | O jogo | Efeito no AG | Correção (v3) |
|---|---|---|---|---|
| 1 | Loop atendia os dois sentidos com qualquer frota | `Line.AddTrain` alterna o sentido de cada trem novo; loop com 1 trem é **mão única** | loop parecia até 2x melhor do que é | um nó por sentido; o genoma diz quantos trens vão em cada sentido |
| 2 | Lotação = média de ocupação da linha inteira | estoura o **trecho mais carregado** de um sentido | lotação subestimada 2–4x; vagão e trem pareciam inúteis | fluxo × headway / lugares por trem, no pior trecho |
| 3 | Fila atual virava taxa diluída no resto do dia | é um lote que já está na plataforma | a demanda de hoje saía **2–3x maior** (Londres: 9,2 pax/s em vez de 1,7) | fila inicial que escoa pela folga dos trens (ou fica, se não tem rota) |
| 4 | Demanda = média do dia | cada entrada solta passageiros nas horas h, h+2, h+4… | trecho que não é um dia inteiro errava (janela de 30 s: 10,1 previstos, 8,8 certos) | valor esperado exato dos spawns que caem em cada trecho |
| 5 | Lotação de estação = fila média vs capacidade | game over é **fila > capacidade** por 47 s | não via o risco de pico | distribuição da fila, `E[(fila − cap)+]`, e o que sobra (sem lugar, sem rota) cresce |
| 6 | Folga para escoar a fila e "quem não cabe" pelo pior trecho da linha | o que conta é o trecho que **sai daquela estação** | fila atual escoava devagar demais (fila prevista 1,44x a medida) | lugares/s − fluxo no trecho de saída (1,11x) |
| 7 | Fila normal (gaussiana) | fila é contagem: Poisson com média variando entre dois trens | com ~4 chegadas por trem, a normal dava 0,7% de passar de 6; o certo é ~3% | Poisson misturada (média uniforme entre trens) mais a parte fixa (fila atual) |

Com (2) corrigido, a penalidade de trem lotado explodiu e o AG passou a
**deixar estações de fora** para não lotar os trens. No jogo isso só troca uma
lotação por outra: quem não embarca continua na plataforma. A v3 põe tudo na
mesma moeda do jogo, **passageiros acima da capacidade na plataforma**: sem
lugar no trem, o excedente fica e cresce até o próximo upgrade (7 dias); sem
rota, fica pelo resto da partida. Com isso o AG voltou a ligar tudo.

## Resultados em jogo

Londres, clássico, três rodadas de self-test em modo validate (2026-09-24). A
tabela usa as rodadas 2 e 3 (26 janelas, dias 8 a 55, até 39 estações),
reprevistas com o modelo final (`tools/validate/replay.py`). As taxas de
"regime" são medidas só nos últimos 2/3 de janelas de 60 s (rodada 3).

| Grandeza | Previsto / medido | Correlação | Leitura |
|---|---|---|---|
| Passageiros nascidos (regime) | 0,96 | 0,98 | a tabela de spawn, a tensão, a centralidade e a hora marcada estão certas |
| Passageiros entregues (regime) | 0,99 | 0,96 | a vazão da rede bate |
| Comprimento dos trilhos (`Link.Length`) | 1,01 | 1,00 | a fórmula octilinear está certa (os cantos arredondados tiram ~1%) |
| Tempo de volta dos trens | 0,96 | 0,94 | cinemática, paradas, embarque e cruzamentos: erro médio de 3,3 s |
| Passageiros a bordo (regime) | 0,96 | 0,77 | a lotação dos trens bate |
| Trens por sentido em loop | 19 de 19 | — | exato: o genoma escolhe e o Applier obedece |
| Fila média por estação | 1,11 | 0,60 | erro médio de 0,7 passageiro |
| Tempo acima da capacidade | pessimista | 0,17 | ver abaixo |

**O que o modelo acerta bem:** demanda, vazão, geometria, tempo de ciclo e
lotação dos trens. Isso sustenta o custo de viagem e a utilização que o AG
usa.

**O ponto fraco é o risco de estação estourar.** O modelo descreve o regime,
e as janelas começam logo depois de o Applier reconstruir a rede. Nesse
momento o jogo não está em regime: os trens saem das estações e limpam as
filas, e os passageiros a bordo das linhas antigas são despejados na próxima
estação. Onde o modelo previa 38% do tempo acima da capacidade, o jogo mediu 0%.
Em regime, o AG mantém as estações abaixo da capacidade, então quase não houve
estouro para calibrar. O viés é conservador (o fitness pune mais que o
necessário), mas a correlação com o estouro medido é fraca.

**O game over da rodada 3** (semana 7, placar 805) veio de uma estação-hub
com 7 esperando, parada por 35 s, que o modelo dava como segura. Ela veio
logo depois de **três reconstruções da rede em 15 s**. No fim de jogo nasce
estação nova a cada poucos segundos, e a rede atual sem ela é tão ruim no
fitness que reconstruir sempre compensa. O modelo não enxerga o custo de
reconstruir. Não é erro de fórmula, é do modo de aplicar: o **apply
incremental** (ligar a estação nova sem refazer tudo) virou o item mais
importante do [[Roadmap]].

**Resultado de jogo.** Com o avaliador v3 e o AG novo, o self-test em Londres
chegou à **semana 7 com placar 727 sem perder** (rodada 2, parou por tempo) e à
**semana 7 com 805** (rodada 3, game over). Com a v2, o mesmo teste perdia na
**semana 5 com 290**.

### Como reproduzir a análise

```bash
python3 tools/validate/analyze.py validation.jsonl          # previsto no jogo x medido
python3 tools/validate/replay.py validation.jsonl <pasta dos val_N.bin> novo.jsonl
python3 tools/validate/analyze.py novo.jsonl                # com o modelo atual
```

O `replay.py` refaz a previsão com o núcleo atual sobre os problemas gravados.
Dá para mexer no modelo e medir o efeito sem abrir o jogo.
