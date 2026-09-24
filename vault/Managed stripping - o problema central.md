# Managed stripping — o problema central

O Mini Metro foi publicado com **managed code stripping** ligado. O Unity varre o
que o jogo usa e apaga o resto das assemblies — inclusive de `mscorlib.dll`,
`System.dll` e dos módulos `UnityEngine.*`.

Consequência prática: **metade da BCL que você espera não existe neste build.**
Os tipos continuam lá, mas com métodos faltando. O compilador comum não reclama
(ele usa o targeting pack do .NET Framework), e o mod morre em silêncio no runtime.

## Como isso se manifesta

- `MissingMethodException` no meio do nada
- `TypeInitializationException` em bibliotecas de terceiros
- pior de tudo: **nada acontece**, porque o `catch` engoliu a exceção

O primeiro sintoma real do projeto foi o log do mod não aparecer. Motivo:
`File.AppendAllText` não existe neste build, e o `Log.Write` engolia o erro.

## A defesa: o compilador vira o auditor

O `MiniMetroGA.csproj` **não** usa o targeting pack do .NET Framework. Ele
referencia o `mscorlib.dll`, `System.dll` e `System.Core.dll` **do próprio jogo**:

```xml
<NoStdLib>true</NoStdLib>
<NoConfig>true</NoConfig>
<AddAdditionalExplicitAssemblyReferences>false</AddAdditionalExplicitAssemblyReferences>
```

Com isso, usar uma API stripada vira **erro de compilação**, não bug de runtime.
Foi assim que `List<T>.GetRange` e o ctor de `RectOffset` foram pegos antes de rodar.

> [!warning] Nunca volte a referenciar o `Microsoft.NETFramework.ReferenceAssemblies`
> aqui. Ele compila coisa que o jogo não tem.

## O IMGUI é caso à parte

O jogo não usa IMGUI (a UI dele é Futile), então o stripping foi brutal:
`GUILayout` ficou só com `Width()` e `Height()`. Sem `Label`, `Button`, `Window`.

Solução: `UnityEngine.IMGUIModule.dll` **não-stripada** em `MiniMetroGA\lib`,
carregada antes da original via `dll_search_path_override` do Doorstop. Ela exige
`netstandard.dll`, que também está lá. Ver [[Carregamento - Doorstop sem BepInEx]].

> [!danger] Substituir só o IMGUIModule funciona. Substituir o resto **não**.
> Trocar `UnityEngine.CoreModule` ou `mscorlib` pelas versões não-stripadas do
> repositório do BepInEx quebra o jogo — são de outro flavor e fazem P/Invoke em
> `System.Native`, que não existe aqui. Detalhes em [[Diário de decisões]].

Ver também: [[APIs ausentes - lista viva]]
