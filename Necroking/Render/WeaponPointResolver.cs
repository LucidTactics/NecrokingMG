using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Necroking.Data.Registries;

namespace Necroking.Render;

/// <summary>
/// Resolves a unit's weapon hilt/tip attachment for the current animation frame.
///
/// Two data sources, in priority order:
///   1. AnimationMeta markers (WeaponBase / WeaponTip) — exported from the
///      source sprite rig in world units relative to the pivot at the feet.
///      This is the authoritative source going forward.
///   2. UnitDef.WeaponPoints (legacy JSON) — manually picked in the unit
///      editor, stored in sprite-frame pixel space.
///
/// Both paths produce the same downstream format (pixel-space WeaponFrameData)
/// so existing rendering code doesn't need to branch. Meta data wins when
/// available; the JSON path remains for units the exporter hasn't covered yet.
/// </summary>
public static class WeaponPointResolver
{
    /// <summary>
    /// Resolve hilt/tip for the current frame, returning a WeaponFrameData in
    /// sprite-frame pixel space (the same coordinate system the unit editor
    /// uses). Returns false when neither source has data.
    ///
    /// refFrameHeight is the unit's idle-anim frame height in pixels — same
    /// value the runtime caches as UnitAnimData.RefFrameHeight.
    /// </summary>
    /// <summary>
    /// One-stop resolution of a unit's CURRENT weapon frame: derives anim name,
    /// sprite angle/flip, and logical frame index from the controller, looks up
    /// the animation meta, and resolves hilt/tip. The single source shared by
    /// GameRenderer.ComputeWeaponAttach and the weapon_attach dev command —
    /// previously each hand-copied this chain, and a drifted diagnostic lies.
    /// The intermediates come back as outs so the dev command can report them.
    /// </summary>
    public static bool TryResolveCurrent(
        UnitDef def, AnimController ctrl, float refFrameHeight, float facingAngle,
        Dictionary<string, AnimationMeta> animMeta,
        out WeaponFrameData frame, out bool fromMeta,
        out string animName, out int spriteAngle, out bool flipX, out int frameIdx,
        out AnimationMeta? meta)
    {
        animName = AnimController.StateToAnimName(ctrl.CurrentState);
        spriteAngle = ctrl.ResolveAngle(facingAngle, out flipX);
        frameIdx = ctrl.GetCurrentFrameIndex(facingAngle);
        meta = null;
        frame = new WeaponFrameData();
        fromMeta = false;
        if (def.Sprite == null) return false;
        animMeta.TryGetValue(AnimMetaLoader.MetaKey(def.Sprite.SpriteName, animName), out meta);
        return TryResolve(def, meta, animName, spriteAngle, frameIdx, refFrameHeight,
            out frame, out fromMeta);
    }

    public static bool TryResolve(
        UnitDef def, AnimationMeta? meta,
        string animName, int spriteAngle, int frameIdx,
        float refFrameHeight,
        out WeaponFrameData frame, out bool fromMeta)
    {
        frame = new WeaponFrameData();
        fromMeta = false;

        // 1) AnimationMeta WeaponBase / WeaponTip
        if (meta != null && refFrameHeight > 0f)
        {
            bool hasBase = meta.TryGetMount(spriteAngle, "WeaponBase", frameIdx, out var basePos);
            bool hasTip  = meta.TryGetMount(spriteAngle, "WeaponTip",  frameIdx, out var tipPos);
            if (hasBase || hasTip)
            {
                // The exporter writes marker positions in the source rig's
                // world units — the necromancer is 2 m tall in the rig, which
                // is what SpriteWorldHeight stores. SpriteScale (e.g. 0.8 for
                // the necromancer) is a gameplay-only render shrink; the
                // exporter doesn't know about it.
                //
                // The legacy weapon-point pixel storage that runtime code
                // reads has SpriteScale baked in (worldH = SpriteWorldHeight ×
                // SpriteScale). To round-trip a meta marker through that
                // pipeline and end up at the right place, we have to divide
                // by SpriteWorldHeight only — leaving SpriteScale out — so
                // that when the runtime multiplies pixels back to world it
                // gets `mount × SpriteScale`, i.e. the rendered-scale offset.
                float rigWorldH = def.SpriteWorldHeight > 0f ? def.SpriteWorldHeight : 1.8f;
                float worldPerPx = rigWorldH / refFrameHeight;
                if (worldPerPx > 0f)
                {
                    // mountWorld (rig units) → frame pixels (legacy-storage
                    // scale). World +Y is up; pixel +Y is down.
                    // Depth (z) is exporter-tracked but not yet used here —
                    // until the exporter flags occluded mounts, callers
                    // assume the weapon renders in front of the body.
                    if (hasBase)
                    {
                        frame.Hilt.X = basePos.X / worldPerPx;
                        frame.Hilt.Y = -basePos.Y / worldPerPx;
                    }
                    if (hasTip)
                    {
                        frame.Tip.X = tipPos.X / worldPerPx;
                        frame.Tip.Y = -tipPos.Y / worldPerPx;
                    }
                    fromMeta = true;
                    return true;
                }
            }
        }

        // 2) Legacy JSON weapon points
        if (def.WeaponPoints.TryGetValue(animName, out var yawDict))
        {
            string yawKey = spriteAngle.ToString();
            if (yawDict.TryGetValue(yawKey, out var frames) && frameIdx >= 0 && frameIdx < frames.Count)
            {
                var src = frames[frameIdx];
                frame.Hilt.X = src.Hilt.X; frame.Hilt.Y = src.Hilt.Y; frame.Hilt.Behind = src.Hilt.Behind;
                frame.Tip.X  = src.Tip.X;  frame.Tip.Y  = src.Tip.Y;  frame.Tip.Behind  = src.Tip.Behind;
                return true;
            }
        }

        return false;
    }
}
