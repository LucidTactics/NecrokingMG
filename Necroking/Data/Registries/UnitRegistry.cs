using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Necroking.Data.Registries;

public class SpriteRef
{
    [JsonPropertyName("atlas")] public string AtlasName { get; set; } = "";
    [JsonPropertyName("name")] public string SpriteName { get; set; } = "";
}

/// <summary>
/// A weapon slot on a unit definition: the weapon id plus optional per-unit-per-slot
/// animation override. Each slot tracks its own override — a unit that lists the same
/// weapon twice can give each copy a different anim.
///
/// JSON formats accepted on load (via UnitWeaponRefJsonConverter):
///   "weapon_id"                                  — bare string, no override
///   { "id": "weapon_id" }                        — object, no override
///   { "id": "weapon_id", "anim": "AttackBite" }  — object with override
/// On save, slots without an override are written as bare strings for a minimal diff
/// against the existing data/units.json.
/// </summary>
[JsonConverter(typeof(UnitWeaponRefJsonConverter))]
public class UnitWeaponRef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>null / empty = "Default" (use WeaponDef.AnimName, else UnitDef.AttackAnim,
    /// else Attack1/Ranged1).</summary>
    [JsonPropertyName("anim")] public string? AnimOverride { get; set; }

    /// <summary>Cosmetic lunge distance (world units) — how far the unit's sprite
    /// visually translates forward toward the target at the hit frame of this
    /// weapon's attack. 0 = no lunge. Simulation position is unaffected; only
    /// Unit.RenderOffset is written, which every draw path inherits.</summary>
    [JsonPropertyName("lungeDist")] public float LungeDist { get; set; }

    public UnitWeaponRef() {}
    public UnitWeaponRef(string id) { Id = id; }
    public UnitWeaponRef(string id, string? anim) { Id = id; AnimOverride = string.IsNullOrEmpty(anim) ? null : anim; }
}

/// <summary>Accepts bare-string and object JSON forms on load; writes bare string
/// when AnimOverride is unset for minimal diff against existing data.</summary>
public class UnitWeaponRefJsonConverter : JsonConverter<UnitWeaponRef>
{
    public override UnitWeaponRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new UnitWeaponRef(reader.GetString() ?? "");

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"Unexpected token {reader.TokenType} for UnitWeaponRef");

        var r = new UnitWeaponRef();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return r;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            string prop = reader.GetString() ?? "";
            reader.Read();
            switch (prop)
            {
                case "id":        r.Id = reader.GetString() ?? ""; break;
                case "anim":      r.AnimOverride = reader.TokenType == JsonTokenType.Null ? null : reader.GetString(); break;
                case "lungeDist": r.LungeDist = reader.GetSingle(); break;
                default:          reader.Skip(); break;
            }
        }
        return r;
    }

    public override void Write(Utf8JsonWriter writer, UnitWeaponRef value, JsonSerializerOptions options)
    {
        // Bare-string form only when nothing extra is set — keeps existing
        // data/units.json diff-free for unmodified slots.
        bool hasAnim = !string.IsNullOrEmpty(value.AnimOverride);
        bool hasLunge = value.LungeDist != 0f;
        if (!hasAnim && !hasLunge)
        {
            writer.WriteStringValue(value.Id);
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        if (hasAnim) writer.WriteString("anim", value.AnimOverride);
        if (hasLunge) writer.WriteNumber("lungeDist", value.LungeDist);
        writer.WriteEndObject();
    }
}

/// <summary>
/// A single weapon attachment point (hilt or tip) with position and behind-flag.
/// </summary>
public class WeaponPointData
{
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("y")] public float Y { get; set; }
    [JsonPropertyName("behind")] public bool Behind { get; set; }
}

/// <summary>
/// Weapon point data for a single frame at a given yaw: hilt + tip positions.
/// </summary>
public class WeaponFrameData
{
    [JsonPropertyName("hilt")] public WeaponPointData Hilt { get; set; } = new();
    [JsonPropertyName("tip")] public WeaponPointData Tip { get; set; } = new();
}

/// <summary>
/// Per-animation timing override for a unit: frame durations and effect time.
/// </summary>
public class UnitAnimTimingOverride
{
    [JsonPropertyName("frameDurationsMs")] public List<int> FrameDurationsMs { get; set; } = new();
    [JsonPropertyName("effectTimeMs")] public int EffectTimeMs { get; set; } = -1;
}

public class UnitStatsJson
{
    [JsonPropertyName("maxHP")] public int MaxHP { get; set; } = 10;
    [JsonPropertyName("strength")] public int Strength { get; set; } = 10;
    [JsonPropertyName("attack")] public int Attack { get; set; } = 10;
    [JsonPropertyName("defense")] public int Defense { get; set; } = 10;
    [JsonPropertyName("magicResist")] public int MagicResist { get; set; } = 10;
    [JsonPropertyName("encumbrance")] public int Encumbrance { get; set; }
    [JsonPropertyName("naturalProt")] public int NaturalProt { get; set; }
    [JsonPropertyName("combatSpeed")] public float CombatSpeed { get; set; } = 8.0f;
}

