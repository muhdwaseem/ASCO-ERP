# Keeps the client test copy online from this PC, with no windows to leave open:
#   - ASCO (deploy\out) in Production mode on http://127.0.0.1:8080
#   - a Cloudflare quick tunnel giving it a public https://....trycloudflare.com address
#   - a watchdog that restarts either one if it stops, and writes the current link to
#     deploy\public-url.txt (the link changes only when the tunnel restarts, e.g. after a reboot)
# Settings come from deploy\hosted.env (ASCO_DEMO_PASSWORD=..., optional ASCO_PORT=8080).
# Start:  powershell -ExecutionPolicy Bypass -WindowStyle Hidden -File deploy\start-public.ps1
# Stop:   powershell -ExecutionPolicy Bypass -File deploy\stop-public.ps1
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$out = Join-Path $here "out"
$cf = "D:\tools\cloudflared\cloudflared.exe"
$logs = Join-Path $here "logs"
$urlFile = Join-Path $here "public-url.txt"
New-Item -ItemType Directory -Force $logs | Out-Null
function Log($m) { Add-Content (Join-Path $logs "watchdog.log") ("{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $m) }

# One watchdog at a time.
$mutex = New-Object System.Threading.Mutex($false, "Local\AscoPublicWatchdog")
if (-not $mutex.WaitOne(0)) { Log "Another watchdog is already running - exiting."; exit 0 }

Get-Content (Join-Path $here "hosted.env") | Where-Object { $_ -match '^\s*([A-Za-z_]+)\s*=\s*(.+?)\s*$' } | ForEach-Object { Set-Item "Env:$($Matches[1])" $Matches[2] }
$port = if ($env:ASCO_PORT) { $env:ASCO_PORT } else { "8080" }
$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:ASPNETCORE_URLS = "http://127.0.0.1:$port"
$env:Database__Provider = "Sqlite"
$env:ConnectionStrings__Sqlite = "Data Source=asco_hosted.db"
$env:Seed__DemoData = "true"
$env:Security__DemoUsersPassword = $env:ASCO_DEMO_PASSWORD
$env:Security__BehindProxy = "true"
$env:DataProtection__KeysPath = Join-Path $out "keys"

function Healthy { try { (Invoke-WebRequest "http://127.0.0.1:$port/health" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200 } catch { $false } }
function Start-Asco {
    Get-Process Asco.Api -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$out*" } | Stop-Process -Force
    $p = Start-Process (Join-Path $out "Asco.Api.exe") -WorkingDirectory $out -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $logs "asco.out.log") -RedirectStandardError (Join-Path $logs "asco.err.log")
    for ($i = 0; $i -lt 60 -and -not (Healthy); $i++) { Start-Sleep 2 }
    Log ("ASCO started (pid {0}), healthy={1}" -f $p.Id, (Healthy)); $p
}
function Start-Tunnel {
    $tlog = Join-Path $logs "tunnel.log"
    $p = Start-Process $cf -ArgumentList "tunnel", "--no-autoupdate", "--url", "http://127.0.0.1:$port" -WindowStyle Hidden -PassThru `
        -RedirectStandardError $tlog -RedirectStandardOutput (Join-Path $logs "tunnel.out.log")
    $url = $null
    for ($i = 0; $i -lt 60 -and -not $url; $i++) {
        Start-Sleep 2
        $m = Select-String -Path $tlog -Pattern 'https://[a-z0-9-]+\.trycloudflare\.com' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($m) { $url = $m.Matches[0].Value }
    }
    if ($url) { Set-Content $urlFile $url; Log "Tunnel up: $url" } else { Log "Tunnel started but no link yet - see logs\tunnel.log" }
    $p
}

$asco = Start-Asco
$tunnel = Start-Tunnel
$misses = 0
while ($true) {
    Start-Sleep 20
    if ($asco.HasExited) { Log "ASCO stopped (exit $($asco.ExitCode)) - restarting"; $asco = Start-Asco; $misses = 0 }
    elseif (-not (Healthy)) { $misses++; if ($misses -ge 3) { Log "ASCO not answering - restarting"; $asco = Start-Asco; $misses = 0 } }
    else { $misses = 0 }
    if ($tunnel.HasExited) { Log "Tunnel stopped - restarting (the link will change)"; $tunnel = Start-Tunnel }
}
