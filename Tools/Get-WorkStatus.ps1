[CmdletBinding()]
param([ValidateRange(1,10)][int]$Recent=3)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
Write-Output 'DakaDaka: read Docs/CURRENT_WORK.md for the active slice; Docs/ROADMAP.md for acceptance order.'
$runRoot=Join-Path $projectRoot 'Logs/runs'
if(Test-Path -LiteralPath $runRoot){
    foreach($runDir in @(Get-ChildItem -LiteralPath $runRoot -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First $Recent)){
        $summary=Join-Path $runDir.FullName 'run.json'
        if(Test-Path -LiteralPath $summary){
            try {
                $run=Get-Content -LiteralPath $summary -Raw | ConvertFrom-Json
                $result=if($run.passed){'PASS'}else{'FAIL'}
                Write-Output ("{0}: {1} set={2} seconds={3}" -f $runDir.Name,$result,$run.set,$run.elapsedSeconds)
                $gateSummary=[string]$run.gates
                if(Test-Path -LiteralPath $gateSummary){
                    $gates=Get-Content -LiteralPath $gateSummary -Raw | ConvertFrom-Json
                    foreach($gate in @($gates.results | Where-Object { !$_.passed })){
                        Write-Output ("  {0}: {1}" -f $gate.name,((@($gate.problems | Select-Object -First 3)) -join ' | '))
                    }
                }
                Write-Output ('  evidence: '+[string]$run.summary)
            } catch { Write-Output ($runDir.Name+': unreadable summary: '+$_.Exception.Message) }
        }else { Write-Output ($runDir.Name+': INCOMPLETE (running or interrupted; no final result)') }
    }
}
$mirror=Join-Path (Split-Path $projectRoot -Parent) 'DakaDaka_ci'
Write-Output ('Mirror lock present: '+(Test-Path -LiteralPath (Join-Path $mirror 'Temp/UnityLockfile')))
$changes=@(& git -C $projectRoot -c core.quotepath=false status --porcelain --untracked-files=all)
if($LASTEXITCODE -ne 0){throw 'Cannot read working tree'}
Write-Output ('Changed/untracked paths: '+$changes.Count+' (working-tree cleanliness alone does not establish acceptance)')

