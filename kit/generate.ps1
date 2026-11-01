# Шаг 3. Генерация по списку jobs.json.
#   1) results\compare\*.png — сравнение чекпойнтов LoRA (по нему выбирают $CharStep/$PropStep/$BgStep в settings.ps1);
#   2) все задачи из jobs.json -> results\<группа>\<задача>\, обзор в results\index.html.
# Уже сделанные картинки пропускаются; -Force перегенерирует, -Only "hub_,papa_" — только эти задачи.
# Задания, отмеченные в Otbor.exe как готовые, не делаются заново; -Regen — только отмеченные на перегенерацию.
param([string]$Only = "", [switch]$Force, [switch]$SkipCompare, [switch]$Regen)
. "$PSScriptRoot\common.ps1"

if (-not (Step-Done "install")) { Fail "Сначала запустите 1_INSTALL.bat." }
Test-UnrealRunning
$gen = Join-Path $PSScriptRoot "generate.py"
$common = @("--kit", $Kit, "--runtime", $Runtime)

if (-not $SkipCompare -and -not $Only -and -not $Regen) {
    Say "Сравнение чекпойнтов LoRA"
    $code = Invoke-Logged "generate.log" "" $Py (@($gen) + $common + @("--compare"))
    if ($code -ne 0) { Fail "Сравнение чекпойнтов завершилось с ошибкой." }
}

Say "Генерация по jobs.json"
$args2 = @($gen) + $common + @("--variants", [string]$Variants)
if ($CharStep) { $args2 += @("--char-step", [string]$CharStep) }
if ($PropStep) { $args2 += @("--prop-step", [string]$PropStep) }
if ($BgStep)   { $args2 += @("--bg-step", [string]$BgStep) }
if ($Only)     { $args2 += @("--only", $Only) }
if ($Force)    { $args2 += "--force" }
if ($Regen)    { $args2 += "--regen" }
$code = Invoke-Logged "generate.log" "" $Py $args2
if ($code -ne 0) { Fail "Генерация завершилась с ошибкой." }

$index = Join-Path $Kit "results\index.html"
Say "Готово: $index"
if (Test-Path $index) { Start-Process $index }
exit 0
