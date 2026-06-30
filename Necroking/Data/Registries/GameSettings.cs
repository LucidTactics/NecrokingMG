using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Necroking.Data.Registries;

public class BloomSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("threshold")] public float Threshold { get; set; } = 0.8f;
    [JsonPropertyName("softKnee")] public float SoftKnee { get; set; } = 0.5f;
    [JsonPropertyName("intensity")] public float Intensity { get; set; } = 1.0f;
    [JsonPropertyName("scatter")] public float Scatter { get; set; } = 0.7f;
    [JsonPropertyName("iterations")] public int Iterations { get; set; } = 6;
    [JsonPropertyName("bicubicUpsampling")] public bool BicubicUpsampling { get; set; } = true;
}

public class GrassSettings
{
    [JsonPropertyName("baseColor")] public ColorJson BaseColor { get; set; } = new() { R = 46, G = 102, B = 20, A = 255 };
    [JsonPropertyName("tipColor")] public ColorJson TipColor { get; set; } = new() { R = 100, G = 166, B = 50, A = 255 };
    [JsonPropertyName("density")] public float Density { get; set; } = 160.0f;
    [JsonPropertyName("height")] public float Height { get; set; } = 150.0f;
    [JsonPropertyName("bladesPerCell")] public int BladesPerCell { get; set; } = 10;
    [JsonPropertyName("cellSize")] public float CellSize { get; set; } = 1.0f;
    [JsonPropertyName("windSpeed")] public float WindSpeed { get; set; } = 1.0f;
    [JsonPropertyName("windStrength")] public float WindStrength { get; set; } = 1.0f;
    [JsonPropertyName("fwidthSmoothing")] public bool FwidthSmoothing { get; set; } = true;
    [JsonPropertyName("minBladeWidth")] public bool MinBladeWidth { get; set; } = true;
}

public class WeatherSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("activePreset")] public string ActivePreset { get; set; } = "";
    [JsonPropertyName("transitionSpeed")] public float TransitionSpeed { get; set; } = 1.0f;
}

public class DayNightSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("dawnDuration")] public float DawnDuration { get; set; } = 60f;   // seconds
    [JsonPropertyName("dayDuration")] public float DayDuration { get; set; } = 240f;
    [JsonPropertyName("duskDuration")] public float DuskDuration { get; set; } = 60f;
    [JsonPropertyName("nightDuration")] public float NightDuration { get; set; } = 240f;
    [JsonPropertyName("dawnPreset")] public string DawnPreset { get; set; } = "dawn";
    [JsonPropertyName("dayPreset")] public string DayPreset { get; set; } = "daylight";
    [JsonPropertyName("duskPreset")] public string DuskPreset { get; set; } = "dusk";
    [JsonPropertyName("nightPreset")] public string NightPreset { get; set; } = "night";
}

/// <summary>Persisted window/display state so the game reopens the way the
/// player left it. Default (<see cref="Windowed"/> = false) is the borderless
/// full-screen "fullscreen" the game has always launched in. Toggling to a
/// resizable window (Alt+Enter) records the mode here, and the last windowed
/// size is remembered so a restart restores the same window. Ignored when a
/// launch override (<c>--resolution</c>) or headless mode owns the window.</summary>
public class DisplaySettings
{
    [JsonPropertyName("windowed")] public bool Windowed { get; set; }
    [JsonPropertyName("windowedWidth")] public int WindowedWidth { get; set; } = 1280;
    [JsonPropertyName("windowedHeight")] public int WindowedHeight { get; set; } = 720;
}

