# Windows PowerShell 5.1. All batch scripts are called from the development checkout.
function Assert-NoRunnerLink([string]$Path) {
    for ($dir = [IO.DirectoryInfo]::new($Path); $null -ne $dir; $dir = $dir.Parent) {
        $item = Get-Item -LiteralPath $dir.FullName -Force -ErrorAction SilentlyContinue
        # OneDrive placeholders may be reparse points without redirecting the path.
        if ($item.LinkType -in 'Junction', 'SymbolicLink') { throw "REFUSED: linked runner path $($dir.FullName)" }
    }
}

function Resolve-TestMirror([string]$ProjectPath) {
    $source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $workspace = 'C:\Users\yrapa\OneDrive\Desktop\Unity_DakaDaka'
    if ((Test-Path -LiteralPath $workspace) -and $source -ne (Join-Path $workspace 'DakaDaka_clean_handoff')) {
        throw 'REFUSED: run the scripts from DakaDaka_clean_handoff'
    }
    if ((Get-Location).ProviderPath -ne $source) { throw 'REFUSED: use the development checkout as working directory' }
    $top = git -C $source rev-parse --show-toplevel
    if ($LASTEXITCODE -ne 0 -or !$top -or [IO.Path]::GetFullPath($top) -ne $source) {
        throw 'REFUSED: runner source must be the development Git root'
    }
    $expected = [IO.Path]::GetFullPath((Join-Path $source '..\DakaDaka_ci'))
    if (![IO.Path]::IsPathRooted($ProjectPath)) { $ProjectPath = Join-Path $source $ProjectPath }
    $target = [IO.Path]::GetFullPath($ProjectPath).TrimEnd('\','/')
    if ($target -ne $expected -or $target -eq $source) { throw "REFUSED: expected test mirror $expected" }
    Assert-NoRunnerLink $source
    Assert-NoRunnerLink $expected
    return $expected
}

function Get-RunnerUnityProject($Process) {
    if (!$Process.CommandLine) { throw "REFUSED: unreadable command line for PID $($Process.ProcessId)" }
    if ($Process.CommandLine -match '(?i)(?:^|\s)-projectPath\s+(?:"([^"]+)"|(\S+))') {
        $project = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
        if (![IO.Path]::IsPathRooted($project)) {
            throw "REFUSED: cannot identify the project of Unity process $($Process.ProcessId)"
        }
        return [IO.Path]::GetFullPath($project).TrimEnd('\','/')
    }
}

function Assert-MirrorIdle([string]$Mirror) {
    if (Test-Path -LiteralPath (Join-Path $Mirror 'Temp\UnityLockfile')) {
        throw 'REFUSED: the mirror has a Unity lock. Leave the lock intact.'
    }
    # Covers an Editor starting up before Unity creates the lock.
    foreach ($process in (Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop)) {
        if ((Get-RunnerUnityProject $process) -eq $Mirror) {
            throw "REFUSED: Unity process $($process.ProcessId) has the mirror open"
        }
    }
}

function Resolve-VerificationOutput([string]$Path, [string]$Mirror) {
    if (![IO.Path]::IsPathRooted($Path)) { $Path = Join-Path (Get-Location).ProviderPath $Path }
    $target = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    foreach ($root in @((Get-Location).ProviderPath, $Mirror)) {
        $logs = Join-Path $root 'Logs'
        if ($target.StartsWith($logs + '\', [StringComparison]::OrdinalIgnoreCase)) {
            Assert-NoRunnerLink $target
            return $target
        }
    }
    throw 'REFUSED: verification evidence must be in a run folder under primary or mirror Logs'
}

# A named mutex spans mirror sync and gaps between Unity processes. Nested scripts
# run on the same PowerShell thread and acquire it recursively.
function Enter-MirrorRun([string]$Name = 'Local\DakaDaka-CI-Runner') {
    $mutex = [Threading.Mutex]::new($false, $Name)
    try {
        if (!$mutex.WaitOne(0)) { throw 'REFUSED: another runner owns the mirror' }
    } catch [Threading.AbandonedMutexException] {
        # WaitOne acquired the abandoned mutex. The caller still checks Unity.
    } catch {
        $mutex.Dispose()
        throw
    }
    return $mutex
}

function Exit-MirrorRun([Threading.Mutex]$Mutex) {
    $Mutex.ReleaseMutex()
    $Mutex.Dispose()
}
