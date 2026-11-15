[CmdletBinding()]
param(
    [string]$Set='fast',
    [switch]$EditMode,
    [switch]$Build,
    [switch]$NoSync,
    [switch]$SkipGates,
    [switch]$NoBuildCache,
    [string]$SelectionPlan,
    [string]$Mirror='..\DakaDaka_ci',
    [string]$UnityExe='C:\Program Files\Unity\Hub\Editor\6000.2.5f1\Editor\Unity.exe',
    [ValidateRange(60,86400)][int]$TimeoutSeconds=7200
)
# Runs gates (one batch Unity on the CI mirror), optional EditMode tests and optional playable build.
# Usage: Tools\Run-All.ps1 [-Set fast|combat|net|visual|all|Gate1,Gate2] [-EditMode] [-Build] [-NoSync] [-Mirror ..\DakaDaka_ci]
$ErrorActionPreference='Stop'
if($SkipGates -and !$EditMode -and !$Build){throw '-SkipGates requires -EditMode or -Build'}
if($SkipGates){$Set='none'}
$env:EMBER_BUILD_CACHE=if($NoBuildCache){'0'}else{'1'}
$sw=[Diagnostics.Stopwatch]::StartNew()
$main=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if([IO.Path]::IsPathRooted($Mirror)){$ci=[IO.Path]::GetFullPath($Mirror)}else{$ci=[IO.Path]::GetFullPath((Join-Path $main $Mirror))}
if(!(Test-Path -LiteralPath $UnityExe -PathType Leaf)){Write-Output 'REFUSED: Unity executable missing'; exit 1}

# --- refuse if another tester owns the mirror
$statusPath=Join-Path $main 'Logs\local-testing\status.json'
if(Test-Path -LiteralPath $statusPath){
    $st=$null
    try{$st=Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json}catch{$st=$null}
    if($st -and ([string]$st.state) -eq 'running'){Write-Output ('REFUSED: ' + $statusPath + ' says state=running (another tester owns the mirror)'); exit 1}
}
if(Test-Path -LiteralPath (Join-Path $ci 'Temp\UnityLockfile')){Write-Output ('REFUSED: mirror is locked (' + (Join-Path $ci 'Temp\UnityLockfile') + ')'); exit 1}

