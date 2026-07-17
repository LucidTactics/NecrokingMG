using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Core;
using Necroking.Render;
using Necroking.Movement;
using Necroking.Game;
using Necroking.GameSystems;
using Necroking.World;
using Necroking.Scenario;
using Necroking.Editor;
using Necroking.Lib;
using Necroking.UI;

namespace Necroking;

// Game1 partial: Draw orchestrator entry point. The frame itself is data — an
// ordered list of phases/passes built in GameRenderer.Pipeline.cs. This file
// keeps only the per-frame entry (menu early-outs, camera snap, pipeline
// execute, present bookkeeping) and the bodies of a few large overlay passes.
partial class GameRenderer
{
    public void Draw(GameTime gameTime)
    {
        _g._drawStopwatch.Restart();
        int screenW = _g.GraphicsDevice.Viewport.Width;
        int screenH = _g.GraphicsDevice.Viewport.Height;

        // Hover-highlight + dev-mark boxes are recaptured each frame during the sprite pass.
        _g._hoverBoxObject = _g._hoverBoxCorpse = _g._hoverBoxUnit = null;
        _g._devMarkBoxes.Clear();

        // --- Boot loading screen (before the main menu, while Game1.Loading.cs
        // drains the startup step queue). MarkLoadingFrameDrawn tells the loader
        // its current label has been presented, unblocking the next step. ---
        if (_g._menuState == MenuState.Loading)
        {
            _g.GraphicsDevice.Clear(new Color(20, 15, 30));
            Materials.Hud.Begin(_g._spriteBatch);
            _g._loadingScreen.Draw(screenW, screenH);
            _g._spriteBatch.End();
            _g.MarkLoadingFrameDrawn();
            _g.CompletePendingDevScreenshot();
            _g.BaseDraw(gameTime);
            return;
        }

        // --- Full-screen menus (each screen class owns its drawing — UI/*Screen.cs) ---
        if (_g._menuState == MenuState.MainMenu)
        {
            _g.GraphicsDevice.Clear(new Color(20, 15, 30));
            Materials.Hud.Begin(_g._spriteBatch);
            _g._mainMenu.Draw(screenW, screenH);
            _g._spriteBatch.End();
            _g.CompletePendingDevScreenshot();
            _g.BaseDraw(gameTime);
            return;
        }

        if (_g._menuState == MenuState.ScenarioList)
        {
            _g.GraphicsDevice.Clear(new Color(20, 15, 30));
            Materials.Hud.Begin(_g._spriteBatch);
            _g._scenarioList.Draw(screenW, screenH);
            _g._spriteBatch.End();
            _g.CompletePendingDevScreenshot();
            _g.BaseDraw(gameTime);
            return;
        }

        // Snap camera to pixel grid to prevent subpixel shimmer on ground/sprites
        // X pixel size = 1/Zoom, Y pixel size = 1/(Zoom*YRatio) due to isometric compression.
        // The Hud phase restores the real (smooth) position for input/HUD.
        _realCameraPos = _g._camera.Position;
        float pixelSizeX = 1f / _g._camera.Zoom;
        float pixelSizeY = 1f / (_g._camera.Zoom * _g._camera.YRatio);
        _g._camera.Position = new Vec2(
            MathF.Round(_realCameraPos.X / pixelSizeX) * pixelSizeX,
            MathF.Round(_realCameraPos.Y / pixelSizeY) * pixelSizeY);

        // --- Execute the frame (phases/passes built in GameRenderer.Pipeline.cs) ---
        // With no world loaded the world phases skip themselves (RenderPhase.When),
        // taking the Scene phase's clear with them — clear here so the Hud phase
        // (settings over the main menu) draws onto a clean backbuffer.
        if (!_g._gameWorldLoaded)
            _g.GraphicsDevice.Clear(new Color(20, 15, 30));
        _pipeline ??= BuildPipeline();
        _ctx.Device = _g.GraphicsDevice;
        _ctx.Batch = _g._spriteBatch;
        _ctx.GameTime = gameTime;
        _ctx.ScreenW = screenW;
        _ctx.ScreenH = screenH;
        _pipeline.Execute(_ctx);

        _g._drawStopwatch.Stop();
        // EMA so the HUD doesn't jitter frame-to-frame.
        const double EmaAlpha = 0.1;
        double drawMs = _g._drawStopwatch.Elapsed.TotalMilliseconds;
        _g._drawMsAvg = _g._drawMsAvg * (1.0 - EmaAlpha) + drawMs * EmaAlpha;

        // Feed perf scenarios raw per-frame samples (EMA hides bench detail).
        if (_g._activeScenario is Scenario.Scenarios.PerfWaterScenario perf)
        {
            perf.LastDrawMs = drawMs;
            perf.LastFrameMs = _g._rawDt * 1000.0;
        }

        // Handle deferred screenshots from scenarios BEFORE the present so
        // GetBackBufferData reads the just-rendered frame. (This used to
        // sit after `return;` below, which made it dead code and silently
        // disabled the screenshot path for scenarios that didn't directly
        // call TakeScreenshot.)
        if (_g._activeScenario?.DeferredScreenshot != null)
        {
            ScenarioScreenshot.TakeScreenshot(_g.GraphicsDevice, _g._activeScenario.DeferredScreenshot);
            _g._activeScenario.DeferredScreenshot = null;
        }

        // Dev-server screenshot (in-game path; menu paths call this too before
        // their own Present).
        _g.CompletePendingDevScreenshot();

        // base.Draw only draws components — the actual GPU swap/Present happens in
        // Game1.EndDraw (overridden there), which is where present time is measured.
        _g.BaseDraw(gameTime);

        // Feed the `perf` dev command's ring buffers. Frame = wall-clock interval
        // between Draw ends, so it captures the whole loop (updates + draw + present).
        // The present slot for this frame is stamped afterwards by Game1.EndDraw.
        double perfFrameMs = _g._perfFrameSw.IsRunning ? _g._perfFrameSw.Elapsed.TotalMilliseconds : 0;
        _g._perfFrameSw.Restart();
        int perfIdx = (int)(_g._perfFrames % _g._perfFrameMs.Length);
        _g._perfFrameMs[perfIdx] = perfFrameMs;
        _g._perfSimMs[perfIdx] = _g._sim?.LastTickMs ?? 0;
        _g._perfDrawMs[perfIdx] = drawMs;
        _g._perfPresentMs[perfIdx] = 0; // stamped in Game1.EndDraw
        _g._perfFrames++;
    }

