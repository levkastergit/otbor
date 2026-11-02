@echo off
rem Step 2 of a scene: every search item is drawn into the picked picture, its layer is cut out (results\91_<scene>).
rem Pick one variant of each item in Otbor.exe.
chcp 65001 >nul
cd /d "%~dp0"
set SCENE=%~1
if "%SCENE%"=="" set SCENE=p0_reception
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\scene.ps1" -Scene %SCENE% -Stage items
pause
