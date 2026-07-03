using Microsoft.Xna.Framework.Graphics;
// The project has its own Necroking.Render.Effect (game effects), so the shader
// type must be aliased explicitly.
using XnaEffect = Microsoft.Xna.Framework.Graphics.Effect;

namespace Necroking.Render;

/// <summary>
/// Legacy shim over the canonical pass states (now defined in Materials).
///
/// The suspend/resume helpers (BeginEffect / EndEffectResumeScene / ...Hud)
/// that used to live here are RETIRED: every effect-draw site now goes through
/// SpriteScope.PushMaterial/PopMaterial (or Suspend/Resume), where the restore
/// state is computed by the pass executor instead of guessed by the call site.
/// The two shipped wrong-restore bugs this class was created to prevent are
/// structurally impossible under the scope model.
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

}