public class GeneralSettings
{
    [JsonPropertyName("showTimeControls")] public bool ShowTimeControls { get; set; } = true;
    [JsonPropertyName("showUnitRadius")] public bool ShowUnitRadius { get; set; }
    [JsonPropertyName("showObjectRadius")] public bool ShowObjectRadius { get; set; }
    [JsonPropertyName("groundTypeWarp")] public float GroundTypeWarp { get; set; } = 1.8f;
    [JsonPropertyName("groundUVWarpAmp")] public float GroundUVWarpAmp { get; set; } = 0.4f;
    [JsonPropertyName("groundUVWarpFreq")] public float GroundUVWarpFreq { get; set; } = 0.15f;
    [JsonPropertyName("editorScrollSpeed")] public float EditorScrollSpeed { get; set; } = 30.0f;
    [JsonPropertyName("editorScrollAccel")] public float EditorScrollAccel { get; set; } = 6.0f;
    [JsonPropertyName("combatLogEnabled")] public bool CombatLogEnabled { get; set; } = true;
    [JsonPropertyName("combatLogLines")] public int CombatLogLines { get; set; } = 10;
    [JsonPropertyName("combatLogFadeTime")] public float CombatLogFadeTime { get; set; } = 3.0f;
    [JsonPropertyName("combatLogFontSize")] public int CombatLogFontSize { get; set; } = 12;
    [JsonPropertyName("wpRapidEdit")] public bool WpRapidEdit { get; set; }
    [JsonPropertyName("buildingsDestructible")] public bool BuildingsDestructible { get; set; } = true;
    [JsonPropertyName("buildingDepositRange")] public float BuildingDepositRange { get; set; } = 5.0f;
    [JsonPropertyName("buildingPlacementRange")] public float BuildingPlacementRange { get; set; } = 20.0f;
    [JsonPropertyName("damageNumbersEnabled")] public bool DamageNumbersEnabled { get; set; } = true;
    [JsonPropertyName("damageNumberColor")] public ColorJson DamageNumberColor { get; set; } = new() { R = 200, G = 80, B = 80, A = 255 };
    [JsonPropertyName("damageNumberSize")] public int DamageNumberSize { get; set; } = 16;
    [JsonPropertyName("damageNumberFadeTime")] public float DamageNumberFadeTime { get; set; } = 0.75f;
    [JsonPropertyName("damageNumberSpeed")] public float DamageNumberSpeed { get; set; } = 1.5f;
    [JsonPropertyName("autoPickupForagables")] public bool AutoPickupForagables { get; set; }
    [JsonPropertyName("pauseDimBackground")] public bool PauseDimBackground { get; set; }
    /// <summary>Keep simulating while the window is unfocused (alt-tabbed). When off
    /// (default) the game freezes until refocused. Input is always ignored while
    /// unfocused regardless — this only controls whether the simulation advances.</summary>
    [JsonPropertyName("runWhenUnfocused")] public bool RunWhenUnfocused { get; set; }

    /// <summary>World-Z gravity (units/sec²) applied to physics-launched bodies
    /// during their flight arc. Realistic value is ~10 if 1 world unit ≈ 1 metre
    /// (the scale sprites are sized for). The engine default of 15 is moderately
    /// snappy — between real-world physics and the original "gamey" 50 — chosen
    /// so trample knockbacks visibly arc without spell knockbacks looking floaty.
    /// Lower = floatier knockbacks that travel further; higher = punchier but
    /// less visible.</summary>
    [JsonPropertyName("gravity")] public float Gravity { get; set; } = 15f;
}

public class ShadowSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("sunAngle")] public float SunAngle { get; set; } = 225.0f;
    [JsonPropertyName("lengthScale")] public float LengthScale { get; set; } = 0.6f;
    [JsonPropertyName("opacity")] public float Opacity { get; set; } = 0.35f;
    [JsonPropertyName("squash")] public float Squash { get; set; } = 0.3f;
    [JsonPropertyName("unitShadowMode")] public int UnitShadowMode { get; set; } = 1;
}

public class CombatSettings
{
    [JsonPropertyName("attackCooldown")] public float AttackCooldown { get; set; } = 3.0f;
    [JsonPropertyName("postAttackLockout")] public float PostAttackLockout { get; set; } = 1.0f;
    [JsonPropertyName("facingThreshold")] public float FacingThreshold { get; set; } = 60f;
    [JsonPropertyName("turnSpeed")] public float TurnSpeed { get; set; } = 360f;
    [JsonPropertyName("meleeRange")] public float MeleeRange { get; set; } = 0.8f;
    [JsonPropertyName("accelHalfTime")] public float AccelHalfTime { get; set; } = 1.2f;
    [JsonPropertyName("accel80Time")] public float Accel80Time { get; set; } = 3.0f;
    [JsonPropertyName("accelFullTime")] public float AccelFullTime { get; set; } = 6.0f;

