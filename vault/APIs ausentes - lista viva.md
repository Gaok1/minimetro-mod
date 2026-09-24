# APIs ausentes — lista viva

Tudo que já foi confirmado como **removido** deste build, e o que usar no lugar.
Contexto: [[Managed stripping - o problema central]].

## BCL

| Ausente | Onde deveria estar | Substituto usado |
|---|---|---|
| `File.AppendAllText` | mscorlib | `new StreamWriter(path, append: true)` |
| `List<T>.GetRange` | mscorlib | loop manual copiando para uma `List<T>` nova |
| `System.Threading.Tasks.Parallel` | mscorlib | `Thread` manual fatiando o índice (`GaEngine.EvaluateAll`) |
| `Module.GetPEKind` | mscorlib | — (é o que mata o `BepInEx.Preloader`) |
| `AmbiguousMatchException(string, Exception)` | mscorlib | — (é o que mata o `HarmonyLib.AccessTools`) |
| `AssemblyLoadEventArgs.LoadedAssembly` (getter) | mscorlib | varrer `AppDomain.CurrentDomain.GetAssemblies()` |
| `System.Runtime.Versioning.TargetFrameworkAttribute` | mscorlib | `<GenerateTargetFrameworkAttribute>false` |

## Presentes (confirmado), mas note onde moram

| API | Assembly a referenciar |
|---|---|
| `HashSet<T>` | `System.Core.dll` |
| `System.Diagnostics.Stopwatch` | `System.dll` |
| `Thread`, `Environment.ProcessorCount/TickCount`, `Array.Resize` | `mscorlib.dll` |
| `SceneManager.sceneLoaded` | `UnityEngine.CoreModule.dll` |
| `File.WriteAllText`, `File.ReadAllText`, `StreamWriter(path, bool)` | `mscorlib.dll` |

## UnityEngine

| Ausente | Substituto usado |
|---|---|
| `RectOffset(int,int,int,int)` | — usamos o padding padrão do skin |
| setters de `RectOffset.left/right/top/bottom` | idem |
| quase todo o `GUILayout` / `GUI` / `GUIStyle` | `UnityEngine.IMGUIModule.dll` não-stripada em `MiniMetroGA\lib` |

## As cinco minas do IMGUI

Esta é a categoria que o compilador **não** pega, e por isso a mais perigosa:
a `IMGUIModule` que trouxemos é a de fábrica, e ela chama membros que o
stripping removeu do `CoreModule` do jogo. O erro só aparece em runtime, dentro
do `OnGUI`, e derruba a janela inteira sem deixar nada na tela.

Auditoria completa (`tools/StripAudit`), o conjunto é exatamente este:

| Membro ausente | Quem chama | Consequência |
|---|---|---|
| `Vector4.get_one` | `GUI.DrawTexture(rect, tex, scaleMode, alphaBlend, imageAspect)` | **todo** `GUI.DrawTexture` de sobrecarga curta |
| `Vector4.op_Multiply` | idem | idem |
| `RectOffset.Remove` | `SliderHandler.ThumbRect()` | **todo** slider e scrollbar |
| `Rect.set_center` | `SliderHandler.ThumbExtRect()` | idem |
| `UnityEngine.SystemClock` | `SliderHandler` (repetição de clique) | idem |
| `TouchScreenKeyboard.get_isRequiredToForceOpen` | `GUI.TextField` | todo campo de texto |

Ou seja, **é proibido usar**: `GUILayout.HorizontalSlider`, `VerticalSlider`,
`GUI.HorizontalScrollbar`, `BeginScrollView` (usa scrollbar), `GUILayout.TextField`
e as sobrecargas curtas de `GUI.DrawTexture`.

Substitutos, todos em `UI/Widgets.cs` e `UI/Theme.cs`:

| Proibido | Substituto |
|---|---|
| `GUI.DrawTexture(r, tex)` | `Theme.Blit()` — chama a sobrecarga que já recebe os dois `Vector4` prontos, pulando a linha do `Vector4.one` |
| `GUILayout.HorizontalSlider` | `Widgets.Slider` — trilha, botão e arrasto desenhados na mão |
| `GUILayout.BeginScrollView` | `Widgets.BeginScroll` — `GUI.BeginGroup` + `BeginArea` deslocada, roda no scroll do mouse |
| `GUILayout.TextField` | — (a UI só usa sliders e botões) |

Seguros e confirmados em uso: `GUI.Window`, `GUI.DragWindow`, `GUI.Label`,
`GUI.Box`, `GUILayout.Button/Label/Box`, `BeginHorizontal/Vertical/Area`,
`GUILayoutUtility.GetRect`, `GUIStyle`, `GUIStyleState.textColor`,
`GUI.backgroundColor`, `GUI.color`, `Event.current`.

## Como checar antes de usar

Para o **nosso** código, o caminho mais barato é só compilar: o projeto referencia
a BCL do próprio jogo e acusa na hora ([[Managed stripping - o problema central]]).

Para código de **terceiros** (a IMGUIModule, ou qualquer DLL que venha a entrar em
`lib/`), o compilador não ajuda — use o auditor:

```bash
cd MiniMetroGA/tools/StripAudit
dotnet run -- "<jogo>/MiniMetroGA/lib/UnityEngine.IMGUIModule.dll" \
             "<jogo>/MiniMetro_Data/Managed"
```

Ele lê os `MemberRef` da assembly e confere um a um contra o que existe de fato
nas assemblies que o jogo carrega. Foi assim que as cinco minas acima saíram de
"erro misterioso em runtime" para uma lista fechada em um comando.

Depois, para descobrir **quais controles** encostam num membro ausente:

```bash
ilspycmd -p -o imgui "<jogo>/MiniMetroGA/lib/UnityEngine.IMGUIModule.dll"
grep -rn "Vector4.one" imgui
```
