param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
Push-Location $workspace
try {
    & $Dotnet run --project tests/DeskLog.Tests/DeskLog.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    $uiData = Join-Path $workspace ('artifacts\ui-check-' + [Guid]::NewGuid().ToString('N'))
    & $Dotnet run --project tests/DeskLog.UiTests/DeskLog.UiTests.csproj -c Release -- --preview --data-dir $uiData
    if ($LASTEXITCODE -ne 0) { throw 'WPF integration tests failed.' }
    & $Dotnet publish src/DeskLog.App/DeskLog.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o artifacts/DeskLog-win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath 'README.md' -Destination 'artifacts/DeskLog-win-x64/使用说明.md' -Force
    Copy-Item -LiteralPath 'VALIDATION.md' -Destination 'artifacts/DeskLog-win-x64/VALIDATION.md' -Force
    Copy-Item -LiteralPath 'THIRD-PARTY-NOTICES.md' -Destination 'artifacts/DeskLog-win-x64/THIRD-PARTY-NOTICES.md' -Force
    Copy-Item -LiteralPath 'licenses' -Destination 'artifacts/DeskLog-win-x64' -Recurse -Force
    Compress-Archive -Path 'artifacts/DeskLog-win-x64/*' -DestinationPath 'artifacts/DeskLog-win-x64.zip' -Force
} finally { Pop-Location }
