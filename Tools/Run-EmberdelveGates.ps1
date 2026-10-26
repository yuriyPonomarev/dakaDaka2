[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ProjectPath,
    [Parameter(Mandatory=$true)][string[]]$Methods,
    [string]$UnityExe='C:\Program Files\Unity\Hub\Editor\6000.2.5f1\Editor\Unity.exe',
    [string]$OutputDirectory,
    [ValidateRange(30,3600)][int]$TimeoutSeconds=900
)
$ErrorActionPreference='Stop'
$resolvedProject=(Resolve-Path -LiteralPath $ProjectPath).Path
if(!(Test-Path -LiteralPath $UnityExe -PathType Leaf)){throw 'Unity executable does not exist'}
if(!(Test-Path -LiteralPath (Join-Path $resolvedProject 'Assets') -PathType Container)){throw 'Not a Unity project'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $resolvedProject ('Logs/gates-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))}
$resolvedOutput=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
$summaryPath=Join-Path $resolvedOutput 'summary.json'
$results=[Collections.Generic.List[object]]::new()
$anyFailed=$false
foreach($method in $Methods){
    if($method -notmatch '^[A-Za-z_][A-Za-z0-9_.]+$'){throw "Invalid execute method: $method"}
    $logPath=Join-Path $resolvedOutput ($method+'.log')
    if(Test-Path -LiteralPath $logPath){throw "Refusing to overwrite existing evidence: $logPath"}
    if(Test-Path -LiteralPath (Join-Path $resolvedProject 'Temp/UnityLockfile')){throw 'Unity project is already locked; close its Editor first'}
    $arguments=@('-batchmode','-nographics','-projectPath',('"'+$resolvedProject+'"'),'-executeMethod',$method,'-logFile',('"'+$logPath+'"'))
    $started=[DateTime]::UtcNow
    $ownedProcess=Start-Process -FilePath $UnityExe -ArgumentList $arguments -PassThru -WindowStyle Hidden
    Write-Output ('START '+$method+' pid='+$ownedProcess.Id)
    $timedOut=$false
    while(!$ownedProcess.WaitForExit(1000)){
        if(([DateTime]::UtcNow-$started).TotalSeconds -gt $TimeoutSeconds){
            $timedOut=$true
            Stop-Process -Id $ownedProcess.Id -Force -ErrorAction SilentlyContinue
            $ownedProcess.WaitForExit()
            break
        }
    }
    $ownedProcess.Refresh()
    $exitCode=$ownedProcess.ExitCode
    $logText=if(Test-Path -LiteralPath $logPath){[IO.File]::ReadAllText($logPath)}else{''}
    $positive=@([regex]::Matches($logText,'(?m)^.*\]\s+PASS\b[^\r\n]*\bchecks=\d+[^\r\n]*\bfailures=0\b[^\r\n]*') | ForEach-Object {$_.Value.Trim()})
    $bad=@([regex]::Matches($logText,'(?m)^.*(?:error CS\d+|\]\s+(?:FAIL\b|mismatch\b)|[A-Za-z]+Exception:|Aborting batchmode due to failure)[^\r\n]*') | ForEach-Object {$_.Value.Trim()} | Select-Object -Unique)
    # com.unity.pipeline (Unity MCP bridge) can log an ObjectDisposedException from its HTTP listener while a batch
    # Editor shuts down. That is editor tooling noise, not game code; ignore only that exact prefix.
    $bad=@($bad | Where-Object {$_ -notmatch '^Pipeline: unhandled error serving '})
    # This fixture deliberately throws through an event subscriber to verify atomic restore.
    # Permit only its exact diagnostic under its exact entrypoint; all other exceptions fail.
    $expectedDiagnostics=@()
    if($method -eq 'Emberdelve.EditorTools.EmberdelveContainerSnapshotGate.Run'){
        $expectedLine='InvalidOperationException: expected test exception from a throwing subscriber'
        $expectedDiagnostics=@($bad | Where-Object {$_ -eq $expectedLine})
        $bad=@($bad | Where-Object {$_ -ne $expectedLine})
    }
    if($method -eq 'Emberdelve.EditorTools.EmberdelveInventoryAuthorityGate.Run'){
        $expectedLine='InvalidOperationException: expected inventory use observer exception'
        $expectedDiagnostics=@($bad | Where-Object {$_ -eq $expectedLine})
        $bad=@($bad | Where-Object {$_ -ne $expectedLine})
    }
    $passed=(!$timedOut -and $exitCode -eq 0 -and $positive.Count -ge 1 -and $bad.Count -eq 0)
    $result=[pscustomobject]@{
        method=$method; passed=$passed; exitCode=$exitCode; timedOut=$timedOut
        elapsedSeconds=[Math]::Round(([DateTime]::UtcNow-$started).TotalSeconds,2)
        summaries=$positive; problems=$bad; expectedDiagnostics=$expectedDiagnostics; log=$logPath
    }
    $results.Add($result)
    [pscustomobject]@{project=$resolvedProject; passed=(@($results | Where-Object {!$_.passed}).Count -eq 0); results=@($results.ToArray())} |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    $statusLabel=if($passed){'PASS '}else{'FAIL '}
    Write-Output ($statusLabel+$method+' exit='+$exitCode)
    $positive | ForEach-Object {Write-Output $_}
    $bad | Select-Object -First 12 | ForEach-Object {Write-Output $_}
    if(!$passed){$anyFailed=$true}
    if($logText -match 'error CS\d+'){break}
}
Write-Output ('Evidence: '+$summaryPath)
if($anyFailed){exit 1}else{exit 0}

