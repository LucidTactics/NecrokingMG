using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Render;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Visual debugger for the pixel-stride calibration system. Renders a selected
/// unit's Idle, Walk peak-stride, and Run peak-stride frames at large zoom with
/// the bottom-50% scan region tinted and leftmost / rightmost detection lines
/// overlaid. Below, an idle-ghost overlay highlights the pixel diff between
/// Idle pose and Walk peak (the "actual leg motion" the subtraction is trying
/// to capture), and a filmstrip shows all Walk frames with their per-frame
/// measured extents so you can verify the peak frame was picked correctly.
///
/// Pick the unit with <c>--unit &lt;sprite_name&gt;</c>. The arg is the SPRITE
/// name (e.g. "Wolf", "Wretched"), not the unit-def id (which is the lowercase
/// variant). The scenario looks up the def whose Sprite.Name matches.
///
/// Usage:
///   --scenario stride_debug --unit Wolf --timeout 60 --resolution 1600x900
///
/// Default unit is "Wolf" since that's the one currently failing calibration.
/// </summary>
public class StrideDebugScenario : ScenarioBase
{
    public override string Name => "StrideDebug";

    private bool _complete;
    private float _elapsed;
    private UnitDef _def;
    private UnitSpriteData? _spriteData;
    private SpriteAtlas? _atlas;
    private StrideCalibration.UnitCalibration? _cal;

    // Browsable unit list — every UnitDef with a sprite + Walk anim. N/P (or
    // left/right arrow) cycles the selection. Built once in OnInit.
    private List<UnitDef> _browsableUnits = new();
    private int _currentIdx;
    private Simulation? _sim;
    private KeyboardState _prevKb;

    // Per-unit cached scan results. Computed once in SelectUnit so DrawDebug
    // (called every frame) doesn't slam the GPU with Texture2D.GetData readbacks.
    // Without this cache the game froze when launched from the menu — ~30 GPU
    // readbacks per frame meant ~1 FPS, and keyboard polling missed key presses.
    private class DisplayCache
    {
        public Keyframe? IdleKf;
        public Keyframe? WalkPeakKf;
        public Keyframe? RunPeakKf;
        public ScanResult IdleScan;
        public ScanResult WalkScan;
        public ScanResult RunScan;
        public List<int> WalkFrameExtents = new();
        public int WalkFilmPeakIdx;
        public List<Keyframe> WalkKfs = new();
    }
    private DisplayCache? _cache;
    // Headless screenshot mode: when a --unit is provided AND we're headless,
    // just take one screenshot and exit (preserves the original scripted-cli
    // workflow). When launched from the menu (no --unit, not headless), stay
    // open so the user can browse with N/P.
    private bool _autoExitMode;

    public override void OnInit(Simulation sim)
    {
        _sim = sim;
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Stride calibration debug ===");

        BuildBrowsableUnits(sim);
        if (_browsableUnits.Count == 0)
        {
            DebugLog.Log(ScenarioLog, "FAIL: no units with sprite+Walk anim found");
            _complete = true;
            return;
        }

        // Pick initial unit: --unit arg matches either the UnitDef id or the
        // sprite name (case-insensitive). Some unit-defs reuse a sister sprite
        // (e.g. def "Bear" uses sprite "JuvenileBear"), so matching by both is
        // friendlier on the command line. Default to "Bear" when launched from
        // the menu (no --unit) since that's the unit we're currently iterating
        // on; swap this default freely as debugging focus moves.
        string requestedUnit = LaunchArgs.Unit ?? "Bear";
        int initialIdx = 0;
        for (int i = 0; i < _browsableUnits.Count; i++)
        {
            var d = _browsableUnits[i];
            bool match = string.Equals(d.Sprite?.SpriteName, requestedUnit,
                            StringComparison.OrdinalIgnoreCase)
                      || string.Equals(d.Id, requestedUnit,
                            StringComparison.OrdinalIgnoreCase);
            if (match) { initialIdx = i; break; }
        }
        SelectUnit(initialIdx);

        // Auto-exit mode: when --unit is given AND headless, take screenshot + exit.
        _autoExitMode = !string.IsNullOrEmpty(LaunchArgs.Unit) && LaunchArgs.Headless;

        BackgroundColor = new Color(28, 28, 36);
        BloomOverride = new BloomSettings { Enabled = false };
        ZoomOnLocation(0f, 0f, 32f);

        CustomUIDraw = DrawDebug;

        if (_autoExitMode)
            DeferredScreenshot = $"stride_debug_{LaunchArgs.Unit}";

        _prevKb = Keyboard.GetState();
    }

