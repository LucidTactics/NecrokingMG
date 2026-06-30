#!/usr/bin/env python3
"""Detect whether a Bash command could WRITE or MODIFY files.

Purpose: a positive-safety gate for the Bash prompt guard. The cheap, reliable
signal isn't "is this command dangerous" (undecidable) but "could this command
touch the filesystem at all". If a command contains **none** of the file-writing
utilities catalogued here **and** does no output redirection to a file, it cannot
create/modify a file and can be treated as read-only — safe to wave through.

This is the inverse of an allow-list: instead of enumerating the (few) safe
commands, we enumerate the (many) ways to write a file and call everything else
read-only. The list is meant to be EXHAUSTIVE and is the thing under active
maintenance — a miss here is a false "read-only" verdict, so err toward adding.

Design notes / limits (read before trusting a False):
  * Name-based. We match the basename of each token (path- and `.exe`-stripped)
    against the catalogues below. `/usr/bin/sed` and `sed.exe` both match `sed`.
  * Interpreters defeat name-based safety. `python -c "open('f','w')…"`, `perl -e`,
    `node -e`, any `*sh -c`, `awk`, … can write any file with no write-named token.
    They're in INTERPRETERS and are treated as "can write" unconditionally — once a
    general interpreter is present we cannot prove read-only, full stop.
  * Wrappers launch other commands. `xargs`, `env`, `find -exec`, `timeout`,
    `parallel`, `nohup`, … run an inner command we don't statically resolve. Treated
    as "can write" (WRAPPERS) — same reasoning as interpreters.
  * Redirection. `>`/`>>`/`2>`/`&>` to a real file is a write even with an
    otherwise-innocent command (`echo x > f`). Detected separately, null-device and
    fd-dup redirects excluded (they write no file).
  * This module makes NO network/exec/read-sensitivity judgements — only "writes a
    file?". `curl`/`scp`/`git clone` are here because they LAND bytes on disk, but a
    plain `curl https://x | grep y` (no -o, piped onward) is a different question this
    module doesn't try to answer; it conservatively flags the tool by name anyway.

Public API:
    can_write_files(cmd) -> bool      # True if cmd could create/modify any file
    write_indicators(cmd) -> list     # the specific reasons (for messaging/tests)
"""
import re

# ---------------------------------------------------------------------------------
# The catalogue. Grouped by mechanism; flattened into NAME_SETS at the bottom.
# Every entry is a bare command basename (no path, no .exe). Keep alphabetical-ish
# within a group so additions are easy to eyeball for duplicates.
# ---------------------------------------------------------------------------------

# --- Text / stream editors that mutate files in place (or write via args) ---------
EDITORS = {
    "sed", "gsed",                       # -i in-place
    "awk", "gawk", "mawk", "nawk",       # gawk -i inplace; awk can open files for write
    "perl",                              # -i / -pi / -ni  (also an interpreter, see below)
    "ed", "red", "ex", "vi", "vim", "nvim", "view", "vimdiff",
    "emacs", "emacsclient",              # --batch --eval / -nw scripted writes
    "nano", "pico", "micro", "joe", "jed", "kak", "hx", "helix",
    "ne", "mg", "zile", "jove", "tilde", "dav",
}

# --- File creators / content writers (write to a path given as an argument) --------
CREATORS = {
    "tee", "sponge",                     # write stdin to named file(s) — no '>' needed
    "cp", "copy", "install", "ginstall",
    "robocopy", "xcopy", "rcp",          # Windows / remote copy -> writes DEST tree
    "mv", "move", "rename", "prename", "rename.ul", "mmv", "mcp",
    "dd",                                # of=FILE
    "touch",                             # create / restamp
    "truncate", "fallocate",             # size / preallocate -> creates/zeros files
    "split", "csplit",                   # emit multiple output files
    "mktemp",                            # creates a temp file/dir
    "ln", "link", "symlink",             # create/clobber links
    "mkfifo", "mknod", "mkdir", "mktemp",
    "shred",                             # overwrites file contents (then optionally -u)
    "wipe", "srm",                       # secure-delete = overwrite
    "vipe", "chronic",                   # moreutils stdin->editor->file
}

