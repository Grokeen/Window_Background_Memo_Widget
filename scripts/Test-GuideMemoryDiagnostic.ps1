<#
name: Test-GuideMemoryDiagnostic.ps1; desc: 실제 검증 DLL을 사용하는 별도 WPF harness의 N/A/B/C fresh-process 진단을 직렬 실행한다.
author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 Stage2 memory isolation.
#>
#Requires -Version 7.2
param(
    [string]$ProductDll = 'src/WindowBackgroundMemoWidget/bin/Release/net10.0-windows/WindowBackgroundMemoWidget.dll',
    [string]$HarnessExecutable = 'artifacts/guide-memory-stage2/harness/GuideMemoryDiagnostic.exe',
    [string]$OutputDirectory = 'artifacts/guide-memory-stage2/initial',
    [ValidateRange(1,5)][int]$Runs = 1,
    [string]$WorkspaceRoot = ''
)
$ErrorActionPreference='Stop'
$projectRoot=if($WorkspaceRoot){$WorkspaceRoot}else{Split-Path $PSScriptRoot -Parent}
function Resolve-DiagnosticPath([string]$path) { if([IO.Path]::IsPathRooted($path)) { return $path }; return Join-Path $projectRoot $path }
$sourceDll=Resolve-DiagnosticPath $ProductDll
$harness=Resolve-DiagnosticPath $HarnessExecutable
$output=Resolve-DiagnosticPath $OutputDirectory
New-Item -ItemType Directory -Path $output -Force | Out-Null
$sourceHash=(Get-FileHash -LiteralPath $sourceDll -Algorithm SHA256).Hash
$summary=[ordered]@{ Result='DIAGNOSTIC-INCOMPLETE'; ProductDllSHA256=$sourceHash; HarnessSHA256=(Get-FileHash -LiteralPath $harness -Algorithm SHA256).Hash; DiagnosticOnly=$true; UserDataTouched=$false; PrivateWorkingSetMeasured=$false; VRAMMeasured=$false; Runs=@() }
try {
    for($iteration=1; $iteration -le $Runs; $iteration++) {
        foreach($mode in @('N','A','B','C')) {
            if((Get-FileHash -LiteralPath $sourceDll -Algorithm SHA256).Hash -ne $sourceHash) { throw 'Product DLL changed during diagnostic sequence.' }
            $diagnosticProfile=Join-Path ([IO.Path]::GetTempPath()) ('Notedex-Integration-'+[guid]::NewGuid().ToString('N'))
            $store=Join-Path $diagnosticProfile 'markdown-v1'
            foreach($folder in @('notes','.trash','.widget','.backups','.conflicts')) { New-Item -ItemType Directory -Path (Join-Path $store $folder) -Force | Out-Null }
            [IO.File]::WriteAllText((Join-Path $diagnosticProfile 'integration.marker'),'synthetic guide memory only')
            [IO.File]::WriteAllText((Join-Path $diagnosticProfile 'user-preferences.json'),'{"Version":1,"DoNotShowUserGuide":true}',[Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText((Join-Path $store '.widget/store.json'),'{"Version":1,"Format":"Notedex.Markdown"}',[Text.UTF8Encoding]::new($false))
            # 기존 quality와 같은10×10000자 본문·배치 부하다. 실제 사용자 프로필을 사용하지 않는다.
            $body=("- [ ] 품질 검사`n`n"+('메모 내용 한글 ABC '*900)).Substring(0,10000)
            $bytes=[Text.Encoding]::UTF8.GetBytes($body)
            $bodyHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
            $notes=@();$originalFiles=@()
            for($index=0;$index -lt 10;$index++) {
                $id=[guid]::NewGuid().ToString('D');$relative="notes/$id.md";$file=Join-Path $store $relative
                [IO.File]::WriteAllBytes($file,$bytes);$originalFiles+=$file
                $notes += [ordered]@{Id=$id;Title="품질 검사 $index";CreatedAt=[DateTimeOffset]::UtcNow;UpdatedAt=[DateTimeOffset]::UtcNow;FolderId=$null;Tags=@();IsPinned=$false;Priority=0;IsDeleted=$false;IsVisible=$true;X=(40+($index%4)*320);Y=(40+[math]::Floor($index/4)*240);Width=300;Height=220;IsLocked=$false;DocumentRevision=0;ContentHash=$bodyHash;AutoTitle=$false;RelativePath=$relative;Appearance=$null;Placement=$null}
            }
            $workspace=@{Version=1;Revision=1;Notes=$notes;Folders=@();GlobalHidden=$false;StartWithWindows=$false}
            [IO.File]::WriteAllText((Join-Path $store '.widget/workspace.json'),($workspace|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
            $resultPath=Join-Path $output ("mode-$mode-run-$iteration.json")
            $config=Join-Path $diagnosticProfile 'diagnostic-config.json'
            [IO.File]::WriteAllText($config,(@{SourceDll=$sourceDll;Profile=$diagnosticProfile;Output=$resultPath;Mode=$mode}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
            $start=[Diagnostics.ProcessStartInfo]::new()
            $start.FileName=$harness;$start.UseShellExecute=$false;$start.WorkingDirectory=$projectRoot
            $start.Environment['NOTEDEX_TEST_PROFILE']=$diagnosticProfile
            $start.Environment['NOTEDEX_GUIDE_DIAGNOSTIC_CONFIG']=$config
            $runtime=Join-Path $env:LOCALAPPDATA 'NotedexTooling/dotnet'
            if(Test-Path -LiteralPath (Join-Path $runtime 'dotnet.exe')){$start.Environment['DOTNET_ROOT']=$runtime}
            $owned=[Diagnostics.Process]::Start($start)
            try {
                if(-not $owned.WaitForExit(70000)){throw 'Diagnostic-owned process did not close within 70 seconds.'}
                if(-not(Test-Path -LiteralPath $resultPath)){throw "Mode $mode did not produce diagnostic output."}
                $result=Get-Content -LiteralPath $resultPath -Raw|ConvertFrom-Json
                if($owned.ExitCode -ne 0 -or $result.Result -ne 'DIAGNOSTIC-COMPLETE' -or -not $result.SourceDllUnchanged){throw "Mode $mode diagnostic failed: $($result.Error)"}
                foreach($original in $originalFiles){if((Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -ne $bodyHash){throw 'Diagnostic changed a canonical note body.'}}
                $summary.Runs += @{Mode=$mode;Iteration=$iteration;File=[IO.Path]::GetFileName($resultPath);PeakPrivateBytes=($result.Samples.PrivateBytes|Measure-Object -Maximum).Maximum;FirstPrivateBytes=$result.Samples[0].PrivateBytes;LastPrivateBytes=$result.Samples[-1].PrivateBytes;MaximumLoadedPages=($result.Samples.LoadedPageCount|Measure-Object -Maximum).Maximum;NormalExit=$true;CanonicalBodiesUnchanged=$true}
                Write-Output "Diagnostic completed: mode $mode run $iteration (real manager + ten widgets)."
            }
            finally { if(-not $owned.HasExited){$owned.Kill();$owned.WaitForExit()};$owned.Dispose() }
        }
    }
    $summary.Result='DIAGNOSTIC-COMPLETE'
}
catch { $summary.Error=$_.Exception.Message;throw }
finally { $summary|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8 }
