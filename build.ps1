<#
    Compila o MiniMetroGA e instala em MiniMetroGA\bin.

        .\build.ps1              compila
        .\build.ps1 -Run         compila e abre o jogo
        .\build.ps1 -Run -Tail   compila, abre o jogo e segue o log

    Requisitos: .NET SDK (qualquer versao recente) no PATH. Para o nucleo
    nativo (o otimizador), Rust com rustup e as build tools do Visual Studio;
    -NoNative pula. Sem cargo, vale a mmopt.dll feita no Linux
    (./build.sh --windows-dll), se existir.
#>
[CmdletBinding()]
param(
    [switch]$Run,
    [switch]$Tail,
    [switch]$NoNative,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$ModRoot  = Split-Path -Parent $MyInvocation.MyCommand.Path
$GameDir  = Split-Path -Parent $ModRoot
$Project  = Join-Path $ModRoot "src\MiniMetroGA\MiniMetroGA.csproj"
$LogFile  = Join-Path $ModRoot "MiniMetroGA.log"
$GameExe  = Join-Path $GameDir "MiniMetro.exe"

Write-Host "Jogo : $GameDir"
Write-Host "Proj : $Project"

# O jogo segura o lock da DLL enquanto roda
$running = Get-Process MiniMetro -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "MiniMetro esta aberto; fechando para liberar a DLL..." -ForegroundColor Yellow
    Stop-Process -Name MiniMetro -Force
    Start-Sleep -Milliseconds 800
}

# Nucleo nativo: o jogo de Windows e x86 (32 bits)
if (-not $NoNative) {
    if (Get-Command cargo -ErrorAction SilentlyContinue) {
        $Target = "i686-pc-windows-msvc"
        if (Get-Command rustup -ErrorAction SilentlyContinue) { rustup target add $Target | Out-Null }
        Write-Host "Compilando o nucleo nativo ($Target)..." -ForegroundColor Cyan
        Push-Location (Join-Path $ModRoot "native\mmopt")
        try { cargo build --release --lib --target $Target }
        finally { Pop-Location }
        if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar o nucleo nativo (use -NoNative para pular)." }
    } else {
        Write-Host "cargo nao encontrado: sem nucleo nativo o otimizador fica desligado." -ForegroundColor Yellow
    }
}

dotnet build $Project -c $Configuration -v m -p:GameDir="$GameDir"
if ($LASTEXITCODE -ne 0) { throw "Falha na compilacao." }

Write-Host "OK." -ForegroundColor Green

if ($Run) {
    if (Test-Path $LogFile) { Remove-Item $LogFile -Force }
    Write-Host "Abrindo o jogo..."
    Start-Process -FilePath $GameExe -WorkingDirectory $GameDir

    if ($Tail) {
        Write-Host "Seguindo $LogFile (Ctrl+C para sair)..." -ForegroundColor Cyan
        while (-not (Test-Path $LogFile)) { Start-Sleep -Milliseconds 300 }
        Get-Content $LogFile -Wait
    }
}