# --- Removers (modify the filesystem; a "write" for guarding purposes) -------------
REMOVERS = {
    "rm", "del", "erase", "unlink", "rmdir", "shred", "wipe", "srm", "trash",
    "trash-put", "gio",                  # gio trash / gio remove
}

# --- Permission / ownership / attribute / timestamp modifiers ---------------------
METADATA = {
    "chmod", "chown", "chgrp", "chattr", "lsattr",   # lsattr reads, but chattr writes; keep pair
    "setfacl", "setfattr", "chflags", "chcon", "restorecon",
    "ln", "touch",                                   # touch -d, ln -sf already above
    "icacls", "attrib", "takeown", "cacls",          # Windows ACL/attr writers
}

# --- Archive / compression: extraction & creation both land files on disk ---------
ARCHIVERS = {
    "tar", "gtar", "bsdtar", "star",
    "zip", "unzip", "funzip", "zipnote", "zipsplit", "zipcloak",
    "gzip", "gunzip", "zcat", "pigz",                # zcat reads; pigz writes — keep family
    "bzip2", "bunzip2", "pbzip2", "lbzip2",
    "xz", "unxz", "lzma", "unlzma",
    "zstd", "unzstd", "pzstd",
    "lz4", "lzop", "compress", "uncompress",
    "7z", "7za", "7zr", "p7zip",
    "rar", "unrar", "unar", "lsar",
    "cpio", "pax", "ar", "ranlib",
    "brotli", "lrzip", "plzip", "lzip",
}

# --- Network fetch/transfer tools that write files to disk -------------------------
NETWORK = {
    "curl", "wget", "wget2",
    "scp", "rsync", "sftp", "rclone", "lftp", "ftp", "tftp",
    "aria2c", "axel", "httpie", "http", "https",     # httpie's `http --download`
    "nc", "ncat", "netcat", "socat",                 # can land bytes via their own out args
    "git",                                           # clone/checkout/restore/apply/pull/stash
    "svn", "hg", "bzr", "fossil",                    # other VCS working-tree writers
    "scp", "putty", "pscp", "psftp",                 # Windows ssh-suite writers
}

# --- Patch / diff appliers ---------------------------------------------------------
PATCHERS = {
    "patch", "gpatch", "dwdiff",
    # `git apply`/`git am` covered by `git` in NETWORK
}

# --- General-purpose interpreters: can open() and write any file. UNSPROVABLE. ----
INTERPRETERS = {
    "sh", "bash", "dash", "zsh", "ksh", "mksh", "fish", "tcsh", "csh", "ash", "busybox",
    "python", "python2", "python3", "py", "pypy", "pypy3",
    "perl", "perl5", "ruby", "jruby",
    "node", "nodejs", "deno", "bun", "ts-node", "tsx",
    "php", "php-cgi",
    "lua", "luajit", "tclsh", "wish", "expect",
    "R", "Rscript", "julia",
    "ghc", "runghc", "runhaskell", "stack", "cabal",
    "groovy", "scala", "kotlin", "clojure", "clj",
    "swift", "elixir", "iex", "erl", "escript",
    "raku", "perl6", "gforth", "gst",                # smalltalk
    "osascript", "powershell", "pwsh", "cscript", "wscript",
    "csi", "dotnet-script", "fsi",                   # C#/F# scripting
    "guile", "scheme", "racket", "sbcl", "clisp", "ecl",
    "bc", "dc",                                      # dc can write? bc no — but both shell-able; keep dc
}

