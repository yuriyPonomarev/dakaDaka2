param([int]$Seed = 12345, [ValidatePattern('^[a-z0-9-]+$')][string]$Tag = 'inspection')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../MirrorGuard.ps1')
$mirror = Resolve-TestMirror '../DakaDaka_ci'
$lease = Enter-MirrorRun
try {
    Assert-MirrorIdle $mirror
    if ($env:EMBER_RAID_AI) { throw 'Review requires the saved inspection setting, with no AI environment override' }
    $evidence = Join-Path $mirror ('Logs/acceptance/soft-traversal-20261122/live-' + $Seed + '-' + $Tag)
    if (Test-Path -LiteralPath $evidence) { throw 'Evidence directory already exists' }
    New-Item -ItemType Directory -Path $evidence -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $mirror 'Assets/Editor') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'TraversalReview.cs') -Destination (Join-Path $mirror 'Assets/Editor/TraversalReview.cs')
    $args = @('-projectPath',('"'+$mirror+'"'),'-executeMethod','TraversalReview.Run',
        '--ember-profile-dir',('"'+$evidence+'/profile"'),'-emberdelveRaidSeed',$Seed,'-emberWallReviewTag',$Tag,
        '-logFile',('"'+$evidence+'/editor.log"'))
    # A visible, isolated review Editor is required by the shared visual procedure.
    $editor = Start-Process -FilePath 'C:/Program Files/Unity/Hub/Editor/6000.2.5f1/Editor/Unity.exe' -ArgumentList $args -PassThru -WindowStyle Normal
    Write-Output ('Review seed='+$Seed+' owned Editor pid='+$editor.Id)
    while (!$editor.WaitForExit(1000)) { }
    $editor.Refresh()
    Assert-MirrorIdle $mirror
    $target = Join-Path (Get-Location).Path ('Logs/acceptance/soft-traversal-20261122/live-'+$Seed+'-'+$Tag)
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -Path ($evidence+'/*') -Destination $target -Recurse -Force
    if ($editor.ExitCode -ne 0) { throw ('Review failed: exit '+$editor.ExitCode+'; evidence retained at '+$target) }
    Get-Content -LiteralPath (Join-Path $target 'result.txt')
} finally { Exit-MirrorRun $lease }
