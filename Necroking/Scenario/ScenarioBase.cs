using System;
using Necroking.Core;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Scenario;

public abstract class ScenarioBase
{
    public const string ScenarioLog = "scenario";

    public abstract string Name { get; }
    public virtual bool WantsUI => false;
    public virtual bool WantsGrass => false;
    public virtual bool WantsGround => false;

    public abstract void OnInit(Simulation sim);
    public abstract void OnTick(Simulation sim, float dt);
    public abstract bool IsComplete { get; }
    public abstract int OnComplete(Simulation sim);

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
}

public abstract class UIScenarioBase : ScenarioBase
{
    public override bool WantsUI => true;
}