# --- Wrappers that launch an inner command we don't statically resolve ------------
WRAPPERS = {
    "xargs", "env", "nohup", "setsid", "timeout", "nice", "ionice", "stdbuf",
    "time", "watch", "flock", "chroot", "unshare", "nsenter", "parallel", "rush",
    "sudo", "doas", "su", "runuser", "gosu", "pkexec",
    "make", "gmake", "ninja", "cmake", "meson", "scons", "bazel", "buck",
    "ssh",                                           # ssh host 'rm -rf …' runs a remote writer
    "ssh-keygen",                                    # writes key files
    "entr",                                          # runs a command on file change
}

# --- Package / build / config tools that write files as a side effect -------------
TOOLING = {
    "npm", "npx", "yarn", "pnpm", "bun", "corepack",
    "pip", "pip3", "pipx", "poetry", "uv", "conda", "mamba", "easy_install",
    "gem", "bundle", "bundler",
    "cargo", "rustup",
    "go", "dotnet", "msbuild", "nuget",
    "mvn", "gradle", "gradlew", "ant", "sbt", "lein",
    "composer", "apt", "apt-get", "dpkg", "yum", "dnf", "rpm", "pacman", "brew",
    "apk", "zypper", "snap", "flatpak", "choco", "scoop", "winget",
    "crontab", "at", "batch",                        # crontab FILE installs; at writes job files
    "systemctl", "update-alternatives",
    "ldconfig", "depmod",
}

# --- Structured-data CLIs with in-place / output-file writes ----------------------
DATA_TOOLS = {
    "jq",                                            # no -i, but commonly `jq … | sponge`
    "yq",                                            # yq -i  (in-place!)
    "dasel",                                         # dasel -w
    "xmlstarlet", "xml",                             # xmlstarlet ed -L  (in-place)
    "sqlite3", "psql", "mysql", "mariadb", "mongo", "mongosh", "redis-cli",
    "duckdb",
    "crudini", "augtool", "git-config",
    "openssl",                                       # -out FILE
    "gpg", "gpg2",                                   # -o FILE
    "git-crypt", "age", "sops",
    "convert", "magick", "mogrify", "ffmpeg", "sox", "gs",   # -o / mogrify in-place / gs -sOutputFile
    "pandoc", "wkhtmltopdf", "soffice", "libreoffice",
}

# --- Filesystem / device / mount level (the heavy hammers) ------------------------
FILESYSTEM = {
    "mkfs", "mke2fs", "mkswap", "mount", "umount", "losetup", "blkdiscard",
    "parted", "fdisk", "sfdisk", "gdisk", "cfdisk", "wipefs",
    "tune2fs", "resize2fs", "fsck", "e2fsck", "badblocks",
    "rsync",                                          # already in NETWORK; harmless dup, sets dedupe
}

# All catalogues that mean "this token can write a file".
NAME_SETS = (
    EDITORS, CREATORS, REMOVERS, METADATA, ARCHIVERS, NETWORK, PATCHERS,
    INTERPRETERS, WRAPPERS, TOOLING, DATA_TOOLS, FILESYSTEM,
)

WRITE_NAMES = set().union(*NAME_SETS)

# --- Side effects that aren't file writes: process & power/system control ----------
# Deliberately kept OUT of WRITE_NAMES so the file-write catalogue stays honest — these
# don't touch the filesystem, but they DO change live system state (kill a process,
# power the box down), so a caller using this module as a "is it safe to wave through"
# gate wants them flagged too. Reached via has_side_effects(), not can_write_files().
PROCESS_POWER = {
    "kill", "pkill", "killall", "taskkill", "tskill", "skill", "kill.exe",
    "shutdown", "reboot", "halt", "poweroff", "init", "telinit",
    "logoff", "rundll32",                 # rundll32 can invoke arbitrary system actions
}

SIDE_EFFECT_NAMES = WRITE_NAMES | PROCESS_POWER