$stamp=Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$outDir=Join-Path $main ('Logs\runs\' + $stamp)
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$gatesOut=Join-Path $outDir 'gates'
if($SelectionPlan){Copy-Item -LiteralPath $SelectionPlan -Destination (Join-Path $outDir 'selection-plan.json')}
Write-Output ('BUILD CACHE '+$env:EMBER_BUILD_CACHE)
$lines=New-Object System.Collections.Generic.List[string]
$allPassed=$true

function Invoke-Unity([string[]]$UnityArgs,[int]$Limit){
    $p=Start-Process -FilePath $UnityExe -ArgumentList $UnityArgs -PassThru -WindowStyle Hidden
    $null=$p.Handle
    $t0=[DateTime]::UtcNow
    $timedOut=$false
    while(!$p.WaitForExit(2000)){
        if(([DateTime]::UtcNow-$t0).TotalSeconds -gt $Limit){
            $timedOut=$true
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            $p.WaitForExit()
            break
        }
    }
    $p.Refresh()
    return [pscustomobject]@{ExitCode=$p.ExitCode;TimedOut=$timedOut}
}

# --- sync main -> mirror
if(!$NoSync){
    Write-Output ('SYNC ' + $main + ' -> ' + $ci)
    $syncLog=Join-Path $outDir 'sync.log'
    & robocopy $main $ci /MIR /XD Library Temp Logs Builds UserSettings obj .git .vs /XF *.csproj *.sln *.user *.suo | Out-File -LiteralPath $syncLog -Encoding ascii
    if($LASTEXITCODE -ge 8){Write-Output ('FAIL: robocopy main sync exit ' + $LASTEXITCODE + ' (see ' + $syncLog + ')'); exit 1}
    $assets=Join-Path $main 'Logs\v2_assets'
    if(Test-Path -LiteralPath $assets){
        & robocopy $assets (Join-Path $ci 'Logs\v2_assets') /MIR | Out-File -LiteralPath $syncLog -Encoding ascii -Append
        if($LASTEXITCODE -ge 8){Write-Output ('FAIL: robocopy v2_assets exit ' + $LASTEXITCODE); exit 1}
    }
}elseif(!(Test-Path -LiteralPath (Join-Path $ci 'Assets'))){Write-Output 'REFUSED: -NoSync but mirror has no Assets'; exit 1}

# --- EditMode tests
$editSummary=$null
if($EditMode){
    $edOut=Join-Path $outDir 'editmode'
    Write-Output 'EDITMODE running'
    $edScript=Join-Path $PSScriptRoot 'Run-EmberdelveEditModeTests.ps1'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $edScript -ProjectPath $ci -TestFilter 'Emberdelve' -OutputDirectory $edOut | Out-Null
    $edExit=$LASTEXITCODE
    $edJson=Join-Path $edOut 'summary.json'
    if(Test-Path -LiteralPath $edJson){
        $e=Get-Content -LiteralPath $edJson -Raw | ConvertFrom-Json
        $editSummary=[pscustomobject]@{passed=[bool]$e.passed;tests=[int]$e.tests;failed=[int]$e.failed;summary=$edJson}
        $word='FAIL'; if($e.passed){$word='PASS'}
        $lines.Add($word + ' EditMode tests=' + $e.tests + ' failed=' + $e.failed + ' ' + $e.elapsedSeconds + 's')
        foreach($pr in @($e.problems | Select-Object -First 3)){$lines.Add('  ' + $pr)}
        if(!$e.passed){$allPassed=$false}
    }else{
        $editSummary=[pscustomobject]@{passed=$false;tests=0;failed=0;summary=$null}
        $lines.Add('FAIL EditMode no summary produced (exit ' + $edExit + ')')
        $allPassed=$false
    }
}

# --- gates: ONE batch Unity process
$gatesSummary=$null
$cacheEvents=@()
if(!$SkipGates){
    $gatesSummary=Join-Path $gatesOut 'summary.json'
    New-Item -ItemType Directory -Path $gatesOut -Force | Out-Null
    $unityLog=Join-Path $gatesOut 'unity.log'
    Write-Output ('GATES set=' + $Set)
    $gateArgs=@('-batchmode','-nographics','-projectPath',('"' + $ci + '"'),'-executeMethod','Emberdelve.EditorTools.EmberdelveGateSuite.RunFromCommandLine','-emberGates',('"' + $Set + '"'),'-emberOut',('"' + $gatesOut + '"'),'-logFile',('"' + $unityLog + '"'))
    $g=Invoke-Unity $gateArgs $TimeoutSeconds
    $gatesPassed=$false
    if(Test-Path -LiteralPath $gatesSummary){
        $gj=Get-Content -LiteralPath $gatesSummary -Raw | ConvertFrom-Json
        $gatesPassed=([bool]$gj.passed -and $g.ExitCode -eq 0 -and !$g.TimedOut)
        $gtxt=Join-Path $gatesOut 'summary.txt'
        if(Test-Path -LiteralPath $gtxt){foreach($l in (Get-Content -LiteralPath $gtxt)){$lines.Add($l)}}
    }else{
        $lines.Add('FAIL gates: no summary.json produced (exit ' + $g.ExitCode + ' timedOut=' + $g.TimedOut + ', see ' + $unityLog + ')')
    }
    if($g.TimedOut){$lines.Add('FAIL gates: whole batch timed out after ' + $TimeoutSeconds + 's')}
    if(!$gatesPassed){$allPassed=$false}
    if(Test-Path -LiteralPath $unityLog){
        # Unity or an indexing process may still hold the log while final metadata is collected.
        # Cache diagnostics are optional; they must not discard the gate's recorded verdict.
        $cacheReader=$null
        try {
            $cacheStream=[IO.File]::Open($unityLog,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            $cacheReader=New-Object IO.StreamReader($cacheStream)
            $cacheEvents=@(while(($cacheLine=$cacheReader.ReadLine()) -ne $null){
                if($cacheLine.StartsWith('[Emberdelve][BuildCache] ')){$cacheLine}
            })
        }catch [IO.IOException]{
            $lines.Add('WARN optional build-cache log metadata unavailable: ' + $_.Exception.Message)
        }finally{if($cacheReader){$cacheReader.Dispose()}}
        foreach($cacheEvent in $cacheEvents){$lines.Add($cacheEvent)}
    }

}

# --- playable build (on the mirror), then copy back
$buildPath=$null
if($Build){
    $ciBuildDir=Join-Path $ci ('Builds\Playable\' + $stamp)
    $ciExe=Join-Path $ciBuildDir 'EmberdelveNet.exe'
    $buildLog=Join-Path $outDir 'build.log'
    Write-Output 'BUILD playable'
    $buildArgs=@('-batchmode','-nographics','-quit','-projectPath',('"' + $ci + '"'),'-logFile',('"' + $buildLog + '"'),'-executeMethod','Emberdelve.EditorTools.EmberdelveWindowsBuild.BuildNetPlayable','--ember-build-output',('"' + $ciExe + '"'))
    $b=Invoke-Unity $buildArgs $TimeoutSeconds
    if($b.ExitCode -eq 0 -and !$b.TimedOut -and (Test-Path -LiteralPath $ciExe)){
        $mainBuildDir=Join-Path $main ('Builds\Playable\' + $stamp)
        New-Item -ItemType Directory -Path $mainBuildDir -Force | Out-Null
        & robocopy $ciBuildDir $mainBuildDir /E /NFL /NDL /NJH /NJS /NP | Out-Null
        if($LASTEXITCODE -lt 8){
            $buildPath=Join-Path $mainBuildDir 'EmberdelveNet.exe'
            $lines.Add('PASS Build ' + $buildPath)
        }else{$lines.Add('FAIL Build copy-back robocopy exit ' + $LASTEXITCODE); $allPassed=$false}
    }else{
        $lines.Add('FAIL Build exit=' + $b.ExitCode + ' timedOut=' + $b.TimedOut + ' (see ' + $buildLog + ')')
        $allPassed=$false
    }
}

# --- results
$elapsed=[Math]::Round($sw.Elapsed.TotalSeconds,1)
$final='FAIL'; if($allPassed){$final='PASS'}
$lines.Add('RESULT ' + $final + ' set=' + $Set + ' elapsed=' + $elapsed + 's')
$summaryTxt=Join-Path $outDir 'summary.txt'
[IO.File]::WriteAllText($summaryTxt,(($lines -join [Environment]::NewLine) + [Environment]::NewLine),(New-Object System.Text.UTF8Encoding($false)))
$run=[pscustomobject]@{passed=$allPassed;set=$Set;stamp=$stamp;editmode=$editSummary;gates=$gatesSummary;build=$buildPath;buildCache=(!$NoBuildCache);cacheEvents=$cacheEvents;selectionPlan=$SelectionPlan;skipGates=[bool]$SkipGates;elapsedSeconds=$elapsed;summary=$summaryTxt}
[IO.File]::WriteAllText((Join-Path $outDir 'run.json'),($run | ConvertTo-Json -Depth 6),(New-Object System.Text.UTF8Encoding($false)))
Get-Content -LiteralPath $summaryTxt | ForEach-Object {Write-Output $_}
Write-Output ('Evidence: ' + $outDir)
if($allPassed){exit 0}else{exit 1}