    /// <summary>Build the list of cycleable units: any UnitDef with a Sprite ref
    /// whose SpriteData has a Walk animation. Skips _copy duplicates (same
    /// sprite, just noise in the cycle). Sorted by sprite name for predictable
    /// ordering.</summary>
    private void BuildBrowsableUnits(Simulation sim)
    {
        var data = sim.GameData;
        if (data == null) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in data.Units.All())
        {
            if (d.Sprite == null || string.IsNullOrEmpty(d.Sprite.SpriteName)) continue;
            if (d.Id.Contains("_copy")) continue;  // dedup variants
            if (!seen.Add(d.Sprite.SpriteName)) continue;  // dedup by sprite
            if (d.SpriteData?.GetAnim("Walk") == null) continue;
            _browsableUnits.Add(d);
        }
        _browsableUnits.Sort((a, b) => string.Compare(
            a.Sprite!.SpriteName, b.Sprite!.SpriteName, StringComparison.OrdinalIgnoreCase));
        DebugLog.Log(ScenarioLog, $"Browsable units: {_browsableUnits.Count}");
    }

    private void SelectUnit(int idx)
    {
        if (_browsableUnits.Count == 0) return;
        _currentIdx = ((idx % _browsableUnits.Count) + _browsableUnits.Count) % _browsableUnits.Count;
        _def = _browsableUnits[_currentIdx];
        _spriteData = _def.SpriteData;
        _cal = _spriteData?.Calibration;
        if (_def.Sprite != null && Atlases != null)
        {
            int aIdx = (int)AtlasDefs.ResolveAtlasName(_def.Sprite.AtlasName);
            _atlas = (aIdx >= 0 && aIdx < Atlases.Length) ? Atlases[aIdx] : null;
        }
        DebugLog.Log(ScenarioLog, $"[{_currentIdx + 1}/{_browsableUnits.Count}] {_def.Sprite?.SpriteName} " +
            $"(def={_def.Id}, CS={_def.Stats?.CombatSpeed}, quad={_def.IsQuadruped})");
        BuildDisplayCache();
    }

    /// <summary>Pre-compute all the per-frame scans for the selected unit.
    /// Each MeasureFrameDebugFull call hits Texture2D.GetData (GPU readback,
    /// slow), so doing this once on unit switch instead of every render frame
    /// is what makes the scenario actually run at frame rate.</summary>
    private void BuildDisplayCache()
    {
        var cache = new DisplayCache();
        // Pick the display frame for each gait (same logic as before, just
        // running scans here ONCE instead of inside DrawFrameBox each frame).
        var (idleKf, _) = PickFrameForGaitInner("Idle", IdleStripFraction);
        var (walkKf, _) = PickFrameForGaitInner("Walk", WalkStripFraction);
        var (runKf, _) = PickFrameForGaitInner("Run", JogRunStripFraction);
        cache.IdleKf = idleKf;
        cache.WalkPeakKf = walkKf;
        cache.RunPeakKf = runKf;
        if (idleKf.HasValue) cache.IdleScan = MeasureFrameDebugFull(idleKf.Value.Frame.Rect, IdleStripFraction);
        if (walkKf.HasValue) cache.WalkScan = MeasureFrameDebugFull(walkKf.Value.Frame.Rect, WalkStripFraction);
        if (runKf.HasValue)  cache.RunScan  = MeasureFrameDebugFull(runKf.Value.Frame.Rect,  JogRunStripFraction);
        // Filmstrip: extent for every walk frame so the per-frame numbers line up.
        var walkAnim = _spriteData?.GetAnim("Walk");
        if (walkAnim != null)
        {
            var kfs = PickKeyframesEast(walkAnim);
            if (kfs != null)
            {
                cache.WalkKfs = kfs;
                int peakIdx = 0;
                for (int i = 0; i < kfs.Count; i++)
                {
                    var (l, r, _) = MeasureFrameDebug(kfs[i].Frame.Rect, WalkStripFraction);
                    int ext = (l >= 0 && r > l) ? r - l : 0;
                    cache.WalkFrameExtents.Add(ext);
                    if (ext > cache.WalkFrameExtents[peakIdx]) peakIdx = i;
                }
                cache.WalkFilmPeakIdx = peakIdx;
            }
        }
        _cache = cache;
    }

    /// <summary>Internal version of PickFrameForGait used during cache build.
    /// Same logic as the public one but doesn't go through the cache lookup
    /// (which would be circular).</summary>
    private (Keyframe? kf, int extent) PickFrameForGaitInner(string animName, float stripFraction)
    {
        var anim = _spriteData?.GetAnim(animName);
        if (anim == null) return (null, 0);
        var kfs = PickKeyframesEast(anim);
        if (kfs == null || kfs.Count == 0) return (null, 0);
        int bestIdx = 0, bestExt = -1;
        for (int i = 0; i < kfs.Count; i++)
        {
            var (l, r, _) = MeasureFrameDebug(kfs[i].Frame.Rect, stripFraction);
            int ext = (l >= 0 && r > l) ? r - l : 0;
            if (ext > bestExt) { bestExt = ext; bestIdx = i; }
        }
        return (kfs[bestIdx], bestExt);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_autoExitMode && _elapsed > 3f) { _complete = true; return; }

        // Keyboard navigation: N/Right = next unit, P/Left = previous,
        // ESC = exit. Edge-trigger via _prevKb so a held key doesn't blast through.
        var kb = Keyboard.GetState();
        bool pressed(Keys k) => kb.IsKeyDown(k) && !_prevKb.IsKeyDown(k);
        if (pressed(Keys.N) || pressed(Keys.Right))
        {
            DebugLog.Log(ScenarioLog, "key NEXT pressed");
            SelectUnit(_currentIdx + 1);
        }
        else if (pressed(Keys.P) || pressed(Keys.Left))
        {
            DebugLog.Log(ScenarioLog, "key PREV pressed");
            SelectUnit(_currentIdx - 1);
        }
        else if (pressed(Keys.Escape))
        {
            DebugLog.Log(ScenarioLog, "key ESC pressed");
            _complete = true;
        }
        _prevKb = kb;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "Stride debug complete.");
        return 0;
    }

    // =========================================================================
    //  Layout constants
    // =========================================================================

    // Per-frame display box dimensions in screen pixels. Pick a size that gives
    // ~6-8x zoom on typical sprites (which are 80-130px wide).
    private const int FrameBoxW = 320;
    private const int FrameBoxH = 320;
    private const int FrameGap = 24;
    // Strip fractions match StrideCalibration's actual per-gait scan sizes:
    //   Idle + Walk use 25% (consistent for the subtraction, tight to ground)
    //   Jog + Run use 40% (back paw lifts to knee at peak stride; 50% pulled
    //   in too much body silhouette on quadrupeds)
    private const float WalkStripFraction = 0.25f;
    private const float JogRunStripFraction = 0.40f;
    private const float IdleStripFraction = 0.25f;

    // Colors
    private static readonly Color BgPanel = new Color(20, 20, 28, 255);
    private static readonly Color BorderColor = new Color(80, 80, 110);
    private static readonly Color StripTint = new Color(60, 100, 180, 60);     // bottom 50% scan region
    private static readonly Color LeftmostLine = new Color(255, 80, 80);       // red
    private static readonly Color RightmostLine = new Color(80, 255, 120);     // green
    private static readonly Color TextBright = new Color(230, 230, 240);
    private static readonly Color TextDim = new Color(150, 150, 165);
    private static readonly Color TextWarn = new Color(255, 200, 100);
    private static readonly Color GhostDiff = new Color(80, 220, 255, 200);    // cyan diff overlay

    // =========================================================================
    //  Draw entry
    // =========================================================================

    private void DrawDebug(SpriteBatch batch, int screenW, int screenH)
    {
        if (Font == null || PixelTexture == null) return;
        if (_def == null || _spriteData == null || _atlas == null)
        {
            batch.DrawString(Font, $"Unit not resolved (use --unit <spriteName>)",
                new Vector2(20, 20), TextWarn);
            return;
        }

        // Title with position counter, plus a small hint for keyboard nav.
        string title = $"Stride Debug - {_def.Sprite!.SpriteName} (def: {_def.Id})  " +
                       $"[{_currentIdx + 1}/{_browsableUnits.Count}]";
        batch.DrawString(Font, title, new Vector2(20, 12), TextBright);
        if (!_autoExitMode)
        {
            batch.DrawString(SmallFont ?? Font, "N/P or Left/Right to cycle units, ESC to exit",
                new Vector2(screenW - 380, 16), TextDim);
        }

        // Stats summary (3 lines, line3 is the tall main font with playback)
        DrawStatsSummary(batch, 20, 50, screenW);

        // Three-frame row — pushed down to clear the stats block (which ends
        // around y=120) AND its line-3 "@MS plays" callout that runs full-width.
        int rowY = 150;
        int rowX = 20;
        DrawFrameBox(batch, "Idle", "Idle", isIdleScan: true,
            rowX, rowY, FrameBoxW, FrameBoxH);
        DrawFrameBox(batch, "Walk peak", "Walk", isIdleScan: false,
            rowX + (FrameBoxW + FrameGap), rowY, FrameBoxW, FrameBoxH);
        DrawFrameBox(batch, "Run peak", "Run", isIdleScan: false,
            rowX + (FrameBoxW + FrameGap) * 2, rowY, FrameBoxW, FrameBoxH);

        // Idle-ghost overlay (Idle drawn semi-transparent over Walk peak)
        int ghostY = rowY + FrameBoxH + 30;
        DrawIdleGhostOverlay(batch, rowX, ghostY, FrameBoxW * 2, FrameBoxH);

        // Walk filmstrip (all frames)
        int stripY = ghostY;
        int stripX = rowX + (FrameBoxW * 2) + FrameGap;
        DrawWalkFilmstrip(batch, stripX, stripY, FrameBoxW, FrameBoxH);
    }

    // =========================================================================
    //  Stats summary
    // =========================================================================

    private void DrawStatsSummary(SpriteBatch batch, int x, int y, int screenW)
    {
        if (_def == null || _cal == null) { return; }
        float cs = _def.Stats?.CombatSpeed ?? 0f;
        float duty = _def.DutyCycle > 0f ? _def.DutyCycle : StrideCalibration.DefaultDutyCycle;
        float bodySub = _def.IsQuadruped ? _cal.IdleFootSpreadPx : 0f;
        float pixelWalkVel = StrideCalibration.ResolveAnimVel(_cal.Walk,
            _def.SpriteWorldHeight, _def.SpriteScale, bodySub, duty);
        float playback = pixelWalkVel > 0f ? cs / pixelWalkVel : 0f;
        Color pbColor = (playback < 0.5f || playback > 2.0f) ? TextWarn : TextBright;

        string line1 = $"CombatSpeed: {cs:F2}    sWHxsSc: {_def.SpriteWorldHeight:F2}x{_def.SpriteScale:F2} = {_def.SpriteWorldHeight*_def.SpriteScale:F2} wu    " +
                       $"Quadruped: {(_def.IsQuadruped ? "YES" : "no")}    DutyCycle: {duty:F2}";
        string line2 = $"Walk stride: {_cal.Walk.StridePx:F0}px    Idle spread: {_cal.IdleFootSpreadPx:F0}px    " +
                       $"BodySub: {bodySub:F0}px    Effective stride: {Math.Max(_cal.Walk.StridePx - bodySub, 1f):F0}px    " +
                       $"WalkCycle: {_cal.Walk.CycleSeconds:F2}s";
        string line3 = $"Computed walkVel: {pixelWalkVel:F2} wu/s    @ CombatSpeed plays: {playback:F2}x";

        batch.DrawString(SmallFont ?? Font!, line1, new Vector2(x, y), TextDim);
        batch.DrawString(SmallFont ?? Font!, line2, new Vector2(x, y + 16), TextDim);
        batch.DrawString(Font!, line3, new Vector2(x, y + 34), pbColor);
    }

    // =========================================================================
    //  Single frame display (with bottom-50% strip, leftmost/rightmost markers)
    // =========================================================================

    private void DrawFrameBox(SpriteBatch batch, string label, string animName,
        bool isIdleScan, int x, int y, int w, int h)
    {
        // Label above
        batch.DrawString(SmallFont ?? Font!, label, new Vector2(x + 4, y - 18), TextBright);

        // Panel bg + border
        FillRect(batch, new Rectangle(x, y, w, h), BgPanel);
        DrawBorder(batch, new Rectangle(x, y, w, h), BorderColor, 1);

        float stripFraction = isIdleScan ? IdleStripFraction
            : (animName == "Walk" ? WalkStripFraction : JogRunStripFraction);
        // Pull the pre-computed frame + scan from the per-unit cache instead of
        // running another GPU readback. Cache is populated once per SelectUnit.
        Keyframe? kf = null;
        ScanResult scan = default;
        if (_cache != null)
        {
            if (animName == "Idle") { kf = _cache.IdleKf; scan = _cache.IdleScan; }
            else if (animName == "Walk") { kf = _cache.WalkPeakKf; scan = _cache.WalkScan; }
            else if (animName == "Run") { kf = _cache.RunPeakKf; scan = _cache.RunScan; }
        }
        if (kf == null) {
            batch.DrawString(SmallFont ?? Font!, "(no anim)", new Vector2(x + 8, y + 8), TextDim);
            return;
        }

        var frame = kf.Value.Frame;
        // Scale the sprite to fit the box while preserving aspect ratio.
        float scale = Math.Min((float)(w - 16) / frame.Rect.Width, (float)(h - 16) / frame.Rect.Height);
        int destW = (int)(frame.Rect.Width * scale);
        int destH = (int)(frame.Rect.Height * scale);
        int destX = x + (w - destW) / 2;
        int destY = y + (h - destH) / 2;
        var destRect = new Rectangle(destX, destY, destW, destH);

        var tex = _atlas!.GetTextureForFrame(frame);
        if (tex != null)
            batch.Draw(tex, destRect, frame.Rect, Color.White);
        if (scan.StripTopY >= 0)
        {
            float ratioY = scale;
            int stripScreenTop = destY + (int)((scan.StripTopY - frame.Rect.Y) * ratioY);
            int stripScreenBot = destY + (int)((scan.StripBotY - frame.Rect.Y + 1) * ratioY);
            FillRect(batch, new Rectangle(destX, stripScreenTop, destW, stripScreenBot - stripScreenTop), StripTint);
        }

        int leftPx = scan.Left;
        int rightPx = scan.Right;
        if (leftPx >= 0 && rightPx > leftPx)
        {
            // Map atlas-X to dest-X
            float ratio = scale; // dest pixels per atlas pixel
            int leftRel = (int)((leftPx - frame.Rect.X) * ratio);
            int rightRel = (int)((rightPx - frame.Rect.X) * ratio);
            int leftScreenX = destX + leftRel;
            int rightScreenX = destX + rightRel;
            // Draw vertical lines for leftmost and rightmost.
            DrawVLine(batch, leftScreenX, destY, destY + destH, LeftmostLine, 2);
            DrawVLine(batch, rightScreenX, destY, destY + destH, RightmostLine, 2);

            int widthPx = rightPx - leftPx;
            batch.DrawString(SmallFont ?? Font!,
                $"L={leftPx-frame.Rect.X} R={rightPx-frame.Rect.X} W={widthPx}px (strip {stripFraction*100:F0}%)",
                new Vector2(x + 4, y + h - 16), TextBright);
        }
    }

    // =========================================================================
    //  Idle-ghost overlay: Idle drawn semi-transparent over Walk peak, with
    //  pixel diff highlighted cyan.
    // =========================================================================

    private void DrawIdleGhostOverlay(SpriteBatch batch, int x, int y, int w, int h)
    {
        batch.DrawString(SmallFont ?? Font!,
            "Idle-ghost overlay (cyan = pixels present in Walk but NOT Idle = actual leg motion)",
            new Vector2(x + 4, y - 18), TextBright);

        FillRect(batch, new Rectangle(x, y, w, h), BgPanel);
        DrawBorder(batch, new Rectangle(x, y, w, h), BorderColor, 1);

        Keyframe? idleKf = _cache?.IdleKf;
        Keyframe? walkKf = _cache?.WalkPeakKf;
        if (idleKf == null || walkKf == null) return;

        var idleFrame = idleKf.Value.Frame;
        var walkFrame = walkKf.Value.Frame;

        // Center both frames in the overlay box, aligned at their bottom centers
        // (since pivots are usually at feet). Use the walk-peak frame's dimensions
        // as the layout reference; the idle frame draws at its own size on top.
        float scale = Math.Min((float)(w - 16) / walkFrame.Rect.Width, (float)(h - 16) / walkFrame.Rect.Height);
        int destW = (int)(walkFrame.Rect.Width * scale);
        int destH = (int)(walkFrame.Rect.Height * scale);
        int destX = x + (w - destW) / 2;
        int destY = y + (h - destH) / 2;

        var tex = _atlas!.GetTextureForFrame(walkFrame);
        if (tex == null) return;

        // 1) Draw Walk peak at full color.
        batch.Draw(tex, new Rectangle(destX, destY, destW, destH), walkFrame.Rect, Color.White);

        // 2) Overlay Idle frame on top, scaled to match. Tinted at low alpha so
        //    its silhouette is faintly visible over the Walk frame.
        int idleW = (int)(idleFrame.Rect.Width * scale);
        int idleH = (int)(idleFrame.Rect.Height * scale);
        int idleX = x + (w - idleW) / 2;  // center horizontally
        int idleY = y + h - (int)(8 * scale) - idleH;  // bottom-align approximately
        batch.Draw(tex, new Rectangle(idleX, idleY, idleW, idleH), idleFrame.Rect,
            new Color(255, 255, 255, 110));

        // 3) Draw vertical lines for Walk's leftmost/rightmost and Idle's
        //    leftmost/rightmost so the user can see both measurements directly.
        int idleL = _cache?.IdleScan.Left ?? -1;
        int idleR = _cache?.IdleScan.Right ?? -1;
        int walkL = _cache?.WalkScan.Left ?? -1;
        int walkR = _cache?.WalkScan.Right ?? -1;
        if (walkL >= 0)
        {
            float ratio = scale;
            int walkLX = destX + (int)((walkL - walkFrame.Rect.X) * ratio);
            int walkRX = destX + (int)((walkR - walkFrame.Rect.X) * ratio);
            DrawVLine(batch, walkLX, destY, destY + destH, LeftmostLine, 2);
            DrawVLine(batch, walkRX, destY, destY + destH, RightmostLine, 2);
        }
        if (idleL >= 0)
        {
            int idleLX = idleX + (int)((idleL - idleFrame.Rect.X) * scale);
            int idleRX = idleX + (int)((idleR - idleFrame.Rect.X) * scale);
            DrawVLine(batch, idleLX, idleY, idleY + idleH, new Color(255, 180, 80), 1);
            DrawVLine(batch, idleRX, idleY, idleY + idleH, new Color(255, 180, 80), 1);
        }

        // Caption with measured numbers.
        int walkWPx = (walkR > walkL) ? walkR - walkL : 0;
        int idleWPx = (idleR > idleL) ? idleR - idleL : 0;
        int diff = walkWPx - idleWPx;
        batch.DrawString(SmallFont ?? Font!,
            $"Walk extent {walkWPx}px    Idle extent {idleWPx}px    Diff = {diff}px",
            new Vector2(x + 4, y + h - 16), TextBright);
    }

    // =========================================================================
    //  Walk filmstrip: all walk frames with per-frame extent labels, peak highlighted
    // =========================================================================

    private void DrawWalkFilmstrip(SpriteBatch batch, int x, int y, int w, int h)
    {
        batch.DrawString(SmallFont ?? Font!,
            "Walk filmstrip (all frames; peak in green border)",
            new Vector2(x + 4, y - 18), TextBright);

        FillRect(batch, new Rectangle(x, y, w, h), BgPanel);
        DrawBorder(batch, new Rectangle(x, y, w, h), BorderColor, 1);

        // Use the cached per-frame extents + peak index instead of re-scanning
        // the GPU every frame. Cache populated by BuildDisplayCache on unit
        // switch.
        if (_cache == null || _cache.WalkKfs.Count == 0) return;
        var kfs = _cache.WalkKfs;
        var extents = _cache.WalkFrameExtents.ToArray();
        int peakIdx = _cache.WalkFilmPeakIdx;

        // Lay out frames horizontally in the strip
        int n = kfs.Count;
        int gap = 4;
        int cellW = (w - gap * (n + 1)) / n;
        int cellH = h - 30;
        for (int i = 0; i < n; i++)
        {
            int cx = x + gap + i * (cellW + gap);
            int cy = y + 8;
            var cellRect = new Rectangle(cx, cy, cellW, cellH);
            DrawBorder(batch, cellRect, i == peakIdx ? RightmostLine : BorderColor,
                       i == peakIdx ? 2 : 1);

            var frame = kfs[i].Frame;
            float scale = Math.Min((float)(cellW - 4) / frame.Rect.Width, (float)(cellH - 4) / frame.Rect.Height);
            int destW = (int)(frame.Rect.Width * scale);
            int destH = (int)(frame.Rect.Height * scale);
            int destX = cx + (cellW - destW) / 2;
            int destY = cy + (cellH - destH) / 2;
            var tex = _atlas!.GetTextureForFrame(frame);
            if (tex != null)
                batch.Draw(tex, new Rectangle(destX, destY, destW, destH), frame.Rect, Color.White);

            // Label: frame index and measured extent
            batch.DrawString(SmallFont ?? Font!, $"{extents[i]}",
                new Vector2(cx + cellW / 2 - 8, y + h - 14), i == peakIdx ? RightmostLine : TextDim);
        }
    }

    // =========================================================================
    //  Helpers
    // =========================================================================

    /// <summary>For Idle: pick any keyframe (we measure the max across all
    private List<Keyframe>? PickKeyframesEast(AnimationData anim)
    {
        // Try east (yaw=0) first; fall back to any authored angle.
        if (anim.AngleFrames.TryGetValue(0, out var east)) return east;
        foreach (var (_, list) in anim.AngleFrames)
            if (list.Count > 0) return list;
        return null;
    }

    /// <summary>Re-implements the bottom-50% leftmost/rightmost scan from
    /// StrideCalibration.MeasureFrameFootSpread, but reads from the atlas
    /// texture's GPU side via GetData. Returns atlas-coordinate leftmost
    /// and rightmost X. Slow (GPU readback) but only used in this debug
    /// scenario.</summary>
    /// <summary>Result of a content-extent-aware scan: leftmost/rightmost X in
    /// atlas coords, plus the strip's vertical bounds (first scanned row to
    /// last scanned row, inclusive, in atlas coords). Strip rows let
    /// DrawFrameBox tint the actual scanned region in the visualization rather
    /// than a "bottom N% of bounding rect" approximation.</summary>
    private record struct ScanResult(int Left, int Right, int StripTopY, int StripBotY);

    private (int left, int right, int rowsScanned) MeasureFrameDebug(Rectangle rect,
        float stripFraction)
    {
        var scan = MeasureFrameDebugFull(rect, stripFraction);
        return (scan.Left, scan.Right, scan.StripBotY - scan.StripTopY + 1);
    }

    private ScanResult MeasureFrameDebugFull(Rectangle rect, float stripFraction)
    {
        if (_atlas == null) return new ScanResult(-1, -1, -1, -1);
        var tex = _atlas.Textures.Count > 0 ? _atlas.Textures[0] : null;
        if (tex == null) return new ScanResult(-1, -1, -1, -1);

        int atlasW = tex.Width, atlasH = tex.Height;
        int xStart = Math.Max(0, rect.X);
        int xEnd = Math.Min(atlasW, rect.X + rect.Width);
        int yStart = Math.Max(0, rect.Y);
        int yEnd = Math.Min(atlasH, rect.Y + rect.Height);
        int stripW = xEnd - xStart;
        int rectH = yEnd - yStart;
        if (stripW <= 0 || rectH <= 0) return new ScanResult(-1, -1, -1, -1);

        var pixels = new Color[stripW * rectH];
        tex.GetData(0, new Rectangle(xStart, yStart, stripW, rectH), pixels, 0, pixels.Length);

        // Find content vertical extent (topmost / bottommost rows with any
        // non-transparent pixel). Matches the production calibration code.
        int contentTop = -1, contentBot = -1;
        for (int y = 0; y < rectH; y++)
        {
            int rowOffset = y * stripW;
            bool hasPixel = false;
            for (int x = 0; x < stripW; x++)
            {
                if (pixels[rowOffset + x].A > 0) { hasPixel = true; break; }
            }
            if (hasPixel)
            {
                if (contentTop < 0) contentTop = y;
                contentBot = y;
            }
        }
        if (contentTop < 0) return new ScanResult(-1, -1, -1, -1);

        int contentH = contentBot - contentTop + 1;
        int rowsToScan = Math.Max(1, (int)(contentH * stripFraction));
        int firstRow = contentBot - rowsToScan + 1;
        if (firstRow < contentTop) firstRow = contentTop;

        int leftmost = int.MaxValue, rightmost = int.MinValue;
        for (int y = firstRow; y <= contentBot; y++)
        {
            int rowOffset = y * stripW;
            for (int x = 0; x < stripW; x++)
            {
                if (pixels[rowOffset + x].A > 0)
                {
                    if (x < leftmost) leftmost = x;
                    if (x > rightmost) rightmost = x;
                }
            }
        }
        // Convert local coords back to atlas coords.
        int stripTopAtlas = yStart + firstRow;
        int stripBotAtlas = yStart + contentBot;
        if (leftmost == int.MaxValue) return new ScanResult(-1, -1, stripTopAtlas, stripBotAtlas);
        return new ScanResult(xStart + leftmost, xStart + rightmost, stripTopAtlas, stripBotAtlas);
    }

    // Primitive helpers
    private void FillRect(SpriteBatch batch, Rectangle r, Color c)
        => batch.Draw(PixelTexture, r, c);

    private void DrawBorder(SpriteBatch batch, Rectangle r, Color c, int thickness)
    {
        Necroking.Render.DrawUtils.DrawRectBorder(batch, PixelTexture, r, c, thickness);
    }

    private void DrawVLine(SpriteBatch batch, int x, int yTop, int yBot, Color c, int thickness)
        => FillRect(batch, new Rectangle(x - thickness / 2, yTop, thickness, yBot - yTop), c);
}