# --- Conditional writers: read-only UNLESS a mutating action flag is present --------
# A few commands are read-only by default and only touch the filesystem when handed a
# specific action flag — `find` is a pure search tool until you add -delete or -exec.
# Treating the whole command as a writer (the WRAPPERS approach) would block the common
# read-only use, so instead we flag these ONLY when one of their mutating flags appears
# anywhere in the command string. We deliberately don't prove the flag belongs to THIS
# invocation's args: a stray match just defers to the normal permission flow (safe), and
# an accidental -delete/-exec is vanishingly rare — the whole point is guarding accidents.
# Map: command basename -> set of flag tokens that turn it into a mutator.
CONDITIONAL_WRITERS = {
    "find": {"-exec", "-execdir", "-ok", "-okdir", "-delete",
             "-fprintf", "-fprint", "-fprint0", "-fls"},
    "gfind": {"-exec", "-execdir", "-ok", "-okdir", "-delete",
              "-fprintf", "-fprint", "-fprint0", "-fls"},
}

# Flag tokens that signal an in-place / output-to-file intent on tools that are
# otherwise read-capable. Presence of any of these is treated as a write signal even
# if (somehow) the leading command slipped the name catalogue. Kept conservative to
# avoid false positives on unrelated `-o`/`-i` flags — these are the high-signal ones.
WRITE_FLAGS = {
    "-i", "--in-place", "--inplace", "-i.bak",
    "--write", "-w",            # dasel -w, sed/perl variants
    "of",                       # dd of=  (matched specially below as a prefix)
}


# ---------------------------------------------------------------------------------
# Tokenizer (quote-aware) — mirrors bash_prompt_guard._tokenize so behaviour matches.
# ---------------------------------------------------------------------------------

def _tokenize(cmd: str):
    toks, buf, quote, started = [], "", None, False
    for c in cmd:
        if quote:
            if c == quote:
                quote = None
            else:
                buf += c
            continue
        if c in ('"', "'"):
            quote, started = c, True
            continue
        if c.isspace():
            if started:
                toks.append(buf)
                buf, started = "", False
            continue
        # Operators split tokens too, so `a&&b` -> ['a', 'b'] for name scanning.
        if c in ("|", "&", ";", "<", ">", "(", ")"):
            if started:
                toks.append(buf)
                buf, started = "", False
            continue
        buf += c
        started = True
    if started:
        toks.append(buf)
    return toks


def _basename(tok: str) -> str:
    """Path- and extension-stripped, lowercased command name. `/usr/bin/Sed.EXE`
    -> 'sed'. Leaves non-command tokens alone (they just won't match the catalogue)."""
    base = tok.rsplit("/", 1)[-1].rsplit("\\", 1)[-1].lower()
    if base.endswith(".exe") or base.endswith(".bat") or base.endswith(".cmd"):
        base = base.rsplit(".", 1)[0]
    return base


# ---------------------------------------------------------------------------------
# Output-redirection detection (lifted from bash_prompt_guard so this module stands
# alone). True only for redirects that land bytes in a real file — null device and
# fd-duplication are excluded.
# ---------------------------------------------------------------------------------

_NULL_REDIRECT = re.compile(
    r"(?:\d*|&)>>?\s*(?:/dev/null|nul)\b"
    r"|\d*>>?&\d+",
    re.IGNORECASE)


def has_output_redirection(command: str) -> bool:
    command = _NULL_REDIRECT.sub("", command)
    in_single = in_double = escape = False
    for ch in command:
        if escape:
            escape = False
            continue
        if ch == "\\":
            if not in_single:
                escape = True
            continue
        if ch == "'" and not in_double:
            in_single = not in_single
            continue
        if ch == '"' and not in_single:
            in_double = not in_double
            continue
        if not in_single and not in_double and ch == ">":
            return True
    return False


# ---------------------------------------------------------------------------------
# Public API.
# ---------------------------------------------------------------------------------

