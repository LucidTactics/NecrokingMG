namespace Necroking.Data;

/// <summary>
/// A unit placed on the map via the editor. Saved in the map JSON
/// and spawned when the game starts.
/// </summary>
public class PlacedUnit
{
    public string UnitDefId { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public string Faction { get; set; } = ""; // empty = use def default
    public string PatrolRouteId { get; set; } = ""; // empty = no patrol
}
