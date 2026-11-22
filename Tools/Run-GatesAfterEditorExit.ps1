param(
    [Parameter(Mandatory=$true)][int]$EditorPid,
    [Parameter(Mandatory=$true)][string]$ProjectPath,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string[]]$Methods,
    [string]$UnityExe='C:\Program Files\Unity\Hub\Editor\6000.2.5f1\Editor\Unity.exe',
    [string]$EditModeFilter,
    [int]$EditModeMinimumTests=1,
    [switch]$Relaunch
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'MirrorGuard.ps1')
$mirror = Resolve-TestMirror $ProjectPath
$editor = Get-Process -Id $EditorPid -ErrorAction SilentlyContinue
if ($editor -and !$editor.HasExited) {
    $process = Get-CimInstance Win32_Process -Filter "Name='Unity.exe' AND ProcessId=$EditorPid" -ErrorAction Stop
    if (!$editor.HasExited) {
        if (!$process -or (Get-RunnerUnityProject $process) -ne $mirror) {
            throw "REFUSED: PID $EditorPid does not identify the mirror Editor"
        }
        if (!$editor.WaitForExit(600000)) { throw "REFUSED: mirror Editor $EditorPid is still running after the wait" }
    }
}
$OutputDirectory = Resolve-VerificationOutput $OutputDirectory $mirror
$lease = Enter-MirrorRun
try {
    Assert-MirrorIdle $mirror
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $wrapLog = Join-Path $OutputDirectory 'wrapper.log'
    $result = 0
    if ($EditModeFilter) {
        & (Join-Path $PSScriptRoot 'Run-EmberdelveEditModeTests.ps1') -ProjectPath $mirror -TestFilter $EditModeFilter -MinimumTests $EditModeMinimumTests -UnityExe $UnityExe -OutputDirectory (Join-Path $OutputDirectory 'editmode') *>&1 | Add-Content $wrapLog
        $result = $LASTEXITCODE
    }
    if ($result -eq 0) {
        & (Join-Path $PSScriptRoot 'Run-EmberdelveGates.ps1') -ProjectPath $mirror -Methods $Methods -UnityExe $UnityExe -OutputDirectory (Join-Path $OutputDirectory 'gates') *>&1 | Add-Content $wrapLog
        $result = $LASTEXITCODE
    }
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'DONE') -Value $result
    if ($Relaunch) {
        Assert-MirrorIdle $mirror
        Start-Process -FilePath $UnityExe -ArgumentList @('-projectPath',('"'+$mirror+'"'))
    }
    exit $result
} finally { Exit-MirrorRun $lease }
