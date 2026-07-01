#!/usr/bin/env python3
"""bashlex-backed structural analysis of a Bash command, with graceful fallback.

The prompt guard used to answer structural questions — how does this command split
into sub-commands, does it redirect to a real file, is there a genuine command/process
substitution or a heredoc — with quote-aware regex/char scanners. Those are cheap but
approximate: a `$(…)` inside SINGLE quotes was treated as a real substitution, and
`$(( 5 > 3 ))` looked like an output redirection. bashlex parses the command into a real
AST, so these become exact:

  * `echo '$(rm -rf x)'`  -> NO substitution (single-quoted; it's a literal string)
  * `echo "$(rm -rf x)"`  -> a real command substitution (double quotes expand it)
  * `a=$(( 5 > 3 ))`      -> arithmetic, NOT a `>` file redirection
  * `echo hi 2>/dev/null` -> a redirect, but to the null device, not a file write

bashlex raises on some genuine inputs — arithmetic expansion is unimplemented, and a
real syntax error throws — so EVERY function here returns whether the parse succeeded.
`analyze()` returns an Analysis whose `.ok` is False when bashlex couldn't parse; the
caller then falls back to its old heuristic. Because the hook is deny-by-default, a
failed parse degrades to the previous (conservative) behaviour, never to a wrong allow.

Public API:
    analyze(cmd) -> Analysis    # .ok, .segments, .leaders, .has_substitution,
                                #   .has_heredoc, .writes_file
"""
from collections import namedtuple

try:
    import bashlex
    _HAVE_BASHLEX = True
except Exception:                       # library missing / import error -> always fall back
    _HAVE_BASHLEX = False


Analysis = namedtuple(
    "Analysis",
    "ok segments leaders has_substitution has_heredoc writes_file",
)

# The empty result handed back whenever bashlex can't parse. ok=False tells the caller
# "trust nothing here; use your fallback".
_FAILED = Analysis(False, [], [], False, False, False)


def _kids(n):
    """Direct child nodes of an AST node, across every attribute bashlex uses to hold
    them. Word substitutions live under `word.parts`; a redirect's target is `output`
    and its body is `heredoc`; a command/process substitution wraps `command`."""
    for attr in ("parts", "list"):
        for c in getattr(n, attr, None) or []:
            yield c
    for attr in ("command", "output", "heredoc"):
        c = getattr(n, attr, None)
        if _is_node(c):
            yield c


def _is_node(x):
    return hasattr(x, "kind")


def _walk(n):
    """Yield every node in the tree (pre-order), substitutions included."""
    yield n
    for c in _kids(n):
        yield from _walk(c)


def _command_nodes(n):
    """Yield every simple-command node, recursing through lists/pipelines/compounds but
    NOT descending into command/process substitutions — a substitution's interior runs
    in its own shell and is handled by `has_substitution` (the whole command defers)."""
    if n.kind in ("commandsubstitution", "processsubstitution"):
        return
    if n.kind == "command":
        yield n
    for attr in ("parts", "list"):
        for c in getattr(n, attr, None) or []:
            yield from _command_nodes(c)


def _basename(word: str) -> str:
    base = word.rsplit("/", 1)[-1].rsplit("\\", 1)[-1].lower()
    if base.endswith((".exe", ".bat", ".cmd")):
        base = base.rsplit(".", 1)[0]
    return base


def _leader(cmd_node, src: str) -> str:
    """Basename of a command's first WORD, skipping leading `VAR=val` assignments so
    `MSYS_NO_PATHCONV=1 robocopy …` -> 'robocopy'. bashlex tags assignments as their own
    node kind, so we just take the first `word` part."""
    for part in getattr(cmd_node, "parts", None) or []:
        if part.kind == "word":
            return _basename(src[part.pos[0]:part.pos[1]])
    return ""


def _is_real_file_write(redir) -> bool:
    """True if a redirect node lands bytes in a real file. Excludes: input redirects
    (`<`, heredocs), fd duplications (`2>&1`, whose target is an int), and the null
    device (`/dev/null`, `nul`)."""
    rtype = getattr(redir, "type", "") or ""
    if ">" not in rtype:
        return False                    # not an output redirect
    if rtype.endswith("&"):
        return False                    # `>&` fd-duplication form
    out = getattr(redir, "output", None)
    if not _is_node(out):
        return False                    # target is an fd number, not a file
    target = (getattr(out, "word", "") or "").lower()
    return target not in ("/dev/null", "nul")


def analyze(cmd: str) -> Analysis:
    """Parse `cmd` with bashlex and summarise the structure the prompt guard cares about.
    Returns Analysis(ok=False, …) if bashlex can't parse — the caller should fall back.

    segments: source text of each simple command (`git push origin master`), for
              per-segment allow-list / intended-prompt checks.
    leaders:  basename of each command's first word (`git`, `robocopy`).
    has_substitution / has_heredoc: genuine `$( )` `` ` ` `` `<( )` / heredoc present.
    writes_file: any redirect writes a real (non-null) file.
    """
    if not _HAVE_BASHLEX or not cmd or not cmd.strip():
        return _FAILED
    try:
        trees = bashlex.parse(cmd)
    except Exception:
        # arithmetic expansion (unimplemented), syntax errors, etc. -> let caller fall back
        return _FAILED

    segments, leaders = [], []
    has_sub = has_heredoc = writes_file = False
    for tree in trees:
        for n in _walk(tree):
            if n.kind in ("commandsubstitution", "processsubstitution"):
                has_sub = True
            elif n.kind == "redirect":
                if getattr(n, "heredoc", None):
                    has_heredoc = True
                if _is_real_file_write(n):
                    writes_file = True
        for cn in _command_nodes(tree):
            seg = cmd[cn.pos[0]:cn.pos[1]].strip()
            if seg:
                segments.append(seg)
            lead = _leader(cn, cmd)
            if lead:
                leaders.append(lead)
    return Analysis(True, segments, leaders, has_sub, has_heredoc, writes_file)
