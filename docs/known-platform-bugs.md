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

## MonoGame DX `Blend.InverseBlendFactor` corrupts the destination at small factors

**Symptom.** A SpriteBatch draw using a custom `BlendState` with
`ColorSourceBlend = Blend.BlendFactor, ColorDestinationBlend = Blend.InverseBlendFactor`
(the classic "lerp toward src by factor f" shape) destroys the destination render
target when `f` is small: with f ≈ 0.01 the result should be ≈ 99% of the existing
dst content, but the dst comes out gutted/near-empty. At larger f (≈ 0.5) the output
looks plausible, which makes the bug read as a *threshold* artifact in whatever
system uses it (for us: bloom collapsing in a narrow zoom band just past each mip
octave — beams thick at zoom 64.0, thin at 64.4, thick again at 90).

**How it was isolated.** Paused scene, identical geometry, screenshot ladder: the
only code-path difference between the good and bad frames was whether the
InverseBlendFactor pass ran. Bisected 2026-07-15 during the zoom-bloom work.

**Workaround (the rule this repo uses).** Don't use `InverseBlendFactor` at all.
Any fractional mix is built from the proven weighted-additive shape only —
`src * BlendFactor + dst * One` — accumulating into a cleared scratch RT when true
lerp semantics are needed (see `BloomRenderer.GetWeightBlend` / `_haloScratch` in
`Necroking/Render/Bloom.cs`). The scatter chain has used the additive shape for
months without issue; `BlendFactor` as a *source* factor is fine.

**If it regresses / porting note.** Unverified whether the fault is MonoGame's
DX blend-state mapping or driver-level; not reproduced in isolation. If a future
MonoGame update fixes it, the scratch-RT construction is still fine to keep — it's
equivalent math with one extra half-res pass.
