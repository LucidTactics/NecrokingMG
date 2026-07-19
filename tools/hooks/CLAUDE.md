## Bash
A `PreToolUse` / `Bash` hook (`tools/hooks/bash_prompt_guard.py`) governs Bash, so the old
"avoid `&&`-chained commands, they force confirmations" worry is gone: the hook
**force-allows** a compound command when *every* segment is individually allow-listed (it
splits on `&&`/`||`/`;`/`|`/newlines), so `cd x && git status && dotnet build` passes
silently. Three things to know:

- **Read-only commands just run.** A command that can't change state — no file-writing
  utility, no `>`/`>>` redirection, no process/power-control command, no `$()`/heredoc — is
  auto-allowed even if it isn't on the allow-list. The "can it write/mutate" catalogue lives
  in [`tools/hooks/file_write_detect.py`](tools/hooks/file_write_detect.py) (≈365 file-writing
  command names + process/power control); it's the inverse of the allow-list, so a *miss*
  there = a command wrongly waved through — **err toward adding** when you touch it. Plain
  `grep`/`cat`/`head`/`tail`/`sort`/`wc`/`sed` now pass straight through; `find` and `sed` are
  read-only until a mutating form (`find -delete`/`-exec`; `sed -i`/`-f`/a `w`-ing script)
  makes them **prompt**. Provably read-only PowerShell one-liners are force-allowed
  (`powershell -c "Get-Process …"`, `Start-Sleep`, whitelisted `Get-*`/pipe cmdlets only),
  and Windows admin CLIs (`wmic`/`reg`/`sc`/`schtasks`/`net`/`netsh`) run in query form but
  prompt on mutating verbs (`reg add`, `sc stop`, `wmic … delete`).
- **Deny-by-default for the rest (aggressive by design).** Any Bash command that *can* mutate
  and isn't allow-listed is bounced back; allow-listed commands pass silently. Sensitive forms
  of allow-listed commands still prompt (`git push`, `find … -delete`). When the gate gets in
  the way, the fix is an `allow` rule, a `rule_intended_prompt` branch, or a catalogue entry —
  don't be shy. Full detail: [docs/avoid-prompting-user.md](docs/avoid-prompting-user.md).
- **Still prefer the dedicated tools for search.** `Grep`/`Glob`/`Read` return clickable links
  and are faster than `grep`/`find`/`cat` via Bash — use them even though the shell forms now
  pass the hook.