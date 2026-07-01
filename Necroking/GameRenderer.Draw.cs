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
using Necroking.UI;

namespace Necroking;

// Game1 partial: Draw orchestrator entry point.
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

        // --- Main menu ---
        if (_g._menuState == MenuState.MainMenu)
        {
            _g.GraphicsDevice.Clear(new Color(20, 15, 30));
            _g._spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            DrawMainMenu(screenW, screenH);
            _g._spriteBatch.End();
            _g.CompletePendingDevScreenshot();
            _g.BaseDraw(gameTime);
            return;
        }

        if (_g._menuState == MenuState.ScenarioList)
        {
            _g.GraphicsDevice.Clear(new Color(20, 15, 30));
            _g._spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            DrawScenarioList(screenW, screenH);
            _g._spriteBatch.End();
            _g.CompletePendingDevScreenshot();
            _g.BaseDraw(gameTime);
            return;
        }

        // Snap camera to pixel grid to prevent subpixel shimmer on ground/sprites
        // X pixel size = 1/Zoom, Y pixel size = 1/(Zoom*YRatio) due to isometric compression
        var realCameraPos = _g._camera.Position;
        float pixelSizeX = 1f / _g._camera.Zoom;
        float pixelSizeY = 1f / (_g._camera.Zoom * _g._camera.YRatio);
        _g._camera.Position = new Vec2(
            MathF.Round(realCameraPos.X / pixelSizeX) * pixelSizeX,
            MathF.Round(realCameraPos.Y / pixelSizeY) * pixelSizeY);

        // Set weather renderer context for this frame
        _g._weatherRenderer.SetContext(_g._spriteBatch, _g._pixel, _g._glowTex, _g._camera, _g._gameTime, _g._gameData, _g.GraphicsDevice);

        // Update fog of war render targets (before bloom, since this changes render targets)
        {
            bool fogActive = (FogOfWarMode)_g._gameData.Settings.FogOfWar.Mode != FogOfWarMode.Off;
            bool editorOpen = _g._menuState != MenuState.None && _g._menuState != MenuState.MainMenu;
            if (fogActive && !editorOpen)
                _g._fogOfWar.Update(_g._spriteBatch, _g._sim.Units, _g._gameData.Settings.FogOfWar, _g._rawDt);
            else
                // Update isn't running this frame, but IsVisible (which culls enemy
                // sprites/shadows/projectiles) keys off the fog system's cached mode.
                // Keep it in sync with the live setting so turning fog Off immediately
                // reveals all enemies instead of leaving them culled against stale fog.
                _g._fogOfWar.SyncMode(_g._gameData.Settings.FogOfWar);
        }

        // Begin bloom scene capture
        var bloomSettings = _g._activeScenario?.BloomOverride ?? _g._gameData.Settings.Bloom;
        bool useBloom = _g._bloom.IsInitialized && bloomSettings.Enabled;
        var clearColor = _g._activeScenario?.BackgroundColor
            ?? LaunchArgs.BgColor
            ?? new Color(30, 30, 40);
        if (useBloom)
            _g._bloom.BeginScene(_g.GraphicsDevice);
        else
            _g.GraphicsDevice.Clear(clearColor);

        // Compute ambient color from weather (brightness + tint) for lit sprite tinting
        _g._ambientColor = _g._weatherRenderer.GetAmbientColor();

        // AlphaBlend with premultiplied-alpha textures (loaded via TextureUtil.LoadPremultiplied)
        _g._spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);

        if (!useBloom)
            _g.GraphicsDevice.Clear(clearColor);

        // --- Ground ---
        if ((_g._activeScenario == null || _g._activeScenario.WantsGround) && !_g._devShotNoGround)
        {
            DrawGround();
            // Perf scenarios can ask Game1 to redraw the ground N extra times to
            // stress the GPU past the 16.67ms vsync budget. The redraws happen
            // BEFORE the rest of the scene so they overwrite each other; only the
            // last write contributes visually.
            if (_g._activeScenario != null && _g._activeScenario.ExtraGroundDrawsPerFrame > 0
                && _g._groundEffect != null && _g._groundVertexMapTex != null && _g._groundSystem.TypeCount > 0)
            {
                int worldW2 = _g._groundSystem.WorldW > 0 ? _g._groundSystem.WorldW : Game1.WorldSize;
                int worldH2 = _g._groundSystem.WorldH > 0 ? _g._groundSystem.WorldH : Game1.WorldSize;
                for (int e = 0; e < _g._activeScenario.ExtraGroundDrawsPerFrame; e++)
                    DrawGroundShader(worldW2, worldH2);
            }
        }

        // --- Roads ---
        DrawRoads();

        // --- Ground-layer objects (traps — render above dirt, below grass) ---
        DrawGroundLayerObjects();

        // --- Magic glyphs (ground level, after traps, before walls) ---
        _g._glyphRenderer.SetContext(_g._spriteBatch, _g._pixel, _g._glowTex, _g._camera, _g._renderer, _g._flipbooks, _g._gameTime);
        _g._glyphRenderer.DrawGround(_g._sim.MagicGlyphs);

        // Build progress bars for blueprint glyphs
        foreach (var g in _g._sim.MagicGlyphs.Glyphs)
        {
            // Show progress bar from the moment the glyph is placed (even at 0%), not only
            // once construction has begun — so players can see "trap placed, awaiting build".
            if (g.State == GameSystems.GlyphState.Blueprint && g.BuildProgress < 1f && g.Alive)
            {
                var gsp = _g._renderer.WorldToScreen(g.Position, 0f, _g._camera);
                DrawBuildProgressBar(gsp, g.BuildProgress, g.Radius);
            }
        }

        // --- Walls ---
        DrawWalls();

        // --- Wading sink offsets ---
        // Compute Unit.WadingSinkOffsetY for every unit before any visual
        // pass reads it. Must run before _g._shadowRenderer.Draw (which reads
        // RenderPos to position shadows) and before DrawUnitsAndObjects
        // (which reads RenderPos for sprites, buffs, damage numbers, etc.).
        _g.UpdateWadingSinkOffsets();

        // --- Shadows ---
        // Grass is no longer drawn here — tufts are merged into the unit Y-sort
        // inside DrawUnitsAndObjects so they can render in front of / behind
        // units based on world Y.
        _g._shadowRenderer.Draw(_g.GraphicsDevice, _g._spriteBatch, _g._glowTex, _g._camera, _g._renderer, _g._sim, _g._gameData, _g._unitAnims, _g._atlases, _g._envSystem, _g._fogOfWar, _g._groundSystem, _g._deathFog, _g._corpseAnims, _g._reanimFx);

        // Hover highlight — ground variants (Circle / Ground Box) BEHIND corpses/units (RTS-style).
        DrawHoverGroundMarkers();

        // --- Corpses ---
        DrawCorpses();

        // --- Units + Environment objects + Poison cloud puffs (merged Y-sort for correct depth) ---
        DrawUnitsAndObjects();

        // --- Projectiles ---
        DrawProjectiles();
        DrawSoulOrbs();
        // --- Drag rope (necromancer → roped corpse) ---
        DrawRope();
        // (Death-fog puffs render inside DrawUnitsAndObjects' merged Y-sort
        // pass so they correctly occlude / are occluded by units & env objects
        // based on relative ground Y — see DepthItemType.DeathFogPuff.)

        // --- Rain (world-space, depth-sorted with scene objects) ---
        _g._weatherRenderer.DrawRain(screenW, screenH);

        _g._spriteBatch.End();

        // Spawn new effects from impacts (once per frame, before drawing)
        SpawnImpactEffects();

        // --- Alpha-blended HDR effects (clouds, smoke) ---
        _g._hdrSpriteEffect?.Parameters["AlphaMode"]?.SetValue(1f);
        _g._spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp,
            effect: _g._hdrSpriteEffect);
        DrawEffectsFiltered(0);
        _g._spriteBatch.End();

        // --- Additive HDR pass (effects + fireball projectiles) ---
        _g._hdrSpriteEffect?.Parameters["AlphaMode"]?.SetValue(0f);
        _g._spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.LinearClamp,
            effect: _g._hdrSpriteEffect);
        DrawProjectilesHdr();
        DrawEffectsFiltered(1);
        _g._reanimFx.DrawAdditive(); // reanimation light + green cloud puffs (additive HDR)
        // Lightning bolts and drains use HDR vertex encoding — draw in this HDR batch
        _g._lightningRenderer.SetGameTime(_g._gameTime);
        _g._lightningRenderer.Draw();
        _g._spriteBatch.End();

        // --- Additive blend pass (energy columns, debug shapes) ---
        _g._spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.LinearClamp);
        _g._glyphRenderer.DrawEnergyColumns(_g._sim.MagicGlyphs);

        // Bloom debug: draw test HDR shapes (multiple additive layers to exceed 1.0)
        if (_g._activeScenario is Scenario.Scenarios.BloomDebugScenario bloomDebug && bloomDebug.DrawTestShapes)
        {
            foreach (var (wx, wy, sz, col, label) in Scenario.Scenarios.BloomDebugScenario.TestShapes)
            {
                var screenPos = _g._renderer.WorldToScreen(new Vec2(wx, wy), 0f, _g._camera);
                int pixSz = (int)(sz * _g._camera.Zoom);
                var rect = new Rectangle((int)screenPos.X - pixSz / 2, (int)screenPos.Y - pixSz / 2, pixSz, pixSz);

                // Draw the shape multiple times additively to push values above 1.0
                int layers = label.Contains("3x") ? 5 : 1;
                for (int l = 0; l < layers; l++)
                    _g._spriteBatch.Draw(_g._pixel, rect, col);
            }
        }

        _g._spriteBatch.End();

        // --- God ray pass (alpha blend + HDR intensity shader) ---
        _g._lightningRenderer.DrawGodRays();

        // --- Alpha blend pass (collecting foragables + damage numbers on top) ---
        _g._spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
        _g._foragables.Draw();
        DrawDamageNumbers();
        _g._spriteBatch.End();

        // End bloom and composite
        if (useBloom)
            _g._bloom.EndScene(_g.GraphicsDevice, _g._spriteBatch, bloomSettings);

        // --- Fog of war overlay (after bloom, before HUD) ---
        // Skip entirely when Off or when any editor is open
        {
            bool fogActive = (FogOfWarMode)_g._gameData.Settings.FogOfWar.Mode != FogOfWarMode.Off;
            bool editorOpen = _g._menuState != MenuState.None && _g._menuState != MenuState.MainMenu;
            if (fogActive && !editorOpen)
            {
                // Draw fog overlay (RTs already updated before bloom pass)
                _g._fogOfWar.Draw(_g._spriteBatch, _g._camera, _g._renderer, screenW, screenH, _g._gameData.Settings.FogOfWar);
            }
        }

        // --- Collision debug overlay (after world, before HUD) ---
        if (_g._collisionDebugMode != CollisionDebugMode.Off)
        {
            _g._spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            _g._debugDraw.DrawCollisionDebug(_g._spriteBatch, _g.GraphicsDevice, _g._sim, _g._camera, _g._renderer,
                _g._collisionDebugMode, _g._envSystem, _g._sim.Pathfinder);
            _g._spriteBatch.End();
        }

        // --- Weapon attach debug overlay (scenario opt-in) ---
        if (_g._activeScenario != null && _g._activeScenario.ShowWeaponAttachDebug)
        {
            _g._spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            DrawWeaponAttachDebug();
            _g._spriteBatch.End();
        }

        // --- Death-fog debug overlay (F5) ---
        if (_g._deathFog.DebugVisible)
        {
            _g._spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
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
                    _g._spriteBatch.Draw(_g._pixel,
                        new Rectangle((int)pos.X - 2, (int)pos.Y - 1, (int)size.X + 4, (int)size.Y + 2),
                        new Color(0, 0, 0, 180));
                    _g._spriteBatch.DrawString(_g._smallFont, label, pos, labelColor);
                }
            }

            _g._spriteBatch.End();
        }

        // --- Gameplay debug overlay (F7) ---
        if (_g._gameplayDebugMode > 0)
        {
            _g._spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            if (_g._gameplayDebugMode == 1)
                DrawHordeDebug();
            else if (_g._gameplayDebugMode == 2)
                DrawUnitInfoDebug();
            _g._spriteBatch.End();
        }

        // --- Wind debug overlay (F6) ---
        if (_g._windDebug)
        {
            try
            {
                _g._spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                DrawWindDebug(screenW, screenH);
                _g._spriteBatch.End();
            }
            catch (Exception ex)
            {
                Core.DebugLog.Log("error", $"Wind debug crash: {ex}");
                _g._windDebug = false;
                try { _g._spriteBatch.End(); } catch { }
            }
        }

        // --- Alt shows object names (+ animation debug for animated objects),
        //     plus corpse names and unit names ---
        if ((_g._menuState == MenuState.MapEditor || _g._menuState == MenuState.None) && _g._input.IsKeyDown(Keys.LeftAlt))
        {
            _g._spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);

            // Draws a centered name label at a world position, boxed for legibility.
            // Returns silently if the position is off-screen or the label is empty.
            void DrawWorldLabel(Vec2 world, string label, Color color)
            {
                if (string.IsNullOrEmpty(label)) return;
                var sp = _g._renderer.WorldToScreen(world, 0f, _g._camera);
                if (sp.X <= -100 || sp.X >= screenW + 100 || sp.Y <= -100 || sp.Y >= screenH + 100) return;
                var textSize = _g._smallFont != null ? _g._smallFont.MeasureString(label) : new Vector2(label.Length * 6, 12);
                var textPos = new Vector2((int)(sp.X - textSize.X * 0.5f), (int)(sp.Y + 4));
                _g._spriteBatch.Draw(_g._pixel, new Rectangle((int)textPos.X - 2, (int)textPos.Y - 1, (int)textSize.X + 4, (int)textSize.Y + 2), new Color(0, 0, 0, 160));
                if (_g._smallFont != null)
                    _g._spriteBatch.DrawString(_g._smallFont, label, textPos, color);
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

        // Restore real camera position (smooth, for input/HUD)
        _g._camera.Position = realCameraPos;

        // --- HUD (drawn after bloom so it's not affected) ---
        _g._spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);

        // --- Weather effects (fog/haze/brightness — rain draws in scene pass) ---
        _g._weatherRenderer.DrawFog(screenW, screenH);

        bool showUI = (_g._activeScenario == null || _g._activeScenario.WantsUI) && !_g._devShotNoUi;
        if (showUI)
            DrawHoverHighlights();
        if (showUI)
            DrawHUD(screenW, screenH);
        if (showUI)
            DrawWorldHoverInfo(screenW, screenH);
        if (showUI)
            DrawAggressionBar(screenW, screenH);

        // Inventory UI (widget-based, drawn over HUD)
        if (showUI)
        {
            _g._inventoryUI.Draw(screenW, screenH);
            _g._tableMenuUI.Draw();
            _g._graveRosterUI.Draw();
            _g._jobBoardUI.Draw();
        }

        // Character stats panel (Tab)
        if (showUI)
            _g._characterStatsUI.Draw(screenW, screenH, _g._sim, _g._gameData.Buffs, ref _g._spellBarState, _g._input, _g._gameData, _g._skillBookState);

        // Unit info sheet (U = player character, O = inspect under cursor)
        if (showUI)
            _g._unitInfoPanel.Draw(screenW, screenH, _g._sim);

        // Spell grimoire (J)
        if (showUI)
            _g._grimoireOverlay.Draw(screenW, screenH);

        // Skill book panel (K) — tabbed Potions/Necromancy/Magic/Metamorphosis trees.
        if (showUI)
            _g._skillBookOverlay.Draw(screenW, screenH);

        // Bottom-right "Recipe Learned" toasts — drawn even when the panel is closed.
        if (showUI)
            DrawSkillLearnToasts(screenW, screenH);

        // Scenario custom UI hook — for shader-test scenarios that draw raw
        // geometry without a real panel.
        if (_g._activeScenario?.CustomUIDraw != null)
            _g._activeScenario.CustomUIDraw(_g._spriteBatch, screenW, screenH);

        // Building menu UI (widget-based)
        if (showUI)
        {
            _g._buildingMenuUI.DrawMenu();
            _g._craftingMenu.Draw();

            // Ghost preview for building placement
            if (_g._buildingMenuUI.IsPlacementActive)
            {
                Vec2 mw = _g._camera.ScreenToWorld(_g._input.MousePos, screenW, screenH);
                var sp = _g._renderer.WorldToScreen(mw, 0f, _g._camera);
                _g._buildingMenuUI.DrawGhostPreview(_g._spriteBatch, _g._pixel, mw, sp, _g._camera, _g._renderer);
            }

        }

        if (_g._gameOver && showUI)
            DrawGameOver(screenW, screenH);
        else if (_g._menuState == MenuState.PauseMenu)
            DrawPauseMenu(screenW, screenH);
        else if (_g._menuState == MenuState.Settings)
            _g._settingsWindow.Draw(screenW, screenH);
        if (_g._menuState == MenuState.UnitEditor)
        {
            _g._unitEditor.Draw(screenW, screenH, gameTime);
            // U23: Handle close request from the editor's [X] button
            if (_g._unitEditor.WantsClose)
            {
                _g._unitEditor.WantsClose = false;
                _g._editorUi.ResetAllState();
                _g._menuState = MenuState.None;
            }
        }
        else if (_g._menuState == MenuState.SpellEditor)
        {
            _g._spellEditor.Draw(screenW, screenH, gameTime);
            if (_g._spellEditor.WantsClose)
            {
                _g._spellEditor.WantsClose = false;
                _g._editorUi.ResetAllState();
                _g._menuState = MenuState.None;
            }
        }
        else if (_g._menuState == MenuState.MapEditor)
        {
            _g._mapEditor.Draw(screenW, screenH);
        }
        else if (_g._menuState == MenuState.UIEditor)
        {
            _g._uiEditor.Draw(screenW, screenH);
        }
        else if (_g._menuState == MenuState.ItemEditor)
        {
            _g._itemEditor.Draw(screenW, screenH, gameTime);
            if (_g._itemEditor.WantsClose)
            {
                _g._itemEditor.WantsClose = false;
                _g._editorUi.ResetAllState();
                _g._menuState = MenuState.None;
            }
        }

        // Draw color picker popup overlay (must be after all editor drawing, on top)
        if (_g._menuState == MenuState.UnitEditor || _g._menuState == MenuState.SpellEditor)
            _g._editorUi.DrawColorPickerPopup();

        // Immediate-mode editor UI reads click edges during Draw, but Update can run
        // several times per Draw under fixed-timestep catch-up (slow frames), which
        // would collapse the one-frame press edge before Draw sees it and drop most
        // clicks. Snapshot the mouse once per Draw so edges are measured against the
        // previous Draw, not the previous Update. _g._editorUi backs Settings + the
        // unit/spell/map/item editors; _g._uiEditor is its own EditorBase instance.
        if (_g._menuState == MenuState.Settings || _g._menuState == MenuState.UnitEditor
            || _g._menuState == MenuState.SpellEditor || _g._menuState == MenuState.MapEditor
            || _g._menuState == MenuState.ItemEditor)
            _g._editorUi.EndDrawFrame();
        else if (_g._menuState == MenuState.UIEditor)
            _g._uiEditor.EndDrawFrame();

        if (_g._font != null && showUI && _g._showPerfReadout)
        {
            double frameMs = _g._rawDt > 0 ? _g._rawDt * 1000.0 : 0.0;
            double simMs = _g._sim.LastTickMs;
            // gpuish: total frame minus CPU portions ≈ what the GPU + vsync is
            // costing us. Useful as a quick "are we GPU bound?" gauge.
            double gpuish = System.Math.Max(0, frameMs - _g._drawMsAvg - simMs);
            string dbg = $"Zoom:{_g._camera.Zoom:F0} Pos:({_g._camera.Position.X:F0},{_g._camera.Position.Y:F0}) Speed:{_g._timeScale:F1}x FPS:{(_g._rawDt > 0 ? 1f / _g._rawDt : 0):F0} | frame:{frameMs:F1}ms sim:{simMs:F2} draw:{_g._drawMsAvg:F2} ground:{_g._groundMsAvg:F2} present:{_g._gpuPresentMsAvg:F2} gpuish:{gpuish:F2}";
            DrawText(_g._smallFont, dbg, new Vector2(10, screenH - 18), new Color(120, 120, 120));
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

        _g._spriteBatch.End();

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

        // Time the Present()/blit done by base.Draw — anything that doesn't
        // overlap with CPU work shows up here. Present blocks until the GPU
        // can accept the frame, so this approximates GPU+vsync wait time.
        var presentSw = System.Diagnostics.Stopwatch.StartNew();
        _g.BaseDraw(gameTime);
        presentSw.Stop();
        _g._gpuPresentMsAvg = _g._gpuPresentMsAvg * (1.0 - EmaAlpha)
                         + presentSw.Elapsed.TotalMilliseconds * EmaAlpha;
    }
}
