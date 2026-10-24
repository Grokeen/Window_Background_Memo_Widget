<#
name         : Build.ps1
desc         : 고정 SDK로 빌드·회귀 검사·자체 포함 단일 EXE를 만들고 루트에 배치한다.
author       : NotedexOrig2
create date  : 2026-10-23
update date  : 2026-10-23 NotedexOrig2 : M1 재현 가능한 빌드 진입점 추가.
#>
param([switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$localDotnet = Join-Path $env:LOCALAPPDATA 'NotedexTooling\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Push-Location -LiteralPath $projectRoot
try {
    & $dotnet build WindowBackgroundMemoWidget.slnx -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $dotnet 'tests/WindowBackgroundMemoWidget.Tests/bin/Release/net10.0-windows/WindowBackgroundMemoWidget.Tests.dll'
    if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed.' }
    if (-not $SkipPublish) {
        & $dotnet publish 'src/WindowBackgroundMemoWidget/WindowBackgroundMemoWidget.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o artifacts/publish --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        Copy-Item -LiteralPath 'artifacts/publish/WindowBackgroundMemoWidget.exe' -Destination 'WindowBackgroundMemoWidget.exe' -Force
        # 전달 파일 자체가 패키징 출력과 일치하는지 확인하고 검증 근거를 남긴다.
        $publishedHash = (Get-FileHash -LiteralPath 'artifacts/publish/WindowBackgroundMemoWidget.exe' -Algorithm SHA256).Hash
        $deliveredHash = (Get-FileHash -LiteralPath 'WindowBackgroundMemoWidget.exe' -Algorithm SHA256).Hash
        if ($publishedHash -ne $deliveredHash) { throw 'Root executable checksum mismatch.' }
        Write-Output 'Root executable ready: WindowBackgroundMemoWidget.exe'
        Write-Output "SHA256: $deliveredHash"
    }
}
finally { Pop-Location }
