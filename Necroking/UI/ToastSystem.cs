using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Render;

namespace Necroking.UI;

/// <summary>Bottom-right corner toast stack — the game-wide "something happened"
/// feedback channel. Any system pushes one via <c>Game1.Instance.Toasts.Push(...)</c>
/// (or <c>Game1.PushSkillToast</c> for the skill-book flavor); an optional OnClick
/// runs when the toast is clicked, and clicking always dismisses. Owns the queue,
/// expiry, layout, hit-testing, click routing and drawing; ToastLayer
/// (UI/Layers/HudLayers.cs) is its seat in the UI router, and
/// Game1.RebuildUIHitRects catalogues the rects for hover blocking.</summary>
public sealed class ToastSystem
{
    public struct Toast
    {
        public string Header;   // e.g. "Recipe Learned"
        public string Body;     // e.g. the skill name
        public Action? OnClick; // optional click-through action
        public float Timer;     // seconds shown so far
        public float Duration;  // seconds total
    }

    private readonly List<Toast> _toasts = new();
    public int Count => _toasts.Count;

    public void Push(string header, string body, Action? onClick = null, float duration = 5f)
        => _toasts.Add(new Toast { Header = header, Body = body, OnClick = onClick, Duration = duration });

    public void Clear() => _toasts.Clear();

    /// <summary>Expiry tick. Runs on world dt (from UpdateAnimations), so toasts
    /// freeze while paused / in editors — deliberate, matches the rest of the HUD FX.</summary>
    public void Update(float dt)
    {
        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            var t = _toasts[i];
            t.Timer += dt;
            if (t.Timer >= t.Duration) _toasts.RemoveAt(i);
            else _toasts[i] = t;
        }
    }

    /// <summary>Geometry helper — the ONE layout function shared by Draw, IndexAt
    /// and AppendHitRects, so clicks always land on what was just rendered.</summary>
    private static Rectangle GetToastRect(int sw, int sh, int stackIndex)
    {
        const int toastW = 280, toastH = 56, padR = 16, padB = 16, gap = 6;
        int yCursor = sh - padB - toastH - stackIndex * (toastH + gap);
        return new Rectangle(sw - padR - toastW, yCursor, toastW, toastH);
    }

    /// <summary>Catalogue the visible toasts into the central UI hit registry
    /// (hover-blocking is derived from it; click routing lives in ToastLayer).</summary>
    public void AppendHitRects(UIHitRegistry reg, int sw, int sh)
    {
        for (int i = 0; i < _toasts.Count; i++)
            reg.Add($"toast.{i}", GetToastRect(sw, sh, i));
    }

    /// <summary>List index of the toast under the cursor, or -1. Toasts are drawn
    /// from the most recent (last-added) up the stack: slot 0 = newest = bottom rect.</summary>
    public int IndexAt(int sw, int sh, int mx, int my)
    {
        for (int i = 0; i < _toasts.Count; i++)
        {
            int stackSlot = _toasts.Count - 1 - i;
            if (GetToastRect(sw, sh, stackSlot).Contains(mx, my))
                return i;
        }
        return -1;
    }

    /// <summary>Clicked toast: dismiss it, then run its OnClick (if any).
    /// Removal happens first so an OnClick that pushes new toasts can't shift
    /// the index out from under the RemoveAt.</summary>
    public void Activate(int listIdx)
    {
        if (listIdx < 0 || listIdx >= _toasts.Count) return;
        var onClick = _toasts[listIdx].OnClick;
        _toasts.RemoveAt(listIdx);
        onClick?.Invoke();
    }

    /// <summary>Bottom-right stack. Each toast slides in (first 10% of life),
    /// holds, then fades out (last 15%).</summary>
    public void Draw(int sw, int sh)
    {
        var g = Game1.Instance;
        if (_toasts.Count == 0 || g._font == null) return;
        var f = g._font!;
        var sf = g._smallFont ?? f;

        const int toastW = 280, toastH = 56, padR = 16, padB = 16, gap = 6;
        int yCursor = sh - padB - toastH;

        // Palette matches the SkillBookPanel's grimoire chrome.
        var leatherMid  = new Color(42, 26, 18);
        var gold        = new Color(218, 184,  96);
        var goldDim     = new Color(108,  84,  40);
        var parchment   = new Color(196, 174, 128);

        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            var t = _toasts[i];
            float life = t.Timer / t.Duration;
            float alpha = 1f;
            if (life < 0.1f) alpha = life / 0.1f;          // slide in
            else if (life > 0.85f) alpha = (1f - life) / 0.15f; // fade out
            alpha = MathHelper.Clamp(alpha, 0f, 1f);
            int slideX = (int)((1f - alpha) * 30); // also slides slightly from the right

            int x = sw - padR - toastW + slideX;
            int y = yCursor;

            var rect = new Rectangle(x, y, toastW, toastH);
            // Drop shadow
            g.Scope.Draw(g._pixel, new Rectangle(rect.X + 3, rect.Y + 3, rect.Width, rect.Height),
                new Color((byte)0, (byte)0, (byte)0, (byte)(160 * alpha)));
            g.Scope.Draw(g._pixel, rect, new Color(leatherMid, alpha));
            // Top gold accent band
            g.Scope.Draw(g._pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2),
                new Color(gold, alpha));
            // Border
            DrawUtils.DrawRectBorder(g.Scope, g._pixel, rect, new Color(goldDim, alpha));

            // Text — header + body, sanitized for the embedded ASCII-only SpriteFont.
            string header = DrawUtils.SanitizeAscii(t.Header);
            string body = DrawUtils.SanitizeAscii(t.Body);
            DrawTextRounded(g, sf, header,
                new Vector2(rect.X + 14, rect.Y + 8),
                new Color(gold, alpha));
            DrawTextRounded(g, f, body,
                new Vector2(rect.X + 14, rect.Y + 24),
                new Color(parchment, alpha));

            yCursor -= toastH + gap;
        }
    }

    private static void DrawTextRounded(Game1 g, SpriteFont f, string text, Vector2 pos, Color color)
        => g.Scope.DrawString(f, text, new Vector2((int)pos.X, (int)pos.Y), color);
}
