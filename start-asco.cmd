@echo off
rem Starts ASCO locally in two windows (they keep running after this window closes).
rem   API: http://localhost:5080    UI: http://localhost:5190
rem Close the two "ASCO" windows to stop it.
cd /d "%~dp0"
if not exist node_modules call npm install

echo Building the ASCO API...
dotnet build server\Asco.Api --artifacts-path artifacts -nologo -v q
if errorlevel 1 (
  echo.
  echo Build failed - see the errors above. If it says a file is in use, close any running "ASCO API" window and try again.
  pause
  exit /b 1
)

start "ASCO API (port 5080)" cmd /k dotnet run --project server\Asco.Api --artifacts-path artifacts --no-build
start "ASCO UI (port 5190)" cmd /k npx vite --port 5190 --strictPort
echo Starting... opening http://localhost:5190 in 15 seconds.
timeout /t 15 /nobreak >nul
start "" http://localhost:5190
