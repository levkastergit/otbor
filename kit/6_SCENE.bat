@echo off
rem Step 1 of a scene: the sketch from the picked art is redrawn into whole pictures (results\90_scenes).
rem Pick one in Otbor.exe, then run 7_SCENE_ITEMS.bat.
chcp 65001 >nul
cd /d "%~dp0"
set SCENE=%~1
if "%SCENE%"=="" set SCENE=p0_reception
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\scene.ps1" -Scene %SCENE% -Stage scene
pause