    // Newtonian acceleration caps (replacing the exponential MoveTime model).
    // wu/s² caps on how fast a unit's velocity vector can change in each axis.
    // - Forward (along current velocity): accel when speeding up, decel when braking.
    //   Decel is ~4-5× accel for legged units (you can brake faster than you push).
    // - Lateral (perpendicular to current velocity): how hard a unit can change
    //   direction. With a constant lateral cap, turn radius scales with v²:
    //   r = v² / maxLateralAccel. Sharp turns at speed → must slow down first.
    [JsonPropertyName("maxAcceleration")] public float MaxAcceleration { get; set; } = 6.0f;
    [JsonPropertyName("maxDeceleration")] public float MaxDeceleration { get; set; } = 25.0f;
    [JsonPropertyName("maxLateralAccel")] public float MaxLateralAccel { get; set; } = 15.0f;

    /// <summary>Duration of one round in seconds. Per-weapon attack cycle =
    /// CooldownRounds × RoundDuration. Also used by rounds-based status effects.</summary>
    [JsonPropertyName("roundDuration")] public float RoundDuration { get; set; } = 3.0f;
}

public class HordeSettings
{
    [JsonPropertyName("circleOffset")] public float CircleOffset { get; set; } = 6.0f;
    [JsonPropertyName("circleRadius")] public float CircleRadius { get; set; } = 12.0f;
    [JsonPropertyName("positionLerp")] public float PositionLerp { get; set; } = 3.0f;
    [JsonPropertyName("rotationLerp")] public float RotationLerp { get; set; } = 1.5f;
    [JsonPropertyName("driftHz")] public float DriftHz { get; set; } = 0.2f;
    [JsonPropertyName("driftAmplitude")] public float DriftAmplitude { get; set; } = 0.7f;
    [JsonPropertyName("idleRadius")] public float IdleRadius { get; set; } = 2.0f;
    /// <summary>How far past EffectiveRadius (the green formation circle) the
    /// horde will engage incoming enemies — the orange F7 debug circle is at
    /// `EffectiveRadius + EngagementOffset`. Static absolute ranges were
    /// replaced with offsets so the engagement + leash bands scale with the
    /// horde as it grows; previously a 30-unit horde had its leash *inside*
    /// its own formation.</summary>
    [JsonPropertyName("engagementOffset")] public float EngagementOffset { get; set; } = 10.0f;
    /// <summary>How far past the engagement circle the leash extends. The red
    /// F7 debug circle is at `EngagementRange + LeashOffset`. A chasing minion
    /// that crosses this is force-returned to its formation slot.</summary>
    [JsonPropertyName("leashOffset")] public float LeashOffset { get; set; } = 10.0f;
    /// <summary>Floor on the effective horde radius used for aggro scans.
    /// Formation/positioning still uses the raw √N EffectiveRadius; this only kicks in
    /// so a small horde (few minions) can still aggro things as the necromancer walks
    /// near them instead of needing to be almost on top of them.</summary>
    [JsonPropertyName("minAggroRadius")] public float MinAggroRadius { get; set; } = 10.0f;
    [JsonPropertyName("returnSpeedMult")] public float ReturnSpeedMult { get; set; } = 0.65f;
    [JsonPropertyName("velocityDirLerp")] public float VelocityDirLerp { get; set; } = 6.0f;
}

public class StartingItem
{
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = "";
    [JsonPropertyName("quantity")] public int Quantity { get; set; } = 1;
}

