using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Necroking.Data;

namespace Necroking.UI;

/// <summary>
/// Hover tooltips for the unit-sheet stat grid. Label/icon hover shows the
/// simple stat tooltip (StatTooltipWindow rebound per stat); value hover shows
/// the ResourceTooltipDyn tabulation of how the number is reached (base rows
/// default-colored, additions green, subtractions red; Final colored vs base).
/// Timing follows the Windows convention: ~400ms initial hover, instant
/// re-show within a 300ms grace window (so scanning a column feels fluid).
/// </summary>
public class StatTooltips
{
    private const float HoverDelay = 0.4f;
    private const float ReshowGrace = 0.3f;

    public readonly record struct StatInfo(string Key, string Title, string Icon, string Desc);

    private static string Ico(string k) => $"assets/UI/Icons/StatTips/{k}_36.png";

    public static readonly Dictionary<string, StatInfo> Info = new()
    {
        ["hp"] = new("hp", "Hit Points", Ico("hp"),
            "Hit points are a unit's lifeblood. Damage that gets past protection removes them; at zero the unit dies."),
        ["magicres"] = new("magicres", "Magic Resistance", Ico("magicres"),
            "Magic resistance is willpower against hostile magic. Spells with penetration roll against it to take effect."),
        ["morale"] = new("morale", "Morale", Ico("morale"),
            "Morale is steadiness under fire. Units failing morale checks when taking casualties or outnumbered will rout."),
        ["size"] = new("size", "Size", Ico("size"),
            "Size is physical bulk. Larger units trample smaller ones and are easier to hit; smaller units can slip past."),
        ["toughness"] = new("toughness", "Toughness", Ico("toughness"),
            "Toughness is the thickness of hide and flesh. It halves incoming damage up to its value — blunting weak blows without ever blocking them outright; heavy hits punch through."),
        ["magicpower"] = new("magicpower", "Magic Power", Ico("magicpower"),
            "TBD — not yet implemented."),
        ["strength"] = new("strength", "Strength", Ico("strength"),
            "Strength is raw physical power and increases most melee attacks (and some ranged). Its also used for various effect checks (knockdown)."),
        ["protection"] = new("protection", "Protection", Ico("protection"),
            "Protection is worn armor. It is subtracted flat from incoming damage before toughness; piercing weapons partly ignore it."),
        ["shield"] = new("shield", "Shield Protection", Ico("shield"),
            "Shield protection is added against blows the shield parries, blocking weaker hits entirely."),
        ["attack"] = new("attack", "Attack", Ico("attack"),
            "Attack is skill at landing blows. It rolls against the defender's defense; weapons can add to it."),
        ["defense"] = new("defense", "Defense", Ico("defense"),
            "Defense is skill at avoiding blows. Attacks roll against it; fatigue and encumbrance erode it."),
        ["parry"] = new("parry", "Parry", Ico("parry"),
            "Parry is the shield's ability to intercept attacks before they land, adding shield protection to the block."),
        ["speed"] = new("speed", "Combat Speed", Ico("speed"),
            "Combat speed is how fast a unit moves and closes distance on the battlefield."),
        ["encumbrance"] = new("encumbrance", "Encumbrance", Ico("encumbrance"),
            "Encumbrance is the burden of gear. Each attack builds fatigue equal to it, eroding defense over a long fight."),
        ["upkeep"] = new("upkeep", "Upkeep", Ico("upkeep"),
            "TBD — not yet implemented."),
    };

    // hover state
    private string? _hoverKey;          // stat key under cursor
    private bool _hoverValue;           // true = number hovered (tabulation)
    private float _hoverTime;
    private float _graceTimer;
    private bool _shown;
    private string? _boundKey;          // what the widgets are currently bound to
    private bool _boundValue;

    public void Update(string? key, bool isValue, float dt)
    {
        if (key == null)
        {
            if (_shown) _graceTimer = ReshowGrace;
            _hoverKey = null;
            _shown = false;
            _hoverTime = 0f;
            if (_graceTimer > 0) _graceTimer -= dt;
            return;
        }
        if (key != _hoverKey || isValue != _hoverValue)
        {
            _hoverKey = key;
            _hoverValue = isValue;
            // moving between cells while shown (or within grace) re-shows instantly
            _hoverTime = (_shown || _graceTimer > 0) ? HoverDelay : 0f;
        }
        else
        {
            _hoverTime += dt;
        }
        _shown = _hoverTime >= HoverDelay;
        if (_shown) _graceTimer = ReshowGrace;
    }

    /// <summary>Draw the active tooltip (if any) at the cursor. rows/final for
    /// the value tabulation come from the caller (panel owns the stat data).</summary>
    public void Draw(RuntimeWidgetRenderer r, int mx, int my, int screenW, int screenH,
        Func<string, (List<ResourceTooltip.Row> Rows, string Final, Color FinalColor)?> breakdown)
    {
        if (!_shown || _hoverKey == null || !Info.TryGetValue(_hoverKey, out var info)) return;

        if (!_hoverValue)
        {
            if (_boundKey != _hoverKey || _boundValue)
            {
                r.SetText("stattip", "tt_title", info.Title);
                r.SetText("stattip", "tt_desc", info.Desc);
                r.SetImage("stattip", "tt_icon", info.Icon);
                _boundKey = _hoverKey; _boundValue = false;
            }
            var (x, y) = Place(mx, my, 222, 103, screenW, screenH);
            r.DrawWidget("StatTooltipWindow", x, y, "stattip");
        }
        else
        {
            var bd = breakdown(_hoverKey);
            if (bd == null) return;
            if (_boundKey != _hoverKey || !_boundValue)
            {
                ResourceTooltip.Bind(r, "statcalc", info.Title, bd.Value.Final, bd.Value.FinalColor,
                    bd.Value.Rows, "");
                r.SetImage("statcalc.0", "icon", info.Icon);
                _boundKey = _hoverKey; _boundValue = true;
            }
            int h = r.MeasureWidgetHeight(ResourceTooltip.WidgetId, "statcalc");
            var (x, y) = Place(mx, my, 222, h, screenW, screenH);
            r.DrawWidget(ResourceTooltip.WidgetId, x, y, "statcalc");
        }
    }

    /// <summary>Cursor offset +16/+20, flipped when clipping the screen edge.</summary>
    private static (int, int) Place(int mx, int my, int w, int h, int sw, int sh)
    {
        int x = mx + 16, y = my + 20;
        if (x + w > sw - 4) x = mx - w - 8;
        if (y + h > sh - 4) y = my - h - 8;
        return (Math.Max(4, x), Math.Max(4, y));
    }
}
