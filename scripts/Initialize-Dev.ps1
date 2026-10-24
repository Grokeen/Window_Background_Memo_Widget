<#
name         : Initialize-Dev.ps1
desc         : 시스템 SDK·PATH를 바꾸지 않고 공식 설치기로 사용자 영역의 고정 .NET SDK를 구성한다.
author       : NotedexOrig2
create date  : 2026-10-24
update date  : 2026-10-24 NotedexOrig2 : 다른 개발 PC용 재현 절차 추가.
#>
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$sdkVersion = (Get-Content -LiteralPath (Join-Path $projectRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$toolDirectory = Join-Path $env:LOCALAPPDATA 'NotedexTooling'
$installDirectory = Join-Path $toolDirectory 'dotnet'
$sdkDirectory = Join-Path $installDirectory "sdk\$sdkVersion"
if (-not (Test-Path -LiteralPath $sdkDirectory)) {
    # 공식 HTTPS 설치 스크립트만 내려받아 명시한 SDK 버전으로 설치한다.
    New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null
    $installer = Join-Path $toolDirectory 'dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    & $installer -Version $sdkVersion -Architecture x64 -InstallDir $installDirectory -NoPath
    # PowerShell 설치 스크립트는 LASTEXITCODE를 보장하지 않으므로 실제 SDK 생성과 버전을 확인한다.
    if (-not (Test-Path -LiteralPath $sdkDirectory)) { throw 'SDK installation failed.' }
}
Push-Location -LiteralPath $projectRoot
try {
    $actualVersion = & (Join-Path $installDirectory 'dotnet.exe') --version
    if ($LASTEXITCODE -ne 0 -or $actualVersion -ne $sdkVersion) { throw "Expected SDK $sdkVersion; actual $actualVersion" }
    Write-Output "Development SDK ready: $actualVersion"
}
finally { Pop-Location }
