<#
name         : Test-NotesQuality.ps1
desc         : 실제 사용자 설정과 분리한 메모10개×10000자 프로필로 5회 시작/유휴 CPU/Private Bytes를 측정한다.
author       : NotedexOrig2
create date  : 2026-10-24
update date  : 2026-10-24 P0 비기능 측정 추가. 렌더 완료·8시간/물리 셸 인수와 구분한다.
#>
#Requires -Version 7.2
param(
    [string]$ExecutablePath = 'src/WindowBackgroundMemoWidget/bin/Release/net10.0-windows/WindowBackgroundMemoWidget.exe',
    [string]$OutputDirectory = 'artifacts/notes-quality',
    [ValidateRange(1,5)][int]$LaunchCount = 5,
    [ValidateRange(10,75)][int]$IdleSeconds = 60,
    [switch]$ShowUserGuide
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$executable = if ([IO.Path]::IsPathRooted($ExecutablePath)) { $ExecutablePath } else { Join-Path $projectRoot $ExecutablePath }
$evidence = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectRoot $OutputDirectory }
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$qualityProfile = Join-Path ([IO.Path]::GetTempPath()) ('Notedex-Integration-' + [guid]::NewGuid().ToString('N'))
$store = Join-Path $qualityProfile 'markdown-v1'
foreach ($folder in @('notes','.trash','.widget','.backups','.conflicts')) { New-Item -ItemType Directory -Path (Join-Path $store $folder) -Force | Out-Null }
Set-Content -LiteralPath (Join-Path $qualityProfile 'integration.marker') -Value 'synthetic ten-note quality profile only' -Encoding utf8
# 2026-10-24 NotedexOrig2: 재실행/첫 실행 조건을 명시하고 안내 이미지의 비용을 암묵적으로 제외하지 않는다.
if (-not $ShowUserGuide) { [IO.File]::WriteAllText((Join-Path $qualityProfile 'user-preferences.json'), '{"Version":1,"DoNotShowUserGuide":true}', [Text.UTF8Encoding]::new($false)) }
[IO.File]::WriteAllText((Join-Path $store '.widget/store.json'), '{"Version":1,"Format":"Notedex.Markdown"}', [Text.UTF8Encoding]::new($false))
$notes = @()
$expected = @{}
$body = ("- [ ] 품질 검사`n`n" + ('메모 내용 한글 ABC ' * 900)).Substring(0,10000)
$bytes = [Text.Encoding]::UTF8.GetBytes($body)
$bodyHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
for ($index = 0; $index -lt 10; $index++) {
    $id = [guid]::NewGuid().ToString('D')
    $relative = "notes/$id.md"
    [IO.File]::WriteAllBytes((Join-Path $store $relative),$bytes)
    $expected[$relative] = $bodyHash
    $notes += [ordered]@{ Id=$id;Title="품질 검사 $index";CreatedAt=[DateTimeOffset]::UtcNow;UpdatedAt=[DateTimeOffset]::UtcNow;FolderId=$null;Tags=@();IsPinned=$false;Priority=0;IsDeleted=$false;IsVisible=$true;X=(40+($index%4)*320);Y=(40+[math]::Floor($index/4)*240);Width=300;Height=220;IsLocked=$false;DocumentRevision=0;ContentHash=$bodyHash;AutoTitle=$false;RelativePath=$relative;Appearance=$null;Placement=$null }
}
$workspace = @{Version=1;Revision=1;Notes=$notes;Folders=@();GlobalHidden=$false;StartWithWindows=$false}
[IO.File]::WriteAllText((Join-Path $store '.widget/workspace.json'),($workspace | ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
$report = [ordered]@{ Result='FAIL';Executable=$executable;SHA256=(Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash;Profile='%TEMP%/Notedex-Integration-<quality-id>';Notes=10;CharactersPerNote=10000;LogicalProcessors=[Environment]::ProcessorCount;StartupReadyMilliseconds=@();IdleSeconds=$IdleSeconds;PhysicalImeDpiShellAccepted=$false;EightHourEnduranceTested=$false;StartupMeasurement='process start to app ready diagnostic; compositor/display completion not measured';UserDataTouched=$false }
$report.UserGuideScenario = if ($ShowUserGuide) { 'first-run: automatic PDF guide visible before ready' } else { 'returning-user: automatic PDF guide disabled by saved preference' }
$codeDll=[IO.Path]::ChangeExtension($executable,'.dll')
$report.CodeDllSha256=if(Test-Path -LiteralPath $codeDll){(Get-FileHash -LiteralPath $codeDll -Algorithm SHA256).Hash}else{$null}
$report.StartupDiagnostics=@()
$owned = $null
function Stop-OwnedQualityProcess {
    if ($null -eq $owned) { return }
    if (-not $owned.HasExited) {
        Set-Content -LiteralPath (Join-Path $qualityProfile 'shutdown.request') -Value 'close test owned process'
        if (-not $owned.WaitForExit(15000)) { $owned.Kill();$owned.WaitForExit();throw 'Quality process failed normal shutdown.' }
    }
    if ($owned.ExitCode -ne 0) { throw 'Quality process returned failure.' }
}
try {
    for ($run = 0; $run -lt $LaunchCount; $run++) {
        foreach ($name in @('startup-probe.json','shutdown.request')) { $path=Join-Path $qualityProfile $name;if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path} }
        $start=[Diagnostics.ProcessStartInfo]::new()
        $start.FileName=$executable;$start.UseShellExecute=$false;$start.WorkingDirectory=$projectRoot
        $start.Environment['NOTEDEX_TEST_PROFILE']=$qualityProfile
        $runtime=Join-Path $env:LOCALAPPDATA 'NotedexTooling/dotnet'
        if(Test-Path -LiteralPath (Join-Path $runtime 'dotnet.exe')){$start.Environment['DOTNET_ROOT']=$runtime}
        $watch=[Diagnostics.Stopwatch]::StartNew()
        $owned=[Diagnostics.Process]::Start($start)
        $probePath=Join-Path $qualityProfile 'startup-probe.json'
        while(-not(Test-Path -LiteralPath $probePath) -and -not $owned.HasExited -and $watch.Elapsed.TotalSeconds -lt 25){Start-Sleep -Milliseconds 25}
        if(-not(Test-Path -LiteralPath $probePath)){throw 'Ten-note startup did not become ready.'}
        $probe=Get-Content -LiteralPath $probePath -Raw | ConvertFrom-Json
        if($probe.Stage -ne 'ready' -or $probe.NoteCount -ne 10 -or -not $probe.TrayCreated){throw 'Ten-note startup state is invalid.'}
        if ($probe.GuideVisible -ne [bool]$ShowUserGuide) { throw 'Guide visibility does not match the measured scenario.' }
        $report.StartupReadyMilliseconds += [math]::Round($watch.Elapsed.TotalMilliseconds,1)
        $report.StartupDiagnostics += $probe.StartupDiagnostics
        if($run -eq $LaunchCount-1){
            Start-Sleep -Seconds 5
            $owned.Refresh();$lastCpu=$owned.TotalProcessorTime.TotalMilliseconds
            $idleWatch=[Diagnostics.Stopwatch]::StartNew();$peakPrivate=0L;$samples=@()
            for($sample=0;$sample -lt $IdleSeconds;$sample++){
                Start-Sleep -Seconds 1
                if($owned.HasExited -or -not $owned.Responding){throw 'Quality process exited or stopped responding.'}
                $owned.Refresh();$peakPrivate=[math]::Max($peakPrivate,$owned.PrivateMemorySize64)
                $samples += $owned.PrivateMemorySize64
            }
            $cpu=100*($owned.TotalProcessorTime.TotalMilliseconds-$lastCpu)/$idleWatch.Elapsed.TotalMilliseconds/[Environment]::ProcessorCount
            $report.AverageMachineCpuPercent=[math]::Round($cpu,3)
            $report.PeakPrivateBytes=$peakPrivate
            $report.AveragePrivateBytes=[math]::Round(($samples | Measure-Object -Average).Average)
            $report.IdleCpuTargetMet=($cpu -le 1)
            $report.PrivateBytesTargetMet=($peakPrivate -le 150MB)
        }
        Stop-OwnedQualityProcess
        $owned.Dispose();$owned=$null
    }
    foreach($relative in $expected.Keys){if((Get-FileHash -LiteralPath (Join-Path $store $relative) -Algorithm SHA256).Hash -ne $expected[$relative]){throw 'Quality test changed canonical note text.'}}
    $report.CanonicalBodiesUnchanged=$true
    $report.ReadyWithinThreeSeconds=(@($report.StartupReadyMilliseconds | Where-Object {$_ -gt 3000}).Count -eq 0)
    $report.Result=if($report.IdleCpuTargetMet -and $report.PrivateBytesTargetMet -and $report.ReadyWithinThreeSeconds){'PASS'}else{'FAIL'}
}
catch { $report.Error=$_.Exception.Message.Replace($qualityProfile,'%TEMP%/Notedex-Integration-<quality-id>');throw }
finally {
    if($null -ne $owned){if(-not $owned.HasExited){$owned.Kill();$owned.WaitForExit()};$owned.Dispose()}
    $report | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $evidence 'quality-results.json') -Encoding utf8
}
$report | ConvertTo-Json -Depth 7
if($report.Result -ne 'PASS'){throw 'Measured quality target failed; see quality-results.json.'}
