[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ProjectPath,
    [string]$TestFilter='Emberdelve.Rules.Tests',
    [string]$UnityExe='C:\Program Files\Unity\Hub\Editor\6000.2.5f1\Editor\Unity.exe',
    [string]$OutputDirectory,
    [ValidateRange(1,100000)][int]$MinimumTests=1,
    [ValidateRange(30,3600)][int]$TimeoutSeconds=900
)
$ErrorActionPreference='Stop'
$project=(Resolve-Path -LiteralPath $ProjectPath).Path
if(!(Test-Path -LiteralPath $UnityExe -PathType Leaf)){throw 'Unity executable missing'}
if(!(Test-Path -LiteralPath (Join-Path $project 'Assets') -PathType Container)){throw 'Not a Unity project'}
if($TestFilter -notmatch '^[A-Za-z_][A-Za-z0-9_.]*$'){throw 'Expected a fully qualified test namespace or class'}
if(Test-Path -LiteralPath (Join-Path $project 'Temp/UnityLockfile')){throw 'Project is locked; close its Editor first'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $project ('Logs/editmode-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))}
$output=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$xmlPath=Join-Path $output 'results.xml'
$logPath=Join-Path $output 'editor.log'
$summaryPath=Join-Path $output 'summary.json'
foreach($path in @($xmlPath,$logPath,$summaryPath)){if(Test-Path -LiteralPath $path){throw "Refusing to overwrite evidence: $path"}}
# Test Framework owns Editor exit after its run. -quit can terminate it before tests finish.
$arguments=@('-batchmode','-nographics','-projectPath',('"'+$project+'"'),'-runTests','-testPlatform','EditMode','-testFilter',$TestFilter,'-testResults',('"'+$xmlPath+'"'),'-logFile',('"'+$logPath+'"'))
$started=[DateTime]::UtcNow
$ownedProcess=Start-Process -FilePath $UnityExe -ArgumentList $arguments -PassThru -WindowStyle Hidden
Write-Output ('START EditMode '+$TestFilter+' pid='+$ownedProcess.Id)
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
$problems=[Collections.Generic.List[string]]::new()
$tests=0; $failed=0; $skipped=0; $inconclusive=0; $status='MissingResults'
if(Test-Path -LiteralPath $xmlPath){
    try{
        [xml]$xml=[IO.File]::ReadAllText($xmlPath)
        $run=$xml.'test-run'
        if($null -eq $run){throw 'NUnit test-run element missing'}
        $tests=[int]$run.total; $failed=[int]$run.failed
        $skipped=[int]$run.skipped; $inconclusive=[int]$run.inconclusive; $status=[string]$run.result
    }catch{$problems.Add('Invalid result XML: '+$_.Exception.Message)}
}else{$problems.Add('No NUnit result XML was produced')}
$log=if(Test-Path -LiteralPath $logPath){[IO.File]::ReadAllText($logPath)}else{''}
[regex]::Matches($log,'(?m)^.*(?:error CS\d+|[A-Za-z]+Exception:|Aborting batchmode due to failure)[^\r\n]*') |
    ForEach-Object {$_.Value.Trim()} | Select-Object -Unique | ForEach-Object {$problems.Add($_)}
$passed=(!$timedOut -and $exitCode -eq 0 -and $status -eq 'Passed' -and $tests -ge $MinimumTests -and $failed -eq 0 -and $skipped -eq 0 -and $inconclusive -eq 0 -and $problems.Count -eq 0)
$result=[pscustomobject]@{project=$project;filter=$TestFilter;passed=$passed;exitCode=$exitCode;timedOut=$timedOut;tests=$tests;failed=$failed;skipped=$skipped;inconclusive=$inconclusive;status=$status;problems=@($problems.ToArray());results=$xmlPath;log=$logPath;elapsedSeconds=[Math]::Round(([DateTime]::UtcNow-$started).TotalSeconds,2)}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding utf8
Write-Output ((@('FAIL','PASS')[[int]$passed])+' EditMode tests='+$tests+' failures='+$failed+' exit='+$exitCode)
$problems | ForEach-Object {Write-Output $_}
Write-Output ('Evidence: '+$summaryPath)
if($passed){exit 0}else{exit 1}