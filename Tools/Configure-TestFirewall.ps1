[CmdletBinding()]
param([switch]$Apply, [switch]$Remove)
$ErrorActionPreference='Stop'
$setupLog=Join-Path (Split-Path $PSScriptRoot -Parent) 'Logs/harness/firewall-setup.log'
New-Item -ItemType Directory -Path (Split-Path $setupLog -Parent) -Force | Out-Null
trap { [IO.File]::AppendAllText($setupLog,([DateTime]::UtcNow.ToString('s')+' '+$_.Exception.Message+[Environment]::NewLine+$_.ScriptStackTrace+[Environment]::NewLine)); exit 1 }
if($Apply -and $Remove){throw 'Choose Apply or Remove'}
$main=Split-Path $PSScriptRoot -Parent
$mirror=[IO.Path]::GetFullPath((Join-Path $main '../DakaDaka_ci'))
$group='DakaDaka Local Test Harness'
$plan=@(foreach($profile in @('net-proof','net-final')){
    foreach($protocol in @('TCP','UDP')){
        [pscustomobject]@{
            Name=('DakaDaka-Harness-'+$profile+'-'+$protocol)
            Program=(Join-Path $mirror ('Builds/HarnessPlayers/'+$profile+'/EmberdelveNet.exe'))
            Protocol=$protocol
            RemoteAddress='127.0.0.1'
        }
    }
})
if(!$Apply -and !$Remove){$plan | Format-List; Write-Output 'Preview only. -Apply requires administrator rights. Localhost only; no LAN/Internet access is opened.'; exit 0}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=[Security.Principal.WindowsPrincipal]::new($identity)
if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    throw 'Administrator approval required once. Run this script with -Apply in an elevated PowerShell. No rules changed.'
}
foreach($entry in $plan){
    $existing=Get-NetFirewallRule -Name $entry.Name -ErrorAction SilentlyContinue
    if($existing){
        $app=$existing | Get-NetFirewallApplicationFilter
        if($existing.Group -ne $group -or $app.Program -ine $entry.Program){throw ('Refusing to change unrelated rule '+$entry.Name)}
        if($Remove){$existing | Remove-NetFirewallRule; Write-Output ('Removed '+$entry.Name); continue}
        $address=$existing | Get-NetFirewallAddressFilter
        $port=$existing | Get-NetFirewallPortFilter
        $expectedProtocol=if($entry.Protocol -eq 'TCP'){'TCP'}else{'UDP'}
        $actualAddresses=@($address.RemoteAddress | Sort-Object)
        if($existing.Action -ne 'Allow' -or $existing.Enabled -ne 'True' -or $existing.Direction -ne 'Inbound' -or
            $existing.Profile -ne 'Any' -or $port.Protocol -ne $expectedProtocol -or
            ($actualAddresses -join ',') -ne ((@('127.0.0.1') | Sort-Object) -join ',')){
            throw ('Rule differs from localhost-only plan; inspect manually: '+$entry.Name)
        }
        Write-Output ('Already configured '+$entry.Name)
    }elseif(!$Remove){
        New-NetFirewallRule -Name $entry.Name -DisplayName $entry.Name -Group $group -Direction Inbound -Action Allow `
            -Enabled True -Profile Any -Program $entry.Program -Protocol $entry.Protocol -RemoteAddress @('127.0.0.1') `
            -EdgeTraversalPolicy Block | Out-Null
        Write-Output ('Configured localhost only: '+$entry.Name)
    }
}
