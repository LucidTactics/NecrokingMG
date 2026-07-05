namespace Necroking.Editor;

/// <summary>
/// Config for the "auto-ground" feature: stamp a ground patch under a placed
/// object (e.g. dirt under trees on grassland). This is the single shared state
/// bag for auto-ground everywhere — the Objects tab holds one global instance and
/// each ProcGen pool (large / small) holds its own. All the stamping and UI live
/// in MapEditorWindow's auto-ground helpers, which operate on one of these.
///
/// The ground type is stored by NAME (not index) so it survives ground-type list
/// reordering and persists sanely in data/procgen_styles.json — the name is
/// resolved to a live index at stamp time.
/// </summary>
public class AutoGroundSettings
{
    public bool Enabled;
    public string TypeName = ""; // ground type name; resolved to an index at stamp time
    public int Size = 1;         // patch radius in ground tiles
    public int Noise = 3;        // # of ragged-edge tiles grown off the patch
}
