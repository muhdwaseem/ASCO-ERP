# Switches the client test link from the changing Cloudflare address to your permanent ngrok one.
# First fill NGROK_AUTHTOKEN and NGROK_DOMAIN in deploy\hosted.env, then run:
#   powershell -ExecutionPolicy Bypass -File deploy\switch-to-ngrok.ps1
$here = $PSScriptRoot
$envFile = Get-Content (Join-Path $here "hosted.env")
$token = ($envFile | Where-Object { $_ -match '^NGROK_AUTHTOKEN=(.+)$' } | ForEach-Object { $Matches[1].Trim() })
$domain = ($envFile | Where-Object { $_ -match '^NGROK_DOMAIN=(.+)$' } | ForEach-Object { $Matches[1].Trim() -replace '^https?://', '' -replace '/.*$', '' })
if (-not $token -or -not $domain) { Write-Host "Fill NGROK_AUTHTOKEN and NGROK_DOMAIN in deploy\hosted.env first." -ForegroundColor Red; exit 1 }

& (Join-Path $here "stop-public.ps1")
Start-Sleep 2
Start-Process powershell.exe -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', (Join-Path $here "start-public.ps1") -WindowStyle Hidden
$url = "https://$domain"
for ($i = 0; $i -lt 45; $i++) {
    Start-Sleep 2
    try { if ((Invoke-WebRequest "$url/health" -UseBasicParsing -TimeoutSec 8 -Headers @{ 'ngrok-skip-browser-warning' = '1' }).StatusCode -eq 200) { Write-Host "Permanent link is live: $url" -ForegroundColor Green; exit 0 } } catch {}
}
Write-Host "Not reachable yet - see deploy\logs\watchdog.log and deploy\logs\tunnel.log" -ForegroundColor Yellow