/// <summary>
/// Off       — no fog, full visibility (units + terrain).
/// Explored  — permanent full reveal once any unit walks within sight (terrain + units).
/// FogOfWar  — classic three-state: visible / fogged (terrain only, units hidden) /
///             unexplored (hidden). Enemy units outside current sight aren't drawn.
/// Hybrid    — visible in current sight, fogged tint over explored-but-not-current
///             areas (terrain AND units stay visible), unexplored stays hidden.
///             Cheat-y visibility: you see all live unit positions on tiles you've
///             walked at least once, with the atmospheric fog of FoW.
/// </summary>
public enum FogOfWarMode { Off = 0, Explored = 1, FogOfWar = 2, Hybrid = 3 }

public class PerformanceSettings
{
    // Cap Dijkstra work per tick and defer overflow to later ticks, so a burst
    // of new flow-field requests (e.g. right after summoning) doesn't stall a
    // frame. Disabled by default so regressions don't hide behind the cap — opt
    // in when you actually want the smoothing.
    [JsonPropertyName("budgetedPathfinding")] public bool BudgetedPathfinding { get; set; }
    [JsonPropertyName("dijkstraBudgetMsPerTick")] public float DijkstraBudgetMsPerTick { get; set; } = 3.0f;

    // Amortize low-urgency AI work (horde follow/return, Unaware awareness
    // scans) across N frames instead of running for every unit every tick.
    // ORCA/movement still runs every frame so collision stays correct;
    // units just hold their last PreferredVel between decisions. Combat
    // states (Chasing/Engaged, Alert/Aggressive) always run every frame.
    [JsonPropertyName("amortizedAI")] public bool AmortizedAI { get; set; }
    [JsonPropertyName("aiUpdateInterval")] public int AIUpdateInterval { get; set; } = 6;

    // The SDF "amoeba" reanimation pose-morph (death→standup silhouette morph). OFF by
    // default — building each morph is heavy (GetData read-back + distance transform) and
    // chews CPU, getting in the way of iterating/testing. When off, raises use the cheap
    // alpha crossfade fallback (death frame → standup frame) with no per-morph build at
    // all. Turn on for the nicer effect.
    [JsonPropertyName("reanimMorph")] public bool ReanimMorph { get; set; }

    // Only relevant when ReanimMorph is on. Pre-builds every morphable unit's pose-morph
    // in the background after a world loads, so the first raise of a type never stalls on
    // its build. OFF by default: it's hundreds of builds that chew CPU for a while after
    // load. When off, morphs build lazily on first use (an occasional one-frame hitch).
    [JsonPropertyName("prewarmReanimMorphs")] public bool PrewarmReanimMorphs { get; set; }
}

public class FogOfWarSettings
{
    [JsonPropertyName("mode")] public int Mode { get; set; } // 0=Off, 1=Explored, 2=FogOfWar
    [JsonPropertyName("defaultSightRange")] public float DefaultSightRange { get; set; } = 10f; // for units with 0 detectionRange
    [JsonPropertyName("unexploredAlpha")] public float UnexploredAlpha { get; set; } = 1.0f;    // 1.0 = fully black
    [JsonPropertyName("foggedAlpha")] public float FoggedAlpha { get; set; } = 0.7f;            // greyed terrain visible
}

/// <summary>Corruption-system tunables: tree dissolve thresholds + durations,
/// ground/grass fade durations, fog simulation rates, and the visual look of
/// the death-fog spritesheet overlay. All persisted in settings.json so live
/// edits via the in-game settings panel survive restarts.</summary>
public class CorruptionSettings
{
    // --- Trees ---
    [JsonPropertyName("treeHealRate")]            public float TreeHealRate { get; set; } = 4f;
    [JsonPropertyName("treeThreshold")]           public float TreeThreshold { get; set; } = 30f;
    [JsonPropertyName("treeCorruptedAbsorbRate")] public float TreeCorruptedAbsorbRate { get; set; } = 0.5f;
    [JsonPropertyName("treeFadeDuration")]        public float TreeFadeDuration { get; set; } = 10f;

    // --- Ground ---
    [JsonPropertyName("groundMaxRatePerSec")] public float GroundMaxRatePerSec { get; set; } = 0.20f;
    [JsonPropertyName("groundFadeDuration")]  public float GroundFadeDuration  { get; set; } = 5f;

