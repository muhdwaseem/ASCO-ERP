# Takes the client test copy offline: stops the watchdog, the tunnel and the hosted ASCO.
$here = $PSScriptRoot
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -like "*start-public.ps1*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Get-Process cloudflared -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "D:\tools\cloudflared\*" } | Stop-Process -Force
Get-Process ngrok -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "D:\tools\ngrok\*" } | Stop-Process -Force
Get-Process Asco.Api -ErrorAction SilentlyContinue | Where-Object { $_.Path -like (Join-Path $here "out") + "*" } | Stop-Process -Force
Remove-Item (Join-Path $here "public-url.txt") -ErrorAction SilentlyContinue
Write-Host "Client test copy is offline."
