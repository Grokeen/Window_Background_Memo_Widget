<#
name         : Test-Smoke.ps1
desc         : 전달할 루트 EXE 자체의 실제 WPF/Win32 창 검증을 수행한다. 사용자 앱은 조작하지 않는다.
author       : NotedexOrig2
create date  : 2026-10-23
update date  : 2026-10-23 NotedexOrig2 : 결과와 종료 코드 검증 추가.
#>
param(
    [string]$OutputDirectory = 'artifacts/smoke',
    [string]$ExecutablePath = 'WindowBackgroundMemoWidget.exe'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$executable = if ([IO.Path]::IsPathRooted($ExecutablePath)) { $ExecutablePath } else { Join-Path $projectRoot $ExecutablePath }
if (-not (Test-Path -LiteralPath $executable)) { throw 'Run scripts/Build.ps1 first.' }
$resolvedOutput = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectRoot $OutputDirectory }
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
$reportPath = Join-Path $resolvedOutput 'm1-smoke.json'
# 명령 셸을 거치지 않고 인수를 개별 전달해 공백과 특수문자가 있는 경로도 보존한다.
$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $executable
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.ArgumentList.Add('--smoke-test')
$startInfo.ArgumentList.Add($reportPath)
$process = [Diagnostics.Process]::Start($startInfo)
if (-not $process.WaitForExit(30000)) {
    $process.Kill()
    throw 'Smoke probe exceeded 30 seconds.'
}
if (-not (Test-Path -LiteralPath $reportPath)) { throw "Smoke report missing; exit $($process.ExitCode)." }
$report = Get-Content -LiteralPath $reportPath -Raw -Encoding utf8 | ConvertFrom-Json
$report.Checks | Format-Table Name,Result,Detail -AutoSize
if ($process.ExitCode -ne 0) { throw "Smoke probe failed; exit $($process.ExitCode). Report: $reportPath" }
Write-Output "Smoke report: $reportPath"
