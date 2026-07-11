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

// Game1 partial: HUD, menus, skill toasts and debug overlays.
partial class GameRenderer
{
    /// <summary>Geometry helper — same layout numbers used in DrawSkillLearnToasts.
    /// Called from the input pass so clicks land on what was just rendered.</summary>
    private Rectangle GetSkillLearnToastRect(int sw, int sh, int stackIndex)
    {
        const int toastW = 280, toastH = 56, padR = 16, padB = 16, gap = 6;
        int yCursor = sh - padB - toastH - stackIndex * (toastH + gap);
        return new Rectangle(sw - padR - toastW, yCursor, toastW, toastH);
    }

    /// <summary>Catalogue the visible corner toasts into the central UI hit
    /// registry (hover-blocking is derived from it; click routing lives in
    /// SkillToastLayer via <see cref="SkillToastIndexAt"/>/<see cref="ActivateSkillToast"/>).</summary>
    internal void AppendSkillToastHitRects(Necroking.UI.UIHitRegistry reg, int sw, int sh)
    {
        for (int i = 0; i < _g._skillLearnToasts.Count; i++)
            reg.Add($"toast.skill_learn.{i}", GetSkillLearnToastRect(sw, sh, i));
    }

    /// <summary>List index of the corner toast under the cursor, or -1. Toasts
    /// are drawn from the most recent (last-added) up the stack: slot 0 = newest
    /// = bottom rect. Hit-test half of the SkillToastLayer routing.</summary>
    internal int SkillToastIndexAt(int sw, int sh, int mx, int my)
    {
        for (int i = 0; i < _g._skillLearnToasts.Count; i++)
        {
            int stackSlot = _g._skillLearnToasts.Count - 1 - i;
            if (GetSkillLearnToastRect(sw, sh, stackSlot).Contains(mx, my))
                return i;
        }
        return -1;
    }

    /// <summary>Clicked toast action: open the skill book on the toast's tab and
    /// dismiss the toast. Action half of the SkillToastLayer routing.</summary>
    internal void ActivateSkillToast(int listIdx)
    {
        if (listIdx < 0 || listIdx >= _g._skillLearnToasts.Count) return;
        var t = _g._skillLearnToasts[listIdx];
        int tabIdx = SkillBookDefs.FindTabIndexFor(t.SkillId);
        _g._skillBookOverlay.Open();
        if (tabIdx >= 0) _g._skillBookOverlay.SetActiveTab(tabIdx);
        _g._skillLearnToasts.RemoveAt(listIdx);
    }

    internal void UpdateSkillLearnToasts(float dt)
    {
        for (int i = _g._skillLearnToasts.Count - 1; i >= 0; i--)
        {
            var t = _g._skillLearnToasts[i];
            t.Timer += dt;
            if (t.Timer >= t.Duration) _g._skillLearnToasts.RemoveAt(i);
            else _g._skillLearnToasts[i] = t;
        }
    }

