using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Lib;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Power-progression check: runs a scripted list of matchups ("1 boar should
/// beat 4 male deer but lose to 6 wolves") and reports whether the current
/// unit stats actually produce those outcomes. Unlike balance_matrix (which
/// SEARCHES for the ~50% body count per pair), this scenario asserts a fixed
/// design intent: each matchup names side A (the hero), side B (a composition
/// of one or more unit types), and the expected winner.
///
/// All trials of a matchup run IN PARALLEL: one fight per arena on a 5x2 grid
/// of arena cells separated by deep-water dividers (unpathable, cost 255), so
/// the fights can't path into each other. Wins are attributed by per-arena
/// unit-Id sets — not faction — and the moment an arena resolves its survivors
/// are despawned (PendingDespawn, no corpse) so leash-less AttackClosest units
/// can't march toward a neighboring arena's fight.
///
/// Fight mechanics are copied from BalanceMatrixScenario: archetype stripped
/// (HordeMinion needs a necromancer), legacy AttackClosest, Morale=100 on both
/// sides so stats decide (not routing), and pending melee swings resolved by
/// the scenario every sim tick (skipping mid-pounce / mid-charge units) since
/// Game1's once-per-frame animation pass can't keep up with fast-forward.
/// Spawn side / spawn order / faction alternate per trial (8-trial cycle) to
/// cancel systematic biases.
///
/// Results are re-written to log/power_progression_results.json after every
/// wave, so a killed run keeps partial data. The HTML report is generated from
/// that file by tools/power_report/make_report.py.
///
/// Env config (all optional):
///   NECRO_POWER_TRIALS         trials per matchup (default 10; >10 runs in waves)
///   NECRO_POWER_FIGHT_TIMEOUT  game-seconds before an unresolved fight is a draw (default 120)
///   NECRO_POWER_SPEED          sim ticks per frame (default 30)
///
/// Run: bin/Debug/Necroking.exe --scenario power_progression --headless --timeout 3600
/// </summary>
public class PowerProgressionScenario : ScenarioBase
{
    public override string Name => "power_progression";
    public override bool WantsGround => true;
    public override int GridSize => 224;

    // Arena layout: Cols x Rows cells of CellSize tiles, deep-water dividers
    // on the cell boundaries. One trial fights per cell.
    private const int Cols = 5, Rows = 2;
    private const float CellSize = 44f;
    private const int DividerHalf = 2;     // divider half-thickness in vertices (~4-5 unpathable tiles)
    private const float SideGap = 9f;      // distance between the two front rows
    private const int RowWidth = 8;        // units per formation row
    private const float Spacing = 1.4f;
    private const float Jitter = 0.35f;

    private int _trials = 10;
    private float _fightTimeout = 120f;
    // Extra sim ticks driven per frame by the scenario itself (LaunchArgs.Speed
    // is parsed but consumed nowhere in Game1 — same batching as balance_matrix).
    private int _speed = 30;

    // --- results model (serialized to JSON) ---
    public class SidePart
    {
        public string Unit { get; set; } = "";
        public int Count { get; set; }
    }

    public class TrialResult
    {
        public string Winner { get; set; } = "";  // A | B | draw
        public int CasualtiesA { get; set; }
        public int CasualtiesB { get; set; }
        public float Duration { get; set; }
    }

    public class MatchupResult
    {
        public string Label { get; set; } = "";
        public List<SidePart> SideA { get; set; } = new();
        public List<SidePart> SideB { get; set; } = new();
        public string ExpectedWinner { get; set; } = "A"; // A | B
        public string Note { get; set; } = "";
        public int SpawnedA { get; set; }         // per trial
        public int SpawnedB { get; set; }
        public int WinsA { get; set; }
        public int WinsB { get; set; }
        public int Draws { get; set; }
        public float WinRateA { get; set; }       // of decisive trials
        public float AvgCasualtiesA { get; set; }
        public float AvgCasualtiesB { get; set; }
        public float AvgDuration { get; set; }
        public bool Pass { get; set; }
        public List<TrialResult> Trials { get; set; } = new();
    }

    public class RunResult
    {
        public string Generated { get; set; } = "";
        public int TrialsPerMatchup { get; set; }
        public float FightTimeout { get; set; }
        public List<MatchupResult> Matchups { get; set; } = new();
    }

    // --- the scripted progression (design intent lives here) ---
    private static SidePart[] S(params (string unit, int count)[] parts)
    {
        var list = new SidePart[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            list[i] = new SidePart { Unit = parts[i].unit, Count = parts[i].count };
        return list;
    }

    private class MatchupDef
    {
        public string Label = "";
        public SidePart[] A = Array.Empty<SidePart>();
        public SidePart[] B = Array.Empty<SidePart>();
        public bool ExpectAWins;
        public string Note = "";
    }

    private static MatchupDef M(string label, SidePart[] a, SidePart[] b, bool expectAWins, string note = "")
        => new() { Label = label, A = a, B = b, ExpectAWins = expectAWins, Note = note };

    private const string Rat = "ZombieRat";
    private const string FDeer = "ZombieFemaleDeer";
    private const string MDeer = "ZombieMaleDeer";
    private const string Wolf = "ZombieWolf";
    private const string Boar = "ZombieBoar";
    private const string Bear = "ZombieJuvenileBear";
    private const string Grizzly = "ZombieGrizzlyBear";

    private readonly MatchupDef[] _matchups =
    {
        M("1 Female Deer vs 3 Rats",          S((FDeer, 1)),   S((Rat, 3)),                 expectAWins: true),
        M("1 Female Deer vs 5 Rats",          S((FDeer, 1)),   S((Rat, 5)),                 expectAWins: false),
        M("1 Male Deer vs 2 Female Deer",     S((MDeer, 1)),   S((FDeer, 2)),               expectAWins: true),
        M("1 Male Deer vs 4 Female Deer",     S((MDeer, 1)),   S((FDeer, 4)),               expectAWins: false),
        M("1 Male Deer vs 1 Wolf",            S((MDeer, 1)),   S((Wolf, 1)),                expectAWins: true, note: "should be somewhat close"),
        M("1 Male Deer vs 2 Wolves",          S((MDeer, 1)),   S((Wolf, 2)),                expectAWins: false),
        M("1 Boar vs 4 Male Deer",            S((Boar, 1)),    S((MDeer, 4)),               expectAWins: true),
        M("1 Boar vs 6 Wolves",               S((Boar, 1)),    S((Wolf, 6)),                expectAWins: false),
        M("1 Bear vs 10 Wolves",              S((Bear, 1)),    S((Wolf, 10)),               expectAWins: true),
        M("1 Bear vs 10 Male Deer",           S((Bear, 1)),    S((MDeer, 10)),              expectAWins: true),
        M("1 Bear vs 2 Boars + 4 Male Deer",  S((Bear, 1)),    S((Boar, 2), (MDeer, 4)),    expectAWins: false),
        M("1 Grizzly vs 15 Wolves",           S((Grizzly, 1)), S((Wolf, 15)),               expectAWins: true),
        M("1 Grizzly vs 15 Male Deer",        S((Grizzly, 1)), S((MDeer, 15)),              expectAWins: true),
        M("1 Grizzly vs 4 Boars",             S((Grizzly, 1)), S((Boar, 4)),                expectAWins: false),
    };

    // --- run state ---
    private enum State { StartMatchup, SpawnWave, Fighting, Done }
    private State _state = State.StartMatchup;
    private bool _complete;
    private int _exitCode;

    private RunResult _run = new();
    private int _matchupIndex;
    private MatchupResult _mr = new();
    private int _trialsDone;

    // wave state: one Arena per concurrently-running trial
    private class Arena
    {
        public int TrialIndex;
        public bool Done;
        public int SpawnedA, SpawnedB;
        public int AliveA, AliveB;
    }

    private readonly List<Arena> _arenas = new();
    // unit Id -> (arena index in _arenas, is side A). Rebuilt every wave.
    private readonly Dictionary<uint, (int arena, bool sideA)> _unitMap = new();
    private float _fightElapsed;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        // These append-only debug logs grow by tens of MB over hundreds of
        // fights — start each run fresh (same hygiene as balance_matrix).
        DebugLog.Clear("combat");
        DebugLog.Clear("ai");
        DebugLog.Clear("jump");

        _trials = EnvInt("NECRO_POWER_TRIALS", _trials);
        _fightTimeout = EnvInt("NECRO_POWER_FIGHT_TIMEOUT", (int)_fightTimeout);
        _speed = EnvInt("NECRO_POWER_SPEED", _speed);

        DebugLog.Log(ScenarioLog, $"=== Power Progression: {_matchups.Length} matchups, " +
            $"{_trials} trials each, fightTimeout={_fightTimeout}s ===");

        foreach (var m in _matchups)
            foreach (var part in Concat(m.A, m.B))
                if (sim.GameData.Units.Get(part.Unit) == null)
                {
                    DebugLog.Log(ScenarioLog, $"FATAL: unit def '{part.Unit}' not found in units.json");
                    _exitCode = 1;
                    _state = State.Done;
                    return;
                }

        PaintDividers();

        _run.TrialsPerMatchup = _trials;
        _run.FightTimeout = _fightTimeout;
        _run.Generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        ZoomOnLocation(Cols * CellSize * 0.5f, Rows * CellSize * 0.5f, 8f);
    }

    private static IEnumerable<SidePart> Concat(SidePart[] a, SidePart[] b)
    {
        foreach (var p in a) yield return p;
        foreach (var p in b) yield return p;
    }

    private static int EnvInt(string name, int fallback)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return int.TryParse(v, out int n) && n > 0 ? n : fallback;
    }

    /// <summary>Deep-water bars on the arena-cell boundaries: vertical lines
    /// between columns, horizontal between rows. Deep water stamps cost 255
    /// into the path grid (see deep_water_blocks), so a finished-but-not-yet-
    /// despawned survivor can't wander into a neighboring fight.</summary>
    private void PaintDividers()
    {
        if (GroundSystem == null) return;
        int deep = GroundSystem.FindType("deep_water");
        if (deep < 0)
        {
            DebugLog.Log(ScenarioLog, "WARN: deep_water ground type not registered; arenas not walled off");
            return;
        }
        int vw = GroundSystem.VertexW, vh = GroundSystem.VertexH;
        for (int c = 1; c < Cols; c++)
        {
            int x0 = (int)(c * CellSize) - DividerHalf, x1 = (int)(c * CellSize) + DividerHalf;
            for (int vy = 0; vy < vh; vy++)
                for (int vx = Math.Max(0, x0); vx <= x1 && vx < vw; vx++)
                    GroundSystem.SetVertex(vx, vy, (byte)deep);
        }
        for (int r = 1; r < Rows; r++)
        {
            int y0 = (int)(r * CellSize) - DividerHalf, y1 = (int)(r * CellSize) + DividerHalf;
            for (int vx = 0; vx < vw; vx++)
                for (int vy = Math.Max(0, y0); vy <= y1 && vy < vh; vy++)
                    GroundSystem.SetVertex(vx, vy, (byte)deep);
        }
    }

    public override void OnTick(Simulation sim, float dt)
    {
        Step(sim, dt);
        // Fast-forward: Game1 ran one sim tick before calling us; drive the rest
        // of the batch ourselves (sim.Tick is the pure, headless-safe step).
        for (int k = 1; k < _speed && !_complete; k++)
        {
            sim.Tick(1f / 60f);
            Step(sim, 1f / 60f);
        }
    }

    private void Step(Simulation sim, float dt)
    {
        switch (_state)
        {
            case State.StartMatchup:
                StartMatchup();
                break;
            case State.SpawnWave:
                SpawnWave(sim);
                _state = State.Fighting;
                break;
            case State.Fighting:
                TickFight(sim, dt);
                break;
            case State.Done:
                _complete = true;
                break;
        }
    }

    // ---------------------------------------------------------------- fights

    private void StartMatchup()
    {
        var def = _matchups[_matchupIndex];
        _mr = new MatchupResult
        {
            Label = def.Label,
            ExpectedWinner = def.ExpectAWins ? "A" : "B",
            Note = def.Note,
        };
        _mr.SideA.AddRange(def.A);
        _mr.SideB.AddRange(def.B);
        foreach (var p in def.A) _mr.SpawnedA += p.Count;
        foreach (var p in def.B) _mr.SpawnedB += p.Count;
        _run.Matchups.Add(_mr);
        _trialsDone = 0;
        _state = State.SpawnWave;
        DebugLog.Log(ScenarioLog, $"=== Matchup {_matchupIndex + 1}/{_matchups.Length}: {def.Label} " +
            $"(expect {_mr.ExpectedWinner} wins) ===");
    }

    private void SpawnWave(Simulation sim)
    {
        _arenas.Clear();
        _unitMap.Clear();
        _fightElapsed = 0f;

        var def = _matchups[_matchupIndex];
        int waveSize = Math.Min(_trials - _trialsDone, Cols * Rows);
        for (int a = 0; a < waveSize; a++)
        {
            int trialIndex = _trialsDone + a;
            float cx = (a % Cols + 0.5f) * CellSize;
            float cy = (a / Cols + 0.5f) * CellSize;
            var arena = new Arena { TrialIndex = trialIndex };
            _arenas.Add(arena);

            // Deterministic per-trial placement jitter (combat rolls stay random).
            var rng = new Random(_matchupIndex * 7919 + trialIndex * 31 + 1);
            // Alternate every systematic advantage across trials (8-trial cycle):
            // which side spawns north, which spawns first (lower unit indices act
            // first in the AI/combat passes), and which side is Undead vs Human
            // (balance_matrix measured a mild unexplained Human-faction edge).
            bool swapPos = (trialIndex & 1) == 1;
            bool swapOrder = (trialIndex & 2) == 2;
            bool swapFaction = (trialIndex & 4) == 4;
            Faction facA = swapFaction ? Faction.Human : Faction.Undead;
            Faction facB = swapFaction ? Faction.Undead : Faction.Human;

            if (!swapOrder)
            {
                arena.SpawnedA = SpawnSide(sim, def.A, cx, cy, north: !swapPos, facA, rng, _arenas.Count - 1, sideA: true);
                arena.SpawnedB = SpawnSide(sim, def.B, cx, cy, north: swapPos, facB, rng, _arenas.Count - 1, sideA: false);
            }
            else
            {
                arena.SpawnedB = SpawnSide(sim, def.B, cx, cy, north: swapPos, facB, rng, _arenas.Count - 1, sideA: false);
                arena.SpawnedA = SpawnSide(sim, def.A, cx, cy, north: !swapPos, facA, rng, _arenas.Count - 1, sideA: true);
            }
        }
    }

    private int SpawnSide(Simulation sim, SidePart[] comp, float cx, float cy, bool north,
        Faction faction, Random rng, int arenaIndex, bool sideA)
    {
        int total = 0;
        foreach (var p in comp) total += p.Count;

        int k = 0;
        foreach (var part in comp)
        {
            for (int n = 0; n < part.Count; n++, k++)
            {
                int row = k / RowWidth, col = k % RowWidth;
                int rowCount = Math.Min(RowWidth, total - row * RowWidth);
                float jx = (float)(rng.NextDouble() * 2 - 1) * Jitter;
                float jy = (float)(rng.NextDouble() * 2 - 1) * Jitter;
                float x = cx - (rowCount - 1) * Spacing * 0.5f + col * Spacing + jx;
                float dy = SideGap * 0.5f + row * Spacing;
                float y = north ? cy - dy + jy : cy + dy + jy;

                int idx = sim.SpawnUnitByID(part.Unit, new Vec2(x, y));
                var u = sim.UnitsMut[idx];
                u.Faction = faction;
                // Strip the def's archetype (HordeMinion for animal zombies — it
                // needs a necromancer to follow). Legacy AttackClosest is the pure
                // leash-less arena brain; pounce/trample still work under it.
                u.Archetype = AI.ArchetypeRegistry.None;
                u.AI = AIBehavior.AttackClosest;
                u.Routine = 0;
                // Both sides fearless so stats decide matchups, not morale.
                u.Stats.Morale = 100;
                _unitMap[u.Id] = (arenaIndex, sideA);
            }
        }
        return total;
    }

    private void TickFight(Simulation sim, float dt)
    {
        _fightElapsed += dt;

        // Melee swings are normally resolved by Game1's animation pass once per
        // FRAME — at _speed sim-ticks per frame every swing would expire
        // unresolved. Resolve them here (symmetric for both sides). Skip
        // mid-jump / mid-charge units: pounce and trample resolve themselves.
        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (!sim.Units[i].Alive || sim.Units[i].PendingAttack.IsNone) continue;
            if (sim.Units[i].Jumping || sim.Units[i].ChargePhase != 0) continue;
            GameSystems.Combat.AttackResolver.ResolvePendingAttack(sim, i);
        }

        foreach (var arena in _arenas) { arena.AliveA = 0; arena.AliveB = 0; }
        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (!sim.Units[i].Alive) continue;
            if (!_unitMap.TryGetValue(sim.Units[i].Id, out var loc)) continue;
            var arena = _arenas[loc.arena];
            if (loc.sideA) arena.AliveA++; else arena.AliveB++;
        }

        bool timeout = _fightElapsed > _fightTimeout;
        bool allDone = true;
        foreach (var arena in _arenas)
        {
            if (arena.Done) continue;
            if (arena.AliveA > 0 && arena.AliveB > 0 && !timeout) { allDone = false; continue; }
            RecordArena(sim, arena, timeout);
        }
        if (!allDone) return;

        _trialsDone += _arenas.Count;
        ClearWorld(sim);
        WriteResults(); // wave checkpoint — a killed run keeps its data

        if (_trialsDone >= _trials)
            FinishMatchup();
        else
            _state = State.SpawnWave;
    }

    private void RecordArena(Simulation sim, Arena arena, bool timeout)
    {
        arena.Done = true;
        var trial = new TrialResult
        {
            Winner = arena.AliveA > 0 && arena.AliveB == 0 ? "A"
                   : arena.AliveB > 0 && arena.AliveA == 0 ? "B" : "draw",
            CasualtiesA = arena.SpawnedA - arena.AliveA,
            CasualtiesB = arena.SpawnedB - arena.AliveB,
            Duration = _fightElapsed,
        };
        _mr.Trials.Add(trial);
        switch (trial.Winner)
        {
            case "A": _mr.WinsA++; break;
            case "B": _mr.WinsB++; break;
            default: _mr.Draws++; break;
        }
        DebugLog.Log(ScenarioLog, $"    trial {arena.TrialIndex}: {trial.Winner}" +
            $"{(trial.Winner == "draw" && timeout ? " by timeout" : "")} " +
            $"(A lost {trial.CasualtiesA}/{arena.SpawnedA}, B lost {trial.CasualtiesB}/{arena.SpawnedB}, " +
            $"{trial.Duration:F0}s)");

        // Retire the survivors NOW: leash-less AttackClosest targets the globally
        // nearest enemy, so an idle winner would path toward a neighboring
        // arena's fight. PendingDespawn vanishes them (no corpse) in
        // Simulation.RemoveDeadUnits' pre-pass — never remove mid-fight.
        int arenaIndex = _arenas.IndexOf(arena);
        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (!sim.Units[i].Alive) continue;
            if (_unitMap.TryGetValue(sim.Units[i].Id, out var loc) && loc.arena == arenaIndex)
                sim.UnitsMut[i].PendingDespawn = true;
        }
    }

    private static void ClearWorld(Simulation sim)
    {
        sim.UnitsMut.Clear();
        sim.CorpsesMut.Clear();
        sim.Projectiles.Clear();
        sim.Physics.Clear();
    }

    private void FinishMatchup()
    {
        int decisive = _mr.WinsA + _mr.WinsB;
        _mr.WinRateA = decisive > 0 ? (float)_mr.WinsA / decisive : 0f;
        float sumCasA = 0f, sumCasB = 0f, sumDur = 0f;
        foreach (var t in _mr.Trials) { sumCasA += t.CasualtiesA; sumCasB += t.CasualtiesB; sumDur += t.Duration; }
        int n = _mr.Trials.Count;
        if (n > 0)
        {
            _mr.AvgCasualtiesA = sumCasA / n;
            _mr.AvgCasualtiesB = sumCasB / n;
            _mr.AvgDuration = sumDur / n;
        }
        bool aWinsMajority = decisive > 0 && _mr.WinRateA > 0.5f;
        _mr.Pass = decisive > 0 && aWinsMajority == (_mr.ExpectedWinner == "A");

        DebugLog.Log(ScenarioLog, $"  MATCHUP {(_mr.Pass ? "PASS" : "FAIL")}: {_mr.Label} — " +
            $"A wins {_mr.WinsA}/{n} ({_mr.WinRateA:P0} of decisive, expected {_mr.ExpectedWinner}), " +
            $"avg casualties A {_mr.AvgCasualtiesA:F1}/{_mr.SpawnedA} B {_mr.AvgCasualtiesB:F1}/{_mr.SpawnedB}");
        WriteResults();

        _matchupIndex++;
        _state = _matchupIndex >= _matchups.Length ? State.Done : State.StartMatchup;
    }

    // --------------------------------------------------------------- output

    private void WriteResults()
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "log");
            Directory.CreateDirectory(dir);
            var opts = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            File.WriteAllText(Path.Combine(dir, "power_progression_results.json"), JsonSerializer.Serialize(_run, opts));
        }
        catch (Exception e)
        {
            DebugLog.Log(ScenarioLog, $"WARN: failed to write power_progression_results.json: {e.Message}");
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        int pass = 0, fail = 0;
        foreach (var m in _run.Matchups)
        {
            if (m.Pass) pass++; else fail++;
        }
        DebugLog.Log(ScenarioLog, $"=== Power Progression complete: {pass} pass / {fail} fail " +
            $"of {_run.Matchups.Count} matchups ===");
        foreach (var m in _run.Matchups)
            DebugLog.Log(ScenarioLog, $"  [{(m.Pass ? "PASS" : "FAIL")}] {m.Label}: A {m.WinRateA:P0} " +
                $"({m.WinsA}-{m.WinsB}-{m.Draws}), expected {m.ExpectedWinner}");
        WriteResults();
        return _exitCode;
    }
}
