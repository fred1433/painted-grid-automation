# Pinned English model for local Tesseract (tessdata_fast tag 4.1.0), checked by SHA-256.
$ErrorActionPreference = 'Stop'
$dir = Join-Path $PSScriptRoot '..\tessdata'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$file = Join-Path $dir 'eng.traineddata'
$expected = '7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2'
if (-not (Test-Path $file)) {
  Invoke-WebRequest -Uri 'https://github.com/tesseract-ocr/tessdata_fast/raw/4.1.0/eng.traineddata' -OutFile $file
}
$actual = (Get-FileHash -Algorithm SHA256 $file).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "eng.traineddata hash mismatch: $actual" }
Write-Host "eng.traineddata sha256 $actual"
