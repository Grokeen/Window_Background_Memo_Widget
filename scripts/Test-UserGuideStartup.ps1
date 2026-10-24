<#
name: Test-UserGuideStartup.ps1; desc: 격리 프로필에서 첫 안내/미체크 종료 뒤 재표시/저장된 억제 선호를 실제 재실행으로 확인한다.
author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 PDF 안내 수명 검사.
#>
#Requires -Version 7.2
param(
    [string]$ExecutablePath = 'src/WindowBackgroundMemoWidget/bin/Release/net10.0-windows/WindowBackgroundMemoWidget.exe',
    [string]$OutputDirectory = 'artifacts/user-guide-startup'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$executable = if ([IO.Path]::IsPathRooted($ExecutablePath)) { $ExecutablePath } else { Join-Path $projectRoot $ExecutablePath }
$evidence = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectRoot $OutputDirectory }
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$guideProfile = Join-Path ([IO.Path]::GetTempPath()) ('Notedex-Integration-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $guideProfile | Out-Null
[IO.File]::WriteAllText((Join-Path $guideProfile 'integration.marker'), 'synthetic guide startup profile only')
$adjacentCodeDll = [IO.Path]::ChangeExtension($executable,'.dll')
$executableVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
$report = [ordered]@{ Result='FAIL'; Executable=$executable; SHA256=(Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash; FileVersion=$executableVersion.FileVersion; ProductVersion=$executableVersion.ProductVersion; CodeDllSHA256=if(Test-Path -LiteralPath $adjacentCodeDll){(Get-FileHash -LiteralPath $adjacentCodeDll -Algorithm SHA256).Hash}else{$null}; UserDataTouched=$false; Checks=@(); CheckboxUiInteraction='separate WPF UI probe'; Launches=@() }
function Invoke-GuideLaunch([bool]$ExpectedGuideVisible) {
    foreach ($name in @('startup-probe.json','shutdown.request')) {
        $path = Join-Path $guideProfile $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName=$executable; $start.UseShellExecute=$false; $start.WorkingDirectory=$projectRoot
    $start.Environment['NOTEDEX_TEST_PROFILE']=$guideProfile
    $runtime = Join-Path $env:LOCALAPPDATA 'NotedexTooling/dotnet'
    if (Test-Path -LiteralPath (Join-Path $runtime 'dotnet.exe')) { $start.Environment['DOTNET_ROOT']=$runtime }
    $ownedGuideProcess = [Diagnostics.Process]::Start($start)
    try {
        $probePath = Join-Path $guideProfile 'startup-probe.json'
        $deadline = [DateTime]::UtcNow.AddSeconds(25)
        while (-not (Test-Path -LiteralPath $probePath) -and -not $ownedGuideProcess.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
        if (-not (Test-Path -LiteralPath $probePath)) { throw 'Guide startup did not become ready.' }
        $probe = Get-Content -LiteralPath $probePath -Raw | ConvertFrom-Json
        if ($probe.Stage -ne 'ready' -or -not $probe.TrayCreated -or $probe.GuideVisible -ne $ExpectedGuideVisible -or $probe.GuideAutomatic -ne $ExpectedGuideVisible) { throw 'Guide visibility or automatic invocation did not match the expected preference.' }
        [IO.File]::WriteAllText((Join-Path $guideProfile 'shutdown.request'), 'normal close test-owned process')
        if (-not $ownedGuideProcess.WaitForExit(15000) -or $ownedGuideProcess.ExitCode -ne 0) { throw 'Guide-owned normal shutdown failed.' }
        $stopped = Get-Content -LiteralPath $probePath -Raw | ConvertFrom-Json
        if ($stopped.Stage -ne 'stopped' -or $stopped.TrayCreated -or $stopped.GuideVisible) { throw 'Guide or tray survived normal shutdown.' }
        return [pscustomobject]@{ ExpectedVisible=$ExpectedGuideVisible; GuideVisible=$probe.GuideVisible; GuideAutomatic=$probe.GuideAutomatic; NormalShutdown=$true }
    }
    finally {
        if (-not $ownedGuideProcess.HasExited) { $ownedGuideProcess.Kill(); $ownedGuideProcess.WaitForExit() }
        $ownedGuideProcess.Dispose()
    }
}
try {
    $report.Launches += Invoke-GuideLaunch $true
    $preferencesPath = Join-Path $guideProfile 'user-preferences.json'
    $unchecked = Get-Content -LiteralPath $preferencesPath -Raw | ConvertFrom-Json
    if ($unchecked.Version -ne 1 -or $unchecked.DoNotShowUserGuide) { throw 'Unchecked automatic guide was not persisted correctly.' }
    $report.Checks += 'first-run automatic guide and unchecked preference on close'
    $report.Launches += Invoke-GuideLaunch $true
    $report.Checks += 'unchecked preference reopens automatic guide on real process restart'
    # 이 fixture는 UI 클릭 검사를 대신하지 않는다. UI가 쓴 것과 동일한 schema의 선호를 앱이 다음 실행에서 읽는지 검사한다.
    [IO.File]::WriteAllText($preferencesPath, '{"Version":1,"DoNotShowUserGuide":true}', [Text.UTF8Encoding]::new($false))
    $savedPreferenceHash = (Get-FileHash -LiteralPath $preferencesPath -Algorithm SHA256).Hash
    $report.Launches += Invoke-GuideLaunch $false
    if ((Get-FileHash -LiteralPath $preferencesPath -Algorithm SHA256).Hash -ne $savedPreferenceHash) { throw 'Suppressed guide unnecessarily rewrote preference.' }
    $report.Checks += 'saved checked preference suppresses automatic guide after restart without rewrite'
    foreach ($invalidSetting in @('{"Version":999,"DoNotShowUserGuide":true}', '{broken', (' ' * 4097))) {
        [IO.File]::WriteAllText($preferencesPath,$invalidSetting,[Text.UTF8Encoding]::new($false))
        $invalidPreferenceHash=(Get-FileHash -LiteralPath $preferencesPath -Algorithm SHA256).Hash
        $report.Launches += Invoke-GuideLaunch $true
        if ((Get-FileHash -LiteralPath $preferencesPath -Algorithm SHA256).Hash -ne $invalidPreferenceHash) { throw 'Unsupported or malformed preference was overwritten during normal startup/shutdown.' }
        $report.Checks += ('unsupported malformed oversized setting starts safely and preserves bytes: ' + $invalidSetting.Length)
    }
    $report.Result = 'PASS'
}
catch { $report.Error=$_.Exception.Message.Replace($guideProfile,'%TEMP%/Notedex-Integration-<guide-id>'); throw }
finally { $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'user-guide-startup-results.json') -Encoding utf8 }
Write-Output "PASS guide startup/restart integration; evidence $evidence"
