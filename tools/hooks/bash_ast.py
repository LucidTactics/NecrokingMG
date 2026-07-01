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
    analyze(cmd) -> Analysis    # .ok, .segments, .commands, .leaders,
                                #   .has_substitution, .has_heredoc, .writes_file

The workhorse the prompt guard leans on is `.commands`: one Command(leader, args) per
simple-command node, where `leader` is the basename of the program actually invoked and
`args` are its OWN dequoted argument words. That's what makes "does this invocation
mutate?" precise — a write-named token sitting in another command's ARGUMENTS (`echo rm`,
`grep -n kill .`, `find . '-delete'` where -delete is the search root) is no longer
mistaken for the command itself, and a find's `-delete`/`-exec` is checked against that
find's args, not the whole command string.
"""
from collections import namedtuple

try:
    import bashlex
    _HAVE_BASHLEX = True
except Exception:                       # library missing / import error -> always fall back
    _HAVE_BASHLEX = False


# One parsed simple-command invocation. `leader` is the basename of the first word
# (path/.exe-stripped, lowercased; '' for an assignment-only command like `FOO=bar`).
# `args` are the remaining words with shell quoting already removed by bashlex, so
# `find . '-delete'` yields args ['.', '-delete'] — a quoted flag still reads as a flag,
# exactly as the shell would treat it.
Command = namedtuple("Command", "leader args")

Analysis = namedtuple(
    "Analysis",
    "ok segments commands leaders has_substitution has_heredoc writes_file",
)

# The empty result handed back whenever bashlex can't parse. ok=False tells the caller
# "trust nothing here; use your fallback".
_FAILED = Analysis(False, [], [], [], False, False, False)


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


def _words(cmd_node):
    """Dequoted word values of a command node, in order — the leading `VAR=val`
    assignments are skipped automatically because bashlex tags them as their own node
    kind (not `word`). bashlex's `.word` has already stripped shell quoting, so
    `find . '-delete'` -> ['find', '.', '-delete'] and a quoted flag still reads as a
    flag. Redirect targets are separate `redirect` nodes, so they never leak in here."""
    return [p.word for p in (getattr(cmd_node, "parts", None) or []) if p.kind == "word"]


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
    commands: a Command(leader, args) per simple command, index-aligned with `segments`
              (segments[i] is the source text of commands[i]). This is the structured view
              callers should prefer — it says WHICH program each invocation runs and with
              WHICH args, so mutation checks don't fire on write-named argument tokens.
    leaders:  the non-empty leaders of `commands`, kept as a flat convenience list.
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

    segments, commands, leaders = [], [], []
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
            if not seg:
                continue
            words = _words(cn)
            leader = _basename(words[0]) if words else ""
            # Append segment + Command together so the two lists stay index-aligned.
            segments.append(seg)
            commands.append(Command(leader, words[1:]))
            if leader:
                leaders.append(leader)
    return Analysis(True, segments, commands, leaders, has_sub, has_heredoc, writes_file)
