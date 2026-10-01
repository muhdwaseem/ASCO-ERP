@echo off
rem Runs the published ASCO package (deploy\out) the way it runs when hosted:
rem Production mode, one address for app + API, its own demo database, all security checks on.
rem It refuses to start until you give the demo logins a new password (the old ones are in public source).
setlocal
cd /d "%~dp0"

if "%ASCO_DEMO_PASSWORD%"=="" set /p ASCO_DEMO_PASSWORD=New password for the demo logins (min 8 chars, upper+lower+digit+symbol):
if "%ASCO_PORT%"=="" set ASCO_PORT=8080

set ASPNETCORE_ENVIRONMENT=Production
set ASPNETCORE_URLS=http://127.0.0.1:%ASCO_PORT%
set Database__Provider=Sqlite
set ConnectionStrings__Sqlite=Data Source=asco_hosted.db
set Seed__DemoData=true
set Security__DemoUsersPassword=%ASCO_DEMO_PASSWORD%
rem Behind Cloudflare Tunnel / ngrok / a reverse proxy: trust its X-Forwarded-* headers (HTTPS + client IP).
set Security__BehindProxy=true
set DataProtection__KeysPath=%~dp0keys

echo ASCO is starting on http://127.0.0.1:%ASCO_PORT%  (Ctrl+C to stop)
"%~dp0Asco.Api.exe"
