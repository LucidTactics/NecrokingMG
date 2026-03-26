using Necroking.Core;
using Necroking.GameSystems;

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
}

public abstract class UIScenarioBase : ScenarioBase
{
    public override bool WantsUI => true;
}
