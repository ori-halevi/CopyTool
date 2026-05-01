@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0CopyTool.ps1"
echo.
echo ============================================================
echo  הסקריפט הסתיים. קוד יציאה: %ERRORLEVEL%
echo ============================================================
pause
