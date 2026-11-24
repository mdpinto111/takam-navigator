# Downloads the multilingual-e5-small embedding model (MIT license) from Hugging Face.
# Usage: powershell -ExecutionPolicy Bypass -File scripts/download-model.ps1
param(
    [string]$Target = (Join-Path $PSScriptRoot "..\models\multilingual-e5-small")
)
$ErrorActionPreference = "Stop"
$base = "https://huggingface.co/intfloat/multilingual-e5-small/resolve/main"
$files = @(
    @{ Remote = "onnx/model.onnx";          Local = "model.onnx" },
    @{ Remote = "sentencepiece.bpe.model";  Local = "sentencepiece.bpe.model" },
    @{ Remote = "tokenizer.json";           Local = "tokenizer.json" }
)
New-Item -ItemType Directory -Force -Path $Target | Out-Null
foreach ($f in $files) {
    $dest = Join-Path $Target $f.Local
    if (Test-Path $dest) { Write-Host "déjà présent : $($f.Local)"; continue }
    Write-Host "téléchargement : $($f.Remote)"
    $ProgressPreference = "SilentlyContinue"
    Invoke-WebRequest -Uri "$base/$($f.Remote)" -OutFile "$dest.part"
    Move-Item "$dest.part" $dest
}
Write-Host "Modèle prêt dans $Target"
