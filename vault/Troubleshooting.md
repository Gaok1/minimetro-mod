# Troubleshooting

## O painel não aparece (F8 não faz nada)

Cheque `<jogo>\MiniMetroGA\MiniMetroGA.log`. O carregamento saudável é:

```
[INFO ] MiniMetroGA 1.0.0 iniciando (Doorstop).
[INFO ] Hook de cena instalado.
[INFO ] UI pronta. F8 abre/fecha o painel.
[INFO ] Host criado.
```

Se **o arquivo nem existe**, o Doorstop não rodou:

- `winhttp.dll` está na raiz do jogo?
- `enabled = true` e `target_assembly = MiniMetroGA\bin\MiniMetroGA.dll` no
  `doorstop_config.ini`?
- `MiniMetroGA\bin\MiniMetroGA.dll` existe?
- o Steam removeu o `winhttp.dll` numa verificação de integridade?

Se parar em **"iniciando"** e não chegar em "Hook de cena instalado", o
`Assembly-CSharp` não foi detectado — improvável, mas veja o `output_log.txt`.

### No Linux

Se o log nem existe:

- a opção de inicialização da Steam é `"<jogo>/MiniMetroGA/run.sh" %command%`?
- `MiniMetroGA/doorstop/libdoorstop.so` e `MiniMetroGA/bin/MiniMetroGA.dll`
  existem? Sem eles o `run.sh` avisa no stderr e abre o jogo sem mod.
- `MINIMETROGA_DISABLE=1` no ambiente?
- rode na mão para ver o stderr: `steam-run "<jogo>/MiniMetroGA/run.sh"`
  (NixOS) ou só `"<jogo>/MiniMetroGA/run.sh"`.

Se aparecer `IMGUIModule STRIPADA carregada`, o search path do Doorstop não
entrou: confira `MiniMetroGA/lib/`. O log do Unity no Linux fica em
`~/.config/unity3d/Dinosaur Polo Club/Mini Metro/Player.log`.

## O mod carrega, mas o painel é invisível (ou aparece vazio)

**O log do mod não vai ajudar** — a exceção acontece dentro do `OnGUI` e vai parar
no log do Unity. Olhe lá:

```bash
grep -c "Exception" "<jogo>/output_log.txt"
grep -A5 "Exception" "<jogo>/output_log.txt" | head -20
```

Um `output_log.txt` de megabytes já é o diagnóstico: é a mesma exceção repetindo a
cada frame.

Se o stack trace passa por `UnityEngine.GUI*` ou `SliderHandler`, é uma das
[[APIs ausentes - lista viva|cinco minas do IMGUI]] — algum controle do IMGUI que
não funciona neste build. Rode o auditor:

```bash
cd MiniMetroGA/tools/StripAudit
dotnet run -- "<jogo>/MiniMetroGA/lib/UnityEngine.IMGUIModule.dll" \
             "<jogo>/MiniMetro_Data/Managed"
```

Se o painel aparece mas o **conteúdo das abas some**, sem exceção nenhuma, é o
`GUILayoutUtility.GetRect` devolvendo rect nulo no evento `Layout`
(ver [[UI e controles]]).

## `Could not load file or assembly ...` no output_log

Falta alguma DLL em `MiniMetroGA\lib`. O mínimo é:

```
UnityEngine.IMGUIModule.dll   (não-stripada)
netstandard.dll
```

## `MissingMethodException` / `TypeInitializationException`

Quase certamente uma API stripada. Ver [[APIs ausentes - lista viva]] e
[[Managed stripping - o problema central]]. Adicione a linha na tabela quando
encontrar uma nova.

## O jogo abre mas fica preto / crasha na inicialização

Você provavelmente colocou assemblies não-stripadas demais em `MiniMetroGA\lib`.
**Só** `UnityEngine.IMGUIModule.dll` e `netstandard.dll` devem estar lá.
Trocar `mscorlib` ou `UnityEngine.CoreModule` quebra o jogo — ver
[[Carregamento - Doorstop sem BepInEx]].

## "Nenhum jogo ativo"

O mod só enxerga uma partida em andamento. No menu, `GameHook.Current` é `null`
de propósito. Entre numa cidade.

## "Estado insuficiente (menos de 2 estações)"

Snapshot precisa de pelo menos duas estações ativas. Espere o jogo começar.

