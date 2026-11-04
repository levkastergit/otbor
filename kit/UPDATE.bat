@echo off
rem Otbor: downloads the latest program and generation scripts from GitHub into this FluxKit folder.
chcp 65001 >nul
cd /d "%~dp0"
set BASE=https://raw.githubusercontent.com/levkastergit/otbor/main
if not exist jobs.json (
  echo Put UPDATE.bat into the FluxKit folder, next to jobs.json.
  pause
  exit /b 1
)
if not exist results mkdir results
call :get Otbor.exe results\Otbor.exe || goto fail
if exist Otbor.exe copy /y results\Otbor.exe Otbor.exe >nul || goto fail
call :get kit/generate.py scripts\generate.py || goto fail
call :get kit/generate.ps1 scripts\generate.ps1 || goto fail
call :get kit/train.ps1 scripts\train.ps1 || goto fail
call :get kit/retrain.py scripts\retrain.py || goto fail
call :get kit/5_REGEN.bat 5_REGEN.bat || goto fail
if not exist scenes mkdir scenes
call :get kit/scene.py scripts\scene.py || goto fail
call :get kit/scene.ps1 scripts\scene.ps1 || goto fail
call :get kit/6_SCENE.bat 6_SCENE.bat || goto fail
call :get kit/7_SCENE_ITEMS.bat 7_SCENE_ITEMS.bat || goto fail
call :get kit/scenes/p0_reception.json scenes\p0_reception.json || goto fail
call :get kit/jobs.json jobs.json || goto fail
call :get kit/8_REF3D.bat 8_REF3D.bat || goto fail
echo.
echo DONE. Run results\Otbor.exe
pause
exit /b 0

:get
echo %~2
curl.exe -fsSL -o "%~2.new" "%BASE%/%~1" || exit /b 1
move /y "%~2.new" "%~2" >nul || exit /b 1
exit /b 0

:fail
echo.
echo FAILED. Close Otbor.exe, check the internet connection and run UPDATE.bat again.
pause
exit /b 1
