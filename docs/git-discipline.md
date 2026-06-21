# Git discipline — Drive-synced collaborator

Full reference for managing git on this repo. The terse trigger list lives in
`CLAUDE.md`; this doc holds the *why* and the procedures. Read it before any push, any
multi-file commit, or any sync operation.

## The setup (why this is delicate)

This repo is shared with a **collaborator (a friend)**, and that friend's machine mirrors its whole
workspace to a shared **Google Drive** folder instead of relying on git. So "the other machine"
below means **the friend's Drive-synced clone**, not a second machine of the user's own.
(Separately, the user sometimes runs **multiple Claude sessions against this same working
directory** — that's the same tree, no git/Drive sync between them; the risk there is concurrent
edits/builds colliding, not stale commits. The rules below are about the Drive-synced friend.)

**Drive sync is NOT a substitute for git** — it has already produced a broken `origin/master` where
committed code referenced symbols whose defining files were never committed/pushed, so the remote
did not compile. The assistant must actively manage git for the user, who may not be comfortable
with git:

1. **Build before every push.** Run `dotnet build Necroking/Necroking.csproj` and confirm it
   succeeds. **Never push code that does not build.** If a relevant scenario exists, run it too
   (`bin/Debug/Necroking.exe --scenario <name> --headless --speed 10`).
2. **Check for untracked/uncommitted files before committing.** Run `git status` and read it.
   New `.cs` files are the usual culprit — a commit that references a new symbol but omits the file
   that *defines* it yields a remote that won't compile. Stage all related changes together so a
   feature's code and the definitions it needs land in the **same commit** (review what's new, then
   `git add -A`). Do not leave a feature half-committed.
3. **Remind the user to push after a working feature is committed.** A local commit the other
   machine can't see is the failure mode here; Drive "syncing" can silently lose or partial it.
   Say something like: *"This is committed and builds — want me to push it to origin now?"*
4. **If `git status` shows the branch is ahead of origin, surface it.** e.g. *"You have N commits
   not yet pushed — push so the other machine stays in sync?"* Still ask permission before the
   actual push (per the rule above), but **do prompt** — don't let working work sit unpushed.
5. **Pull before starting fresh work.** If a pull won't fast-forward (histories diverged because
   someone pushed from the other machine), stop and tell the user rather than force-resolving.

## Per-machine user settings (do NOT commit them)

As of 2026-06-16, the **per-machine user files live in the `user settings/` folder** at the project
root, which is **`.gitignore`d** — never shared via git. These are: `settings.json` (ESC-menu
settings), `weather.json` (weather presets), and `spellbar.json` (spell-bar loadout). The game
**seeds** each from its shipped `data/` default on first run (`GamePaths.SeededUserFile`), then writes
only to the user copy — so **`data/settings.json` / `data/weather.json` / `data/spellbar.json` no
longer churn**.

**One-time migration when an older clone syncs (the other machine):** after pulling, if `git status`
shows any of those three **`data/*.json`** files modified (the machine's old local values), run the
game once — it copies them into `user settings/` automatically — then
`git checkout -- data/settings.json data/weather.json data/spellbar.json` to drop the now-redundant
tracked changes. The machine's settings are now local. (If they're already clean, nothing to do.)
Never re-add `user settings/` to git or write these back into `data/`.
