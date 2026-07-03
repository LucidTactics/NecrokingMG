using Microsoft.Xna.Framework.Graphics;
// The project has its own Necroking.Render.Effect (game effects), so the shader
// type must be aliased explicitly.
using XnaEffect = Microsoft.Xna.Framework.Graphics.Effect;

namespace Necroking.Render;

/// <summary>
/// The canonical outer-batch states that one-off effect draws interrupt, plus
/// helpers to suspend/resume them around an effect batch.
///
/// Every effect-draw site used to hand-roll
/// End() / Begin(effect) / Draw / End() / Begin(restore-guess), each hardcoding
/// its private copy of the outer batch state. Two shipped bugs came from a site
/// guessing the restore state wrong (a PointClamp restore inside the LinearClamp
/// scene pass switched the whole rest of the scene to point filtering whenever
/// that effect drew). The restore states now live HERE and nowhere else: if the
/// scene or HUD pass ever changes blend/sampler, update the fields below and
/// every suspend site follows automatically.
///
/// UIShaders keeps its own injected-restore mechanism (its restore state is a
/// constructor parameter) — it already solved this problem; don't convert it.
/// </summary>
internal static class EffectBatch
{
    // --- Canonical pass states now live in Materials (Materials.Scene /
    // Materials.Hud); these delegate so existing call sites keep working while
    // the migration runs. New code should reference Materials directly. ---

    /// <summary>Scene pass: premultiplied-alpha sprites, linear filtering.</summary>
    public static BlendState SceneBlend => Materials.Scene.Blend;
    public static SamplerState SceneSampler => Materials.Scene.Sampler;

    /// <summary>HUD pass: premultiplied-alpha UI, point filtering (crisp pixel UI).</summary>
    public static BlendState HudBlend => Materials.Hud.Blend;
    public static SamplerState HudSampler => Materials.Hud.Sampler;

    /// <summary>Begin the scene pass with its canonical state.</summary>
    public static void BeginScenePass(SpriteBatch batch)
        => batch.Begin(SpriteSortMode.Deferred, SceneBlend, SceneSampler);

    /// <summary>Begin the HUD pass with its canonical state.</summary>
    public static void BeginHudPass(SpriteBatch batch)
        => batch.Begin(SpriteSortMode.Deferred, HudBlend, HudSampler);

    /// <summary>End the current batch and begin a one-off effect batch. Pair with
    /// EndEffectResumeScene / EndEffectResumeHud (whichever pass this interrupted).</summary>
    public static void BeginEffect(SpriteBatch batch, XnaEffect effect, BlendState blend,
        SamplerState sampler, SpriteSortMode sortMode = SpriteSortMode.Immediate)
    {
        batch.End();
        batch.Begin(sortMode, blend, sampler, null, null, effect);
    }

    /// <summary>End the effect batch and resume the scene pass.</summary>
    public static void EndEffectResumeScene(SpriteBatch batch)
    {
        batch.End();
        BeginScenePass(batch);
    }

    /// <summary>End the effect batch and resume the HUD pass.</summary>
    public static void EndEffectResumeHud(SpriteBatch batch)
    {
        batch.End();
        BeginHudPass(batch);
    }
}
