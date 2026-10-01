# Makes the client test copy start by itself whenever you sign in to Windows, and stops the PC
# from sleeping while it is plugged in (sleep would take the link offline).
# Undo: delete the "ASCO public link" shortcut from shell:startup, and run
#       powercfg /change standby-timeout-ac 30
$start = Join-Path $PSScriptRoot "start-public.ps1"
$lnk = Join-Path ([Environment]::GetFolderPath("Startup")) "ASCO public link.lnk"
$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut($lnk)
$s.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$s.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$start`""
$s.WindowStyle = 7
$s.Description = "Keeps the ASCO client test link online"
$s.Save()
powercfg /change standby-timeout-ac 0
powercfg /change hibernate-timeout-ac 0
Write-Host "Autostart installed: $lnk"
Write-Host "Sleep on mains power: never (screen can still turn off and lock)."
