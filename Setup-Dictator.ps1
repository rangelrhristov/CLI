$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$venv = Join-Path $root ".dictator-venv"
$python = Join-Path $venv "Scripts\python.exe"
$requirements = Join-Path $root "dictator\requirements.txt"
$modelsDir = Join-Path $root "dictator\models"
$modelName = "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8"
$modelDir = Join-Path $modelsDir $modelName
$archive = Join-Path $modelsDir "$modelName.tar.bz2"
$url = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/$modelName.tar.bz2"

if (-not (Test-Path -LiteralPath $venv)) {
  python -m venv $venv
}

& $python -m pip install --upgrade pip
& $python -m pip install -r $requirements

New-Item -ItemType Directory -Force -Path $modelsDir | Out-Null

if (-not (Test-Path -LiteralPath (Join-Path $modelDir "encoder.int8.onnx"))) {
  if (-not (Test-Path -LiteralPath $archive)) {
    Write-Host "Downloading Parakeet model. This is about 630 MB and only happens once..."
    Invoke-WebRequest -Uri $url -OutFile $archive
  }

  Write-Host "Extracting Parakeet model..."
  & $python -c "import pathlib, tarfile; archive=pathlib.Path(r'$archive'); dest=pathlib.Path(r'$modelsDir'); tarfile.open(archive, 'r:bz2').extractall(dest, filter='data')"
}

Write-Host "FD Dictator is ready."
