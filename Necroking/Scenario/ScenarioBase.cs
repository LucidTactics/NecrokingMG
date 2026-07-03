using System;
using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Render;
using Necroking.World;

namespace Necroking.Scenario;

public abstract class ScenarioBase
{
    public const string ScenarioLog = "scenario";

    public abstract string Name { get; }
    public virtual bool WantsUI => false;
    public virtual bool WantsGrass => false;
    public virtual bool WantsGround => false;
    /// <summary>Optional spell IDs to seed the spell bar, slot 0 first
    /// (UI tests don't run StartGame, which normally loads spellbar.json).</summary>
    public virtual string[]? DebugSpells => null;
    /// <summary>When true, Game1 disables IsFixedTimeStep + vsync so the game
    /// runs at unlocked framerate. Used by perf scenarios to measure raw GPU
    /// throughput without the 60Hz cap and sleep-padding masking the cost.
    /// Has no effect outside scenarios.</summary>
    public virtual bool BenchmarkMode => false;

    /// <summary>Plumbed by Game1 before OnInit when WantsGround is true so
    /// the scenario can register new ground types and paint the vertex map.</summary>
    public World.GroundSystem? GroundSystem;

    /// <summary>Set by Game1 to mirror its IsFixedTimeStep/vsync state after
    /// BenchmarkMode has been applied. Lets a scenario verify the toggle
    /// actually took effect.</summary>
    public bool VsyncEnabled = true;
    public bool FixedTimeStepEnabled = true;

    /// <summary>When > 0, Game1 calls DrawGroundShader this many extra times
    /// per frame. Used by perf scenarios to stress the GPU past the vsync
    /// budget so cost differences become measurable in real frame time.
    /// Has no effect when WantsGround is false.</summary>
    public int ExtraGroundDrawsPerFrame;

    /// <summary>
    /// Override to request a larger grid for the scenario. Default is 64.
    /// </summary>
    public virtual int GridSize => 64;

    public abstract void OnInit(Simulation sim);
    public abstract void OnTick(Simulation sim, float dt);
    public abstract bool IsComplete { get; }
    public abstract int OnComplete(Simulation sim);

    // Background clear color override (set by scenario, consumed by Game1)
    public Color? BackgroundColor;

    // Bloom settings override (set by scenario, consumed by Game1)
    public BloomSettings? BloomOverride;

    // Weather override: set preset ID to enable weather for this scenario
    public string? WeatherPreset;

    // Request a specific menu state (editor) to be opened by Game1
    // Set to non-null to have Game1 switch to that MenuState
    public string? RequestedMenuState;

    // Request that Game1 select the first item in the active editor
    public bool RequestSelectFirst;

    // Request that Game1 select a spell by display name in the spell editor
    public string? RequestSelectSpellByName;

    // Camera override
    public bool HasCameraOverride;
    public float CameraX, CameraY, CameraZoom = 64f;

    public void ZoomOnLocation(float worldX, float worldY, float zoom)
    {
        HasCameraOverride = true;
        CameraX = worldX;
        CameraY = worldY;
        CameraZoom = zoom;
    }

    // Request tab switch on MapEditor (set to non-null; consumed by Game1)
    public string? RequestedMapTab;

    // Request tab switch on UIEditor (set to non-null; consumed by Game1)
    public string? RequestedUITab;

    // Request selecting a specific widget by id in the UIEditor's Widgets tab
    // (set to non-null; consumed by Game1). Lets editor scenarios screenshot a
    // chosen widget's preview rather than just the first in the list.
    public string? RequestSelectUIWidgetById;

    // Request opening a sub-editor popup on UnitEditor
    public bool RequestOpenWeaponSub;

    // Request opening the buff manager popup on SpellEditor
    public bool RequestOpenBuffManager;

    // Collision debug mode override (set by scenario, consumed by Game1)
    public CollisionDebugMode? CollisionDebugOverride;

    // Deferred screenshot name (taken by main loop after rendering)
    public string? DeferredScreenshot;

    // Grass map access (set by Game1 before OnInit when WantsGrass)
    public byte[]? GrassMap;
    public int GrassW, GrassH;

    public void SetGrassType(int cx, int cy, int typeIndex)
    {
        if (GrassMap == null || cx < 0 || cx >= GrassW || cy < 0 || cy >= GrassH) return;
        GrassMap[cy * GrassW + cx] = (byte)(typeIndex + 1); // +1 because 0 = no grass
    }

    public void FillGrass(byte value)
    {
        if (GrassMap != null) Array.Fill(GrassMap, value);
    }

    // Road system access (set by Game1 before OnInit)
    public RoadSystem? RoadSystem;

    // Inventory system access (set by Game1 before OnInit)
    public GameSystems.Inventory? Inventory;
    public Data.Registries.ItemRegistry? ItemRegistry;

    // Inventory UI control
    public bool RequestOpenInventory;
    public bool RequestCloseInventory;
    public Game.InventoryUI? InventoryUI;

    // Skill book panel (new tabbed grimoire) control
    public bool RequestOpenSkillBook;
    public bool RequestCloseSkillBook;
    public UI.SkillBookOverlay? SkillBookOverlay;

    // Shader-based UI primitives (used by shader test scenarios)
    public Render.UIShaders? UIShaders;

    // Widget renderer access for UI widget test scenarios. Only plumbed when
    // WantsWidgetRenderer is true — initializing it drags in the inventory
    // family UIs, which other scenarios shouldn't pay for.
    public virtual bool WantsWidgetRenderer => false;
    public UI.RuntimeWidgetRenderer? WidgetRenderer;
    /// <summary>Draws a unit def's idle atlas sprite into a screen rect
    /// (Game1.DrawUnitIdleSprite). Plumbed when WantsWidgetRenderer is true.</summary>
    public Action<string, Microsoft.Xna.Framework.Rectangle>? DrawUnitSprite;

    // Sprite atlases (used by debug scenarios that need to draw arbitrary sprite
    // frames — e.g. stride calibration visualizer). Plumbed from Game1 before OnInit.
    public Render.SpriteAtlas[]? Atlases;

    // Font + pixel texture for debug scenarios that draw text and outlines.
    // Plumbed from Game1 before OnInit (same path as Atlases).
    public Microsoft.Xna.Framework.Graphics.SpriteFont? Font;
    public Microsoft.Xna.Framework.Graphics.SpriteFont? SmallFont;
    public Microsoft.Xna.Framework.Graphics.Texture2D? PixelTexture;

    // Scenario custom UI hook: called during Game1's UI pass if set, with
    // the active SpriteBatch. Used by shader-test scenarios to draw test
    // geometry without needing a full panel. Batch has already been Begun.
    public Action<Microsoft.Xna.Framework.Graphics.SpriteBatch, int, int>? CustomUIDraw;

    /// <summary>
    /// When true, the renderer overlays a debug marker at each unit's resolved
    /// weapon hilt (cyan) and tip (yellow) on top of the sprite. Used by the
    /// weapon-attach validation scenario to verify exporter mount points and
    /// the pixel/world conversion line up with the visible weapon.
    /// </summary>
    public bool ShowWeaponAttachDebug;
}

public abstract class UIScenarioBase : ScenarioBase
{
    public override bool WantsUI => true;
}
