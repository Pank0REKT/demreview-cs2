# Публикация DemReview: self-contained single-file сборки для win-x64 и linux-x64.
# Использование:  .\publish.ps1            # обе платформы
#                 .\publish.ps1 win-x64    # только Windows
param([string[]] $Rids = @('win-x64', 'linux-x64'))

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src/DemReview.Desktop/DemReview.Desktop.csproj'

foreach ($rid in $Rids) {
    $out = Join-Path $root "artifacts/$rid"
    Write-Host "==> Публикация $rid в $out"
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }

    dotnet publish $project -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none `
        -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish для $rid завершился с ошибкой $LASTEXITCODE" }

    Write-Host "==> Готово: $out"
}
