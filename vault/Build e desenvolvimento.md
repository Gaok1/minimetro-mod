# Build e desenvolvimento

## Requisitos

- .NET SDK no PATH (qualquer versão recente; testado com 9.0.312)
- opcional, para explorar o jogo: `dotnet tool install -g ilspycmd`

Não é preciso Visual Studio nem Unity instalado.

> [!note] Linux
> No Linux use `./build.sh` (mesmas opções, `--run --tail`). Detalhes em
> [[Linux - build nativo]].

## Compilar

```powershell
cd "<jogo>\MiniMetroGA"
.\build.ps1                 # compila e instala em bin\
.\build.ps1 -Run            # compila e abre o jogo
.\build.ps1 -Run -Tail      # compila, abre o jogo e segue o log
```

Ou direto:

```bash
dotnet build src/MiniMetroGA/MiniMetroGA.csproj -c Release
```

O target `DeployToGame` copia o `MiniMetroGA.dll` para `MiniMetroGA\bin`
automaticamente após o build.

> [!tip] O jogo segura o lock da DLL enquanto está aberto.
> O `build.ps1` fecha o processo antes de compilar.

## Se o jogo estiver em outro caminho

`GameDir` é uma propriedade MSBuild com default no `.csproj`:

```bash
dotnet build ... -p:GameDir="D:\jogos\MiniMetro"
```

## Depurar

O log fica em `<jogo>\MiniMetroGA\MiniMetroGA.log`. Toda exceção do `Update`,
`OnGUI`, do `Applier` e da thread do AG cai lá.

```powershell
Get-Content "<jogo>\MiniMetroGA\MiniMetroGA.log" -Wait
```

`redirect_output_log = true` no `doorstop_config.ini` também gera
`<jogo>\output_log.txt` com a saída do Unity — útil quando o problema é
carregamento de assembly (`Could not load file or assembly ...`).

Para log mais verboso, ligue `MiniMetroGA.Log.Verbose = true`.

## Explorando o código do jogo

```bash
ilspycmd -p -o ./src "<jogo>/MiniMetro_Data/Managed/Assembly-CSharp.dll"   # projeto inteiro
ilspycmd -l c "<jogo>/MiniMetro_Data/Managed/Assembly-CSharp.dll"          # listar classes
ilspycmd -t Line "<jogo>/MiniMetro_Data/Managed/Assembly-CSharp.dll"       # uma classe
```

Use também para **checar se uma API sobreviveu ao stripping** antes de usá-la —
embora o projeto já esteja configurado para o compilador acusar
([[Managed stripping - o problema central]]).

## Desinstalar

Apague `winhttp.dll`, `.doorstop_version`, `doorstop_config.ini` e a pasta
`MiniMetroGA\`. Nada dentro de `MiniMetro_Data\` foi tocado, então o jogo volta ao
original. Para desligar temporariamente sem apagar: `enabled = false` no
`doorstop_config.ini`.

## Verificar a integridade pelo Steam

Steam → propriedades do jogo → arquivos locais → verificar. Os arquivos do mod
ficam fora de `MiniMetro_Data`, então sobrevivem — mas o Steam pode remover o
`winhttp.dll`. Se o mod parar de carregar depois de uma verificação, é isso.

Ver também: [[Troubleshooting]]
