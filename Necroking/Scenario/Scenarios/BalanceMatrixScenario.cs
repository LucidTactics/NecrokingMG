using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Unit-balance tournament. For every unordered pair in the roster, finds the
/// squad counts that give a ~50% win rate — "how many X does it take to beat
/// BASE Y?". Each fight is a clean arena duel: both sides spawn as leash-less
/// legacy AttackClosest units (archetype stripped) on opposing factions, so
/// pure stats/weapons decide the outcome, not horde/flee behavior.
///
/// Algorithm per pair: evaluate BASE v BASE first (mirror pairs stop there —
/// they double as a harness sanity check, expected ~50%). If the baseline is
/// outside the 40–60% band, hold the stronger side at BASE and search the
/// weaker side's count upward (Lanchester-seeded guess, then bracket+bisect on
/// integers, capped at MAXCOUNT). Trials per count use early stopping: clearly
/// lopsided counts are abandoned after a few fights.
///
/// Results are re-written to log/balance_results.json after every completed
/// count-config, so a killed run keeps its partial data. The HTML matrix
/// report is generated from that file by tools/balance_report.py.
///
/// Env config (all optional):
///   NECRO_BALANCE_UNITS   comma-separated unit def ids (default: animal zombies)
///   NECRO_BALANCE_BASE    stronger-side squad size (default 5)
///   NECRO_BALANCE_TRIALS  max trials per count-config (default 20)
///   NECRO_BALANCE_MAXCOUNT search cap for the weaker side (default 30)
///   NECRO_BALANCE_FIGHT_TIMEOUT game-seconds before a fight is a draw (default 90)
///
/// Run: bin/Debug/Necroking.exe --scenario balance_matrix --headless --speed 30 --timeout 3600
/// </summary>
public class BalanceMatrixScenario : ScenarioBase
{
    public override string Name => "balance_matrix";

    private const float BandLo = 0.40f, BandHi = 0.60f;
    private const float ArenaX = 32f, ArenaY = 32f;
    private const float SideGap = 9f;      // distance between the two front rows
    private const int RowWidth = 8;        // units per formation row
    private const float Spacing = 1.4f;
    private const float Jitter = 0.35f;

    private string[] _roster =
    {
        "ZombieRat", "WolfCubZombie", "ZombieFemaleDeer", "ZombieMaleDeer",
        "ZombieWolf", "ZombieBoar", "ZombieJuvenileBear", "ZombieGrizzlyBear",
        "ZombieGreatBoar",
    };
    private int _base = 5;
    private int _maxCount = 30;
    private int _maxTrials = 20;
    private int _minTrials = 10;
    private float _fightTimeout = 90f;
    // Extra sim ticks driven per frame by the scenario itself. LaunchArgs.Speed
    // (--speed) is parsed but consumed nowhere in Game1 as of 2026-07, so the
    // scenario batches sim.Tick calls in its own OnTick instead.
    private int _speed = 30;

    // --- results model (serialized to JSON) ---
    public class ConfigResult
    {
        public int CountA { get; set; }
        public int CountB { get; set; }
        public int WinsA { get; set; }
        public int WinsB { get; set; }
        public int Draws { get; set; }
        public float AvgDuration { get; set; }
        public float AvgWinnerSurvivors { get; set; }
        public int Trials => WinsA + WinsB + Draws;
        public float SumDuration;      // accumulators, not serialized (fields)
        public float SumSurvivors;
    }

    public class PairResult
    {
        public string UnitA { get; set; } = "";
        public string UnitB { get; set; } = "";
        public string Resolution { get; set; } = "";  // band | closest | cap | stalemate
        public ConfigResult? Final { get; set; }
        public List<ConfigResult> Configs { get; set; } = new();
    }

    public class RunResult
    {
        public string Generated { get; set; } = "";
        public int BaseCount { get; set; }
        public int MaxTrials { get; set; }
        public string[] Units { get; set; } = Array.Empty<string>();
        public List<PairResult> Pairs { get; set; } = new();
    }

