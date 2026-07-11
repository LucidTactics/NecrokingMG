# Known Platform Bugs

Framework/OS-level bugs we've hit, why they happen, and the workaround this repo
uses. Check here before "fixing" a symptom that smells like the engine lying to us —
the fix may already exist, or the naive fix may be known not to work.

## MonoGame/SDL `Game.IsActive` starts `true` and is event-driven — stale focus, click-through

**Symptom.** Clicks made in *other* applications land on the game's UI (menu buttons
get clicked in a window you never looked at). Happens when the game is launched while
another app holds focus — e.g. started by a supervisor/script while you keep working
elsewhere — and persists for *every* click until the game window is focused and
unfocused once.

**Root cause.** MonoGame's DesktopGL platform (SdlGamePlatform) initialises
`IsActive = true` and afterwards only *reacts to SDL events*:

```csharp
case Sdl.Window.EventId.FocusGained:
    this.IsActive = true;
    continue;
case Sdl.Window.EventId.FocusLost:
    this.IsActive = false;
    continue;
```

It never *polls* the OS. If the game starts without focus, focus was never on the
window, so no `FocusLost` ever arrives — `IsActive` stays `true` indefinitely while
the game is actually in the background. Meanwhile `Mouse.GetState()` reports button
state globally (not just clicks on our window), so every focus-gate built on
`IsActive` waves background clicks straight through to the UI.

**Why the naive fixes don't work.**
- *"Freeze input while `!IsActive`"* (the old gate): never engages, because
  `IsActive` is stuck `true`.
- *"Swallow clicks on the `IsActive` false→true edge"*: there is no edge — the flag
  was born `true`. And even spam-clicks between releases pass, since each new click
  arrives with the flag still `true`.

**Workaround (implemented).** Don't trust the flag — ask the OS every frame.
`Core.WindowChrome.IsForegroundWindow()` P/Invokes `GetForegroundWindow()` +
`GetWindowThreadProcessId()` and reports whether the foreground window belongs to
this process (`null` off Windows → fall back to `IsActive`). The single input funnel
in `Game1.Update` computes `bool windowFocused` from it and uses that everywhere it
previously used `IsActive` (the `unfocused` freeze/neutralise gate,
`userInteractingWithWindow`).

One companion piece in the same gate block: **scenario/headless exemption** —
automated runs (`LaunchArgs.Headless`, `LaunchArgs.Scenario`, `_activeScenario`)
never hold OS foreground, so they are exempt from the gate exactly as before;
otherwise the test harness would freeze.

**Accepted edge:** the click that lands *on the game window* to focus it may still
register as a game click (the OS grants foreground before that mouse-down is first
polled, so the first focused frame can see a fresh press edge). That's deliberate —
the click was aimed at the game — and matches common game behaviour. If it ever
becomes annoying, the known remedy is a swallow-until-release latch on the
`windowFocused` false→true transition, feeding the buttons-stripped `MouseState`
(the `cursorOutside` pattern) into `_input.Capture` until both buttons release.

**Where the code lives.** `Necroking/Core/WindowChrome.cs`
(`IsForegroundWindow()`), `Necroking/Game1.cs` `Update()` focus-gate block (search
`windowFocused`).

**If it regresses / porting note.** Off Windows the poll returns `null` and the game
falls back to the buggy `IsActive`, so Linux/macOS builds would re-inherit the bug —
add an equivalent foreground poll per platform (or fix upstream: MonoGame could
initialise `IsActive` from `SDL_GetKeyboardFocus()`).
