using System;
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

    /// <summary>
    /// Override to request a larger grid for the scenario. Default is 64.
    /// </summary>
    public virtual int GridSize => 64;

    public abstract void OnInit(Simulation sim);
    public abstract void OnTick(Simulation sim, float dt);
    public abstract bool IsComplete { get; }
    public abstract int OnComplete(Simulation sim);

    // Bloom settings override (set by scenario, consumed by Game1)
    public BloomSettings? BloomOverride;

    // Weather override: set preset ID to enable weather for this scenario
    public string? WeatherPreset;

    // Request a specific menu state (editor) to be opened by Game1
    // Set to non-null to have Game1 switch to that MenuState
    public string? RequestedMenuState;

    // Request that Game1 select the first item in the active editor
    public bool RequestSelectFirst;

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
}

public abstract class UIScenarioBase : ScenarioBase
{
    public override bool WantsUI => true;
}
