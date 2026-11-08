SsdVirtual – portable package (Windows 10/11 x64)

The virtual drives are created by a Windows service (SsdVirtualService) and the WinFsp
file system driver, so even the portable package needs a one-time setup as administrator:

  1. Extract this folder to a permanent location (e.g. D:\Apps\SsdVirtual).
  2. Right-click PowerShell → Run as administrator, then:
       cd D:\Apps\SsdVirtual
       powershell -ExecutionPolicy Bypass -File .\install-service.ps1
  3. Start SsdVirtual.exe and add your Google Drive / TeraBox accounts.

To remove: run uninstall-service.ps1 as administrator, then delete the folder.
The installer (SsdVirtual-Setup.exe) does all of this automatically.
