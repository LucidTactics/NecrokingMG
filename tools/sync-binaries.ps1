<#
  sync-binaries.ps1 — move the gitignored binary assets between this repo and the
  shared Google Drive folder. git carries the code + small data; THIS carries the
  ~380 MB of images + the big map that git ignores (everything under assets/).

  Usage:
    pwsh tools/sync-binaries.ps1            # PULL: Drive -> repo (run after `git pull`)
    pwsh tools/sync-binaries.ps1 -Push      # PUSH: repo -> Drive (run after adding/editing assets)

  One-time setup: set $DriveAssets below to YOUR Google-Drive folder that holds the
  assets (the same folder your teammate's Drive syncs). It must mirror the repo's
  assets/ layout (assets/UI/..., assets/Sprites/..., assets/maps/..., etc.).

  Notes:
   - No deletions (uses robocopy /XO, newer-wins, additive). If you DELETE an asset,
     remove it from both sides by hand — binary sync can't safely auto-delete.
   - The repo must live OUTSIDE any Drive-synced folder, or Drive will corrupt .git.
#>
param([switch]$Push)

# >>> EDIT THIS to your Google Drive assets folder <<<
$DriveAssets = "G:\Other computers\My Computer\assets"

$RepoAssets = (Resolve-Path (Join-Path $PSScriptRoot "..\assets")).Path

if (-not (Test-Path $DriveAssets)) {
    Write-Error "Drive folder not found: '$DriveAssets'. Edit `$DriveAssets at the top of this script."
    exit 1
}

if ($Push) { $src = $RepoAssets;   $dst = $DriveAssets; $dir = "repo -> Drive (push)" }
else       { $src = $DriveAssets;  $dst = $RepoAssets;  $dir = "Drive -> repo (pull)" }

Write-Host "Syncing assets: $dir"
Write-Host "  from: $src"
Write-Host "  to:   $dst"

# /E copy subdirs (incl. empty); /XO skip files that are older in dest (newer-wins);
# NO /MIR so nothing is ever deleted; quiet output; light retries.
robocopy $src $dst /E /XO /NFL /NDL /NJH /NP /R:1 /W:1 | Out-Null
$code = $LASTEXITCODE
# robocopy exit codes 0-7 are success (0 = nothing to do, 1 = files copied, etc.)
if ($code -ge 8) { Write-Error "robocopy failed (exit $code)"; exit $code }
Write-Host "Done ($dir)."
