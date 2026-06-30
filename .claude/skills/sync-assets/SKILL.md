---
name: sync-assets
description: Pull changed/new game asset files from the collaborator's shared Google Drive into the local gitignored assets/ folder. Use when the user says "sync assets", "pull assets", "update assets from Drive", "get the latest art/sprites/maps", or wants the files a teammate (Raymond) changed since the last sync. Covers connecting/troubleshooting the Google Drive connector and the robocopy mechanics.
---

# Sync assets from the shared Google Drive

`assets/` is **gitignored** (see `.gitignore` — "The ENTIRE assets/ folder is
distributed out-of-band"). The ~380 MB of art lives in a **shared Google Drive
folder owned by raymond.demere@gmail.com**, not in git. This skill **pulls** the
files that changed on Drive into the local repo. (The **push** side — sending local
asset edits back to Drive — is `tools/sync-binaries.ps1 -Push`; this skill does not
push.)

## Key facts (verified this workflow)

- **Drive folder id:** `1WGTDfsm5eSRv7brnsZ7RKsj018q8PNsJ` ("assets").
- **Local mount (Google Drive for Desktop):**
  `G:\.shortcut-targets-by-id\1WGTDfsm5eSRv7brnsZ7RKsj018q8PNsJ\assets`
  It's a **shared-drive shortcut target** (stream-on-access, but fully copyable —
  reading a file triggers its on-demand download). The drive letter / exact path can
  differ per machine; if `G:\.shortcut-targets-by-id\...` doesn't exist, look under
  `G:\My Drive\...` or ask the user to open the folder in Explorer and paste the path.
- **Repo target:** `C:\Users\Johan\source\repos\Lucid\NecrokingMG\assets`
- The Drive tree **mirrors** the repo layout: `assets/Sprites/`, `assets/maps/`,
  `assets/Environment/<Buildings|Ground|...>/`, `assets/Effects/`, `assets/Items/`, ...

## The fast path — robocopy newer-wins (no connector needed)

This copies every file that is **new or newer on Drive** and skips anything identical
or older, i.e. exactly "the changed assets". Run it and you're done:

```bash
MSYS_NO_PATHCONV=1 robocopy \
  "G:\.shortcut-targets-by-id\1WGTDfsm5eSRv7brnsZ7RKsj018q8PNsJ\assets" \
  "C:\Users\Johan\source\repos\Lucid\NecrokingMG\assets" \
  /E /XO /NJH /NJS /NDL /NP /R:1 /W:1; echo "EXIT=$?"
```

- `/E` recurse, `/XO` exclude-older (newer-wins → only changed/new files copy),
  **no `/MIR`** so nothing is ever deleted. robocopy **exit code 0–7 = success**
  (0 = nothing to do, 1 = files copied). 8+ = real error.
- **`MSYS_NO_PATHCONV=1` is mandatory from Git Bash** — without it, Bash mangles the
  `/E` `/XO` flags into fake paths like `C:/Program Files/Git/XO` and robocopy fails
  with "Invalid Parameter".
- The **bash prompt-guard hook force-allows robocopy when the destination is inside
  the project**, so this runs **without a permission prompt**. (A dest *outside* the
  project, or `/MIR`/`/PURGE`, still prompts — by design.)

## The smart path — report what changed, then pull (uses the connector)

When the user wants to **see what changed / who changed it** (or pull only a subset),
use the Google Drive connector to read the activity, then robocopy. The connector is
`mcp__9d4989f4-...__*` (search its tools by keyword if deferred).

1. **Find recent edits** (pick a cutoff — the user's last sync, or ask):
   ```
   search_files: modifiedTime > '2026-06-20T00:00:00Z'
                 and mimeType != 'application/vnd.google-apps.folder'
                 and owner = 'raymond.demere@gmail.com'
   ```
   Paginate via `pageToken`. `search_files` matches only a **direct** `parentId`, not
   descendants — that's why you filter by `owner` + `modifiedTime` across the whole
   Drive instead of trying to scope to the folder tree.
2. **Map each file's folder to a local path:** `get_file_metadata` on the file's
   `parentId` gives the folder `title` and its parent; chain up to `assets` to build
   `assets/<...>/`. Present a grouped table (file · size · new/update).
3. **Pull** — robocopy the affected folders with an explicit file list (same command
   shape as the fast path but naming the files), OR just run the fast-path whole-tree
   command, which already copies exactly those changed files.

**Never** pull large binaries through the connector's `download_file_content` — it
returns base64, and the sprite sheets (20–45 MB) and `assets/maps/default.json`
(~55 MB) will blow up context. The connector is for **finding/ reporting**; robocopy
from the mount does the **copying**.

## Connecting the Google Drive connector + troubleshooting

The connector lives under **Settings → Connectors → "Connectors have moved to
Customize"** (the connector URL is Google's generic `https://drivemcp.googleapis.com/mcp/v1`
— that's correct; it binds to *your* Drive via a Google OAuth sign-in, not via the URL).

Symptom seen this session: the UI shows **"Connected"** but every tool call returns
**`This connector requires authentication`**, and `mcp-registry list_connectors`
reports zero installed connectors. Things that did **not** fix it: a conversation
reload; a full app restart. **What fixed it:** **disconnect and reconnect** the
connector — the reconnect surfaced the Google "enable access to Google Drive" consent
page, which is the OAuth grant that was missing. If Disconnect is greyed out / the
tooltip says "doesn't use authentication", look for a different management surface
(the Customize page) to remove + re-add it. After reconnecting, verify with a cheap
`list_recent_files` call before relying on it.

If the connector simply won't authorize, the **fast path needs no connector at all** —
fall back to it.

## After pulling — verify (git won't show it)

Because `assets/` is gitignored, **`git status` will NOT list the copied files** — that
is expected, not a failure. Verify the pull landed with the file tools instead:

- `Glob "assets/Sprites/*"` / `Glob "assets/maps/*"` etc. to confirm new files exist.
- `git check-ignore assets/maps/default.json` returns the path → confirms it's ignored
  (so there's nothing to commit for assets).

Report what was copied (the robocopy output lists each file as `New File` / `Newer`).
There is nothing to commit or push — the assets are out-of-band by design.
