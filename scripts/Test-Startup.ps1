<#
name: Test-Startup.ps1; desc: 동일 EXE 무인수 경로의 실제 창/트레이/단일 실행/종료를 격리 프로필로 검사한다.
author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 정상 시작 통합 검사.
#>
param([string]$ExecutablePath = 'WindowBackgroundMemoWidget.exe', [string]$OutputDirectory = 'artifacts/startup', [switch]$UiProbe, [switch]$CrashRestart)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$executable = if ([IO.Path]::IsPathRooted($ExecutablePath)) { $ExecutablePath } else { Join-Path $projectRoot $ExecutablePath }
$evidenceRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectRoot $OutputDirectory }
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
$profile = Join-Path ([IO.Path]::GetTempPath()) ('Notedex-Integration-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $profile | Out-Null
Set-Content -LiteralPath (Join-Path $profile 'integration.marker') -Value 'isolated synthetic integration profile' -Encoding utf8
if ($UiProbe) { Set-Content -LiteralPath (Join-Path $profile 'ui-fixture.request') -Value 'test-owned synthetic memos only' }
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $executable
$start.UseShellExecute = $false
$start.Environment['NOTEDEX_TEST_PROFILE'] = $profile
$first = $null
$second = $null
$report = [ordered]@{ Executable = $executable; SHA256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash; Profile = '%TEMP%/Notedex-Integration-<unique-id>'; Arguments = @(); ShellExecute = $false; Checks = @(); PhysicalUserAcceptance = $false }
try {
    $first = [Diagnostics.Process]::Start($start)
    $probePath = Join-Path $profile 'startup-probe.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    while (-not (Test-Path -LiteralPath $probePath) -and -not $first.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath $probePath)) { throw "Normal startup missing; profile $profile; exited=$($first.HasExited)" }
    $probe = Get-Content -LiteralPath $probePath -Raw | ConvertFrom-Json
    if ($probe.Stage -ne 'ready' -or $probe.Hwnd -eq 0 -or -not $probe.WindowVisible -or -not $probe.TrayCreated) { throw 'Normal window/tray initialization failed.' }
    $report.Checks += 'normal-argument-process-HWND-window-and-tray'
    $second = [Diagnostics.Process]::Start($start)
    if (-not $second.WaitForExit(10000) -or $second.ExitCode -ne 0) { throw 'Second instance did not exit normally.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do { $probe = Get-Content -LiteralPath $probePath -Raw | ConvertFrom-Json; if ($probe.Activations -gt 0) { break }; Start-Sleep -Milliseconds 100 } while ([DateTime]::UtcNow -lt $deadline)
    if ($probe.Activations -lt 1) { throw 'Existing window activation was not observed.' }
    $report.Checks += 'single-instance-and-activation-message'
    if ($UiProbe) {
        $uiResults = Join-Path $profile 'ui-results.json'
        $deadline = [DateTime]::UtcNow.AddSeconds(40)
        while (-not (Test-Path -LiteralPath $uiResults) -and -not $first.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        if (-not (Test-Path -LiteralPath $uiResults)) { throw 'UI integration result missing.' }
        Copy-Item -LiteralPath $uiResults -Destination (Join-Path $evidenceRoot 'ui-results.json')
        Get-ChildItem -LiteralPath $profile -Filter '*.png' -File | Copy-Item -Destination $evidenceRoot
        $uiReport = Get-Content -LiteralPath $uiResults -Raw | ConvertFrom-Json
        if ($uiReport.Result -ne 'PASS') { throw "UI integration failed: $($uiReport.Error)" }
        $report.Checks += 'real-management-window-controls-and-render'
        if ($CrashRestart) {
            $deadline = [DateTime]::UtcNow.AddSeconds(5)
            do { $committed = Get-Content -LiteralPath $probePath -Raw | ConvertFrom-Json; if ($committed.Stage -eq 'ui-complete') { break }; Start-Sleep -Milliseconds 50 } while ([DateTime]::UtcNow -lt $deadline)
            if ($committed.Stage -ne 'ui-complete') { throw 'UI commit completion fingerprint missing.' }
            $first.Kill()
            $first.WaitForExit()
            $first.Dispose()
            Remove-Item -LiteralPath (Join-Path $profile 'ui-fixture.request')
            Remove-Item -LiteralPath $probePath
            $first = [Diagnostics.Process]::Start($start)
            $deadline = [DateTime]::UtcNow.AddSeconds(25)
            while (-not (Test-Path -LiteralPath $probePath) -and -not $first.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
            if (-not (Test-Path -LiteralPath $probePath)) { throw 'Restart after forced termination failed.' }
            $reopened = Get-Content -LiteralPath $probePath -Raw | ConvertFrom-Json
            if ($reopened.NoteCount -ne $committed.NoteCount -or $reopened.ContentSHA256 -ne $committed.ContentSHA256) { throw 'Committed content did not survive forced termination.' }
            $report.Checks += 'forced-process-termination-and-committed-data-restart'
        }
    }
    Set-Content -LiteralPath (Join-Path $profile 'shutdown.request') -Value 'close only test-owned process'
    if (-not $first.WaitForExit(15000) -or $first.ExitCode -ne 0) { throw 'Normal shutdown failed.' }
    $stopped = Get-Content -LiteralPath $probePath -Raw | ConvertFrom-Json
    if ($stopped.Stage -ne 'stopped' -or $stopped.TrayCreated) { throw 'Tray disposal not confirmed.' }
    $report.Checks += 'normal-shutdown-and-tray-disposal'
    $report.Result = 'PASS'
}
catch { $report.Result = 'FAIL'; $report.Error = $_.Exception.Message.Replace($profile, '%TEMP%/Notedex-Integration-<unique-id>'); throw }
finally {
    foreach ($ownedProcess in @($second, $first)) { if ($null -ne $ownedProcess) { if (-not $ownedProcess.HasExited) { $ownedProcess.Kill() }; $ownedProcess.Dispose() } }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'startup-results.json') -Encoding utf8
}
Write-Output "PASS normal startup integration; evidence $evidenceRoot"
