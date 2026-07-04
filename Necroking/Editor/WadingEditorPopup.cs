using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Render;
using Necroking.UI;

namespace Necroking.Editor;

/// <summary>
/// Modal popup for tuning a unit's per-orientation wading waterlines.
/// Opened from the unit editor when a unit is selected. Edits the unit's
/// <see cref="UnitDef.WadingFractionByDirection"/> and
/// <see cref="UnitDef.WadingTopFractionByDirection"/> via a drag-the-line
/// interaction over an 8-orientation sprite preview.
///
/// V1 limitations (planned for Phase B follow-up):
///   • Preview uses overlay rectangles (water-color rect occludes the
///     submerged portion of the sprite) rather than the actual wading
///     shader. Looks approximately like in-game but not pixel-exact.
///   • Diagonal cut slope is not yet shown; lines are always horizontal.
///   • Slope itself is not yet editable (auto-computed from sprite angle).
/// </summary>
public class WadingEditorPopup
{
    private readonly EditorBase _ui;
    private bool _isOpen;
    private UnitDef? _targetUnit;
    private GameData _gameData = null!;
    private SpriteAtlas[] _atlases = Array.Empty<SpriteAtlas>();

    // Working copies of the fractions. The user edits these; Apply&Close
    // writes them back to the unit def (and marks the unit editor's
    // unsaved-changes flag). Cancel discards.
    private DirectionalFractions _editBottom = new();
    private DirectionalFractions _editTop = new();
    private bool _editTopActive; // true once the unit has any top-cut values
    private bool _editOverridesDefault; // true = save per-unit; false = unit inherits defaults

    // Working copy at popup-open time, for diffing on Apply (so we know
    // whether to clear vs assign WadingFractionByDirection).
    private bool _openedWithBottomOverride;
    private bool _openedWithTopOverride;

    // --- Orientation tabs ---
    // Internal sector indices match DirectionalFractions.SectorLabels:
    // 0=E, 1=SE, 2=S, 3=SW, 4=W, 5=NW, 6=N, 7=NE.
    private int _selectedSector = 2; // start on S (head-on view)

    // --- Drag state ---
    private enum DragTarget { None, Bottom, Top }
    private DragTarget _dragging = DragTarget.None;
    private float _dragGrabOffsetPx;

    /// <summary>Input layer this popup runs at. Higher than dropdowns (1)
    /// and the existing sub-editors (2) so dragging interactions inside
    /// the popup aren't blocked by anything underneath, while everything
    /// underneath sees IsInputBlocked(0..2) = true and ignores clicks.</summary>
    private const int PopupInputLayer = 3;

    /// <summary>Modal-stack entry — pushed onto Game1.Popups when the
    /// popup opens so ESC routes to Close() and outside clicks would
    /// be intercepted if we wanted (we leave LightDismiss=false so the
    /// user has to explicitly Apply or Cancel).</summary>
    public readonly ActionModalLayer ModalLayer = new() { LightDismiss = false };

    // --- Preview state ---
    private AnimController _previewAnim = new();
    private float _previewWalkFrameTime = 0f; // ms — held static at one walk frame
    private UnitSpriteData? _lastSpriteData;

    /// <summary>Game1 hands these in via SetShaderContext after the wading
    /// shader is loaded at startup. If <see cref="_wadingEffect"/> is null
    /// (asset missing / pre-load) the preview falls back to the no-shader
    /// overlay-rect approach so the editor still renders something useful.</summary>
    private Microsoft.Xna.Framework.Graphics.Effect? _wadingEffect;
    private float _cameraYRatio = 0.6f;

    public void SetShaderContext(Microsoft.Xna.Framework.Graphics.Effect? wadingEffect, float cameraYRatio)
    {
        _wadingEffect = wadingEffect;
        _cameraYRatio = cameraYRatio > 0f ? cameraYRatio : 0.6f;
    }

    public bool IsOpen => _isOpen;

