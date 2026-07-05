"""Junction-safe `git worktree remove` for Windows.

Why this exists: on git-for-windows (verified 2.51.2), `git worktree remove --force`
recursively deletes the worktree and FOLLOWS junctions/reparse points inside it,
destroying the junction TARGET's contents. On 2026-07-05 that emptied the gitignored
385 MB assets/ folder (recovered from the shared Drive). The Bash guard hook redirects
raw `git worktree remove --force` here.

What it does:
  1. Walks the worktree and removes every directory reparse point (junction / dir
     symlink) AS A LINK (os.rmdir on the link — never touches the target).
  2. Runs `git worktree remove --force <path>` on the now link-free tree.

Usage:  python tools/safe_worktree_remove.py <worktree-path>
Stdlib-only; safe to run with any Python 3.8+, including the embeddable build.
"""

import os
import stat
import subprocess
import sys


def is_reparse_dir(entry: os.DirEntry) -> bool:
    """True for junctions and directory symlinks (any directory reparse point)."""
    try:
        st = entry.stat(follow_symlinks=False)
    except OSError:
        return False
    if entry.is_symlink():
        return entry.is_dir(follow_symlinks=True) or True
    attrs = getattr(st, "st_file_attributes", 0)
    reparse = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    return bool(attrs & reparse) and entry.is_dir(follow_symlinks=False)


def remove_links(root: str) -> int:
    """Remove every directory reparse point under root (as links). Returns count."""
    removed = 0
    stack = [root]
    while stack:
        d = stack.pop()
        try:
            entries = list(os.scandir(d))
        except OSError:
            continue
        for e in entries:
            if e.is_dir(follow_symlinks=False):
                if is_reparse_dir(e):
                    os.rmdir(e.path)  # deletes ONLY the link, never the target
                    print(f"removed link: {e.path}")
                    removed += 1
                else:
                    stack.append(e.path)
    return removed


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    wt = os.path.abspath(sys.argv[1])
    if not os.path.isdir(wt):
        print(f"error: not a directory: {wt}")
        return 1
    # Sanity: a linked worktree has a .git FILE (pointer), not a .git directory.
    dotgit = os.path.join(wt, ".git")
    if not os.path.isfile(dotgit):
        print(f"error: {wt} does not look like a linked git worktree (.git file missing)")
        return 1

    n = remove_links(wt)
    print(f"{n} reparse point(s) removed; running git worktree remove --force")
    r = subprocess.run(["git", "worktree", "remove", "--force", wt])
    return r.returncode


if __name__ == "__main__":
    sys.exit(main())