public class UnitDef : IHasId
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("type")] public string UnitType { get; set; } = "Dynamic";
    [JsonPropertyName("faction")] public string Faction { get; set; } = "Undead";
    [JsonPropertyName("ai")] public string AI { get; set; } = "AttackClosest";
    [JsonPropertyName("orcaPriority")] public int OrcaPriority { get; set; }
    [JsonPropertyName("size")] public int Size { get; set; } = 2;
    [JsonPropertyName("radius")] public float Radius { get; set; } = 0.495f;
    [JsonPropertyName("spriteScale")] public float SpriteScale { get; set; } = 1.0f;
    [JsonPropertyName("spriteWorldHeight")] public float SpriteWorldHeight { get; set; } = 1.8f;
    [JsonPropertyName("color")] public ColorJson? Color { get; set; }
    [JsonPropertyName("sprite")] public SpriteRef? Sprite { get; set; }
    [JsonPropertyName("stats")] public UnitStatsJson? Stats { get; set; }
    [JsonPropertyName("zombieTypeID")] public string ZombieTypeID { get; set; } = "";
    [JsonPropertyName("spellID")] public string SpellID { get; set; } = "";
    [JsonPropertyName("maxMana")] public float MaxMana { get; set; }
    [JsonPropertyName("manaRegen")] public float ManaRegen { get; set; }
    [JsonPropertyName("weapons")] public List<UnitWeaponRef> Weapons { get; set; } = new();
    [JsonPropertyName("armors")] public List<string> Armors { get; set; } = new();
    [JsonPropertyName("shields")] public List<string> Shields { get; set; } = new();

    [JsonPropertyName("attackAnim")] public string? AttackAnim { get; set; }

    // Combat overrides (nullable = use global CombatSettings default)
    [JsonPropertyName("attackCooldown")] public float? AttackCooldown { get; set; }
    [JsonPropertyName("postAttackLockout")] public float? PostAttackLockout { get; set; }
    [JsonPropertyName("turnSpeed")] public float? TurnSpeed { get; set; }
    [JsonPropertyName("accelHalfTime")] public float? AccelHalfTime { get; set; }
    [JsonPropertyName("accel80Time")] public float? Accel80Time { get; set; }
    [JsonPropertyName("accelFullTime")] public float? AccelFullTime { get; set; }

    /// <summary>
    /// Weapon attachment points: anim name -> yaw angle -> list of WeaponFrameData (one per frame).
    /// </summary>
    [JsonPropertyName("weaponPoints")]
    public Dictionary<string, Dictionary<string, List<WeaponFrameData>>> WeaponPoints { get; set; } = new();

    /// <summary>
    /// Per-animation timing overrides: anim name -> UnitAnimTimingOverride.
    /// </summary>
    [JsonPropertyName("animTimings")]
    public Dictionary<string, UnitAnimTimingOverride> AnimTimings { get; set; } = new();

    // AI behavior framework
    [JsonPropertyName("archetype")] public string Archetype { get; set; } = ""; // ArchetypeRegistry name (empty = legacy AI)

    // Awareness config (used by AwarenessSystem)
    [JsonPropertyName("detectionRange")] public float DetectionRange { get; set; }
    [JsonPropertyName("detectionBreakRange")] public float DetectionBreakRange { get; set; }
    [JsonPropertyName("alertDuration")] public float AlertDuration { get; set; } = 2f;
    [JsonPropertyName("alertEscalateRange")] public float AlertEscalateRange { get; set; }
    [JsonPropertyName("groupAlertRadius")] public float GroupAlertRadius { get; set; }
}

public class UnitRegistry : RegistryBase<UnitDef>
{
    protected override string RootKey => "units";

    public int CountUnitsWithWeapon(string weaponID)
    {
        int count = 0;
        foreach (var def in _defs.Values)
            foreach (var w in def.Weapons)
                if (w.Id == weaponID) { count++; break; }
        return count;
    }

    public int CountUnitsWithArmor(string armorID)
    {
        int count = 0;
        foreach (var def in _defs.Values)
            if (def.Armors.Contains(armorID)) count++;
        return count;
    }

    public int CountUnitsWithShield(string shieldID)
    {
        int count = 0;
        foreach (var def in _defs.Values)
            if (def.Shields.Contains(shieldID)) count++;
        return count;
    }

    public void RemoveWeaponFromAll(string weaponID)
    {
        foreach (var def in _defs.Values)
            def.Weapons.RemoveAll(w => w.Id == weaponID);
    }