    /// <summary>Death-fog debug overlay (F5) — fog cells plus per-corruptable-object
    /// stress labels. Body moved verbatim out of Draw() (step 0); owns its batch.</summary>
    private void DrawDeathFogDebugOverlay()
    {
        Materials.Hud.Begin(_g._spriteBatch);
        _g._deathFog.DrawDebug(_g._spriteBatch, _g._pixel, _g._renderer, _g._camera);

        // Per-corruptable-object stress label: "stress/threshold" when stress > 0,
        // or "DEAD" once corrupted. Skip clean trees to reduce overlay clutter.
        float threshold = _g._deathFog.CorruptionThreshold;
        int corrW = _g.GraphicsDevice.Viewport.Width;
        int corrH = _g.GraphicsDevice.Viewport.Height;
        for (int oi = 0; oi < _g._envSystem.ObjectCount; oi++)
        {
            var obj = _g._envSystem.GetObject(oi);
            var def = _g._envSystem.GetDef(obj.DefIndex);
            if (!def.IsCorruptable) continue;
            var rt = _g._envSystem.GetObjectRuntime(oi);
            if (!rt.Alive) continue;
            bool dying = rt.CorruptionTime > 0f && !rt.Corrupted;
            if (!rt.Corrupted && !dying && rt.CorruptionStress <= 0.01f) continue;

            var sp = _g._renderer.WorldToScreen(new Vec2(obj.X, obj.Y), 0f, _g._camera);
            if (sp.X < -50 || sp.X > corrW + 50 || sp.Y < -50 || sp.Y > corrH + 50) continue;

            string label;
            Color labelColor;
            if (rt.Corrupted)
            {
                label = "DEAD";
                labelColor = new Color(255, 100, 100);
            }
            else if (dying)
            {
                float dur = _g._deathFog.CorruptionTransitionDuration;
                label = $"DYING {rt.CorruptionTime:F1}/{dur:F0}s";
                labelColor = new Color(255, 160, 80);
            }
            else
            {
                label = $"{rt.CorruptionStress:F1}/{threshold:F0}";
                labelColor = new Color(255, 220, 120);
            }

            if (_g._smallFont != null)
            {
                var size = _g._smallFont.MeasureString(label);
                var pos = new Vector2((int)(sp.X - size.X * 0.5f), (int)(sp.Y - 16));
                _g.Scope.Draw(_g._pixel,
                    new Rectangle((int)pos.X - 2, (int)pos.Y - 1, (int)size.X + 4, (int)size.Y + 2),
                    new Color(0, 0, 0, 180));
                _g.Scope.DrawString(_g._smallFont, label, pos, labelColor);
            }
        }

        _g._spriteBatch.End();
    }

