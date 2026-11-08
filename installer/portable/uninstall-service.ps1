# Removes SsdVirtualService (portable install). Cache and settings are kept.
#Requires -RunAsAdministrator
sc.exe stop SsdVirtualService | Out-Null
Start-Sleep 5
sc.exe delete SsdVirtualService | Out-Null
Write-Host 'SsdVirtualService removed. Cache: C:\SsdVirtualCache (default), settings: %ProgramData%\SsdVirtual.'