    // --- Grass ---
    [JsonPropertyName("grassFadeDuration")] public float GrassFadeDuration { get; set; } = 10f;

    // --- Fog simulation ---
    [JsonPropertyName("diffusionRate")]    public float DiffusionRate    { get; set; } = 0.18f;
    [JsonPropertyName("sourceRateScale")]  public float SourceRateScale  { get; set; } = 1.0f;
    [JsonPropertyName("sinkRateScale")]    public float SinkRateScale    { get; set; } = 1.0f;

    // --- Fog visual (death-fog sprite overlay) ---
    [JsonPropertyName("fogVisibilityThreshold")]     public float FogVisibilityThreshold     { get; set; } = 0.02f;
    [JsonPropertyName("fogSaturationDensity")]       public float FogSaturationDensity       { get; set; } = 1.0f;
    [JsonPropertyName("fogMaxAlpha")]                public float FogMaxAlpha                { get; set; } = 0.20f;
    [JsonPropertyName("fogFlipbookCycleSeconds")]    public float FogFlipbookCycleSeconds    { get; set; } = 3f;
    [JsonPropertyName("fogPuffWorldSizeMultiplier")] public float FogPuffWorldSizeMultiplier { get; set; } = 1.5f;
    [JsonPropertyName("fogPositionJitter")]          public float FogPositionJitter          { get; set; } = 0.4f;
    [JsonPropertyName("fogTint")] public ColorJson FogTint { get; set; } = new() { R = 185, G = 210, B = 180, A = 255 };
}

/// <summary>How the unit stat sheet (the "character sheet" UnitInfoPanel) is
/// surfaced for non-player units, plus tuning knobs we expect to iterate on.
/// <para>Two modes:</para>
/// • <b>Press-to-inspect</b> (default): press 'O' over a unit to pin its sheet
///   (optionally pausing). Press 'O' again to close.<br/>
/// • <b>Auto-show on hover</b> (Factorio-style): the sheet follows the cursor —
///   hover any unit to see its stats, move off to dismiss. No click, no pause.
/// </summary>
public class TooltipsSettings
{
    /// <summary>True = Factorio-style: hovering a unit auto-opens its stat sheet
    /// (no pause). False = press 'O' to inspect the unit under the cursor.</summary>
    [JsonPropertyName("autoShowUnitStats")] public bool AutoShowUnitStats { get; set; }

    /// <summary>Cursor pick radius (world units) for selecting which unit the
    /// hover / 'O' inspect targets. Larger = more forgiving, easier to grab a
    /// unit in a crowd; smaller = must be near-center.</summary>
    [JsonPropertyName("hoverPickRadius")] public float HoverPickRadius { get; set; } = 1.5f;

    /// <summary>Only affects press-to-inspect mode: whether pressing 'O' to open
    /// a unit sheet also pauses the game. Auto-show-on-hover never pauses.</summary>
    [JsonPropertyName("pauseOnManualInspect")] public bool PauseOnManualInspect { get; set; } = true;

    /// <summary>Show a floating info tooltip (name, HP, owner, processing state)
    /// when hovering a building/structure.</summary>
    [JsonPropertyName("showBuildingInfo")] public bool ShowBuildingInfo { get; set; } = true;

    /// <summary>Show a floating info tooltip (name, category, description) when
    /// hovering a foragable item lying on the ground.</summary>
    [JsonPropertyName("showGroundItemInfo")] public bool ShowGroundItemInfo { get; set; } = true;

    /// <summary>Show a floating info tooltip (unit name, state) when hovering a
    /// corpse lying on the ground — useful for spotting what's reanimatable.</summary>
    [JsonPropertyName("showCorpseInfo")] public bool ShowCorpseInfo { get; set; } = true;

    /// <summary>Cursor pick radius (world units) for hovering ground objects —
    /// buildings and foragable items. Buildings are large so this is a bit
    /// wider than the unit pick radius by default.</summary>
    [JsonPropertyName("groundPickRadius")] public float GroundPickRadius { get; set; } = 2.0f;

