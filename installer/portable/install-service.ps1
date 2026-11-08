# SsdVirtual portable: installs WinFsp (if missing) and registers SsdVirtualService from this folder.
# Run from an elevated PowerShell:  powershell -ExecutionPolicy Bypass -File .\install-service.ps1
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$winfsp = Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\WinFsp' -ErrorAction SilentlyContinue
if (-not $winfsp) {
    Write-Host 'Installing WinFsp...'
    $p = Start-Process msiexec.exe -ArgumentList '/i', "`"$here\winfsp.msi`"", '/qn', '/norestart' -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "WinFsp installation failed ($($p.ExitCode))" }
}
$exe = Join-Path $here 'SsdVirtualService.exe'
if (Get-Service SsdVirtualService -ErrorAction SilentlyContinue) {
    sc.exe stop SsdVirtualService | Out-Null
    Start-Sleep 3
    sc.exe config SsdVirtualService binPath= "`"$exe`"" start= auto | Out-Null
} else {
    sc.exe create SsdVirtualService binPath= "`"$exe`"" start= auto DisplayName= "SsdVirtual Service" | Out-Null
}
sc.exe failure SsdVirtualService reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
sc.exe start SsdVirtualService | Out-Null
Write-Host 'SsdVirtualService installed and started. Run SsdVirtual.exe to add accounts.'
