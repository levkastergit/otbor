# Шаг 2. Обучает три LoRA на FLUX.2 [klein] base 4B через ai-toolkit:
#   pd_char_v1 — персонажи (datasets\char), pd_prop_v1 — предметы (datasets\prop), pd_bg_v1 — фоны (datasets\bg).
# Готовая LoRA пропускается. Прерванное обучение ai-toolkit продолжает с последнего чекпойнта.
# -Retrain: pd_char_v2 на исходных образцах плюс отобранных (retrain.py), затем лист сравнения с pd_char_v1.
param([ValidateSet("all", "char", "prop", "bg")][string]$Only = "all", [switch]$Retrain)
. "$PSScriptRoot\common.ps1"

if (-not (Step-Done "install")) { Fail "Сначала запустите 1_INSTALL.bat." }
$gpu = Get-Gpu
Write-Host ("Видеокарта: {0}, {1:N1} ГБ" -f $gpu.Name, ($gpu.VramMiB / 1024))
Test-UnrealRunning
$lowVram = if ($gpu.VramMiB -lt 14000) { "true" } else { "false" }

# Конфиг по образцу BFL «FLUX.2 [klein] Style Training» (docs.bfl.ai) для модели 4B;
# quantize = 8-битный трансформер и текстовый энкодер, чтобы влезть в 16 ГБ.
# noise_scheduler "flowmatch" в примере BFL не указан, но без него ai-toolkit берёт значение
# по умолчанию "ddpm" и на первом шаге падает: "`mu` must be passed when `use_dynamic_shifting`...".
$template = @'
job: extension
config:
  name: "__NAME__"
  process:
    - type: "diffusion_trainer"
      training_folder: "__OUT__"
      device: "cuda"
      trigger_word: "__TRIGGER__"
      network:
        type: "lora"
        linear: 32
        linear_alpha: 32
        conv: 16
        conv_alpha: 16
      save:
        dtype: "bf16"
        save_every: 250
        max_step_saves_to_keep: 12
      datasets:
        - folder_path: "__DATA__"
          caption_ext: "txt"
          caption_dropout_rate: 0.05
          cache_latents_to_disk: true
          resolution: [__RES__]
      train:
        batch_size: 1
        steps: __STEPS__
        gradient_accumulation_steps: 1
        train_unet: true
        train_text_encoder: false
        gradient_checkpointing: true
        noise_scheduler: "flowmatch"
        optimizer: "adamw8bit"
        lr: 0.0001
        timestep_type: "weighted"
        content_or_style: "balanced"
        dtype: "bf16"
      model:
        name_or_path: "black-forest-labs/FLUX.2-klein-base-4B"
        arch: "flux2_klein_4b"
        quantize: true
        quantize_te: true
        low_vram: __LOWVRAM__
meta:
  name: "[name]"
  version: "1.0"
'@

$loras = @(
    @{ Key = "char"; Name = "pd_char_v1"; Trigger = "PDCH4R"; Steps = $CharSteps; Res = "512, 768, 1024" },
    @{ Key = "prop"; Name = "pd_prop_v1"; Trigger = "PDPR0P"; Steps = $PropSteps; Res = "512, 768" },
    @{ Key = "bg";   Name = "pd_bg_v1";   Trigger = "PDB4CK"; Steps = $BgSteps;   Res = "512, 768, 1024" }
)
$out = Join-Path $Runtime "output"
$cfgDir = Join-Path $Runtime "configs"
New-Item -ItemType Directory -Force $out, $cfgDir | Out-Null

if ($Retrain) {
    Say "Собираю набор: исходные образцы плюс отобранные"
    $code = Invoke-Logged "train_pd_char_v2.log" "" $Py @((Join-Path $PSScriptRoot "retrain.py"), "--kit", $Kit)
    if ($code -ne 0) { Fail "Набор для дообучения не собран." }
    $previous = Join-Path $out "pd_char_v2"
    if (Test-Path $previous) { Remove-Item -Recurse -Force $previous }
    # 3000 шагов — верхняя граница BFL для персонажей: картинок стало больше, а шагов на каждую меньше.
    $loras = @(@{ Key = "char_v2"; Name = "pd_char_v2"; Trigger = "PDCH4R"; Steps = 3000; Res = "512, 768, 1024" })
}

foreach ($l in $loras) {
    if ($Only -ne "all" -and $Only -ne $l.Key) { continue }
    $final = Join-Path $out "$($l.Name)\$($l.Name).safetensors"
    if (Test-Path $final) { Say "$($l.Name): уже обучена, пропускаю ($final)"; continue }
    $data = Join-Path $Kit "datasets\$($l.Key)"
    if (-not (Test-Path $data)) { Warn "Нет папки $data — $($l.Name) пропускаю."; continue }
    if (-not $l.Steps) { Warn "В settings.ps1 не задано число шагов для $($l.Name) — пропускаю. Возьмите settings.ps1 из нового набора."; continue }
    $yaml = $template
    $yaml = $yaml.Replace("__NAME__", $l.Name).Replace("__OUT__", ($out -replace '\\', '/'))
    $yaml = $yaml.Replace("__TRIGGER__", $l.Trigger).Replace("__DATA__", ($data -replace '\\', '/'))
    $yaml = $yaml.Replace("__RES__", $l.Res).Replace("__STEPS__", [string]$l.Steps).Replace("__LOWVRAM__", $lowVram)
    $cfg = Join-Path $cfgDir "$($l.Name).yaml"
    [IO.File]::WriteAllText($cfg, $yaml, (New-Object Text.UTF8Encoding($false)))
    Say "Обучаю $($l.Name): $($l.Steps) шагов, чекпойнт каждые 250 (лог: results\logs\train_$($l.Name).log)"
    $t0 = Get-Date
    $code = Invoke-Logged "train_$($l.Name).log" $Aitk $Py @("run.py", $cfg)
    if ($code -ne 0) { Fail "Обучение $($l.Name) завершилось с ошибкой (код $code)." }
    if (-not (Test-Path $final)) { Fail "ai-toolkit завершился, но файла $final нет." }
    Write-Host ("Готово за {0:N0} мин." -f ((Get-Date) - $t0).TotalMinutes)
}
if ($Retrain) {
    Say "Сравниваю с прежней моделью"
    $code = Invoke-Logged "generate.log" "" $Py @((Join-Path $PSScriptRoot "generate.py"), "--kit", $Kit, "--runtime", $Runtime, "--compare-char", "pd_char_v2")
    if ($code -ne 0) { Fail "Лист сравнения не собран." }
    Say "Дообучение закончено: results\compare\pd_char_v2.png"
    exit 0
}
Say "Обучение закончено. Дальше: 3_GENERATE.bat"
exit 0