## Aplicar não constrói nada / "N falha(s)"

Causas comuns:

- **sem linhas disponíveis** — o genoma pede mais linhas do que o jogo permite.
  Baixe *Max de linhas* na aba Parâmetros.
- **túnel/ponte insuficiente** — o AG não propõe mais travessias do que o estoque
  (a rede é inviável para ele). Se mesmo assim o jogo recusar, a detecção de água
  divergiu: o log do `Applier` mostra "travessias no jogo X, no modelo Y".
- **modo EXTREME** — não permite remover trilhos, então reconstruir do zero falha.

Ligue `Log.Verbose` para o motivo por linha.

## "ATENCAO: N linha(s) SEM TREM"

O estoque de locomotivas acabou antes de todas as linhas serem servidas. Com o
orçamento corrigido isso não deveria mais acontecer — se acontecer, é bug, e o
log do mod diz qual linha ficou (`Linha 3 ficou sem trem: ...`).

Confira **Linhas operáveis** no cartão de inventário: é `min(linhas, trens)`, e é
o teto que o AG respeita. Se estiver menor que o número de linhas, é normal —
significa que a cidade te deu mais linhas do que trens.

## "vagoes +0/0"

Não é bug. `Line.ApplyAsset(AssetType.Carriage, ...)` checa
`GetAvailableAssets(Carriage)` e recusa se for zero — e o Mini Metro só oferece
vagão em algumas semanas. Confira o cartão **Inventário da cidade** na aba
Controle: se `Vagões` está `0 / 0`, a cidade nunca deu nenhum e não há o que
aplicar. A mensagem de resultado diz isso explicitamente.

## Os vagões vão para linhas que não precisam

Foi bug, e a causa era a **unidade da demanda** — não o operador de mutação. Se
voltar a acontecer, o diagnóstico está no cartão **Demanda** (aba Controle) e na aba
**Fitness**:

- `Lotação de linha` zerado na decomposição do fitness = a capacidade não está
  entrando na conta, e vagão volta a não ter gradiente. Suba `DemandScale`.
- **"lotação vira espera (2º passe)"** desligado nos Parâmetros tem o mesmo efeito:
  os vagões deixam de influenciar tempo de viagem.
- `Geração total` em passageiros/dia muito longe do que se vê no mapa = o modelo de
  demanda está descalibrado. Ver [[Modelo de demanda]].

## "Tabela de spawn: réplica interna"

O cartão **Demanda** mostra isso em laranja quando a reflexão em
`StationDatabase.schedules` falhou e o mod caiu na cópia hardcoded. Não é fatal — a
réplica confere com esta versão do jogo — mas depois de um update do Mini Metro ela
pode ter envelhecido. Confira `StationDatabase.Load()` no decompilado contra
`SpawnModel.BuiltIn()`.

## "Demanda calculada deu zero; usando modelo uniforme de reserva"

No log do mod. Nenhuma das formas presentes na cidade gera passageiro segundo a
tabela. Acontece de verdade numa cidade só de estações GEM (a agenda do GEM é vazia
no jogo — não é bug do mod, ver [[Modelo de demanda]]). Fora esse caso, suspeite de
um `StationType` novo que a tabela não conhece.

## O AG demora demais

Custo ≈ `população × gerações × estações × passes`. Numa cidade cheia com pop 200 e
900 gerações dá para levar mais de um minuto. Use o preset **rápido**, ou baixe
*Gerações*. O gráfico normalmente achata bem antes do fim.

Desligar **"lotação vira espera (2º passe)"** corta o tempo pela metade, mas ao
preço de os vagões deixarem de ter efeito — prefira baixar *Gerações* primeiro.

## Cliques na UI viram linhas no mapa

Não deveria: o mod liga `game.IsLocked` quando o cursor está sobre o painel.
Se acontecer, é bug — o retângulo da janela e a posição do mouse podem estar
dessincronizados em resolução com DPI scaling. Registre em [[Diário de decisões]].

## Depois de aplicar, a rede ficou pior

A [[Função de fitness]] é um **modelo**, não o jogo. Se o resultado não bate com a
prática, o caminho é ajustar os pesos na aba Parâmetros — especialmente
*Escala de demanda*, *Penalidade de baldeação* e *Lotação de linha*.
Anote o que funcionou.