    /// <summary>Draw an outline box around the world object currently under the
    /// cursor (unit / corpse / building / ground item) so it's clear which object
    /// the hover tooltip belongs to. World objects only — never UI.</summary>
    [JsonPropertyName("showHoverHighlight")] public bool ShowHoverHighlight { get; set; } = true;

    /// <summary>Hover-highlight marker style for buildings, encoded as shape*4 + lineStyle.
    /// Shapes: 0 Circle, 1 Corners, 2 Rectangle, 3 Ground Box, 4 Diamond Box.
    /// Line styles: 0 Thick-Solid, 1 Thin-Solid, 2 Thick-Faint, 3 Thin-Faint.
    /// Default 17 = Diamond Box / Thin-Solid (matches the iso building footprints).</summary>
    [JsonPropertyName("hoverHighlightBuilding")] public int HoverHighlightBuilding { get; set; } = 17;

    /// <summary>Hover-highlight marker style for everything else (units, corpses,
    /// foragable ground items), same encoding as <see cref="HoverHighlightBuilding"/>.
    /// Default 1 = Circle / Thin-Solid (RTS selection-ring look).</summary>
    [JsonPropertyName("hoverHighlightRest")] public int HoverHighlightRest { get; set; } = 1;

    /// <summary>Debug readout: when the cursor is over the world (not UI), dump a
    /// block of text about the exact world position under the cursor in the
    /// bottom-left corner — fog level, cell coords, etc. Off by default; a
    /// developer/inspection aid rather than a player-facing tooltip.</summary>
    [JsonPropertyName("showWorldHoverDebug")] public bool ShowWorldHoverDebug { get; set; }
}

public class GameSettingsData
{
    [JsonPropertyName("bloom")] public BloomSettings Bloom { get; set; } = new();
    [JsonPropertyName("grass")] public GrassSettings Grass { get; set; } = new();
    [JsonPropertyName("weather")] public WeatherSettings Weather { get; set; } = new();
    [JsonPropertyName("dayNight")] public DayNightSettings DayNight { get; set; } = new();
    [JsonPropertyName("display")] public DisplaySettings Display { get; set; } = new();
    [JsonPropertyName("general")] public GeneralSettings General { get; set; } = new();
    [JsonPropertyName("shadow")] public ShadowSettings Shadow { get; set; } = new();
    [JsonPropertyName("horde")] public HordeSettings Horde { get; set; } = new();
    [JsonPropertyName("combat")] public CombatSettings Combat { get; set; } = new();
    [JsonPropertyName("fogOfWar")] public FogOfWarSettings FogOfWar { get; set; } = new();
    [JsonPropertyName("performance")] public PerformanceSettings Performance { get; set; } = new();
    [JsonPropertyName("corruption")] public CorruptionSettings Corruption { get; set; } = new();
    [JsonPropertyName("tooltips")] public TooltipsSettings Tooltips { get; set; } = new();
    [JsonPropertyName("startingInventory")] public List<StartingItem> StartingInventory { get; set; } = new();

    public bool Load(string path)
    {
        if (!Core.JsonFile.Load<GameSettingsData>(path, null, out var loaded)) return false;
        if (loaded == null) return false;
        Bloom = loaded.Bloom;
        Grass = loaded.Grass;
        Weather = loaded.Weather;
        DayNight = loaded.DayNight;
        // Display defaults (borderless fullscreen) if missing from older settings.json files.
        Display = loaded.Display ?? new DisplaySettings();
        General = loaded.General;
        Shadow = loaded.Shadow;
        Horde = loaded.Horde;
        Combat = loaded.Combat;
        FogOfWar = loaded.FogOfWar;
        Performance = loaded.Performance;
        // Corruption defaults if missing from older settings.json files.
        Corruption = loaded.Corruption ?? new CorruptionSettings();
        // Tooltips defaults if missing from older settings.json files.
        Tooltips = loaded.Tooltips ?? new TooltipsSettings();
        StartingInventory = loaded.StartingInventory;
        return true;
    }

    public bool Save(string path)
    {
        return Core.JsonFile.Save(path, this, Core.JsonDefaults.Indented);
    }
}