    public void RemoveArmorFromAll(string armorID)
    {
        foreach (var def in _defs.Values)
            def.Armors.Remove(armorID);
    }

    public void RemoveShieldFromAll(string shieldID)
    {
        foreach (var def in _defs.Values)
            def.Shields.Remove(shieldID);
    }

    /// <summary>
    /// Resolve a unit's equipment into final combat stats.
    /// Looks up weapons, armor, shields from registries and aggregates.
    /// </summary>
    public UnitStats BuildStats(string id, WeaponRegistry weapons, ArmorRegistry armors, ShieldRegistry shields)
    {
        var def = Get(id);
        if (def == null) return new UnitStats();

        var stats = def.Stats ?? new UnitStatsJson();
        var s = new UnitStats
        {
            MaxHP = stats.MaxHP,
            HP = stats.MaxHP,
            Strength = stats.Strength,
            Attack = stats.Attack,
            Defense = stats.Defense,
            MagicResist = stats.MagicResist,
            Encumbrance = stats.Encumbrance,
            NaturalProt = stats.NaturalProt,
            CombatSpeed = stats.CombatSpeed
        };

        // Resolve weapons. Each weapon slot can carry a per-unit-per-slot anim override
        // that wins over the WeaponDef's anim field. Fallback chain at resolve time:
        //   slot.AnimOverride  (new, per-unit-per-slot)
        //   → WeaponDef.AnimName  (weapon-level default, data/weapons.json "anim")
        //   → null (consumer falls through to UnitDef.AttackAnim or Attack1/Ranged1)
        foreach (var slot in def.Weapons)
        {
            var w = weapons.Get(slot.Id);
            if (w == null) continue;

            string? resolvedAnim = !string.IsNullOrEmpty(slot.AnimOverride)
                ? slot.AnimOverride
                : w.AnimName;

            var ws = new WeaponStats
            {
                Damage = w.Damage,
                AttackBonus = w.AttackBonus,
                DefenseBonus = w.DefenseBonus,
                Length = w.Length,
                Name = w.DisplayName,
                IsRanged = w.IsRanged,
                AnimName = resolvedAnim,
                CooldownRounds = w.CooldownRounds,
                Priority = w.Priority,
                LungeDist = slot.LungeDist,
                PounceMinRange = w.PounceMinRange,
                PounceMaxRange = w.PounceMaxRange,
                PounceArcPeak = w.PounceArcPeak,
                PounceAirSpeed = w.PounceAirSpeed,
                SweepArcDegrees = w.SweepArcDegrees,
                SweepRadius = w.SweepRadius,
                SweepHitsAllies = w.SweepHitsAllies,
            };
            if (System.Enum.TryParse<WeaponArchetype>(w.Archetype, true, out var arch))
                ws.Archetype = arch;

            if (w.IsRanged)
            {
                s.RangedWeapons.Add(ws);
                s.RangedRange.Add(w.Range);
                s.RangedDirectRange.Add(w.DirectRange);
                s.RangedCooldownTime.Add(w.Cooldown);
                s.RangedDmg.Add(w.RangedDamage);
            }
            else
            {
                s.MeleeWeapons.Add(ws);
            }

            foreach (var b in w.Bonuses)
            {
                // Aggregate on UnitStats for legacy consumers + write per-weapon on WeaponStats.
                if (b == "ArmorPiercing") { s.HasArmorPiercing = true; ws.HasArmorPiercing = true; }
                if (b == "ArmorNegating") { s.HasArmorNegating = true; ws.HasArmorNegating = true; }
                if (b == "Knockdown")     { s.HasKnockdown     = true; ws.HasKnockdown     = true; }
            }
        }

        // Stable-sort weapons by Priority descending so Simulation.UpdateCombat's
        // weapon scan picks higher-priority attacks first. LINQ OrderByDescending is
        // stable — equal Priority preserves the original list order (ties break by
        // weapon-list order, same as before the Priority field existed).
        if (s.MeleeWeapons.Count > 1)
            s.MeleeWeapons = System.Linq.Enumerable.ToList(
                System.Linq.Enumerable.OrderByDescending(s.MeleeWeapons, w => w.Priority));
        if (s.RangedWeapons.Count > 1)
            s.RangedWeapons = System.Linq.Enumerable.ToList(
                System.Linq.Enumerable.OrderByDescending(s.RangedWeapons, w => w.Priority));

        // Backward compat: populate primary weapon from first melee
        if (s.MeleeWeapons.Count > 0)
        {
            s.Damage = s.MeleeWeapons[0].Damage;
            s.Length = s.MeleeWeapons[0].Length;
        }

        // Resolve armor
        foreach (var aid in def.Armors)
        {
            var a = armors.Get(aid);
            if (a == null) continue;
            s.Armor.BodyProtection += a.BodyProtection;
            s.Armor.HeadProtection += a.HeadProtection;

            foreach (var b in a.Bonuses)
            {
                if (b == "TrueArmor") s.HasTrueArmor = true;
                if (b == "Barbed") s.HasBarbed = true;
            }
        }

        // Resolve shields — take best
        foreach (var sid in def.Shields)
        {
            var sh = shields.Get(sid);
            if (sh == null) continue;
            if (sh.Parry > s.ShieldParry)
            {
                s.ShieldProtection = sh.Protection;
                s.ShieldParry = sh.Parry;
                s.ShieldDefense = sh.Defense;
            }
        }

        return s;
    }