    /// <summary>Returns true when the popup just consumed input on this
    /// frame, so the parent editor knows to skip its own input handling.</summary>
    public bool ConsumedInput { get; private set; }

    /// <summary>Set true by Apply&Close so the unit editor can update its
    /// dirty flag and re-sync caches.</summary>
    public bool HasUnsavedChanges { get; private set; }

    public WadingEditorPopup(EditorBase ui)
    {
        _ui = ui;
        // ESC and other modal-cancel paths route here. Acts as Cancel:
        // discards in-flight edits.
        ModalLayer.OnCancelAction = Close;
    }

    public void Open(UnitDef def, GameData gameData, SpriteAtlas[] atlases)
    {
        _targetUnit = def;
        _gameData = gameData;
        _atlases = atlases;
        _isOpen = true;
        _dragging = DragTarget.None;
        HasUnsavedChanges = false;

        // Load current values into working copies. A unit's
        // WadingFractionByDirection / WadingTopFractionByDirection being
        // null means it inherits the defaults — we copy from the
        // current defaults so the editor shows what's actually rendered.
        _openedWithBottomOverride = def.WadingFractionByDirection != null;
        _openedWithTopOverride = def.WadingTopFractionByDirection != null;
        _editOverridesDefault = _openedWithBottomOverride || _openedWithTopOverride;

        _editBottom = new DirectionalFractions();
        _editBottom.CopyFrom(def.WadingFractionByDirection ?? WadingDefaults.QuadrupedBottom);
        _editBottom.EnsureDiagonalsBackfilled();

        _editTop = new DirectionalFractions();
        _editTop.CopyFrom(def.WadingTopFractionByDirection ?? WadingDefaults.QuadrupedTop);
        _editTop.EnsureDiagonalsBackfilled();
        _editTopActive = HasAnyNonZero(_editTop);

        // Force-init preview anim controller for this unit's sprite data
        // (re-resolves frames against the unit's atlas).
        _lastSpriteData = null;
    }

    public void Close()
    {
        _isOpen = false;
        _targetUnit = null;
        _dragging = DragTarget.None;
    }

    /// <summary>Apply: write the working copies back to the unit def and
    /// close. The unit editor's save flow then persists units.json.</summary>
    private void Apply()
    {
        if (_targetUnit == null) { Close(); return; }
        if (_editOverridesDefault)
        {
            _targetUnit.WadingFractionByDirection = CloneFractions(_editBottom);
            _targetUnit.WadingTopFractionByDirection = _editTopActive
                ? CloneFractions(_editTop) : null;
        }
        else
        {
            _targetUnit.WadingFractionByDirection = null;
            _targetUnit.WadingTopFractionByDirection = null;
        }
        HasUnsavedChanges = true;
        Close();
    }

    public void Draw(int screenW, int screenH, float dt)
    {
        ConsumedInput = false;
        if (!_isOpen || _targetUnit == null) return;
        ConsumedInput = true;

        // Overlay contract: blocks every widget the host (unit editor) drew
        // earlier this frame via the next-frame pre-raise, and lets our own
        // widgets interact at PopupInputLayer.
        _ui.BeginOverlay(PopupInputLayer);

        // Modal overlay dims the rest of the screen.
        _ui.DrawRect(new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 150));

        int popW = 900, popH = 600;
        // Clamp to the window so the action buttons stay reachable on small screens.
        int popX = Math.Max(0, (screenW - popW) / 2);
        int popY = Math.Max(0, (screenH - popH) / 2);
        // Update modal layer's panel rect each frame so window-resize
        // tracks correctly. ContainsMouse uses this for outside-click
        // dismissal (currently disabled via LightDismiss=false but we
        // still want it accurate).
        ModalLayer.Panel = new Rectangle(popX, popY, popW, popH);

        _ui.DrawPanel(popX, popY, popW, popH, $"Wading Editor — {_targetUnit.Id}");

