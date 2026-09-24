# UI e controles

## Atalhos

| Tecla | Ação |
|---|---|
| **F8** | abre/fecha o painel |
| **F9** | pausa/retoma o jogo |
| **F10** | roda o AG no estado atual |
| **F11** | aplica a melhor solução encontrada |

Definidos em `ModBehaviour.cs` (campos estáticos `ToggleKey`, `PauseKey`, ...).

## Overlay (`UI/Hud.cs`)

Barra sempre visível no canto inferior esquerdo, independente do painel:

- um **chip por atalho** (`F8 painel`, `F9 pausar`, `F10 otimizar`, `F11 aplicar`).
  O chip **pisca em verde** quando a tecla é apertada — é o retorno de que o mod
  recebeu o comando, e não o jogo.
- chips de estado ficam **acesos em azul**: `F8` enquanto o painel está aberto,
  `F9` enquanto o jogo está pausado.
- **faixa de status do AG** com barra de progresso: `AG rodando ger 87/400
  melhor 1842`. Serve para acompanhar a otimização com o painel fechado.
- **toasts**: cada ação do mod (pausou, começou, aplicou, falhou) vira uma linha
  colorida que some em 5 s. Mesma mensagem que aparece na barra de status do painel.

Pode ser desligado na aba **Controle → Overlay**.

O overlay é desenhado em coordenadas absolutas de tela, sem `GUILayout`, e não
captura clique nenhum.

## Abas

### Controle
- **Pausar / Otimizar / Parar** e **Aplicar melhor solução**
- **Inventário da cidade**: linhas, trens, vagões, túneis e baldeações, cada um
  como `disponível agora / total já concedido`, em barra. É a resposta para
  "por que não entrou nenhum vagão?" — normalmente porque a cidade ainda não deu
  nenhum. Lido direto do `AssetDatabase` a cada 250 ms
  (`GameHook.ReadInventory`), então funciona antes de rodar o AG.
- barra de progresso, melhor fitness, média, gerações estagnadas, avaliações/ms
- **gráfico de convergência** ao vivo — verde = melhor, laranja = média,
  com opção de escala log (recomendada: a queda inicial é enorme)
- **Modo automático**: a cada N segundos pausa, reotimiza, aplica e despausa.
  É o modo "assistir o AG jogar".

### Parâmetros
Tudo de `GaConfig` em slider, agrupado:

- **Algoritmo**: população, gerações, elitismo, torneio, taxas, mutações por filho,
  reinjeção por estagnação, semear com a rede atual
- **Espaço de busca**: máx. de linhas, mín/máx estações por linha, permitir circulares
- **Demanda e simulação**: gamma das formas, peso das filas atuais, escala de
  demanda, velocidade do trem, tempo de parada, penalidade de baldeação/embarque
- **Pesos do fitness**: destino inalcançável, estação fora da rede, lotação de
  linha, lotação de estação, custo de trilho, túnel acima do orçamento

Presets **rápido** (pop 60 / 120 ger) e **caprichado** (pop 200 / 900 ger).

### Fitness
Decomposição do custo do melhor indivíduo em barras + indicadores:
tempo médio de viagem, pares sem rota, estações desatendidas, travessias usadas,
trilho total, pior lotação de linha.

É aqui que se descobre qual peso ajustar. Ver [[Função de fitness]].

### Rede
Preview da melhor rede desenhado pixel a pixel (`UI/Plot.cs`), com paleta parecida
com a do jogo. **Pontos vermelhos = estações fora de qualquer linha.**
Abaixo, a lista de rotas, cada uma com o quadradinho da cor que ela tem no preview.

### Log
Atalhos + as últimas 60 linhas de `Log.Recent()`, para diagnosticar sem sair do jogo.

## Notas de implementação

A UI **não usa os controles do IMGUI**, porque metade deles estoura neste build.
Ver a seção "As cinco minas do IMGUI" em [[APIs ausentes - lista viva]].

- Tudo que é pintado passa por `Theme.Blit()`, a única sobrecarga de
  `GUI.DrawTexture` que não cai no `Vector4.one`.
- Slider, checkbox, abas, barra de progresso e rolagem são desenhados na mão em
  `UI/Widgets.cs`.
- A janela é `GUI.Window` (rect fixo, não `GUILayout.Window`), arrastável pela
  barra de título e **redimensionável** pela alça no canto inferior direito.
- Enquanto o cursor está sobre o painel, `game.IsLocked = true` — sem isso um
  clique na UI vira traço de linha no mapa.
- Gráfico e preview redesenham no máximo a cada 200 ms, com um buffer de pixels
  por tamanho de textura (evita lixo pro GC).
- Sem `padding` customizado no estilo: o ctor de `RectOffset` foi stripado. Todos
  os estilos nascem como cópia de um do `GUI.skin` e só trocam cor e tamanho.

### Duas armadilhas de IMGUI que custaram caro

**1. `GUILayoutUtility.GetRect` mente durante o evento `Layout`.**
Ele devolve `Rect(0,0,0,0)` nesse passe — e o `Layout` é justamente o passe que
dimensiona os controles. Abrir a área de rolagem com esse rect fazia todo o
conteúdo ser medido com largura negativa, e o painel aparecia **vazio**. Solução:
guardar o rect bom do último `Repaint` e usar ele em todos os passes.

**2. O `hotControl` do IMGUI é posicional.**
`GUIUtility.GetControlID` numera os controles pela ordem de aparição. Como esta UI
muda de conteúdo entre frames (o cartão de inventário só existe com partida ativa,
a linha de "próxima rodada" só aparece no modo automático), a numeração anda — e um
arrasto começado num slider terminava escrevendo no vizinho. `Widgets.Slider` usa
uma **chave derivada do rótulo**, não um id posicional, e `Widgets.ReleaseIfMouseUp`
solta o arrasto quando o botão do mouse não está mais fisicamente pressionado
(o `MouseUp` se perde quando a janela troca de foco).

Corolário: **estrutura de layout idêntica em todos os passes**. Pular um bloco em
alguns eventos desbalanceia os grupos e o Unity passa a repetir
`pushing more GUIClips than you are popping` a cada frame.
