# Сцена поиска как одна картина (scripts\scene.py).
#   -Stage scene : эскиз из отобранных картинок -> варианты цельной картины в results\90_scenes (отбор в Otbor.exe)
#   -Stage items : в отобранную картину вписываются открыватели и предметы -> results\91_<сцена> (отбор в Otbor.exe)
param([string]$Scene = "p0_reception", [ValidateSet("scene", "items")][string]$Stage = "scene", [string]$Only = "", [switch]$Force)
. "$PSScriptRoot\common.ps1"

if (-not (Step-Done "install")) { Fail "Сначала запустите 1_INSTALL.bat." }
Test-UnrealRunning
$py = Join-Path $PSScriptRoot "scene.py"
$args2 = @($py, "--kit", $Kit, "--runtime", $Runtime, "--scene", $Scene, "--stage", $Stage)
$args2 += @("--variants", $(if ($Stage -eq "scene") { "4" } else { "3" }))
if ($BgStep) { $args2 += @("--bg-step", [string]$BgStep) }
if ($Only)   { $args2 += @("--only", $Only) }
if ($Force)  { $args2 += "--force" }

Say "Сцена $Scene, шаг $Stage"
$code = Invoke-Logged "scene.log" "" $Py $args2
if ($code -ne 0) { Fail "Сцена: шаг $Stage завершился с ошибкой (см. logs\scene.log)." }
Say "Готово. Откройте Otbor.exe и отберите варианты."
exit 0