        if (_ui.DrawButton("X", popX + popW - 30, popY + 3, 24, 22, EditorBase.DangerColor, layer: PopupInputLayer))
        {
            Close();
            _ui.EndOverlay();
            return;
        }

        // Orientation tabs (8 across the top under the panel header).
        DrawOrientationTabs(popX + 12, popY + 36, popW - 24);

        // Layout: preview box on the left, value sidebar on the right.
        int contentTop = popY + 80;
        int previewSize = 380;
        int previewX = popX + 16;
        int previewY = contentTop;
        DrawPreview(previewX, previewY, previewSize, dt);

        int sidebarX = previewX + previewSize + 20;
        int sidebarY = contentTop;
        int sidebarW = popW - previewSize - 60;
        int sidebarH = popH - 130;
        DrawValueSidebar(sidebarX, sidebarY, sidebarW, sidebarH);

        // Bottom action buttons.
        DrawActionButtons(popX + 12, popY + popH - 38, popW - 24);

        _ui.EndOverlay();
    }

    // =========================================================================
    //  Orientation tabs
    // =========================================================================

    private void DrawOrientationTabs(int x, int y, int w)
    {
        int n = DirectionalFractions.SectorLabels.Length; // 8
        int tabW = (w - (n - 1) * 4) / n;
        int tabH = 28;
        for (int i = 0; i < n; i++)
        {
            int tx = x + i * (tabW + 4);
            bool active = i == _selectedSector;
            var bg = active ? EditorBase.AccentColor : EditorBase.ButtonBg;
            if (_ui.DrawButton(DirectionalFractions.SectorLabels[i], tx, y, tabW, tabH, bg, layer: PopupInputLayer))
            {
                _selectedSector = i;
                _dragging = DragTarget.None;
            }
        }
    }

    // =========================================================================
    //  Preview box — sprite at the selected orientation with waterlines drawn
    //  as draggable overlays. Submerged regions are filled with a water-color
    //  rect to approximate the in-game cut.
    // =========================================================================

    /// <summary>Map a sector index 0..7 to a representative world facing
    /// angle (Necroking convention: 0°=E, 45°=SE, …). Used to drive the
    /// preview anim controller's facing.</summary>
    private static float SectorToFacingDeg(int sector) => sector * 45f;

    private void DrawPreview(int x, int y, int size, float dt)
    {
        // Water-colored background — same hue as the in-game shore foam
        // is rendered against, so the editor preview reads like in-game.
        var waterBg = new Color(95, 130, 130);
        _ui.DrawRect(new Rectangle(x, y, size, size), waterBg);
        _ui.DrawBorder(new Rectangle(x, y, size, size), EditorBase.PanelBorder);

        if (_targetUnit?.Sprite == null) { DrawNoSpriteMsg(x, y, size); return; }

        var atlasId = AtlasDefs.ResolveAtlasName(_targetUnit.Sprite.AtlasName);
        if (atlasId >= _atlases.Length) { DrawNoSpriteMsg(x, y, size); return; }
        var atlas = _atlases[atlasId];
        if (!atlas.IsLoaded) { DrawNoSpriteMsg(x, y, size); return; }
        var spriteData = atlas.GetUnit(_targetUnit.Sprite.SpriteName);
        if (spriteData == null) { DrawNoSpriteMsg(x, y, size); return; }

        if (spriteData != _lastSpriteData)
        {
            _previewAnim.Init(spriteData);
            _previewAnim.ForceState(AnimState.Walk);
            _lastSpriteData = spriteData;
        }
        _previewAnim.AnimTime = _previewWalkFrameTime;

        float worldFacing = SectorToFacingDeg(_selectedSector);
        var fr = _previewAnim.GetCurrentFrame(worldFacing);
        if (!fr.Frame.HasValue) { DrawNoSpriteMsg(x, y, size); return; }
        var frame = fr.Frame.Value;
        int spriteAngle = _previewAnim.ResolveAngle(worldFacing, out _);

        var (refTopV, refBotV) = _previewAnim.GetReferenceBodyBbox(
            spriteAngle, frame.BodyTopV, frame.BodyBottomV);

        // Scale sprite to fit ~95% of the preview box.
        float scaleX = (float)size * 0.95f / frame.Rect.Width;
        float scaleY = (float)size * 0.95f / frame.Rect.Height;
        float scale = Math.Min(scaleX, scaleY);
        float drawW = frame.Rect.Width * scale;
        float drawH = frame.Rect.Height * scale;
        float drawX = x + (size - drawW) * 0.5f;
        float drawY = y + (size - drawH) * 0.5f;

        var frameTex = atlas.GetTextureForFrame(frame);
        if (frameTex == null) { DrawNoSpriteMsg(x, y, size); return; }

        // Per-orientation cut values + auto-computed slope. The shader
        // takes a SPRITE-angle-based slope (same convention as
        // WadingState.Compute): for SW/W/NW the sprite is rendered with
        // FlipHorizontally, and SpriteBatch's flip reverses the shader's
        // localU sweep — so the SAME slope value produces a mirrored
        // visual cut for flipped sprites. Our UI line draw doesn't go
        // through that flip, so for the UI we negate the slope on
        // flipped sprites to match the visible shader output.
        float bottomFrac = MathHelper.Clamp(_editBottom.GetByIndex(_selectedSector), 0f, 1f);
        float bottomWaterlineV = refBotV - bottomFrac * (refBotV - refTopV);
        float topFrac = MathHelper.Clamp(_editTop.GetByIndex(_selectedSector), 0f, 1f);
        bool topVisible = _editTopActive && topFrac > 0f;
        float topWaterlineV = refTopV + topFrac * (refBotV - refTopV);
        float slopeForShader = ComputeBottomSlopeFromSpriteAngle(spriteAngle, _targetUnit.IsQuadruped);
        float slopeForUI = fr.FlipX ? -slopeForShader : slopeForShader;

        // Render the sprite — with the wading shader if available, else
        // fall back to the no-shader overlay-rect approach so the editor
        // still works when the shader hasn't loaded.
        var pos = new Vector2(drawX + drawW * 0.5f, drawY + drawH * 0.5f);
        bool shaderRendered = false;
        if (_wadingEffect != null)
        {
            shaderRendered = DrawSpriteWithWadingShader(
                frameTex, frame, fr.FlipX, pos, scale,
                bottomWaterlineV, topVisible ? topWaterlineV : -1f, slopeForShader);
        }
        if (!shaderRendered)
        {
            // Fallback path — draw sprite raw, then overlay water rects
            // below the bottom cut and above the top cut.
            var effects = fr.FlipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
            var origin = new Vector2(frame.Rect.Width * 0.5f, frame.Rect.Height * 0.5f);
            _ui.DrawTexture(frameTex, pos, frame.Rect, Color.White, 0f, origin, scale, effects);

            float bottomY = drawY + bottomWaterlineV * drawH;
            int clipBottomY = Math.Min(y + size, (int)bottomY);
            int submergedBottomH = Math.Max(0, (y + size) - clipBottomY);
            if (submergedBottomH > 0)
                _ui.DrawRect(new Rectangle(x, clipBottomY, size, submergedBottomH), new Color(95, 130, 130, 220));
            if (topVisible)
            {
                float topY = drawY + topWaterlineV * drawH;
                int clipTopY = Math.Max(y, (int)topY);
                int submergedTopH = Math.Max(0, clipTopY - y);
                if (submergedTopH > 0)
                    _ui.DrawRect(new Rectangle(x, y, size, submergedTopH), new Color(95, 130, 130, 220));
            }
        }

        // Faint body bbox lines (horizontal — they bound the V range, not
        // the body silhouette in screen space, so always horizontal).
        var bboxColor = new Color(255, 255, 255, 60);
        _ui.DrawRect(new Rectangle(x + 2, (int)(drawY + refTopV * drawH), size - 4, 1), bboxColor);
        _ui.DrawRect(new Rectangle(x + 2, (int)(drawY + refBotV * drawH), size - 4, 1), bboxColor);

        // Waterline indicators — drawn as tilted lines following the
        // visible slope. Use the UI-corrected slope (sign-flipped for
        // mirrored sprites) so the cyan bar matches the shader cut.
        var bottomLineColor = new Color(120, 220, 255);
        var topLineColor = new Color(255, 180, 80);
        DrawWaterlineIndicator(drawX, drawY, drawW, drawH, bottomWaterlineV, slopeForUI, bottomLineColor);
        if (topVisible)
            DrawWaterlineIndicator(drawX, drawY, drawW, drawH, topWaterlineV, 0f, topLineColor);

        // Value readouts beside the lines. Show the VISIBLE slope (after
        // flipX correction) since that's what the user is looking at.
        float bottomCenterY = drawY + bottomWaterlineV * drawH;
        _ui.DrawText($"bottom: {bottomFrac:F2}  slope: {slopeForUI:F2}",
            new Vector2(x + 6, bottomCenterY + 6), bottomLineColor);
        if (topVisible)
        {
            float topCenterY = drawY + topWaterlineV * drawH;
            _ui.DrawText($"top: {topFrac:F2}",
                new Vector2(x + 6, topCenterY - 18), topLineColor);
        }

        // Drag — uses the center V (at the sprite's mid-U), since the
        // line is symmetric around U=0.5. Vertical drag moves the line's
        // center; the slope stays auto-computed.
        HandleDrag(x, y, size,
                   (int)(drawY + bottomWaterlineV * drawH),
                   (int)(drawY + topWaterlineV * drawH),
                   topVisible,
                   refTopV, refBotV, drawY, drawH);
    }

    /// <summary>Render the sprite with the wading shader applied —
    /// matching the in-game render path. Switches batch state away from
    /// the editor's normal LinearClamp/AlphaBlend setup, draws the
    /// sprite via the wading effect, then restores the normal batch so
    /// subsequent editor UI continues to draw correctly.</summary>
    private bool DrawSpriteWithWadingShader(
        Texture2D tex, in SpriteFrame frame, bool flipX,
        Vector2 screenPos, float scale,
        float waterlineCenterV, float topWaterlineCenterV, float waterlineSlope)
    {
        if (_wadingEffect == null) return false;
        var sb = _ui.SpriteBatch;

        // Atlas U/V range of this frame.
        float atlasW = tex.Width, atlasH = tex.Height;
        float frameLeftU = frame.Rect.X / atlasW;
        float frameRightU = (frame.Rect.X + frame.Rect.Width) / atlasW;
        float frameTopV = frame.Rect.Y / atlasH;
        float frameBotV = (frame.Rect.Y + frame.Rect.Height) / atlasH;

        _wadingEffect.Parameters["FrameLeftU"]?.SetValue(frameLeftU);
        _wadingEffect.Parameters["FrameRightU"]?.SetValue(frameRightU);
        _wadingEffect.Parameters["FrameTopV"]?.SetValue(frameTopV);
        _wadingEffect.Parameters["FrameBottomV"]?.SetValue(frameBotV);
        _wadingEffect.Parameters["WaterlineCenterV"]?.SetValue(waterlineCenterV);
        _wadingEffect.Parameters["WaterlineSlope"]?.SetValue(waterlineSlope);
        _wadingEffect.Parameters["TopWaterlineCenterV"]?.SetValue(topWaterlineCenterV);
        _wadingEffect.Parameters["TopWaterlineSlope"]?.SetValue(0f);
        // FoamHalfWidth/TopFoamHalfWidth/UnderwaterAlpha/FoamColor are constants,
        // set once at load (Game1 LoadContent, Wading block) on this shared instance.

        // Switch batch state to the wading effect.
        sb.End();
        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp,
                 null, null, _wadingEffect);
        Render.Materials.NoteAdHocBatch(); // wading-effect batch, draws White only

        var origin = new Vector2(frame.Rect.Width * 0.5f, frame.Rect.Height * 0.5f);
        var effects = flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        sb.Draw(tex, screenPos, frame.Rect, Color.White, 0f, origin, scale, effects, 0f);

        sb.End();
        // Restore the canonical HUD/editor batch state. The HUD pass runs
        // PointClamp (see EffectBatch.HudSampler) — restoring LinearClamp here
        // left all editor text drawn after the wading preview blurry.
        Render.Materials.Hud.Begin(sb);
        return true;
    }

    /// <summary>Slope formula matches WadingState.Compute exactly: takes
    /// the SPRITE angle (post-resolution) — same for SE and SW since
    /// SW renders as a horizontally-flipped SE — and computes
    /// sin/cos × cameraYRatio. Caller is responsible for negating the
    /// returned slope for UI display on flipped sprites, since the
    /// shader's flipX-induced localU reversal does the visual mirror
    /// automatically but the UI line draw doesn't.</summary>
    private float ComputeBottomSlopeFromSpriteAngle(int spriteAngleDeg, bool isQuadruped)
    {
        if (!isQuadruped) return 0f;
        float spriteAngleRad = spriteAngleDeg * MathF.PI / 180f;
        float cosA = MathF.Cos(spriteAngleRad);
        float sinA = MathF.Sin(spriteAngleRad);
        if (MathF.Abs(cosA) < WadingConfig.CosThresholdForHorizontal) return 0f;
        float slope = sinA * _cameraYRatio / cosA;
        return MathHelper.Clamp(slope, -WadingConfig.MaxBodySlope, WadingConfig.MaxBodySlope);
    }

    /// <summary>Draw a tilted waterline indicator across the sprite's
    /// horizontal extent. Slope is dV/dU in sprite-normalized space;
    /// converted to screen pixel slope via the frame's drawn dimensions.
    /// The line passes through (centerU=0.5, centerV).</summary>
    private void DrawWaterlineIndicator(
        float drawX, float drawY, float drawW, float drawH,
        float centerV, float slope, Color color)
    {
        // Endpoints at U=0 and U=1, V = centerV + slope * (U - 0.5).
        float vLeft = centerV + slope * (0f - 0.5f);
        float vRight = centerV + slope * (1f - 0.5f);
        float xLeft = drawX;
        float xRight = drawX + drawW;
        float yLeft = drawY + vLeft * drawH;
        float yRight = drawY + vRight * drawH;
        DrawLine(xLeft, yLeft, xRight, yRight, color, thicknessPx: 2);
    }

    /// <summary>Small line-drawing helper — stretches the 1×1 white
    /// pixel by (len, thickness) and rotates it so the resulting quad
    /// spans (x1,y1)→(x2,y2). Used for the tilted waterline indicators.</summary>
    private void DrawLine(float x1, float y1, float x2, float y2, Color color, int thicknessPx = 1)
    {
        float dx = x2 - x1, dy = y2 - y1;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f) return;
        float angle = MathF.Atan2(dy, dx);
        _ui.Scope.Draw(
            _ui.PixelTexture,
            new Vector2(x1, y1),
            null,
            color,
            angle,
            Vector2.Zero,
            new Vector2(len, thicknessPx),
            SpriteEffects.None,
            0f);
    }

    private void DrawNoSpriteMsg(int x, int y, int size)
    {
        _ui.DrawText("(no sprite data)",
            new Vector2(x + size * 0.5f - 50, y + size * 0.5f - 8),
            EditorBase.TextDim);
    }

    private void HandleDrag(int boxX, int boxY, int boxSize,
                             int bottomLineY, int topLineY, bool topVisible,
                             float refTopV, float refBotV, float drawY, float drawH)
    {
        // Read mouse state from EditorBase rather than Mouse.GetState()
        // so we respect the InputLayer system — IsInputBlocked at our
        // popup's layer returns false (we're at the top of the stack),
        // but if something else later pushes a higher modal it will
        // correctly block our drag.
        if (_ui.IsInputBlocked(PopupInputLayer)) { _dragging = DragTarget.None; return; }
        var ms = _ui.GetMouseState();
        int mx = ms.X, my = ms.Y;
        bool mouseDown = ms.LeftButton == ButtonState.Pressed;

        // Inside the preview box?
        bool insideBox = mx >= boxX && mx < boxX + boxSize && my >= boxY && my < boxY + boxSize;

        // Start drag on mouse-down near a waterline.
        if (_dragging == DragTarget.None && mouseDown && insideBox)
        {
            const int grabRadiusPx = 8;
            int distBottom = Math.Abs(my - bottomLineY);
            int distTop = topVisible ? Math.Abs(my - topLineY) : int.MaxValue;
            if (distBottom <= grabRadiusPx && distBottom <= distTop)
            {
                _dragging = DragTarget.Bottom;
                _dragGrabOffsetPx = my - bottomLineY;
                // Any drag is a clear intent to persist a change — turn
                // on the per-unit override so Apply&Close actually writes
                // back. Previously this was a manual checkbox; not
                // toggling it silently discarded the user's edits.
                _editOverridesDefault = true;
                DebugLog.Log("editor", $"Wading drag start: BOTTOM sector={DirectionalFractions.SectorLabels[_selectedSector]} mouseY={my} lineY={bottomLineY}");
            }
            else if (topVisible && distTop <= grabRadiusPx)
            {
                _dragging = DragTarget.Top;
                _dragGrabOffsetPx = my - topLineY;
                _editOverridesDefault = true;
                DebugLog.Log("editor", $"Wading drag start: TOP sector={DirectionalFractions.SectorLabels[_selectedSector]} mouseY={my} lineY={topLineY}");
            }
        }

        // Update during drag.
        if (_dragging != DragTarget.None && mouseDown)
        {
            float targetLineY = my - _dragGrabOffsetPx;
            float waterlineV = (targetLineY - drawY) / drawH;
            float bodyVRange = refBotV - refTopV;
            if (bodyVRange < 0.001f) bodyVRange = 0.001f;

            if (_dragging == DragTarget.Bottom)
            {
                float frac = (refBotV - waterlineV) / bodyVRange;
                frac = MathHelper.Clamp(frac, 0f, 1f);
                _editBottom.SetByIndex(_selectedSector, frac);
            }
            else // Top
            {
                float frac = (waterlineV - refTopV) / bodyVRange;
                frac = MathHelper.Clamp(frac, 0f, 1f);
                _editTop.SetByIndex(_selectedSector, frac);
            }
        }

        // Release on mouse-up.
        if (!mouseDown) _dragging = DragTarget.None;
    }

    // =========================================================================
    //  Value sidebar — show all 8 sectors' values, the override checkbox,
    //  and helper buttons (Mirror, Reset, Save Default).
    // =========================================================================

    private void DrawValueSidebar(int x, int y, int w, int h)
    {
        int curY = y;
        _ui.DrawText("Bottom waterline", new Vector2(x, curY), EditorBase.TextBright);
        curY += 22;
        for (int i = 0; i < 8; i++)
        {
            bool sel = i == _selectedSector;
            string label = DirectionalFractions.SectorLabels[i];
            float v = _editBottom.GetByIndex(i);
            DrawValueRow(x, curY, w, label, v, sel ? new Color(120, 220, 255) : EditorBase.TextColor);
            curY += 18;
        }

        curY += 10;

        // Top waterline section with enable toggle.
        bool newTopActive = _ui.DrawButton(
            _editTopActive ? "[x] Top cut (used for swim pose)" : "[ ] Top cut (used for swim pose)",
            x, curY, w, 22, EditorBase.ButtonBg, layer: PopupInputLayer);
        if (newTopActive)
        {
            _editTopActive = !_editTopActive;
            if (!_editTopActive)
            {
                // Disabling clears all values so it can be re-enabled cleanly.
                for (int i = 0; i < 8; i++) _editTop.SetByIndex(i, 0f);
            }
        }
        curY += 26;
        if (_editTopActive)
        {
            for (int i = 0; i < 8; i++)
            {
                bool sel = i == _selectedSector;
                string label = DirectionalFractions.SectorLabels[i];
                float v = _editTop.GetByIndex(i);
                DrawValueRow(x, curY, w, label, v, sel ? new Color(255, 180, 80) : EditorBase.TextColor);
                curY += 18;
            }
        }

        curY += 12;

        // Override mode toggle — when off, the unit inherits from
        // WadingDefaults at runtime. When on, the current edit values
        // are stored on the unit def directly.
        if (_ui.DrawButton(
                _editOverridesDefault ? "[x] Per-unit override (uses values above)" : "[ ] Per-unit override (inherits default)",
                x, curY, w, 22, EditorBase.ButtonBg, layer: PopupInputLayer))
        {
            _editOverridesDefault = !_editOverridesDefault;
            if (!_editOverridesDefault)
            {
                // Inheriting → reload editor's working copy from current defaults.
                _editBottom.CopyFrom(WadingDefaults.QuadrupedBottom);
                _editTop.CopyFrom(WadingDefaults.QuadrupedTop);
                _editTopActive = HasAnyNonZero(_editTop);
            }
        }
        curY += 26;

        // Action buttons (per-unit / default operations).
        int btnW = w;
        if (_ui.DrawButton("Mirror L ↔ R  (E-side → W-side)", x, curY, btnW, 22, layer: PopupInputLayer))
        {
            _editBottom.MirrorEastToWest();
            _editTop.MirrorEastToWest();
        }
        curY += 26;
        if (_ui.DrawButton("Reset to default", x, curY, btnW, 22, layer: PopupInputLayer))
        {
            _editBottom.CopyFrom(WadingDefaults.QuadrupedBottom);
            _editTop.CopyFrom(WadingDefaults.QuadrupedTop);
            _editTopActive = HasAnyNonZero(_editTop);
            _editOverridesDefault = false;
        }
        curY += 26;
        if (_ui.DrawButton("Save as default (writes wading_defaults.json)",
                           x, curY, btnW, 22, EditorBase.AccentColor, layer: PopupInputLayer))
        {
            // Push the current edit values into WadingDefaults and persist.
            WadingDefaults.QuadrupedBottom.CopyFrom(_editBottom);
            WadingDefaults.QuadrupedTop.CopyFrom(_editTop);
            string defaultsPath = GamePaths.Resolve("data/wading_defaults.json");
            bool ok = WadingDefaultsFile.Save(defaultsPath);
            DebugLog.Log("editor", $"Save wading defaults: ok={ok} path={defaultsPath}");
        }
    }

    private void DrawValueRow(int x, int y, int w, string label, float value, Color textColor)
    {
        _ui.DrawText(label, new Vector2(x, y), textColor);
        _ui.DrawText(value.ToString("F2"), new Vector2(x + 40, y), textColor);
    }

    // =========================================================================
    //  Bottom action row
    // =========================================================================

    private void DrawActionButtons(int x, int y, int w)
    {
        int btnW = 140;
        if (_ui.DrawButton("Apply & Close", x + w - btnW * 2 - 8, y, btnW, 28, EditorBase.SuccessColor, layer: PopupInputLayer))
            Apply();
        if (_ui.DrawButton("Cancel", x + w - btnW, y, btnW, 28, layer: PopupInputLayer))
            Close();
    }

    // =========================================================================
    //  Helpers
    // =========================================================================

    private static bool HasAnyNonZero(DirectionalFractions f)
    {
        for (int i = 0; i < 8; i++)
            if (f.GetByIndex(i) != 0f) return true;
        return false;
    }

    private static DirectionalFractions CloneFractions(DirectionalFractions src)
    {
        var c = new DirectionalFractions();
        c.CopyFrom(src);
        return c;
    }
}
