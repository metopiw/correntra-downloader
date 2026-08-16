@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\dev-start.ps1"
if errorlevel 1 (
  echo.
  echo Correntra baslatilamadi. Yukaridaki hata ayrintisini kontrol edin.
  pause
  exit /b 1
)
endlocal