    // --- run state ---
    private enum State { SpawnFight, Fighting, Cleanup, Done }
    private State _state = State.SpawnFight;
    private bool _complete;
    private int _exitCode;

    private RunResult _run = new();
    private List<(int ia, int ib)> _pairs = new();
    private int _pairIndex;
    private int _totalFights;

    // per-pair search state
    private PairResult _pr = new();
    private bool _baselineDone;
    private bool _searchB;               // true → B is the weaker side being scaled up
    private int _lo, _hi, _curr;

    // current config + fight state
    private ConfigResult _cfg = new();
    private int _trialIndex;
    private float _fightElapsed;
    private Faction _facA = Faction.Undead; // side A's faction THIS trial (alternates)

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        // These append-only debug logs would grow by tens of MB over a full
        // tournament (thousands of fights) — start each run fresh.
        DebugLog.Clear("combat");
        DebugLog.Clear("ai");
        DebugLog.Clear("jump");

        var envUnits = Environment.GetEnvironmentVariable("NECRO_BALANCE_UNITS");
        if (!string.IsNullOrWhiteSpace(envUnits))
            _roster = envUnits.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _base = EnvInt("NECRO_BALANCE_BASE", _base);
        _maxTrials = EnvInt("NECRO_BALANCE_TRIALS", _maxTrials);
        _maxCount = EnvInt("NECRO_BALANCE_MAXCOUNT", _maxCount);
        _fightTimeout = EnvInt("NECRO_BALANCE_FIGHT_TIMEOUT", (int)_fightTimeout);
        _speed = EnvInt("NECRO_BALANCE_SPEED", _speed);
        _minTrials = Math.Min(_minTrials, _maxTrials);

        DebugLog.Log(ScenarioLog, $"=== Balance Matrix: {_roster.Length} units, base={_base}, " +
            $"maxTrials={_maxTrials}, maxCount={_maxCount}, fightTimeout={_fightTimeout}s ===");

        foreach (var id in _roster)
        {
            if (sim.GameData.Units.Get(id) == null)
            {
                DebugLog.Log(ScenarioLog, $"FATAL: unit def '{id}' not found in units.json");
                _exitCode = 1;
                _state = State.Done;
                return;
            }
        }

        for (int i = 0; i < _roster.Length; i++)
            for (int j = i; j < _roster.Length; j++)
                _pairs.Add((i, j));