    /// <summary>Alt-key name labels: env objects (+ animation debug), corpse names,
    /// unit names. Body moved verbatim out of Draw() (step 0); owns its batch.</summary>
    private void DrawAltNameLabels(int screenW, int screenH)
    {
        Materials.Hud.Begin(_g._spriteBatch);

        // Draws a centered name label at a world position, boxed for legibility.
        // Returns silently if the position is off-screen or the label is empty.
        void DrawWorldLabel(Vec2 world, string label, Color color)
        {
            if (string.IsNullOrEmpty(label)) return;
            var sp = _g._renderer.WorldToScreen(world, 0f, _g._camera);
            if (sp.X <= -100 || sp.X >= screenW + 100 || sp.Y <= -100 || sp.Y >= screenH + 100) return;
            var textSize = _g._smallFont != null ? _g._smallFont.MeasureString(label) : new Vector2(label.Length * 6, 12);
            var textPos = new Vector2((int)(sp.X - textSize.X * 0.5f), (int)(sp.Y + 4));
            _g.Scope.Draw(_g._pixel, new Rectangle((int)textPos.X - 2, (int)textPos.Y - 1, (int)textSize.X + 4, (int)textSize.Y + 2), new Color(0, 0, 0, 160));
            if (_g._smallFont != null)
                _g.Scope.DrawString(_g._smallFont, label, textPos, color);
        }

        for (int oi = 0; oi < _g._envSystem.ObjectCount; oi++)
        {
            if (!_g._envSystem.IsObjectVisible(oi)) continue;
            var obj = _g._envSystem.GetObject(oi);
            var def = _g._envSystem.GetDef(obj.DefIndex);
            string label = !string.IsNullOrEmpty(def.Name) ? def.Name : def.Id;
            // Animated objects: append frame info after the name
            if (def.IsAnimated && def.AnimTotalFrames > 1)
            {
                var rt = _g._envSystem.GetObjectRuntime(oi);
                int frame = Math.Clamp((int)rt.AnimTime, 0, def.AnimTotalFrames - 1);
                string dir = rt.AnimReversed ? "<" : ">";
                label = $"{label}  F{frame}/{def.AnimTotalFrames - 1} {dir}";
            }
            DrawWorldLabel(new Vec2(obj.X, obj.Y), label, new Color(220, 220, 255));
        }

        // Corpse names — what's lying around to reanimate.
        var corpses = _g._sim.Corpses;
        for (int ci = 0; ci < corpses.Count; ci++)
        {
            var cp = corpses[ci];
            if (cp.ConsumedBySummon) continue;
            var cdef = !string.IsNullOrEmpty(cp.UnitDefID) ? _g._gameData.Units.Get(cp.UnitDefID) : null;
            string cname = cdef != null && cdef.DisplayName.Length > 0 ? cdef.DisplayName : cp.UnitType.ToString();
            DrawWorldLabel(cp.Position, $"{cname} corpse", new Color(255, 220, 200));
        }

        // Unit names — living units on the field.
        for (int ui = 0; ui < _g._sim.Units.Count; ui++)
        {
            var u = _g._sim.Units[ui];
            if (!u.Alive) continue;
            var udef = !string.IsNullOrEmpty(u.UnitDefID) ? _g._gameData.Units.Get(u.UnitDefID) : null;
            string uname = udef != null && udef.DisplayName.Length > 0 ? udef.DisplayName
                         : !string.IsNullOrEmpty(u.UnitDefID) ? u.UnitDefID : u.Type.ToString();
            DrawWorldLabel(u.Position, uname, new Color(200, 255, 210));
        }

        _g._spriteBatch.End();
    }

