# Crops one icon PNG per unit in a balance_matrix results file, for the HTML
# balance report. Reads each unit's sprite atlas + name from data/units.json,
# finds the sprite's Icon frame (fallback: first Idle frame) in the atlas
# .spritemeta, and crops it out of the atlas PNG with System.Drawing.
#
# Usage (from repo root):
#   powershell -ExecutionPolicy Bypass -File tools/balance_report/crop_icons.ps1
# Then generate the report:
#   python tools/balance_report/make_report.py
param(
    [string]$ResultsPath = "bin/Debug/log/balance_results.json",
    [string]$OutDir = "tools/balance_report/icons"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)  # repo root
Add-Type -AssemblyName System.Drawing

$resultsFile = Join-Path $root $ResultsPath
$results = Get-Content $resultsFile -Raw | ConvertFrom-Json
$unitsJson = Get-Content (Join-Path $root "data/units.json") -Raw | ConvertFrom-Json
$defs = @{}
foreach ($u in $unitsJson.units) { $defs[$u.id] = $u }

$outPath = Join-Path $root $OutDir
New-Item -ItemType Directory -Force $outPath | Out-Null

$atlasCache = @{}
foreach ($id in $results.units) {
    $def = $defs[$id]
    if ($null -eq $def) { Write-Warning "no unit def for '$id'"; continue }
    $atlas = $def.sprite.atlas
    $sname = $def.sprite.name
    $metaPath = Join-Path $root "assets/Sprites/$atlas.spritemeta"

    $line = Select-String -Path $metaPath -Pattern ("^" + [regex]::Escape("$sname.Icon.")) | Select-Object -First 1
    if ($null -eq $line) {
        $line = Select-String -Path $metaPath -Pattern ("^" + [regex]::Escape("$sname.Idle.")) | Select-Object -First 1
    }
    if ($null -eq $line) { Write-Warning "no Icon/Idle frame for '$sname' in $atlas"; continue }

    $fields = $line.Line.Split("`t")
    $rect = $fields[1].Split(',')
    $x = [int]$rect[0]; $y = [int]$rect[1]; $w = [int]$rect[2]; $h = [int]$rect[3]

    if (-not $atlasCache.ContainsKey($atlas)) {
        $atlasCache[$atlas] = [System.Drawing.Bitmap]::FromFile((Join-Path $root "assets/Sprites/$atlas.png"))
    }
    $bmp = $atlasCache[$atlas]
    $crop = $bmp.Clone([System.Drawing.Rectangle]::new($x, $y, $w, $h), $bmp.PixelFormat)
    $file = Join-Path $outPath "$id.png"
    $crop.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $crop.Dispose()
    Write-Host "cropped $id <- $atlas/$sname ($w x $h)"
}
foreach ($bmp in $atlasCache.Values) { $bmp.Dispose() }
