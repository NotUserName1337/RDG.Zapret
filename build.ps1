# Build / publish helper for RDG.Zapret.
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [string]$Runtime = "win-x64",
    [switch]$NoZip,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\RDG.Zapret\RDG.Zapret.csproj"
$out = Join-Path $root "artifacts\RDG.Zapret-$Runtime"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet SDK не найден. Установите .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0"
}

if ($Clean -and (Test-Path $out)) {
    Remove-Item -LiteralPath $out -Recurse -Force
}
New-Item -ItemType Directory -Path $out -Force | Out-Null

if (Test-Path $out) {
    Write-Host "Existing files in $out are preserved (strategies, lists, bin). Use -Clean to wipe the folder." -ForegroundColor DarkGray
}

$self = if ($SelfContained) { "true" } else { "false" }
$icon = Join-Path $root "src\RDG.Zapret\Assets\app.ico"
if (-not (Test-Path $icon)) {
    & (Join-Path $root "tools\make-icon.ps1")
}

Write-Host "Publishing RDG.Zapret ($Runtime, self-contained=$self)..." -ForegroundColor Cyan

$arguments = @(
    "publish", $project,
    "-c", "Release",
    "-r", $Runtime,
    "--self-contained", $self,
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:PublishTrimmed=false",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-o", $out
)

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish завершился с ошибкой ($LASTEXITCODE)"
}

if (-not $NoZip) {
    $zip = Join-Path $root "artifacts\RDG.Zapret-$Runtime.zip"
    if (Test-Path $zip) {
        Remove-Item -LiteralPath $zip -Force
    }
    Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "Archive: $zip" -ForegroundColor Green
}

Write-Host "Ready: $out\RDG.Zapret.exe" -ForegroundColor Green
if (-not $SelfContained) {
    Write-Host "Target PC needs .NET 8 Desktop Runtime, or rebuild with -SelfContained." -ForegroundColor Yellow
}