    /// <summary>The HUD/UI block. Runs inside the Hud phase's open batch
    /// (EffectBatch.BeginHudPass state). Everything layered — HUD chrome,
    /// panels, overlays, button rows, toasts, menus, editors — draws through
    /// <see cref="Necroking.UI.UIRouter.Draw"/>: ONE bottom-up walk of the same
    /// z-ordered list input walks top-down, so draw order and click order can
    /// never disagree. Only the non-layered bits (hover highlights under the
    /// HUD, scenario hook, color picker, EndDrawFrame, debug readouts) remain
    /// inline here.</summary>
    private void DrawHudBlock(int screenW, int screenH, GameTime gameTime)
    {
        bool showUI = _g.ShowUIForDraw;
        if (showUI)
            DrawHoverHighlights();

        _g._uiRouter.Draw(new Necroking.UI.UICtx(screenW, screenH, _g._clock.VisualTime, gameTime));

        // Scenario custom UI hook — for shader-test scenarios that draw raw
        // geometry without a real panel.
        if (_g._activeScenario?.CustomUIDraw != null)
            _g._activeScenario.CustomUIDraw(_g._spriteBatch, screenW, screenH);

        // Draw color picker popup overlay (must be after all editor drawing, on top)
        if (_g._menuState == MenuState.UnitEditor || _g._menuState == MenuState.SpellEditor)
            _g._editorUi.DrawColorPickerPopup();

        // Per-editor end-of-draw housekeeping (dropdown reconcile) — gated like the
        // editors themselves.
        if (_g._menuState == MenuState.Settings || _g._menuState == MenuState.UnitEditor
            || _g._menuState == MenuState.SpellEditor || _g._menuState == MenuState.MapEditor
            || _g._menuState == MenuState.ItemEditor || _g._menuState == MenuState.Multiplayer)
            _g._editorUi.EndDrawFrame();
        else if (_g._menuState == MenuState.UIEditor)
            _g._uiEditor.EndDrawFrame();

        // Immediate-mode editor UI reads click edges during Draw, but Update can run
        // several times per Draw under fixed-timestep catch-up (slow frames), which
        // would collapse the one-frame press edge before Draw sees it and drop most
        // clicks. Snapshot the mouse once per Draw so edges are measured against the
        // previous Draw, not the previous Update. Runs UNCONDITIONALLY — a snapshot
        // gated on an editor being open goes stale across close/open and replays
        // the closing click into the next editor session (spell-editor insta-close).
        _g._input.SnapshotDrawFrame();

        if (_g._font != null && showUI && _g._showPerfReadout)
        {
            double frameMs = _g._rawDt > 0 ? _g._rawDt * 1000.0 : 0.0;
            double simMs = _g._sim.LastTickMs;
            // gpuish: total frame minus CPU portions ≈ what the GPU + vsync is
            // costing us. Useful as a quick "are we GPU bound?" gauge.
            double gpuish = System.Math.Max(0, frameMs - _g._drawMsAvg - simMs);
            string dbg = $"Zoom:{_g._camera.Zoom:F0} Pos:({_g._camera.Position.X:F0},{_g._camera.Position.Y:F0}) Speed:{_g._timeScale:F1}x FPS:{(_g._rawDt > 0 ? 1f / _g._rawDt : 0):F0} | frame:{frameMs:F1}ms sim:{simMs:F2} draw:{_g._drawMsAvg:F2} ground:{_g._groundMsAvg:F2} present:{_g._gpuPresentMsAvg:F2} gpuish:{gpuish:F2}"
                + $" | world:{_worldPass?.LastItemCount ?? 0}i/{_worldPass?.LastBatchCount ?? 0}b fx:{_fxPass?.LastItemCount ?? 0}i/{_fxPass?.LastBatchCount ?? 0}b";
            DrawText(_g._smallFont, dbg, new Vector2(10, screenH - 18), new Color(120, 120, 120));
        }

        // Zoom/bloom debug readout (`zoomhud` dev command): the exact zoom and
        // bloom code-path values for THIS frame, so sweep screenshots carry data.
        if (_g._devZoomHud && _g._smallFont != null && showUI)
        {
            var bl = _g._bloom;
            string zdbg = $"ZOOM {_g._camera.Zoom:F1} | bias {bl.DebugBias:+0.00;-0.00} | "
                + $"vres x{bl.DebugFSpread:F2} iters {bl.DebugIters} | dim x{bl.DebugComp:F2}";
            var zsize = _g._smallFont.MeasureString(zdbg);
            DrawText(_g._smallFont, zdbg,
                new Vector2((int)(screenW - zsize.X - 10), 86), new Color(255, 230, 90));
        }

        // Show collision debug mode label when active
        if (_g._collisionDebugMode != CollisionDebugMode.Off && _g._smallFont != null)
        {
            string label = $"[F8] Collision Debug: {DebugDraw.GetModeLabel(_g._collisionDebugMode)}";
            DrawText(_g._smallFont, label, new Vector2(10, screenH - 36), new Color(255, 200, 80));
        }
        if (_g._waterDebug && _g._smallFont != null)
        {
            DrawText(_g._smallFont, "[F2] Water Debug", new Vector2(10, screenH - 54), new Color(120, 220, 255));
        }

        // UI-debug overlay — drawn last so it sits over every UI region. Outlines
        // each _uiHits entry with a 1px yellow border drawn INSIDE the rect (via
        // DrawRectBorder), so screen-edge rects still show a full box.
        if (_g._uiDebugDrawMode == 1)
        {
            var scope = new SpriteScope(_g._spriteBatch, Materials.Hud);
            foreach (var e in _g._uiHits.Entries)
            {
                if (e.FullScreen || e.Probe != null) continue; // no concrete rect to outline
                DrawUtils.DrawRectBorder(scope, _g._pixel, e.Rect, Color.Yellow);
            }
        }
    }
}