    /// <summary>
    /// Load weapon_points.json and populate WeaponPoints on each UnitDef.
    /// The file uses flat keys: hx, hy, hb, tx, ty, tb per frame.
    /// </summary>
    public bool LoadWeaponPoints(string path)
    {
        if (!File.Exists(path)) return true; // not an error if file doesn't exist

        try
        {
            string json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("units", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return true;

            foreach (var uobj in arr.EnumerateArray())
            {
                string uid = uobj.TryGetProperty("unit", out var uidProp) ? uidProp.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(uid)) continue;
                var def = Get(uid);
                if (def == null) continue;

                if (!uobj.TryGetProperty("anims", out var anims) || anims.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var animProp in anims.EnumerateObject())
                {
                    string animName = animProp.Name;
                    if (animProp.Value.ValueKind != JsonValueKind.Object) continue;

                    var yawDict = new Dictionary<string, List<WeaponFrameData>>();
                    foreach (var yawProp in animProp.Value.EnumerateObject())
                    {
                        string yawKey = yawProp.Name;
                        if (yawProp.Value.ValueKind != JsonValueKind.Array) continue;

                        var frameList = new List<WeaponFrameData>();
                        foreach (var f in yawProp.Value.EnumerateArray())
                        {
                            var fd = new WeaponFrameData
                            {
                                Hilt = new WeaponPointData
                                {
                                    X = f.TryGetProperty("hx", out var hx) ? hx.GetSingle() : 0f,
                                    Y = f.TryGetProperty("hy", out var hy) ? hy.GetSingle() : 0f,
                                    Behind = f.TryGetProperty("hb", out var hb) && hb.GetBoolean()
                                },
                                Tip = new WeaponPointData
                                {
                                    X = f.TryGetProperty("tx", out var tx) ? tx.GetSingle() : 0f,
                                    Y = f.TryGetProperty("ty", out var ty) ? ty.GetSingle() : 0f,
                                    Behind = f.TryGetProperty("tb", out var tb) && tb.GetBoolean()
                                }
                            };
                            frameList.Add(fd);
                        }
                        yawDict[yawKey] = frameList;
                    }
                    def.WeaponPoints[animName] = yawDict;
                }
            }
            return true;
        }
        catch (Exception ex) { Core.DebugLog.Log("error", $"Failed to load weapon points {path}: {ex.Message}"); return false; }
    }

    /// <summary>
    /// Save weapon_points.json from each UnitDef's WeaponPoints data.
    /// Uses the flat-key format: hx, hy, hb, tx, ty, tb.
    /// </summary>
    public bool SaveWeaponPoints(string path)
    {
        try
        {
            var unitArr = new List<object>();
            foreach (var id in _orderedIDs)
            {
                if (!_defs.TryGetValue(id, out var def)) continue;
                if (def.WeaponPoints.Count == 0) continue;

                var animMap = new Dictionary<string, object>();
                foreach (var (animName, yawDict) in def.WeaponPoints)
                {
                    var yawObj = new Dictionary<string, object>();
                    foreach (var (yawKey, frames) in yawDict)
                    {
                        var frameArr = new List<object>();
                        foreach (var fd in frames)
                        {
                            frameArr.Add(new Dictionary<string, object>
                            {
                                ["hx"] = fd.Hilt.X, ["hy"] = fd.Hilt.Y, ["hb"] = fd.Hilt.Behind,
                                ["tx"] = fd.Tip.X,   ["ty"] = fd.Tip.Y,   ["tb"] = fd.Tip.Behind
                            });
                        }
                        yawObj[yawKey] = frameArr;
                    }
                    animMap[animName] = yawObj;
                }

                unitArr.Add(new Dictionary<string, object>
                {
                    ["unit"] = def.Id,
                    ["anims"] = animMap
                });
            }

            var doc = new Dictionary<string, object> { ["units"] = unitArr };
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(doc, options);
            File.WriteAllText(path, json);
            return true;
        }
        catch (Exception ex) { Core.DebugLog.Log("error", $"Failed to save weapon points {path}: {ex.Message}"); return false; }
    }
}
