using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data.Registries;
using Necroking.Lib;
using Necroking.Render;

namespace Necroking;

// Game1 partial: the render pipeline as data — phases and passes built once,
// executed by Draw(). Step 0 of the render redesign (todos/render-pipeline-design.md):
// every pass wraps a legacy draw block verbatim; later steps replace blocks with
// sprite-queue passes. Order in this file IS the frame order.
partial class GameRenderer
{
    private RenderPipeline? _pipeline;
    private readonly RenderContext _ctx = new();
    private SpriteQueuePass? _worldPass;
    private SpriteQueuePass? _fxPass;
    private SpriteDrawCallback? _cbFxAlpha, _cbFxProjectilesHdr, _cbFxEffectsAdd,
        _cbFxReanim, _cbFxLightning, _cbFxShapes;

    // Per-frame values shared between Scene OnBegin and OnEnd (computed once
    // per frame in Scene.OnBegin).
    private bool _frameUseBloom;
    private Data.Registries.BloomSettings? _frameBloomSettings;
    private Color _frameClearColor;
    private Vec2 _realCameraPos;

    /// <summary>Collect the HDR/additive effects queue (runs after the world
    /// pass and the fog depth-occluder stamp).</summary>
    private void CollectFxItems(RenderContext ctx)
    {
        _cbFxAlpha ??= (SpriteScope _, int _, int _) => DrawEffectsFiltered(0);
        _cbFxProjectilesHdr ??= (SpriteScope _, int _, int _) => DrawProjectilesHdr();
        _cbFxEffectsAdd ??= (SpriteScope _, int _, int _) => DrawEffectsFiltered(1);
        _cbFxReanim ??= (SpriteScope s, int _, int _) =>
        {
            if (_g._gameData.Settings.Performance.DepthSortedFog && _g._depthCutoutEffect != null)
            {
                // Depth-sorted fog: draw ALL reanim particles (light + clouds +
                // dust) in ONE Y-sorted pass, so bright and dark puffs interleave
                // by spawn position, depth-testing the units' stamps so a risen
                // unit occludes them. It manages its own batches, so suspend the
                // shared additive batch around it.
                s.Suspend();
                _g._reanimFx.DrawSortedParticles(_g._hdrSpriteEffect);
                s.Resume();
            }
            else
            {
                _g._reanimFx.DrawAdditive(); // reanimation light + green cloud puffs (additive HDR)
            }
        };
        _cbFxLightning ??= (SpriteScope _, int _, int _) =>
        {
            // Telegraph circles and strike flashes draw here as HDR sprites; the
            // bolts/tendrils themselves are collected as ribbon vertices and drawn
            // post-batch by the LightningTris pass (additive = order-independent).
            _g._lightningRenderer.SetGameTime(_g._gameTime);
            _g._lightningRenderer.Draw();
        };
        _cbFxShapes ??= (SpriteScope _, int _, int _) =>
        {
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
                        _g.Scope.Draw(_g._pixel, rect, col);
                }
            }
        };

        // Fallbacks mirror the old behavior when HdrSprite.fx failed to load:
        // same blend/sampler, no effect.
        var matAlpha = Materials.HdrAlpha ?? Materials.Scene;
        var matAdd = Materials.HdrAdditive ?? Materials.AdditiveShapes;

        var q = _fxPass!;
        // Ground-fog wisps (depth-tested vs the occluder stamps drawn just
        // before this pass) — FogWisps layer sorts before the HDR bands.
        _g._groundFog.CollectWisps(q, _g._ambientColor, ctx.ScreenW, ctx.ScreenH);
        q.SubmitCallback(WorldLayer.EffectsHdrAlpha, _cbFxAlpha, 0, 0, matAlpha);
        q.SubmitCallback(WorldLayer.EffectsHdrAdditive, _cbFxProjectilesHdr, 0, 0, matAdd);
        q.SubmitCallback(WorldLayer.EffectsHdrAdditive, _cbFxEffectsAdd, 0, 0, matAdd);
        q.SubmitCallback(WorldLayer.EffectsHdrAdditive, _cbFxReanim, 0, 0, matAdd);
        q.SubmitCallback(WorldLayer.EffectsHdrAdditive, _cbFxLightning, 0, 0, matAdd);
        q.SubmitCallback(WorldLayer.AdditiveShapes, _cbFxShapes, 0, 0, Materials.AdditiveShapes);
    }

    /// <summary>Dev-command surface: list all phases/passes with enabled state,
    /// last-frame timing, and queue stats (items/batches).</summary>
    internal string DescribePipeline()
    {
        if (_pipeline == null) return "pipeline not built yet (draw one frame first)";
        var sb = new System.Text.StringBuilder();
        foreach (var phase in _pipeline.Phases)
        {
            sb.Append('[').Append(phase.Name).Append("]\n");
            foreach (var pass in phase.Passes)
            {
                sb.Append(pass.Enabled ? "  on  " : "  OFF ").Append(pass.Name)
                  .Append(' ').Append(pass.LastMs.ToString("F2")).Append("ms");
                if (pass is SpriteQueuePass q)
                    sb.Append(" items=").Append(q.LastItemCount)
                      .Append(" batches=").Append(q.LastBatchCount);
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>Dev-command surface: toggle a pass by name. Returns false if no
    /// pass matches.</summary>
    internal bool TrySetPassEnabled(string name, bool enabled)
    {
        var pass = _pipeline?.FindPass(name);
        if (pass == null) return false;
        pass.Enabled = enabled;
        return true;
    }

    private RenderPipeline BuildPipeline()
    {
        var p = new RenderPipeline();

        // ---- Phase: Prep — RT-touching jobs that must run before the scene RT binds ----
        var prep = p.AddPhase(new RenderPhase("Prep"));

        prep.Add(new CustomPass("WeatherContext", ctx =>
        {
            // Set weather renderer context for this frame
            _g._weatherRenderer.SetContext(_g._spriteBatch, _g._pixel, _g._glowTex, _g._camera, _g._gameTime, _g._gameData, _g.GraphicsDevice);
        }));

        prep.Add(new CustomPass("FogOfWarUpdate", ctx =>
        {
            // Update fog of war render targets (before bloom, since this changes render targets)
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
        }));

        // ---- Phase: Scene — the world, captured into the HDR scene RT when bloom is on ----
        var scene = p.AddPhase(new RenderPhase("Scene"));

        scene.OnBegin = ctx =>
        {
            // Begin bloom scene capture
            var bloomSettings = _g._activeScenario?.BloomOverride ?? _g._gameData.Settings.Bloom;
            _frameBloomSettings = bloomSettings;
            _frameUseBloom = _g._bloom.IsInitialized && bloomSettings.Enabled;
            _frameClearColor = _g._activeScenario?.BackgroundColor
                ?? LaunchArgs.BgColor
                ?? new Color(30, 30, 40);
            if (_frameUseBloom)
                _g._bloom.BeginScene(_g.GraphicsDevice);
            else
                _g.GraphicsDevice.Clear(_frameClearColor);

            // Compute ambient color from weather (brightness + tint) for lit sprite tinting
            _g._ambientColor = _g._weatherRenderer.GetAmbientColor();

            if (!_frameUseBloom)
                _g.GraphicsDevice.Clear(_frameClearColor);
        };

        scene.Add(new CustomPass("Ground", ctx =>
        {
            if ((_g._activeScenario == null || _g._activeScenario.WantsGround || _g._userHasInteractedWithWindow) && !_g._devShotNoGround)
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
        }));

        scene.Add(new CustomPass("WadingSinkOffsets", ctx =>
        {
            // Compute Unit.WadingSinkOffsetY for every unit before any visual
            // pass reads it. Must run before the world pass draws (shadows and
            // unit sprites both read RenderPos).
            _g.UpdateWadingSinkOffsets();
        }));

        // The world sprite pass: roads → traps → glyphs → walls → shadows →
        // hover markers → corpses → Y-sorted units/objects/particles →
        // projectiles/rope → rain, as layer bands in one sorted queue.
        // Collection order (and thus determinism ties) matches the old block
        // order; consecutive same-material layers merge into single batches.
        _worldPass = new SpriteQueuePass("World", Materials.Scene,
            () => _g._camera.Position.Y, capacity: 1024)
        {
            Collect = CollectWorldItems,
        };
        scene.Add(_worldPass);

        scene.Add(new CustomPass("FogDepthOccluders", ctx =>
        {
            // Stamp units' depth silhouettes into the scene RT's (already-bound,
            // already-cleared) depth buffer so depth-tested fog (reanim smoke,
            // ground-fog wisps) can test against them below. Runs for the
            // DepthSortedFog perf setting OR whenever ground fog is active.
            if (_g._gameData.Settings.Performance.DepthSortedFog || _g._groundFog.HasActiveBanks)
                DrawFogDepthOccluders();
        }));

        // Spawn new effects from impacts (once per frame, before drawing)
        scene.Add(new CustomPass("SpawnImpactEffects", ctx => SpawnImpactEffects()));

        // HDR / additive effects queue: alpha HDR clouds → additive HDR
        // (fireballs, effects, reanim, lightning) → plain additive shapes.
        // The AlphaMode uniform flip is gone — HdrAlpha/HdrAdditive are two
        // Effect.Clone materials with the mode baked at load. Consecutive
        // same-material items merge into one batch.
        _fxPass = new SpriteQueuePass("HdrEffects", Materials.Scene,
            () => _g._camera.Position.Y, capacity: 32)
        {
            Collect = CollectFxItems,
        };
        scene.Add(_fxPass);

        // Lightning ribbons + god rays (HDR intensity triangle passes) — manage
        // their own device state, must run after the additive sprite batch ends.
        scene.Add(new CustomPass("LightningTris", ctx => _g._lightningRenderer.DrawTriangleEffects()));

        scene.Add(new CustomPass("ForagablesDamageNumbers", ctx =>
        {
            // Top-of-world alpha pass (collecting foragables + damage numbers)
            Materials.Scene.Begin(ctx.Batch);
            _g._foragables.Draw();
            DrawDamageNumbers();
            ctx.Batch.End();
        }));

        // End bloom and composite back to the backbuffer
        scene.OnEnd = ctx =>
        {
            if (_frameUseBloom)
                _g._bloom.EndScene(ctx.Device, ctx.Batch, _frameBloomSettings!);
        };

        // ---- Phase: Post — backbuffer composites and debug overlays ----
        var post = p.AddPhase(new RenderPhase("Post"));

        post.Add(new CustomPass("FogOfWarOverlay", ctx =>
        {
            // Fog of war overlay (after bloom, before HUD).
            // Skip entirely when Off or when any editor is open.
            bool fogActive = (FogOfWarMode)_g._gameData.Settings.FogOfWar.Mode != FogOfWarMode.Off;
            bool editorOpen = _g._menuState != MenuState.None && _g._menuState != MenuState.MainMenu;
            if (fogActive && !editorOpen)
            {
                // Draw fog overlay (RTs already updated before bloom pass)
                _g._fogOfWar.Draw(_g._spriteBatch, _g._camera, _g._renderer, ctx.ScreenW, ctx.ScreenH, _g._gameData.Settings.FogOfWar);
            }
        }));

        post.Add(new CustomPass("CollisionDebug", ctx =>
        {
            if (_g._collisionDebugMode != CollisionDebugMode.Off)
            {
                Materials.Hud.Begin(_g._spriteBatch);
                _g._debugDraw.DrawCollisionDebug(_g._spriteBatch, _g.GraphicsDevice, _g._sim, _g._camera, _g._renderer,
                    _g._collisionDebugMode, _g._envSystem, _g._sim.Pathfinder);
                _g._spriteBatch.End();
            }
        }));

        post.Add(new CustomPass("WeaponAttachDebug", ctx =>
        {
            if (_g._activeScenario != null && _g._activeScenario.ShowWeaponAttachDebug)
            {
                Materials.Hud.Begin(_g._spriteBatch);
                DrawWeaponAttachDebug();
                _g._spriteBatch.End();
            }
        }));

        post.Add(new CustomPass("DeathFogDebug", ctx =>
        {
            if (_g._deathFog.DebugVisible)
                DrawDeathFogDebugOverlay();
        }));

        post.Add(new CustomPass("GameplayDebug", ctx =>
        {
            if (_g._gameplayDebugMode > 0)
            {
                Materials.Hud.Begin(_g._spriteBatch);
                if (_g._gameplayDebugMode == 1)
                    DrawHordeDebug();
                else if (_g._gameplayDebugMode == 2)
                    DrawUnitInfoDebug();
                _g._spriteBatch.End();
            }
        }));

        post.Add(new CustomPass("WindDebug", ctx =>
        {
            if (_g._windDebug)
            {
                try
                {
                    Materials.Hud.Begin(_g._spriteBatch);
                    DrawWindDebug(ctx.ScreenW, ctx.ScreenH);
                    _g._spriteBatch.End();
                }
                catch (Exception ex)
                {
                    Core.DebugLog.Log("error", $"Wind debug crash: {ex}");
                    _g._windDebug = false;
                    try { _g._spriteBatch.End(); } catch { }
                }
            }
        }));

        post.Add(new CustomPass("AltLabels", ctx =>
        {
            // Alt shows object names (+ animation debug for animated objects),
            // plus corpse names and unit names
            if ((_g._menuState == MenuState.MapEditor || _g._menuState == MenuState.None)
                && _g._input.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.LeftAlt))
                DrawAltNameLabels(ctx.ScreenW, ctx.ScreenH);
        }));

        // ---- Phase: Hud — UI over everything, unaffected by bloom, unsnapped camera ----
        var hud = p.AddPhase(new RenderPhase("Hud"));

        hud.OnBegin = ctx =>
        {
            // Restore real camera position (smooth, for input/HUD)
            _g._camera.Position = _realCameraPos;

            // HUD pass state defined in EffectBatch (HudBlend/HudSampler).
            Render.EffectBatch.BeginHudPass(_g._spriteBatch);
        };

        // Weather effects (fog/haze/brightness — rain draws in scene pass).
        // Runs inside the HUD batch; the scope resumes Materials.Hud after the
        // fog shader's fullscreen quad.
        hud.Add(new CustomPass("WeatherFog", ctx =>
            _g._weatherRenderer.DrawFog(new SpriteScope(ctx.Batch, Materials.Hud), ctx.ScreenW, ctx.ScreenH)));

        hud.Add(new CustomPass("Hud", ctx => DrawHudBlock(ctx.ScreenW, ctx.ScreenH, ctx.GameTime)));

        hud.OnEnd = ctx => _g._spriteBatch.End();

        return p;
    }
}