    /// <summary>Bottom-right stack of "Recipe Learned" / "Skill Unlocked" toasts.
    /// Each toast slides in (first 0.25s), holds, then fades out (last 0.6s).</summary>
    internal void DrawSkillLearnToasts(int sw, int sh)
    {
        if (_g._skillLearnToasts.Count == 0 || _g._font == null) return;
        var f = _g._font!;
        var sf = _g._smallFont ?? f;

        const int toastW = 280, toastH = 56, padR = 16, padB = 16, gap = 6;
        int yCursor = sh - padB - toastH;

        // Palette matches the SkillBookPanel's grimoire chrome.
        var leatherDark = new Color(26, 13, 8);
        var leatherMid  = new Color(42, 26, 18);
        var gold        = new Color(218, 184,  96);
        var goldDim     = new Color(108,  84,  40);
        var parchment   = new Color(196, 174, 128);

        for (int i = _g._skillLearnToasts.Count - 1; i >= 0; i--)
        {
            var t = _g._skillLearnToasts[i];
            float life = t.Timer / t.Duration;
            float alpha = 1f;
            if (life < 0.1f) alpha = life / 0.1f;          // slide in
            else if (life > 0.85f) alpha = (1f - life) / 0.15f; // fade out
            alpha = MathHelper.Clamp(alpha, 0f, 1f);
            int slideX = (int)((1f - alpha) * 30); // also slides slightly from the right

            int x = sw - padR - toastW + slideX;
            int y = yCursor;
            byte a = (byte)(255 * alpha);

            var rect = new Rectangle(x, y, toastW, toastH);
            // Drop shadow
            _g.Scope.Draw(_g._pixel, new Rectangle(rect.X + 3, rect.Y + 3, rect.Width, rect.Height),
                new Color((byte)0, (byte)0, (byte)0, (byte)(160 * alpha)));
            _g.Scope.Draw(_g._pixel, rect, new Color(leatherMid, alpha));
            // Top gold accent band
            _g.Scope.Draw(_g._pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2),
                new Color(gold, alpha));
            // Border
            DrawToastBorder(rect, new Color(goldDim, alpha));

            // Text — header + skill name
            string header = t.Header;
            string body = t.SkillName;
            // Sanitize for the embedded ASCII-only SpriteFont.
            header = SanitizeAscii(header);
            body = SanitizeAscii(body);
            DrawTextRounded(sf, header,
                new Vector2(rect.X + 14, rect.Y + 8),
                new Color(gold, alpha));
            DrawTextRounded(f, body,
                new Vector2(rect.X + 14, rect.Y + 24),
                new Color(parchment, alpha));

            yCursor -= toastH + gap;
        }
    }

    private void DrawToastBorder(Rectangle r, Color c)
    {
        Necroking.Render.DrawUtils.DrawRectBorder(_g._spriteBatch, _g._pixel, r, c);
    }

    private void DrawTextRounded(SpriteFont f, string text, Vector2 pos, Color color)
        => _g.Scope.DrawString(f, text, new Vector2((int)pos.X, (int)pos.Y), color);

    private static string SanitizeAscii(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        bool needs = false;
        for (int i = 0; i < text.Length; i++)
            if (text[i] > 126 || (text[i] < 32 && text[i] != '\n')) { needs = true; break; }
        if (!needs) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text) sb.Append(ch >= 32 && ch <= 126 ? ch : '?');
        return sb.ToString();
    }

    /// <summary>
    /// Scenario debug overlay: draw a green dot at each unit's resolved weapon
    /// hilt and a red dot at the tip, with a magenta line connecting them.
    /// Use this to visually validate that the WeaponPointResolver lines up
    /// with the visible weapon in the rendered sprite.
    /// </summary>
    private void DrawWeaponAttachDebug()
    {
        _g._debugDraw.EnsurePixel(_g.GraphicsDevice);
        for (int i = 0; i < _g._sim.Units.Count; i++)
        {
            if (!_g._sim.Units[i].Alive) continue;
            uint uid = _g._sim.Units[i].Id;
            if (!_g._unitAnims.TryGetValue(uid, out var animData)) continue;
            var unitDef = _g._gameData.Units.Get(_g._sim.Units[i].UnitDefID);
            if (unitDef == null) continue;

            var attach = ComputeWeaponAttach(i, unitDef, animData);
            if (!attach.Valid) continue;

            var hiltSp = _g._renderer.WorldToScreenPx(attach.HiltWorld, attach.HiltHeight * _g._camera.Zoom, _g._camera);
            var tipSp  = _g._renderer.WorldToScreenPx(attach.TipWorld,  attach.TipHeight  * _g._camera.Zoom, _g._camera);

            // Magenta line / lime hilt / red tip — saturated channels that
            // don't collide with the sprite's natural palette so the dots
            // stay visible against any pose.
            _g._debugDraw.DrawLine(_g._spriteBatch, hiltSp, tipSp, new Color(255, 0, 255, 220));
            DrawDebugDot(hiltSp, new Color(60, 255, 60));
            DrawDebugDot(tipSp,  new Color(255, 40, 40));
        }
    }

    private void DrawDebugDot(Vector2 pos, Color color)
    {
        int r = 3;
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
                if (dx * dx + dy * dy <= r * r)
                    _g.Scope.Draw(_g._pixel, new Rectangle((int)pos.X + dx, (int)pos.Y + dy, 1, 1), color);
    }

    private void DrawWindDebug(int screenW, int screenH)
    {
        if (_g._pixel == null || _g._renderer == null) return;

        // Sample wind at a grid of world positions and draw colored quads
        // Scale cell size with zoom so we never have too many cells on screen
        float cellSize = MathF.Max(2f, 40f / MathF.Max(_g._camera.Zoom, 1f));

        // Get view bounds in world space
        var topLeft = _g._renderer.ScreenToWorld(Vector2.Zero, _g._camera);
        var bottomRight = _g._renderer.ScreenToWorld(new Vector2(screenW, screenH), _g._camera);
        float minX = MathF.Floor(topLeft.X / cellSize) * cellSize;
        float minY = MathF.Floor(topLeft.Y / cellSize) * cellSize;
        float maxX = MathF.Ceiling(bottomRight.X / cellSize) * cellSize;
        float maxY = MathF.Ceiling(bottomRight.Y / cellSize) * cellSize;

        // Safety cap: limit to ~2500 cells max
        int maxCells = 50;
        if ((maxX - minX) / cellSize > maxCells) maxX = minX + maxCells * cellSize;
        if ((maxY - minY) / cellSize > maxCells) maxY = minY + maxCells * cellSize;

        float windAngle = 0f;
        for (float wy = minY; wy < maxY; wy += cellSize)
        {
            for (float wx = minX; wx < maxX; wx += cellSize)
            {
                float gust = EnvironmentSystem.SampleWind(wx, wy, _g._gameTime, out windAngle);
                if (gust < 0.01f) continue; // skip fully still cells

                var sp = _g._renderer.WorldToScreen(new Vec2(wx, wy), 0f, _g._camera);
                float halfPx = cellSize * _g._camera.Zoom * 0.5f;
                int px = (int)(sp.X - halfPx);
                int py = (int)(sp.Y - halfPx * _g._camera.YRatio);
                int pw = (int)(cellSize * _g._camera.Zoom);
                int ph = (int)(cellSize * _g._camera.Zoom * _g._camera.YRatio);

                // Color: blue(still) → yellow → white(peak)
                byte r = (byte)(55 + (int)(200 * gust));
                byte g = (byte)(55 + (int)(200 * gust));
                byte b = (byte)(55 + (int)(80 * (1f - gust)));
                byte a = (byte)(40 + (int)(80 * gust));
                _g.Scope.Draw(_g._pixel, new Rectangle(px, py, pw, ph), new Color(r, g, b, a));
            }
        }

        // Direction arrow in top-left corner
        float arrowLen = 40f;
        float arrowX = 60f;
        float arrowY = 60f;

        // Arrow body
        float adx = MathF.Cos(windAngle) * arrowLen;
        float ady = MathF.Sin(windAngle) * arrowLen;
        DrawDebugLine(new Vector2(arrowX - adx * 0.5f, arrowY - ady * 0.5f),
                      new Vector2(arrowX + adx * 0.5f, arrowY + ady * 0.5f), Color.White);
        // Arrowhead
        float headAngle1 = windAngle + 2.5f;
        float headAngle2 = windAngle - 2.5f;
        float headLen = 12f;
        var tip = new Vector2(arrowX + adx * 0.5f, arrowY + ady * 0.5f);
        DrawDebugLine(tip, tip + new Vector2(MathF.Cos(headAngle1) * headLen, MathF.Sin(headAngle1) * headLen), Color.White);
        DrawDebugLine(tip, tip + new Vector2(MathF.Cos(headAngle2) * headLen, MathF.Sin(headAngle2) * headLen), Color.White);

        // Background circle for arrow
        _g.Scope.Draw(_g._pixel, new Rectangle((int)arrowX - 45, (int)arrowY - 45, 90, 90), new Color(0, 0, 0, 120));

        // Label
        if (_g._smallFont != null)
        {
            float angleDeg = windAngle * 180f / MathF.PI;
            _g.Scope.DrawString(_g._smallFont, $"Wind {angleDeg:F0} deg",
                new Vector2(16, 108), Color.White);
            _g.Scope.DrawString(_g._smallFont, "F6: Wind Debug",
                new Vector2(16, 122), new Color(180, 180, 180));
        }
    }

    private void DrawDebugLine(Vector2 a, Vector2 b, Color color)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f) return;
        float angle = MathF.Atan2(dy, dx);
        _g.Scope.Draw(_g._pixel, a, null, color, angle, Vector2.Zero, new Vector2(len, 1f), SpriteEffects.None, 0f);
    }

    /// <summary>Draw one F7 horde ring (a circle outline) plus a label at its north
    /// edge showing the ring's name + world radius. Concentric rings stack their
    /// labels at different heights so all three stay readable.</summary>
    private void DrawHordeRing(Vec2 center, float radius, Color color, string label, int screenW, int screenH)
    {
        _g._debugDraw.DrawCircle(_g._spriteBatch, _g._renderer, _g._camera, center, radius, color, 48);
        if (_g._smallFont == null || radius < 0.5f) return;
        var top = _g._camera.WorldToScreen(center + new Vec2(0f, -radius), 0f, screenW, screenH);
        var sz = _g._smallFont.MeasureString(label);
        var labelColor = new Color(color.R, color.G, color.B, (byte)255);
        _g.Scope.DrawString(_g._smallFont, label,
            new Vector2((int)(top.X - sz.X / 2f), (int)(top.Y - sz.Y - 1)), labelColor);
    }

    private void DrawHordeDebug()
    {
        _g._debugDraw.EnsurePixel(_g.GraphicsDevice);
        int screenW = _g.GraphicsDevice.Viewport.Width, screenH = _g.GraphicsDevice.Viewport.Height;
        var horde = _g._sim.Horde;
        var settings = horde.Settings;

        // Three horde rings, each labeled with its world radius so they're easy to
        // tell apart (they're concentric, so the labels stack by radius up the north
        // edge). Alpha kept high enough that all three read clearly over the ground.
        //   formation (green) = EffectiveRadius           — where units stand (√N)
        //   engage    (orange)= formation + EngagementOffset — horde grabs enemies here
        //   leash     (red)   = engage + LeashOffset         — chaser force-returned here
        DrawHordeRing(horde.CircleCenter, horde.EffectiveRadius, new Color(110, 230, 110, 200),
            $"formation {horde.EffectiveRadius:F0}", screenW, screenH);
        DrawHordeRing(horde.CircleCenter, horde.EngagementRange, new Color(255, 185, 60, 210),
            $"engage {horde.EngagementRange:F0}", screenW, screenH);
        DrawHordeRing(horde.CircleCenter, horde.LeashRadius, new Color(255, 70, 70, 210),
            $"leash {horde.LeashRadius:F0}", screenW, screenH);

        // Formation facing arrow
        var facingDir = new Vec2(MathF.Cos(horde.CircleFacing), MathF.Sin(horde.CircleFacing));
        var centerSp = _g._camera.WorldToScreen(horde.CircleCenter, 0f, screenW, screenH);
        var facingEnd = _g._camera.WorldToScreen(horde.CircleCenter + facingDir * 3f, 0f, screenW, screenH);
        _g._debugDraw.DrawArrow(_g._spriteBatch, centerSp, facingEnd, new Color(100, 255, 100, 200));

        // Unit slots and connections
        foreach (var unit in horde.HordeUnits)
        {
            int unitIdx = _g._sim.ResolveUnitID(unit.UnitID);
            if (unitIdx < 0 || !_g._sim.Units[unitIdx].Alive) continue;

            var unitPos = _g._sim.Units[unitIdx].Position;
            var unitSp = _g._camera.WorldToScreen(unitPos, 0f, screenW, screenH);

            // A commanded minion (Routine 4 = RoutineCommanded) is attack-moving to a
            // player-issued point, not its formation slot. Draw the line to where it was
            // actually commanded to go (Unit.MoveTarget) in a distinct magenta, with an
            // arrowhead pointing at the order point — this is the order, not the slot it
            // happens to own. (HordeUnitState has no Commanded value; the formation slot
            // it would otherwise occupy is stale while under orders, so don't draw it.)
            bool commanded = _g._sim.Units[unitIdx].Routine == 4; // RoutineCommanded
            if (commanded)
            {
                var cmdTarget = _g._sim.Units[unitIdx].MoveTarget;
                var cmdSp = _g._camera.WorldToScreen(cmdTarget, 0f, screenW, screenH);
                var cmdCol = new Color(255, 90, 235, 210);
                // Command-point marker (larger cross to distinguish from slot crosses)
                _g.Scope.Draw(_g._pixel, new Rectangle((int)cmdSp.X - 4, (int)cmdSp.Y, 9, 1), cmdCol);
                _g.Scope.Draw(_g._pixel, new Rectangle((int)cmdSp.X, (int)cmdSp.Y - 4, 1, 9), cmdCol);
                _g._debugDraw.DrawArrow(_g._spriteBatch, unitSp, cmdSp, cmdCol);
            }
            // Line to slot target (formation followers only)
            else if (horde.GetTargetPosition(unit.UnitID, out Vec2 slotPos))
            {
                var slotSp = _g._camera.WorldToScreen(slotPos, 0f, screenW, screenH);
                // Slot marker (small cross)
                _g.Scope.Draw(_g._pixel, new Rectangle((int)slotSp.X - 3, (int)slotSp.Y, 7, 1), new Color(100, 200, 100, 150));
                _g.Scope.Draw(_g._pixel, new Rectangle((int)slotSp.X, (int)slotSp.Y - 3, 1, 7), new Color(100, 200, 100, 150));
                // Line from unit to slot
                Color lineCol = unit.State switch
                {
                    HordeUnitState.Following => new Color(100, 200, 100, 100),
                    HordeUnitState.Engaged => new Color(255, 80, 80, 150),
                    HordeUnitState.Chasing => new Color(255, 200, 80, 150),
                    HordeUnitState.Returning => new Color(80, 150, 255, 150),
                    _ => new Color(150, 150, 150, 100)
                };
                _g._debugDraw.DrawLine(_g._spriteBatch, unitSp, slotSp, lineCol);
            }

            // State label — show "Commanded" for units under orders (HordeUnitState is
            // stale for them, since the command path sets Unit.Routine directly).
            if (_g._smallFont != null)
            {
                string stateLabel = commanded ? "Commanded" : unit.State.ToString();
                _g.Scope.DrawString(_g._smallFont, stateLabel,
                    new Vector2(unitSp.X + 8, unitSp.Y - 16), new Color(200, 200, 200, 200));
            }
        }

        // Mode label
        if (_g._smallFont != null)
            _g.Scope.DrawString(_g._smallFont, "[F7] Debug: Horde",
                new Vector2(10, 26), new Color(100, 255, 100, 200));
    }

    private void DrawUnitInfoDebug()
    {
        _g._debugDraw.EnsurePixel(_g.GraphicsDevice);
        int screenW = _g.GraphicsDevice.Viewport.Width, screenH = _g.GraphicsDevice.Viewport.Height;

        for (int i = 0; i < _g._sim.Units.Count; i++)
        {
            if (!_g._sim.Units[i].Alive) continue;

            var pos = _g._sim.Units[i].Position;
            var sp = _g._camera.WorldToScreen(pos, 0f, screenW, screenH);
            float speed = _g._sim.Units[i].Velocity.Length();
            float maxSpeed = _g._sim.Units[i].MaxSpeed;

            // Line to target
            var target = _g._sim.Units[i].Target;
            if (target.IsUnit)
            {
                int tIdx = _g._sim.ResolveUnitID(target.UnitID);
                if (tIdx >= 0 && _g._sim.Units[tIdx].Alive)
                {
                    var tSp = _g._camera.WorldToScreen(_g._sim.Units[tIdx].Position, 0f, screenW, screenH);
                    _g._debugDraw.DrawLine(_g._spriteBatch, sp, tSp, new Color(255, 100, 100, 100));
                }
            }

            // Velocity vector
            if (speed > 0.1f)
            {
                var velEnd = _g._camera.WorldToScreen(pos + _g._sim.Units[i].Velocity.Normalized() * 1.5f, 0f, screenW, screenH);
                _g._debugDraw.DrawArrow(_g._spriteBatch, sp, velEnd, new Color(80, 200, 255, 150));
            }

            if (_g._smallFont == null) continue;

            // Get animation state
            string animLabel = "?";
            uint uid = _g._sim.Units[i].Id;
            if (_g._unitAnims.TryGetValue(uid, out var animData))
                animLabel = animData.Ctrl.CurrentState.ToString();

            string aiLabel = "?";
            var aiArchetypeId = _g._sim.Units[i].Archetype;
            aiLabel = AI.ArchetypeRegistry.GetName(aiArchetypeId);
            var aiArchetype = AI.ArchetypeRegistry.Get(aiArchetypeId);

            if (aiArchetype != null)
            {
                string routineLabel = aiArchetype.GetRoutineName(_g._sim.Units[i].Routine);
                aiLabel = $"{aiLabel} - {routineLabel}";
            }

            // Build the info string. Base line always shows AI + velocity + anim +
            // max speed. Extra lines appear only when the corresponding state is
            // interesting (non-default) — keeps idle units uncluttered while making
            // a unit in a weird state (stuck in Knockdown, dangling OverrideAnim,
            // lost PendingAttack, etc.) obvious at a glance.
            var sb = new System.Text.StringBuilder();
            sb.Append(aiLabel).Append('\n');
            // eff + ms are the two values SetEffort writes (MoveEffort + the derived
            // MaxSpeed cap) — show both so slow-stroll routines (e.g. Walk×0.5) are
            // legible at a glance.
            sb.Append($"v:{speed:F1} {animLabel} eff:{_g._sim.Units[i].MoveEffort} ms:{maxSpeed:F1}");

            var ov = _g._sim.Units[i].OverrideAnim;
            if (ov.IsActive)
            {
                string dur = ov.Duration < 0 ? "loop" : (ov.Duration == 0 ? "once" : $"{ov.Duration:F1}s");
                sb.Append($"\nov:{ov.State} p{ov.Priority} {dur}");
                if (_g._sim.Units[i].OverrideStarted) sb.Append(" *");
            }

            var inc = _g._sim.Units[i].Incap;
            if (inc.Active || inc.Recovering)
            {
                sb.Append($"\nincap:{(inc.Active ? "A" : "")}{(inc.Recovering ? "R" : "")}");
                sb.Append($" hold={inc.HoldAnim} rec={inc.RecoverAnim}");
                if (inc.RecoverTimer != 0f) sb.Append($" t={inc.RecoverTimer:F2}");
                if (inc.HoldAtEnd) sb.Append(" @end");
            }

            if (!_g._sim.Units[i].PendingAttack.IsNone)
            {
                uint tgtId = _g._sim.Units[i].PendingAttack.IsUnit ? _g._sim.Units[i].PendingAttack.UnitID : 0;
                // ASCII-only: the small SpriteFont is authored with a limited glyph
                // range; any char outside that range throws ArgumentException on
                // DrawString and crashes the game (non-ASCII arrow was the bug).
                sb.Append($"\npend:>{tgtId} w{_g._sim.Units[i].PendingWeaponIdx}");
                if (_g._sim.Units[i].PendingWeaponIsRanged) sb.Append(" R");
                if (_g._sim.Units[i].CurrentAttackLungeDist > 0f)
                    sb.Append($" lunge={_g._sim.Units[i].CurrentAttackLungeDist:F2}");
            }

            if (_g._sim.Units[i].JumpPhase != 0)
            {
                string phaseName = _g._sim.Units[i].JumpPhase switch
                {
                    1 => "Takeoff", 2 => "Airborne", 3 => "Landing", 4 => "Recovery",
                    _ => $"P{_g._sim.Units[i].JumpPhase}"
                };
                sb.Append($"\njump:{phaseName} Z={_g._sim.Units[i].Z:F2}");
            }

            float rox = _g._sim.Units[i].RenderOffset.X, roy = _g._sim.Units[i].RenderOffset.Y;
            if (rox * rox + roy * roy > 0.0001f)
                sb.Append($"\nrenderOff:({rox:F2},{roy:F2})");

            if (_g._sim.Units[i].InCombat || _g._sim.Units[i].AttackCooldown > 0f
                || _g._sim.Units[i].PostAttackTimer > 0f)
            {
                sb.Append("\ncd:");
                if (_g._sim.Units[i].InCombat) sb.Append("InCombat ");
                if (_g._sim.Units[i].AttackCooldown > 0f) sb.Append($"a{_g._sim.Units[i].AttackCooldown:F1} ");
                if (_g._sim.Units[i].PostAttackTimer > 0f) sb.Append($"p{_g._sim.Units[i].PostAttackTimer:F1}");
            }

            string info = sb.ToString();
            // Approximate width for anchoring — SpriteFont.MeasureString is accurate
            // but per-unit MeasureString every frame is hot-path waste.
            var textPos = new Vector2(sp.X - info.Length, sp.Y - 28);
            _g.Scope.DrawString(_g._smallFont, info, textPos, new Color(255, 255, 200, 220));
        }

        // Mode label
        if (_g._smallFont != null)
            _g.Scope.DrawString(_g._smallFont, "[F7] Debug: Unit Info",
                new Vector2(10, 26), new Color(80, 200, 255, 200));
    }

    private static string GetRoutineName(byte archetype, byte routine) => archetype switch
    {
        AI.ArchetypeRegistry.WolfPack => routine switch { 0 => "IdleRoam", 1 => "Sleep", 2 => "Fight", _ => $"R{routine}" },
        AI.ArchetypeRegistry.DeerHerd => routine switch { 0 => "IdleRoam", 1 => "Sleep", 2 => "Alert", 3 => "Flee", 4 => "Calm", 5 => "FightBack", _ => $"R{routine}" },
        AI.ArchetypeRegistry.HordeMinion => routine switch { 0 => "Follow", 1 => "Chase", 2 => "Engage", 3 => "Return", _ => $"R{routine}" },
        AI.ArchetypeRegistry.PatrolSoldier or AI.ArchetypeRegistry.GuardStationary or AI.ArchetypeRegistry.ArmyUnit =>
            routine switch { 0 => "Idle", 1 => "Alert", 2 => "Combat", 3 => "Return", _ => $"R{routine}" },
        AI.ArchetypeRegistry.ArcherUnit or AI.ArchetypeRegistry.CasterUnit =>
            routine switch { 0 => "Idle", 1 => "Alert", 2 => "Combat", 3 => "Return", _ => $"R{routine}" },
        _ => $"R{routine}"
    };

    private static string GetSubroutineName(byte archetype, byte routine, byte sub) => archetype switch
    {
        AI.ArchetypeRegistry.WolfPack when routine == 2 => sub switch { 0 => "Approach", 1 => "Strike", 2 => "Disengage", 3 => "Cooldown", _ => $"S{sub}" },
        AI.ArchetypeRegistry.WolfPack => sub switch { 0 => "Walk", 1 => "Idle", _ => $"S{sub}" },
        AI.ArchetypeRegistry.DeerHerd when routine == 5 => sub switch { 0 => "Chase", 1 => "Attack", _ => $"S{sub}" },
        AI.ArchetypeRegistry.DeerHerd => sub switch { 0 => "Walk", 1 => "Idle", _ => $"S{sub}" },
        AI.ArchetypeRegistry.HordeMinion => $"S{sub}",
        AI.ArchetypeRegistry.PatrolSoldier when routine == 0 => sub switch { 0 => "Walking", 1 => "Waiting", _ => $"S{sub}" },
        _ when routine == 2 => sub switch { 0 => "Chase", 1 => "Attack", _ => $"S{sub}" },
        _ => sub switch { 0 => "Walk", 1 => "Idle", _ => $"S{sub}" },
    };

    /// <summary>Debug readout (Settings → Tooltips → "Show world position info"):
    /// when the cursor is over the world (not UI, no menu open), dump a block of
    /// text about the exact world position under the cursor in the bottom-left
    /// corner, anchored bottom-left (the block grows upward from the bottom).
    /// First/foremost line is the death-fog density at that position. Add more
    /// lines here as more per-position data becomes worth surfacing. Drawn into
    /// the HUD's already-open SpriteBatch (no Begin/End of its own).</summary>
    internal void DrawWorldHoverInfo(int screenW, int screenH)
    {
        if (!_g._gameData.Settings.Tooltips.ShowWorldHoverDebug) return;
        if (_g._smallFont == null) return;
        // Only when actually hovering the world: no UI element under the cursor and
        // no full-screen menu open. The map editor is allowed — hover-inspect works
        // there, and HoverBlockedByUI knows its panel/popup rules.
        if (_g._menuState != MenuState.None && _g._menuState != MenuState.MapEditor) return;
        if (_g.HoverBlockedByUI(screenW, screenH)) return;

        Vec2 mw = _g._camera.ScreenToWorld(_g._input.MousePos, screenW, screenH);

        // Off the map → nothing to report (cursor is over empty void around the world).
        if (_g._groundSystem != null &&
            (mw.X < 0f || mw.Y < 0f || mw.X >= _g._groundSystem.WorldW || mw.Y >= _g._groundSystem.WorldH))
            return;

        _g._deathFog.WorldToCell(mw.X, mw.Y, out int fcx, out int fcy);
        float fog = _g._deathFog.Sample(mw.X, mw.Y);

        // Build the readout. The first data line is the death-fog level; everything
        // below is supporting context. Keep lines short — they're bottom-left and
        // shouldn't crowd the spell bar.
        var lines = new List<(string text, Color color)>
        {
            ("WORLD HOVER",                                       new Color(180, 220, 255)),
            ($"death fog: {fog:F4}",                              new Color(170, 255, 180)),
            ($"pos: ({mw.X:F1}, {mw.Y:F1})",                      new Color(220, 220, 220)),
            ($"fog cell: ({fcx}, {fcy})  cs={_g._deathFog.CellSize}",new Color(180, 180, 185)),
        };

        var f = _g._smallFont!;
        int lineH = f.LineSpacing;
        int pad = 8;
        int gap = 2;

        float maxW = 0f;
        foreach (var (text, _) in lines)
            maxW = MathF.Max(maxW, f.MeasureString(SanitizeAscii(text)).X);

        int blockH = lines.Count * lineH + (lines.Count - 1) * gap;
        int boxW = (int)MathF.Ceiling(maxW) + pad * 2;
        int boxH = blockH + pad * 2;

        int boxX = 8;                       // bottom-left, small margin from the edge
        int boxY = screenH - 8 - boxH;      // bottom-adjusted: anchor to the bottom edge

        // Translucent backing so the text reads over any terrain.
        DrawPanel(new Rectangle(boxX, boxY, boxW, boxH), new Color(12, 14, 18, 205), new Color(70, 110, 150));

        int ty = boxY + pad;
        foreach (var (text, color) in lines)
        {
            _g.Scope.DrawString(f, SanitizeAscii(text),
                new Vector2(boxX + pad, ty), color);
            ty += lineH + gap;
        }
    }

    internal void DrawHUD(int screenW, int screenH)
    {
        _g._hudRenderer.Draw(screenW, screenH, _g._sim, _g._gameData,
            _g._inventory, _g._inventoryUI.IsVisible,
            _g._spellBarState,
            _g._timeScale,
            DrawSpellCategoryIcon, BuildMenuOpenMask(), _g._paused,
            _g._slotFlash, BuildEditorOpenMask(),
            // Top-right button rows are drawn by their own HudTop-band router
            // layers so they sit ABOVE panels/overlays, matching where they
            // take input.
            drawTopRows: false);
    }

    /// <summary>Cursor tooltips at their z-position (Tooltip band, topmost) —
    /// see <see cref="Necroking.UI.CursorTooltipLayer"/>.</summary>
    internal void DrawHudTooltips(int screenW, int screenH)
    {
        _g._hudRenderer.DrawCursorTooltips(screenW, screenH, _g._sim, _g._gameData,
            _g._inventory, _g._hoveredObjectIdx, _g._envSystem,
            _g._hoveredBellyUnitId, _g._hoveredCorpseIdx, _g._hoveredUnitIdx,
            _g._menuState == MenuState.MapEditor);
    }

    /// <summary>Draw the core-menu button row at its z-position (HudTop band).
    /// Pass a parked-off-screen cursor to suppress the hover highlight when the
    /// row isn't the hover owner.</summary>
    internal void DrawMenuButtonsRow(int screenW, int mx, int my)
        => _g._hudRenderer.DrawMenuButtons(screenW, BuildMenuOpenMask(), mx, my);

    /// <summary>Editor-launcher row, same contract as <see cref="DrawMenuButtonsRow"/>.</summary>
    internal void DrawEditorButtonsRow(int screenW, int mx, int my)
        => _g._hudRenderer.DrawEditorButtons(screenW, BuildEditorOpenMask(), mx, my);

    /// <summary>Bitmask of which editor is open, by HUDRenderer.Editor* index, for
    /// highlighting the editor-launcher row.</summary>
    private int BuildEditorOpenMask()
    {
        return _g._menuState switch
        {
            MenuState.UnitEditor  => 1 << HUDRenderer.EditorUnit,
            MenuState.SpellEditor => 1 << HUDRenderer.EditorSpell,
            MenuState.MapEditor   => 1 << HUDRenderer.EditorMap,
            MenuState.UIEditor    => 1 << HUDRenderer.EditorUi,
            _ => 0,
        };
    }

    /// <summary>Bitmask of which core menus are open, by HUDRenderer.Menu* index,
    /// for highlighting the top-right menu buttons.</summary>
    internal int BuildMenuOpenMask()
    {
        int m = 0;
        if (_g._inventoryUI.IsVisible)     m |= 1 << HUDRenderer.MenuInventory;
        if (_g._craftingMenu.IsVisible)    m |= 1 << HUDRenderer.MenuCrafting;
        if (_g._buildingMenuUI.IsVisible)  m |= 1 << HUDRenderer.MenuBuilding;
        if (_g._grimoireOverlay.IsVisible) m |= 1 << HUDRenderer.MenuGrimoire;
        if (_g._skillBookOverlay.IsVisible) m |= 1 << HUDRenderer.MenuSkills;
        if (_g._characterStatsUI.IsVisible) m |= 1 << HUDRenderer.MenuCharacter;
        return m;
    }

    /// <summary>Toggle a core menu by its HUDRenderer.Menu* index — the click-side
    /// mirror of the keyboard shortcuts (I/C/B/J/K/Tab), including the per-side
    /// panel exclusivity (Game1.CloseSameSidePanels).</summary>
    internal void ToggleCoreMenu(int idx, int screenW, int screenH)
    {
        _g.EnsureInventoryUIsInitialized();
        switch (idx)
        {
            case HUDRenderer.MenuInventory:
                _g._inventoryUI.Toggle(screenW, screenH);
                break;
            case HUDRenderer.MenuCrafting:
                if (!_g._craftingMenu.IsVisible) _g.CloseSameSidePanels(Game1.PanelSide.Left, _g._craftingMenu);
                _g._craftingMenu.Toggle(screenW, screenH);
                break;
            case HUDRenderer.MenuBuilding:
                if (!_g._buildingMenuUI.IsVisible) _g.CloseSameSidePanels(Game1.PanelSide.Left, _g._buildingMenuUI);
                _g._buildingMenuUI.Toggle(screenW, screenH);
                break;
            case HUDRenderer.MenuGrimoire:
                if (!_g._grimoireOverlay.IsVisible) _g.CloseSameSidePanels(Game1.PanelSide.Left, _g._grimoireOverlay);
                _g._grimoireOverlay.Toggle();
                break;
            case HUDRenderer.MenuSkills:
                _g._skillBookOverlay.Toggle();
                break;
            case HUDRenderer.MenuCharacter:
                if (!_g._characterStatsUI.IsVisible) _g.CloseSameSidePanels(Game1.PanelSide.Left, _g._characterStatsUI);
                _g._characterStatsUI.Toggle();
                break;
        }
    }

    /// <summary>Draw the aggression bar (Shift+Q/Shift+E controlled) centered just
    /// above the spell bar, with a warm token on the active level's node.
    /// The bar size AND the node positions are read live from the widget def, so the
    /// token tracks any resize / re-spacing of the bar in the UI editor. Runs INSIDE
    /// the open HUD batch: DrawWidget draws into it, and UIShaders.DrawCircle End()s
    /// it, draws its own Immediate quad, then re-Begin()s it — so the batch MUST be
    /// open when this is called (DrawCircle throws on a closed batch).</summary>
    internal void DrawAggressionBar(int screenW, int screenH)
    {
        if (!GetAggressionBarLayout(screenW, screenH, out var bar, out var nodes)) return;

        _g._widgetRenderer.DrawWidget("AggressionBar", bar.X, bar.Y);

        // Token: the dot lands on the active node no matter how the bar is sized or
        // spaced (layout read live, see GetAggressionBarLayout). DrawCircle briefly
        // suspends the open HUD batch (End → Immediate quad → Begin).
        int level = Math.Clamp(_g._sim.Horde.AggressionLevel, 0, nodes.Count - 1);
        var nr = nodes[level];
        var center = new Vector2(nr.X + nr.Width / 2f, nr.Y + nr.Height / 2f);
        float radius = MathF.Max(4f, nr.Width * 0.32f); // token scales with the node
        var fill = new Color(255, 196, 64); // vivid gold accent
        _g._uiShaders.DrawCircle(_g._spriteBatch, center, radius, radius * 1.7f, fill, fill, new Color(255, 196, 64, 120));

        DrawAggressionTooltip(screenW, screenH, bar, nodes);
    }

    /// <summary>Screen-space layout of the aggression bar: the bar rect and each
    /// level node's rect (index 0 = leftmost = least aggressive), read live from the
    /// widget def so input hit-testing and drawing share one source of truth.
    /// Returns false when the bar is hidden (a menu is open) or the def is missing.</summary>
    internal bool GetAggressionBarLayout(int screenW, int screenH,
        out Rectangle bar, out System.Collections.Generic.List<Rectangle> nodes)
    {
        bar = default;
        nodes = null!;
        if (_g._menuState != MenuState.None) return false;

        var barDef = _g._widgetRenderer.GetWidgetDef("AggressionBar");
        if (barDef == null) return false;

        var barLayout = _g._hudRenderer.GetSpellBarLayout(screenH);
        int x = (screenW - barDef.Width) / 2;
        int y = barLayout.barY - barDef.Height - 6; // sit just above the spell bar
        bar = new Rectangle(x, y, barDef.Width, barDef.Height);

        nodes = new System.Collections.Generic.List<Rectangle>();
        foreach (var c in barDef.Children.Where(c => c.Widget == "CircularToggle").OrderBy(c => c.X))
            nodes.Add(new Rectangle(x + c.X, y + c.Y, c.Width, c.Height));
        return nodes.Count > 0;
    }

    /// <summary>Index of the node whose center is closest (by X) to the cursor —
    /// lets a click anywhere along the bar snap to the nearest level.</summary>
    internal static int NearestAggroNode(System.Collections.Generic.List<Rectangle> nodes, int mx)
    {
        int best = 0, bestD = int.MaxValue;
        for (int i = 0; i < nodes.Count; i++)
        {
            int d = Math.Abs(nodes[i].Center.X - mx);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>Hover tooltip for the aggression bar: names the level the cursor is
    /// over (the one a click would select) and what it does. Called right after the
    /// token's DrawCircle, which leaves a SpriteBatch open — so we draw into that
    /// active batch rather than starting our own (a nested Begin would throw).</summary>
    private void DrawAggressionTooltip(int screenW, int screenH,
        Rectangle bar, System.Collections.Generic.List<Rectangle> nodes)
    {
        if (_g._smallFont == null) return;

        var hover = bar;
        hover.Inflate(0, 8); // a little vertical slack makes the thin bar easier to hit
        int mx = (int)_g._input.MousePos.X, my = (int)_g._input.MousePos.Y;
        if (!hover.Contains(mx, my)) return;

        int idx = Math.Clamp(NearestAggroNode(nodes, mx), 0, AggroNames.Length - 1);
        string title = $"Aggression: {AggroNames[idx]}";
        string desc = AggroDescs[idx];
        string hint = "Click to set  -  Shift+Q / Shift+E";

        int lineH = _g._smallFont.LineSpacing;
        int pad = 8;
        int innerW = (int)MathF.Ceiling(MathF.Max(_g._smallFont.MeasureString(title).X,
            MathF.Max(_g._smallFont.MeasureString(desc).X, _g._smallFont.MeasureString(hint).X)));
        int w = innerW + pad * 2;
        int h = pad * 2 + lineH * 3 + 4;

        int tx = bar.X + (bar.Width - w) / 2;
        int ty = bar.Y - h - 6;
        tx = Math.Clamp(tx, 4, Math.Max(4, screenW - w - 4));
        if (ty < 4) ty = bar.Bottom + 6; // flip below if it would clip off the top

        DrawPanel(new Rectangle(tx, ty, w, h), new Color(20, 16, 12, 235), new Color(120, 95, 60));
        int cy = ty + pad;
        DrawText(_g._smallFont, title, new Vector2(tx + pad, cy), new Color(255, 210, 130));
        cy += lineH;
        DrawText(_g._smallFont, desc, new Vector2(tx + pad, cy), new Color(210, 200, 185));
        cy += lineH + 4;
        DrawText(_g._smallFont, hint, new Vector2(tx + pad, cy), new Color(140, 130, 115));
    }

    internal void DrawPauseMenu(int screenW, int screenH)
    {
        if (_g._gameData.Settings.General.PauseDimBackground)
            _g.Scope.Draw(_g._pixel, new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 150));

        int boxW = 350;
        int btnCount = 10; // Resume + 5 editors + Settings + Multiplayer + Main Menu + Quit (hit-tests: Game1.cs pause-menu click block — keep in lockstep!)
        int btnH2 = 40, btnGap2 = 10;
        int controlLines = 4;
        int boxH = 60 + btnCount * (btnH2 + btnGap2) + 10 + controlLines * 16 + 20;
        int boxX = (screenW - boxW) / 2;
        int boxY = (screenH - boxH) / 2;
        DrawPanel(new Rectangle(boxX, boxY, boxW, boxH), new Color(30, 30, 50, 235), new Color(100, 100, 180), 3);

        if (_g._largeFont != null)
        {
            string title = "PAUSED";
            var titleSize = _g._largeFont.MeasureString(title);
            DrawText(_g._largeFont, title, new Vector2(boxX + boxW / 2f - titleSize.X / 2f, boxY + 15), Color.White);
        }

        // Menu items
        int btnW = 280, btnH = 40, btnGap = 10;
        int menuX = boxX + (boxW - btnW) / 2;
        int menuY = boxY + 60;

        DrawMenuButton("Resume", menuX, ref menuY, btnW, btnH, btnGap);
        DrawMenuButton("Unit Editor (F9)", menuX, ref menuY, btnW, btnH, btnGap);
        DrawMenuButton("Spell Editor (F10)", menuX, ref menuY, btnW, btnH, btnGap);
        DrawMenuButton("Map Editor (F11)", menuX, ref menuY, btnW, btnH, btnGap);
        DrawMenuButton("UI Editor (F12)", menuX, ref menuY, btnW, btnH, btnGap);
        DrawMenuButton("Item Editor", menuX, ref menuY, btnW, btnH, btnGap);
        DrawMenuButton("Settings", menuX, ref menuY, btnW, btnH, btnGap);
        DrawMenuButton("Multiplayer", menuX, ref menuY, btnW, btnH, btnGap);

        menuY += 10;
        DrawMenuButton("Main Menu", menuX, ref menuY, btnW, btnH, btnGap);
        DrawMenuButton("Quit", menuX, ref menuY, btnW, btnH, btnGap);

        // Controls reference
        if (_g._smallFont != null)
        {
            string[] controls = {
                "WASD - Move     Space - Jump",
                "Q/E/1-8 - Cast spells",
                "Shift - Run    G - Ghost mode",
                "+/- - Speed   Scroll - Zoom"
            };
            for (int i = 0; i < controls.Length; i++)
                DrawText(_g._smallFont, controls[i], new Vector2(boxX + 20, menuY + 10 + i * 16), new Color(140, 140, 160));
        }
    }

    private void DrawMainMenu(int screenW, int screenH)
    {
        DrawMenuBackdrop(screenW, screenH);

        // Title
        if (_g._largeFont != null)
        {
            string title = "NECROKING";
            var titleSize = _g._largeFont.MeasureString(title);
            int titleY = screenH / 5;
            // Shadow
            DrawText(_g._largeFont, title, new Vector2(screenW / 2f - titleSize.X / 2f + 3, titleY + 3), new Color(0, 0, 0, 180));
            DrawText(_g._largeFont, title, new Vector2(screenW / 2f - titleSize.X / 2f, titleY), new Color(220, 180, 100));

            string subtitle = "Rise of the Undead";
            var subSize = _g._font?.MeasureString(subtitle) ?? Vector2.Zero;
            DrawText(_g._font, subtitle, new Vector2(screenW / 2f - subSize.X / 2f, titleY + 30), new Color(180, 160, 120, 200));
        }

        // Menu buttons
        int btnW = 320, btnH = 55, btnGap = 18;
        int menuX = screenW / 2 - btnW / 2;
        int menuY = screenH / 2 + 20;

        DrawMenuButton("Play", menuX, ref menuY, btnW, btnH, btnGap);
        DrawMenuButton("Play Test Map", menuX, ref menuY, btnW, btnH, btnGap);
        DrawMenuButton("Scenarios", menuX, ref menuY, btnW, btnH, btnGap);
        DrawMenuButton("Quit", menuX, ref menuY, btnW, btnH, btnGap);

        // Version info
        DrawText(_g._smallFont, "MonoGame Port v0.1", new Vector2(10, screenH - 20), new Color(80, 80, 100));
    }

    private void DrawScenarioList(int screenW, int screenH)
    {
        DrawMenuBackdrop(screenW, screenH);

        // Title
        if (_g._largeFont != null)
        {
            string title = "SCENARIOS";
            var titleSize = _g._largeFont.MeasureString(title);
            int titleY = screenH / 6;
            DrawText(_g._largeFont, title, new Vector2(screenW / 2f - titleSize.X / 2f + 3, titleY + 3), new Color(0, 0, 0, 180));
            DrawText(_g._largeFont, title, new Vector2(screenW / 2f - titleSize.X / 2f, titleY), new Color(180, 220, 100));
        }

        if (_g._font != null)
        {
            string subtitle = "Select a scenario to run";
            var subSize = _g._font.MeasureString(subtitle);
            DrawText(_g._font, subtitle, new Vector2(screenW / 2f - subSize.X / 2f, screenH / 6 + 35), new Color(140, 140, 160));
        }

        // Scenario buttons (5-wide grid)
        GetScenarioGridLayout(screenW, screenH, out int cols, out int btnW, out int btnH, out int btnGap, out int gridX, out int menuY, out int rowsVisible);

        var names = new List<string>(ScenarioRegistry.GetNames());
        names.Reverse(); // Newest first
        int visibleCount = Math.Min(names.Count - _g._scenarioScrollOffset, rowsVisible * cols);
        for (int i = 0; i < visibleCount; i++)
        {
            int nameIdx = i + _g._scenarioScrollOffset;
            if (nameIdx >= names.Count) break;
            int col = i % cols, row = i / cols;
            int bx = gridX + col * (btnW + btnGap);
            int by = menuY + row * (btnH + btnGap);
            DrawMenuButtonAt(names[nameIdx], bx, by, btnW, btnH);
        }

        // Back button (centered below the grid)
        int usedRows = (visibleCount + cols - 1) / cols;
        int backY = menuY + usedRows * (btnH + btnGap) + 10;
        int backW = 320;
        DrawMenuButtonAt("< Back", screenW / 2 - backW / 2, backY, backW, btnH);

        // Scroll hint
        if (names.Count > visibleCount + _g._scenarioScrollOffset)
            DrawText(_g._smallFont, "Scroll for more...", new Vector2(screenW / 2f - 50, screenH - 40), new Color(100, 100, 120));
    }

    // Shared layout for the scenario grid so click-handling and drawing stay in sync.
    internal void GetScenarioGridLayout(int screenW, int screenH, out int cols, out int btnW, out int btnH, out int btnGap, out int gridX, out int menuY, out int rowsVisible)
    {
        cols = 5;
        btnGap = 12;
        btnH = 45;
        menuY = screenH / 4 + 60;
        int maxGridW = Math.Min(screenW - 80, 1400);
        btnW = (maxGridW - (cols - 1) * btnGap) / cols;
        int gridW = cols * btnW + (cols - 1) * btnGap;
        gridX = screenW / 2 - gridW / 2;
        // Leave room at the bottom for the back button + scroll hint.
        rowsVisible = Math.Max(1, (screenH - menuY - 110) / (btnH + btnGap));
    }

    // Draws a menu button at an absolute position (grid-friendly; no advancing cursor).
    private void DrawMenuButtonAt(string text, int x, int y, int w, int h)
    {
        int mx = (int)_g._input.MousePos.X, my = (int)_g._input.MousePos.Y;
        bool hover = mx >= x && mx < x + w && my >= y && my < y + h;
        Color bg = hover ? new Color(90, 60, 120, 240) : new Color(60, 40, 80, 220);
        _g.Scope.Draw(_g._pixel, new Rectangle(x, y, w, h), bg);
        _g.Scope.Draw(_g._pixel, new Rectangle(x, y, w, 2), new Color(220, 180, 100, hover ? 255 : 120));
        _g.Scope.Draw(_g._pixel, new Rectangle(x, y + h - 2, w, 2), new Color(220, 180, 100, hover ? 255 : 60));

        if (_g._font != null)
        {
            // Shrink overly long scenario names to fit inside the narrower grid cell.
            var textSize = _g._font.MeasureString(text);
            float scale = textSize.X > w - 12 ? (w - 12) / textSize.X : 1f;
            DrawText(_g._font, text,
                new Vector2((int)(x + w / 2f - textSize.X * scale / 2f), (int)(y + (h - textSize.Y * scale) / 2f)),
                new Color(255, 245, 220), scale);
        }
    }

    private void DrawMenuButton(string text, int x, ref int y, int w, int h, int gap)
    {
        int mx = (int)_g._input.MousePos.X, my = (int)_g._input.MousePos.Y;
        bool hover = mx >= x && mx < x + w && my >= y && my < y + h;
        Color bg = hover ? new Color(90, 60, 120, 240) : new Color(60, 40, 80, 220);
        _g.Scope.Draw(_g._pixel, new Rectangle(x, y, w, h), bg);
        _g.Scope.Draw(_g._pixel, new Rectangle(x, y, w, 2), new Color(220, 180, 100, hover ? 255 : 120));
        _g.Scope.Draw(_g._pixel, new Rectangle(x, y + h - 2, w, 2), new Color(220, 180, 100, hover ? 255 : 60));

        if (_g._font != null)
        {
            var textSize = _g._font.MeasureString(text);
            DrawText(_g._font, text, new Vector2((int)(x + w / 2f - textSize.X / 2f), (int)(y + (h - textSize.Y) / 2f)),
                new Color(255, 245, 220));
        }
        y += h + gap;
    }

    internal void DrawGameOver(int screenW, int screenH)
    {
        _g.Scope.Draw(_g._pixel, new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 160));
        if (_g._largeFont != null)
        {
            string title = "NECROMANCER FALLEN";
            var size = _g._largeFont.MeasureString(title);
            DrawText(_g._largeFont, title, new Vector2(screenW / 2f - size.X / 2f, screenH / 2f - 30), new Color(200, 50, 50));
        }
        if (_g._font != null)
        {
            string sub = "Press R to restart";
            var size = _g._font.MeasureString(sub);
            DrawText(_g._font, sub, new Vector2(screenW / 2f - size.X / 2f, screenH / 2f + 10), new Color(180, 180, 200));
        }
    }

    private void DrawText(SpriteFont? font, string text, Vector2 pos, Color color)
    {
        if (font != null)
            _g.Scope.DrawString(font, text, pos, color);
    }

    private void DrawText(SpriteFont? font, string text, Vector2 pos, Color color, float scale)
    {
        if (font != null)
            _g.Scope.DrawString(font, text, pos, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }

    /// <summary>Draw a filled panel (rectangle) with an optional accent bar at the top (or bottom).
    /// Used for HUD tooltips, pause menu box, and menu buttons.
    /// The accent bar is drawn on top of the fill at the specified height (default 2px).</summary>
    private void DrawPanel(Rectangle r, Color fill, Color accent, int accentH = 2, bool bottomAccent = false)
    {
        _g.Scope.Draw(_g._pixel, r, fill);
        if (bottomAccent)
            _g.Scope.Draw(_g._pixel, new Rectangle(r.X, r.Bottom - accentH, r.Width, accentH), accent);
        else
            _g.Scope.Draw(_g._pixel, new Rectangle(r.X, r.Y, r.Width, accentH), accent);
    }

    /// <summary>Draw the menu background: cover-scale bg image (or fallback fill) + dark overlay for contrast.
    /// Called by DrawMainMenu and DrawScenarioList as their first statement.</summary>
    private void DrawMenuBackdrop(int screenW, int screenH)
    {
        // Background image (scaled to fill, centered)
        if (_g._mainMenuBg != null)
        {
            float bgScale = MathF.Max((float)screenW / _g._mainMenuBg.Width,
                                      (float)screenH / _g._mainMenuBg.Height);
            float bgW = _g._mainMenuBg.Width * bgScale;
            float bgH = _g._mainMenuBg.Height * bgScale;
            _g.Scope.Draw(_g._mainMenuBg,
                new Rectangle((int)((screenW - bgW) * 0.5f), (int)((screenH - bgH) * 0.5f),
                              (int)bgW, (int)bgH),
                Color.White);
        }
        else
        {
            _g.Scope.Draw(_g._pixel, new Rectangle(0, 0, screenW, screenH), new Color(20, 15, 30));
        }
        // Dark overlay for contrast
        _g.Scope.Draw(_g._pixel, new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 120));
    }
}
