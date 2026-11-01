@echo off
rem Generates only the jobs marked for regeneration in Otbor.exe.
chcp 65001 >nul
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\generate.ps1" -Regen
pause