def _indicators(cmd: str, names):
    """Shared scan: reasons `cmd` could affect state, matching command tokens against
    `names`. Redirection and write-flag signals are always included (they imply a file
    write regardless of the command name)."""
    reasons = []
    if has_output_redirection(cmd):
        reasons.append("output redirection to a file (>, >>, 2>, &>)")

    toks = _tokenize(cmd)
    tokset = set(toks)
    seen = set()
    for tok in toks:
        base = _basename(tok)
        if base in names and base not in seen:
            seen.add(base)
            verb = "process/power-control" if base in PROCESS_POWER else "file-writing"
            reasons.append(f"{verb} command: {base}")
        # Conditional writers (find -delete/-exec, …): mutator only if a trigger flag is
        # present anywhere in the command. Read-only `find . -name x` slips through.
        if base in CONDITIONAL_WRITERS and base not in seen:
            triggers = CONDITIONAL_WRITERS[base] & tokset
            if triggers:
                seen.add(base)
                reasons.append(f"file-writing command: {base} ({sorted(triggers)[0]})")
        # dd of=FILE and friends — `of=` / `output=` style assignment positionals.
        if re.match(r"^(of|output|out)=", tok, re.IGNORECASE):
            reasons.append(f"output-file argument: {tok}")
        # High-signal in-place / write flags.
        if tok in WRITE_FLAGS or tok.startswith("-i.") or re.match(r"^--in-?place", tok):
            reasons.append(f"in-place/write flag: {tok}")
    return reasons


def write_indicators(cmd: str):
    """Reasons `cmd` could WRITE A FILE (tool name, write flag, or output redirection).
    Empty list => filesystem-read-only by static inspection. Process/power-control
    commands (kill, shutdown, …) are NOT counted here — see side_effect_indicators."""
    return _indicators(cmd, WRITE_NAMES)


def can_write_files(cmd: str) -> bool:
    """True if `cmd` could create or modify a file. False => no file write detected.

    A False is only as trustworthy as the catalogue above — see the module docstring's
    limits. Interpreters and wrappers always return True because their writes can't be
    statically excluded."""
    return bool(write_indicators(cmd))


def conditional_write_triggers(cmd: str):
    """Return [(command, flag), …] for each conditional writer (find, …) whose mutating
    action flag is present in `cmd` — e.g. `find . -delete` -> [('find', '-delete')].
    Empty list if none are triggered (the command is in its read-only form). Lets a
    caller single out 'allow-listed read-only tool used in its mutating mode' for special
    handling, separate from the catch-all can_write_files."""
    toks = _tokenize(cmd)
    tokset = set(toks)
    out, seen = [], set()
    for tok in toks:
        base = _basename(tok)
        if base in CONDITIONAL_WRITERS and base not in seen:
            trig = CONDITIONAL_WRITERS[base] & tokset
            if trig:
                seen.add(base)
                out.append((base, sorted(trig)[0]))
    return out


def side_effect_indicators(cmd: str):
    """Reasons `cmd` could change system state: every write_indicators reason PLUS
    process/power-control commands (kill, taskkill, shutdown, reboot, …). Use this when
    the question is 'is it safe to auto-accept', not just 'does it write a file'."""
    return _indicators(cmd, SIDE_EFFECT_NAMES)


def has_side_effects(cmd: str) -> bool:
    """True if `cmd` could write a file OR change live system state (process/power
    control). False => safe to treat as read-only and side-effect-free."""
    return bool(side_effect_indicators(cmd))


if __name__ == "__main__":
    import sys
    if len(sys.argv) > 1:
        c = " ".join(sys.argv[1:])
        inds = side_effect_indicators(c)
        print(f"has_side_effects: {bool(inds)}  (can_write_files: {can_write_files(c)})")
        for r in inds:
            print(f"  - {r}")
    else:
        print(f"Catalogued {len(WRITE_NAMES)} file-writing + {len(PROCESS_POWER)} "
              f"process/power command names.")