        _run.BaseCount = _base;
        _run.MaxTrials = _maxTrials;
        _run.Units = _roster;
        _run.Generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        DebugLog.Log(ScenarioLog, $"{_pairs.Count} pairs to evaluate");
        StartPair();
        ZoomOnLocation(ArenaX, ArenaY, 20f);
    }

    private static int EnvInt(string name, int fallback)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return int.TryParse(v, out int n) && n > 0 ? n : fallback;
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
            case State.SpawnFight:
                SpawnFight(sim);
                _state = State.Fighting;
                break;
            case State.Fighting:
                TickFight(sim, dt);
                break;
            case State.Cleanup:
                // Fight result was recorded in TickFight; the world was cleared
                // there too. Decide what to run next.
                Advance(sim);
                break;
            case State.Done:
                _complete = true;
                break;
        }
    }

    // ---------------------------------------------------------------- fights

    private void SpawnFight(Simulation sim)
    {
        _fightElapsed = 0f;
        // Deterministic per-trial placement jitter (combat rolls stay random).
        var rng = new Random(_pairIndex * 7919 + _pr.Configs.Count * 613 + _trialIndex * 31 + 1);
        // Alternate every systematic advantage across trials (8-trial cycle
        // covers all combos): which side spawns north (map bias), which side
        // spawns first (lower unit indices act first in the AI/combat passes),
        // and which side is Undead vs Human — boar-mirror diagnostics measured
        // a mild Human-faction edge (~17-9 over 26 trials) whose root cause is
        // still unknown, so it gets averaged out rather than trusted.
        bool swapPos = (_trialIndex & 1) == 1;
        bool swapOrder = (_trialIndex & 2) == 2;
        bool swapFaction = (_trialIndex & 4) == 4;

        var (ia, ib) = _pairs[_pairIndex];
        string unitA = _roster[ia], unitB = _roster[ib];
        // Debug lever: NECRO_BALANCE_INVERT=1 fixes side A as Human (and disables
        // the per-trial faction alternation) to probe side-vs-faction bias.
        string invertEnv = Environment.GetEnvironmentVariable("NECRO_BALANCE_INVERT");
        bool aIsHuman = invertEnv == "1" || (invertEnv == null && swapFaction);
        Faction facA = aIsHuman ? Faction.Human : Faction.Undead;
        Faction facB = aIsHuman ? Faction.Undead : Faction.Human;
        _facA = facA; // TickFight attributes the winner by faction
        if (!swapOrder)
        {
            SpawnSide(sim, unitA, _cfg.CountA, north: !swapPos, facA, rng);
            SpawnSide(sim, unitB, _cfg.CountB, north: swapPos, facB, rng);
        }
        else
        {
            SpawnSide(sim, unitB, _cfg.CountB, north: swapPos, facB, rng);
            SpawnSide(sim, unitA, _cfg.CountA, north: !swapPos, facA, rng);
        }
    }

    private void SpawnSide(Simulation sim, string defId, int count, bool north, Faction faction, Random rng)
    {
        for (int k = 0; k < count; k++)
        {
            int row = k / RowWidth, col = k % RowWidth;
            int rowCount = Math.Min(RowWidth, count - row * RowWidth);
            float jx = (float)(rng.NextDouble() * 2 - 1) * Jitter;
            float jy = (float)(rng.NextDouble() * 2 - 1) * Jitter;
            float x = ArenaX - (rowCount - 1) * Spacing * 0.5f + col * Spacing + jx;
            float dy = SideGap * 0.5f + row * Spacing;
            float y = north ? ArenaY - dy + jy : ArenaY + dy + jy;

            int idx = sim.SpawnUnitByID(defId, new Vec2(x, y));
            var u = sim.UnitsMut[idx];
            u.Faction = faction;
            // Strip the def's archetype (HordeMinion for animal zombies — it
            // needs a necromancer to follow). Legacy AttackClosest is the pure
            // leash-less arena brain; special weapon attacks (pounce, trample)
            // still work under it.
            u.Archetype = AI.ArchetypeRegistry.None;
            u.AI = AIBehavior.AttackClosest;
            u.Routine = 0;
            // No routing on either side: the Undead faction is fearless by rule,
            // so the Human-faction stand-in must be too or morale (not stats)
            // decides matchups. Morale >= 50 counts as mindless/fearless.
            u.Stats.Morale = 100;
        }
    }

    private void TickFight(Simulation sim, float dt)
    {
        _fightElapsed += dt;

        // Melee swings are normally resolved by Game1's animation pass at the
        // swing's action-moment frame — that pass runs once per FRAME, so at
        // _speed sim-ticks per frame every swing would expire unresolved
        // (SwingJanitor). Resolve them here instead: attacks land instantly
        // with no windup. Symmetric for both sides, so relative balance holds.
        // Skip mid-jump units — a pounce queues its attack for the landing and
        // JumpSystem's (sim-side) landing callback resolves it; resolving early
        // would apply pounce damage at liftoff. Same caution for charge phases
        // (TrampleSystem resolves those itself).
        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (!sim.Units[i].Alive || sim.Units[i].PendingAttack.IsNone) continue;
            if (sim.Units[i].Jumping || sim.Units[i].ChargePhase != 0) continue;
            sim.ResolvePendingAttack(i);
        }

        int aliveA = 0, aliveB = 0;
        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (!sim.Units[i].Alive) continue;
            if (sim.Units[i].Faction == _facA) aliveA++;
            else aliveB++;
        }

        bool timeout = _fightElapsed > _fightTimeout;
        if (aliveA > 0 && aliveB > 0 && !timeout) return;

        // record trial
        _totalFights++;
        if (aliveA > 0 && aliveB == 0)
        {
            _cfg.WinsA++;
            _cfg.SumSurvivors += aliveA;
            _cfg.SumDuration += _fightElapsed;
            DebugLog.Log(ScenarioLog, $"    trial {_trialIndex}: A wins ({aliveA} left, {_fightElapsed:F0}s)");
        }
        else if (aliveB > 0 && aliveA == 0)
        {
            _cfg.WinsB++;
            _cfg.SumSurvivors += aliveB;
            _cfg.SumDuration += _fightElapsed;
            DebugLog.Log(ScenarioLog, $"    trial {_trialIndex}: B wins ({aliveB} left, {_fightElapsed:F0}s)");
        }
        else
        {
            _cfg.Draws++;
            DebugLog.Log(ScenarioLog, $"    trial {_trialIndex}: DRAW{(timeout ? " by timeout" : "")} (A={aliveA} B={aliveB} alive)");
        }

        ClearWorld(sim);
        _trialIndex++;
        _state = State.Cleanup;
    }

    private static void ClearWorld(Simulation sim)
    {
        sim.UnitsMut.Clear();
        sim.CorpsesMut.Clear();
        sim.Projectiles.Clear();
        sim.Physics.Clear();
    }

    // ------------------------------------------------------------- algorithm

    /// <summary>Decisive win fraction for side A (draws excluded); -1 if no decisive trials.</summary>
    private static float WinFracA(ConfigResult c)
    {
        int decisive = c.WinsA + c.WinsB;
        return decisive == 0 ? -1f : (float)c.WinsA / decisive;
    }

    private bool ConfigNeedsMoreTrials()
    {
        int t = _cfg.Trials;
        if (t >= _maxTrials) return false;
        int decisive = _cfg.WinsA + _cfg.WinsB;
        if (decisive >= 6 && (_cfg.WinsA == 0 || _cfg.WinsB == 0)) return false; // shutout
        if (t >= _minTrials && decisive > 0)
        {
            float p = (float)_cfg.WinsA / decisive;
            if (Math.Abs(p - 0.5f) > 0.25f) return false; // clearly lopsided
        }
        return true;
    }

    private void Advance(Simulation sim)
    {
        if (ConfigNeedsMoreTrials())
        {
            _state = State.SpawnFight;
            return;
        }
        FinishConfig();

        float p = WinFracA(_cfg);
        if (p < 0f)
        {
            // every trial was a draw — nothing will separate these two
            FinishPair("stalemate", _cfg);
        }
        else if (!_baselineDone)
        {
            _baselineDone = true;
            var (ia, ib) = _pairs[_pairIndex];
            if (ia == ib || (p >= BandLo && p <= BandHi))
            {
                FinishPair("band", _cfg);
            }
            else
            {
                // stronger side stays at _base; weaker side count gets searched
                _searchB = p > BandHi; // A won too much → scale B up
                float q = Math.Clamp(_searchB ? p : 1f - p, 0.65f, 0.95f);
                float mult = MathF.Sqrt(q / (1f - q)); // Lanchester-ish first guess
                _lo = _base;
                _hi = -1;
                _curr = Math.Clamp((int)MathF.Round(_base * mult), _base + 1, _maxCount);
                StartConfig(_curr);
            }
        }
        else
        {
            float pWeak = _searchB ? 1f - p : p;
            if (pWeak >= BandLo && pWeak <= BandHi)
            {
                FinishPair("band", _cfg);
            }
            else if (pWeak < BandLo) // weaker side still loses at _curr
            {
                _lo = _curr;
                if (_hi < 0)
                {
                    if (_curr >= _maxCount) { FinishPair("cap", _cfg); return; }
                    _curr = Math.Min(_maxCount, _curr + Math.Max(1, _curr * 2 / 5));
                    StartConfig(_curr);
                }
                else Bisect();
            }
            else // weaker side now wins too much
            {
                _hi = _curr;
                Bisect();
            }
        }
    }

    private void Bisect()
    {
        int next = (_lo + _hi) / 2;
        if (next <= _lo)
        {
            // adjacent bracket — pick whichever evaluated config sat closest to 50%
            ConfigResult best = _cfg;
            float bestDist = float.MaxValue;
            foreach (var c in _pr.Configs)
            {
                int weakCount = _searchB ? c.CountB : c.CountA;
                if (weakCount != _lo && weakCount != _hi) continue;
                float wp = WinFracA(c);
                if (wp < 0f) continue;
                float d = Math.Abs(wp - 0.5f);
                if (d < bestDist) { bestDist = d; best = c; }
            }
            FinishPair("closest", best);
        }
        else
        {
            _curr = next;
            StartConfig(_curr);
        }
    }

    private void StartConfig(int weakCount)
    {
        _cfg = new ConfigResult
        {
            CountA = _searchB ? _base : weakCount,
            CountB = _searchB ? weakCount : _base,
        };
        _trialIndex = 0;
        _state = State.SpawnFight;
        DebugLog.Log(ScenarioLog, $"  config: {_cfg.CountA} {_pr.UnitA} vs {_cfg.CountB} {_pr.UnitB}");
    }

    private void FinishConfig()
    {
        int decisive = _cfg.WinsA + _cfg.WinsB;
        if (decisive > 0)
        {
            _cfg.AvgDuration = _cfg.SumDuration / decisive;
            _cfg.AvgWinnerSurvivors = _cfg.SumSurvivors / decisive;
        }
        _pr.Configs.Add(_cfg);
        DebugLog.Log(ScenarioLog, $"  result: {_cfg.CountA}v{_cfg.CountB} -> A {_cfg.WinsA} / B {_cfg.WinsB} " +
            $"/ draws {_cfg.Draws} (avgDur {_cfg.AvgDuration:F1}s, avgSurv {_cfg.AvgWinnerSurvivors:F1})");
        WriteResults();
    }

    private void StartPair()
    {
        var (ia, ib) = _pairs[_pairIndex];
        _pr = new PairResult { UnitA = _roster[ia], UnitB = _roster[ib] };
        _run.Pairs.Add(_pr);
        _baselineDone = false;
        _searchB = false;
        DebugLog.Log(ScenarioLog, $"=== Pair {_pairIndex + 1}/{_pairs.Count}: {_pr.UnitA} vs {_pr.UnitB} " +
            $"(fights so far: {_totalFights}) ===");
        StartConfig(_base); // baseline: base v base (_searchB is false → both sides get _base)
    }

    private void FinishPair(string resolution, ConfigResult final)
    {
        _pr.Resolution = resolution;
        _pr.Final = final;
        DebugLog.Log(ScenarioLog, $"  PAIR DONE [{resolution}]: {final.CountA} {_pr.UnitA} ~ {final.CountB} {_pr.UnitB} " +
            $"(A winrate {WinFracA(final):P0} over {final.WinsA + final.WinsB} decisive)");
        WriteResults();

        _pairIndex++;
        if (_pairIndex >= _pairs.Count)
        {
            _state = State.Done;
            return;
        }
        StartPair();
    }

    // --------------------------------------------------------------- output

    private void WriteResults()
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "log");
            Directory.CreateDirectory(dir);
            var opts = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            File.WriteAllText(Path.Combine(dir, "balance_results.json"), JsonSerializer.Serialize(_run, opts));
        }
        catch (Exception e)
        {
            DebugLog.Log(ScenarioLog, $"WARN: failed to write balance_results.json: {e.Message}");
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"=== Balance Matrix complete: {_pairIndex}/{_pairs.Count} pairs, " +
            $"{_totalFights} fights ===");
        WriteResults();
        return _exitCode;
    }
}
