param(
    [Parameter(Mandatory=$true)][int]$EditorPid,
    [Parameter(Mandatory=$true)][string]$ProjectPath,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string[]]$Methods,
    [string]$UnityExe='C:\Program Files\Unity\Hub\Editor\6000.2.5f1\Editor\Unity.exe',
    [switch]$Relaunch
)
$ErrorActionPreference='Continue'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$wrapLog=Join-Path $OutputDirectory 'wrapper.log'
"wait pid=$EditorPid $(Get-Date -Format o)" | Add-Content $wrapLog
$p=Get-Process -Id $EditorPid -ErrorAction SilentlyContinue
if($p){ $p.WaitForExit(600000) | Out-Null }
$lock=Join-Path $ProjectPath 'Temp/UnityLockfile'
for($i=0;$i -lt 60 -and (Test-Path -LiteralPath $lock);$i++){
    try { Remove-Item -LiteralPath $lock -Force -ErrorAction Stop } catch { Start-Sleep -Seconds 1 }
}
"editor gone $(Get-Date -Format o)" | Add-Content $wrapLog
$gateOut=Join-Path $OutputDirectory 'gates'
& (Join-Path $ProjectPath 'Tools/Run-EmberdelveGates.ps1') -ProjectPath $ProjectPath -Methods $Methods -UnityExe $UnityExe -OutputDirectory $gateOut *>&1 | Add-Content $wrapLog
"gates exit=$LASTEXITCODE $(Get-Date -Format o)" | Add-Content $wrapLog
Set-Content -LiteralPath (Join-Path $OutputDirectory 'DONE') -Value "$LASTEXITCODE"
if($Relaunch){ Start-Process -FilePath $UnityExe -ArgumentList @('-projectPath',('"'+$ProjectPath+'"')) }
