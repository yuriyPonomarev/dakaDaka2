"""Exercise the Windows runner entry points with inert process fixtures."""
from pathlib import Path
import os
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


@unittest.skipUnless(os.name == "nt", "Windows Unity runners")
class MirrorGuardTests(unittest.TestCase):
    def shell(self, body, cwd=ROOT):
        script = "$ErrorActionPreference='Stop'\n. ./Tools/MirrorGuard.ps1\n" + body + "\nexit 0"
        result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive",
                                 "-ExecutionPolicy", "Bypass", "-Command", script],
                                cwd=cwd, capture_output=True, text=True, errors="replace", timeout=30)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_entrypoints_refuse_wrong_project_before_output(self):
        self.shell(r"""
$entries = @(
    { & ./Tools/Run-All.ps1 -Mirror . },
    { & ./Tools/Run-EmberdelveGates.ps1 -ProjectPath ../DakaDaka -Methods Example },
    { & ./Tools/Run-EmberdelveEditModeTests.ps1 -ProjectPath ../DakaDaka_ci/nested },
    { & ./Tools/Run-GatesAfterEditorExit.ps1 -ProjectPath . -EditorPid $PID -Methods Example -OutputDirectory 'unused' }
)
foreach ($entry in $entries) {
    try { & $entry; throw 'Unexpected acceptance' }
    catch { if ($_.Exception.Message -notlike 'REFUSED: expected test mirror*') { throw } }
}
$mirror = Resolve-TestMirror '../DakaDaka_ci'
$null = Resolve-VerificationOutput 'Logs/test' $mirror
try { Resolve-VerificationOutput '../DakaDaka/Logs/test' $mirror; throw 'Unexpected evidence path' }
catch { if ($_.Exception.Message -notlike 'REFUSED: verification evidence*') { throw } }
""")

    def test_build_shortcut_refuses_output_in_project_sources(self):
        result = subprocess.run(
            ["cmd.exe", "/d", "/c", "Tools\\net\\Build-NetPlayable.bat", str(ROOT / "Assets" / "invalid.exe")],
            cwd=ROOT, capture_output=True, text=True, errors="replace", timeout=30)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("REFUSED: choose a build output", result.stdout + result.stderr)

    def test_playable_copyback_uses_mirror_and_preserves_custom_name(self):
        # Exercise the real Windows copy-back branch without building the game or reserving the live mirror.
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            source = base / "source"
            mirror = base / "DakaDaka_ci"
            runner = source / "Tools"
            runner.mkdir(parents=True)
            (mirror / "Assets").mkdir(parents=True)
            shutil.copy2(ROOT / "Tools/Run-All.ps1", runner)
            # Replace only the host boundary in this standalone fixture. All paths are temporary.
            (runner / "MirrorGuard.ps1").write_text("""
function Resolve-TestMirror { return (Resolve-Path ../DakaDaka_ci).Path }
function Assert-NoRunnerLink { }
function Assert-MirrorIdle($target) {
    if ($target -ne (Resolve-Path ../DakaDaka_ci).Path) { throw 'Wrong mirror' }
}
function Enter-MirrorRun { }
function Exit-MirrorRun { }
function Resolve-VerificationOutput($target) { return $target }
""")
            (source / "fixture.exe").touch()
            self.shell(r"""
function Start-Process($FilePath, $ArgumentList, [switch]$PassThru, $WindowStyle) {
    if ($FilePath -ne (Join-Path (Get-Location) 'fixture.exe')) { throw 'Unexpected executable' }
    $project = $ArgumentList[[array]::IndexOf($ArgumentList, '-projectPath') + 1].Trim('"')
    if ($project -ne (Resolve-Path ../DakaDaka_ci).Path) { throw 'Unexpected editor target' }
    $output = $ArgumentList[[array]::IndexOf($ArgumentList, '--ember-build-output') + 1].Trim('"')
    $expected = Join-Path $project 'Builds/Playable'
    if (!$output.StartsWith($expected + [IO.Path]::DirectorySeparatorChar)) { throw 'Unexpected build destination' }
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force
    Set-Content -LiteralPath $output -Value 'test player'
    Set-Content -LiteralPath (Join-Path (Split-Path -Parent $output) 'UnityPlayer.dll') -Value 'test companion'
    $fake = [pscustomobject]@{Handle=0; ExitCode=0}
    $fake | Add-Member ScriptMethod WaitForExit { return $true }
    $fake | Add-Member ScriptMethod Refresh { }
    return $fake
}
$dest = Join-Path (Get-Location) 'Builds/Custom Folder/Named Player.exe'
& ./Tools/Run-All.ps1 -NoSync -SkipGates -Build -UnityExe (Join-Path (Get-Location) 'fixture.exe') -PlayableOutput $dest
if ($LASTEXITCODE -ne 0) { throw 'Build runner failed' }
""", cwd=source)
            self.assertEqual((source / "Builds/Custom Folder/Named Player.exe").read_text().strip(), "test player")
            self.assertTrue((source / "Builds/Custom Folder/UnityPlayer.dll").is_file())

    def test_live_editor_and_lock_are_independent(self):
        self.shell(r"""
$mirror = Resolve-TestMirror '../DakaDaka_ci'
function Test-Path { return $simulateLock }
function Get-CimInstance { return [pscustomobject]@{ProcessId=42;CommandLine=$commandLine} }
$simulateLock = $false
$commandLine = 'Unity.exe -projectPath "' + (Get-Location).ProviderPath + '"'
Assert-MirrorIdle $mirror
$commandLine = 'Unity.exe -projectPath "' + $mirror + '"'
try { Assert-MirrorIdle $mirror; throw 'Live mirror accepted' }
catch { if ($_.Exception.Message -notlike 'REFUSED: Unity process 42*') { throw } }
$simulateLock = $true
try { Assert-MirrorIdle $mirror; throw 'Locked mirror accepted' }
catch { if ($_.Exception.Message -notlike 'REFUSED: the mirror has a Unity lock*') { throw } }
""")

    def test_wrapper_refuses_main_pid_and_preserves_lock_on_timeout(self):
        self.shell(r"""
$mirror = Resolve-TestMirror '../DakaDaka_ci'
function Get-Process {
    $fake = [pscustomobject]@{HasExited=$false}
    $fake | Add-Member ScriptMethod WaitForExit { param($time) return $false }
    return $fake
}
function Get-CimInstance { return [pscustomobject]@{CommandLine=('Unity.exe -projectPath "' + $editorPath + '"')} }
function Remove-Item { throw 'Attempted to remove a lock' }
function New-Item { throw 'Wrote output before refusal' }
foreach ($editorPath in @((Get-Location).ProviderPath, $mirror)) {
    $reason = if ($editorPath -eq $mirror) { 'REFUSED: mirror Editor*still running*' } else { 'REFUSED: PID*' }
    try {
        & ./Tools/Run-GatesAfterEditorExit.ps1 -EditorPid 42 -ProjectPath $mirror -Methods Example -OutputDirectory Logs/test
        throw 'Unexpected launch'
    } catch { if ($_.Exception.Message -notlike $reason) { throw } }
}
""")

    def test_competing_run_and_recursive_acquisition(self):
        self.shell(r"""
# Use an isolated name for this test so a genuine verification run can continue.
$name = "Local\DakaDaka-Test-$PID"
$first = Enter-MirrorRun $name
try {
    $inner = Enter-MirrorRun $name
    Exit-MirrorRun $inner
    $child = ". ./Tools/MirrorGuard.ps1; try { Enter-MirrorRun '$name'; exit 1 } catch { if (" + '$_.Exception.Message' + " -like 'REFUSED: another runner*') { exit 0 }; exit 2 }"
    & powershell.exe -NoProfile -NonInteractive -Command $child
    if ($LASTEXITCODE -ne 0) { throw 'Second process did not refuse the busy mirror' }
} finally { Exit-MirrorRun $first }
$again = Enter-MirrorRun $name
Exit-MirrorRun $again
""")
