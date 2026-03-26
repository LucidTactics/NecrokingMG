using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;

namespace Necroking.GameSystems;

public enum RegionShape : byte { Rectangle, Circle }
public enum PostSpawnBehavior : byte { Default = 0, Raid, Patrol }

public class TriggerRegion
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public RegionShape Shape { get; set; } = RegionShape.Rectangle;
    public float X { get; set; }
    public float Y { get; set; }
    public float HalfW { get; set; } = 5f;
    public float HalfH { get; set; } = 5f;
    public float Radius { get; set; } = 5f;

    public bool ContainsPoint(Vec2 p) => Shape switch
    {
        RegionShape.Rectangle => p.X >= X - HalfW && p.X <= X + HalfW && p.Y >= Y - HalfH && p.Y <= Y + HalfH,
        RegionShape.Circle => (p - new Vec2(X, Y)).LengthSq() <= Radius * Radius,
        _ => false
    };
}

public class PatrolRoute
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<Vec2> Waypoints { get; set; } = new();
    public bool Loop { get; set; } = true;
}

// Condition types (using class hierarchy instead of C++ variants)
public abstract class ConditionNode
{
    public abstract bool Evaluate(TriggerEvalContext ctx, int instanceIdx);
}

public class CondEntersRegion : ConditionNode
{
    public string RegionID { get; set; } = "";
    public int MinCount { get; set; } = 1;
    public override bool Evaluate(TriggerEvalContext ctx, int instanceIdx) => false; // implemented in TriggerSystem
}

public class CondUnitsKilled : ConditionNode
{
    public int Count { get; set; } = 1;
    public bool Cumulative { get; set; } = true;
    public override bool Evaluate(TriggerEvalContext ctx, int instanceIdx) => false;
}

public class CondGameTime : ConditionNode
{
    public float Time { get; set; }
    public override bool Evaluate(TriggerEvalContext ctx, int instanceIdx) => ctx.GameTime >= Time;
}

public class CondCooldown : ConditionNode
{
    public float Interval { get; set; } = 10f;
    public override bool Evaluate(TriggerEvalContext ctx, int instanceIdx) => false;
}

public class CondAnd : ConditionNode
{
    public List<ConditionNode> Children { get; set; } = new();
    public override bool Evaluate(TriggerEvalContext ctx, int instanceIdx)
    {
        foreach (var c in Children) if (!c.Evaluate(ctx, instanceIdx)) return false;
        return true;
    }
}

public class CondOr : ConditionNode
{
    public List<ConditionNode> Children { get; set; } = new();
    public override bool Evaluate(TriggerEvalContext ctx, int instanceIdx)
    {
        foreach (var c in Children) if (c.Evaluate(ctx, instanceIdx)) return true;
        return false;
    }
}

public class CondNot : ConditionNode
{
    public ConditionNode? Child { get; set; }
    public override bool Evaluate(TriggerEvalContext ctx, int instanceIdx) =>
        Child != null && !Child.Evaluate(ctx, instanceIdx);
}

// Effect types
public abstract class TriggerEffect
{
    public abstract void Execute(TriggerExecContext ctx, int instanceIdx);
}

public class EffActivateTrigger : TriggerEffect
{
    public string TriggerID { get; set; } = "";
    public override void Execute(TriggerExecContext ctx, int instanceIdx) { }
}

public class EffDeactivateTrigger : TriggerEffect
{
    public string TriggerID { get; set; } = "";
    public override void Execute(TriggerExecContext ctx, int instanceIdx) { }
}

public class EffSpawnUnits : TriggerEffect
{
    public string UnitDefID { get; set; } = "";
    public int Count { get; set; } = 1;
    public Faction Faction { get; set; } = Faction.Human;
    public string RegionID { get; set; } = "";
    public Vec2 Position { get; set; }
    public float SpawnAngle { get; set; }
    public float SpawnDistance { get; set; } = 2f;
    public float SpawnInterval { get; set; }
    public PostSpawnBehavior PostBehavior { get; set; } = PostSpawnBehavior.Default;
    public string PatrolRouteID { get; set; } = "";
    public override void Execute(TriggerExecContext ctx, int instanceIdx) { }
}

public class EffKillUnits : TriggerEffect
{
    public string RegionID { get; set; } = "";
    public int MaxKills { get; set; }
    public override void Execute(TriggerExecContext ctx, int instanceIdx) { }
}

public class TriggerDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool ActiveByDefault { get; set; } = true;
    public string BoundObjectID { get; set; } = "";
    public ConditionNode? Condition { get; set; }
    public List<TriggerEffect> Effects { get; set; } = new();
    public bool OneShot { get; set; }
    public int MaxFireCount { get; set; }
}

public class TriggerRuntimeState
{
    public bool Active { get; set; } = true;
    public int FireCount { get; set; }
    public float CooldownTimer { get; set; }
    public int KillCounter { get; set; }
    public bool LastConditionResult { get; set; }
}

public class TriggerInstance
{
    public string InstanceID { get; set; } = "";
    public string ParentTriggerID { get; set; } = "";
    public string BoundObjectID { get; set; } = "";
    public bool ActiveByDefault { get; set; } = true;
    public bool AutoCreated { get; set; }
}

// Context objects for evaluation/execution
public class TriggerEvalContext
{
    public float GameTime;
}

public class TriggerExecContext
{
}
