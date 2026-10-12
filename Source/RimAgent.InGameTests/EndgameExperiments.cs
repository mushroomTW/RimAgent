using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using RimAgent.Agent;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent.InGameTests
{
    /// <summary>
    /// 結局路線的真模型實驗（不是測試，不斷言）共用的骨架：只有隔離環境的 Config 裡有標記檔才啟動，
    /// 開局把模型改成免費版、開自主模式與執行紀錄，切超加速，遊戲無故停住太久就恢復，每次取樣寫一行 JSONL。
    /// 需要 seed 玩家的 RimAgent 設定、框架的供應商設定與金鑰檔（見各標記檔的註解）。
    /// </summary>
    public abstract class EndgameExperimentBase : GameComponent
    {
        /// <summary>BYOK 只走帶 :free 的端點；不帶的版本由第三方提供者服務、扣 Credits。</summary>
        public const string FreeModel = "OpenRouter:nvidia/nemotron-3-super-120b-a12b:free";
        private const float PausedResumeSeconds = 20f;

        private bool _started;
        private bool _speedApplied;
        private float _pausedSince = -1f;
        private string _timelinePath;

        protected bool Done { get; private set; }
        protected int StartTick { get; private set; }
        protected float StartRealtime { get; private set; }

        protected abstract string MarkerFileName { get; }
        protected abstract string TimelineFileName { get; }
        protected abstract int SampleTicks { get; }
        protected string LogPrefix => TestHelpers.LogPrefix + GetType().Name + ": ";

        protected string MarkerText { get; private set; }

        private bool Enabled
        {
            get
            {
                if (MarkerText != null) return true;
                string path = Path.Combine(GenFilePaths.ConfigFolderPath, MarkerFileName);
                if (!File.Exists(path)) return false;
                MarkerText = File.ReadAllText(path);
                return true;
            }
        }

        public override void GameComponentTick()
        {
            if (Done || !Enabled) return;
            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) return;
            int tick = Find.TickManager.TicksGame;
            if (!_started)
            {
                _started = true;
                StartTick = tick;
                StartRealtime = Time.realtimeSinceStartup;
                string folder = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimAgent");
                Directory.CreateDirectory(folder);
                _timelinePath = Path.Combine(folder, TimelineFileName);
                if (!Begin(tick)) Done = true;
                return;
            }
            if (!_speedApplied)
            {
                // 原版在 PlayerHasControl 為 false 時靜默忽略速度設定，逐 tick 重設到讀回為止。
                Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
                _speedApplied = Find.TickManager.CurTimeSpeed == TimeSpeed.Superfast;
            }
            Step(tick);
            if (tick % SampleTicks == 0) Write(Sample(tick));
        }

        public override void GameComponentUpdate()
        {
            if (Done || !_started) return;
            if (!Find.TickManager.Paused || Find.WindowStack.WindowsForcePause)
            {
                _pausedSince = -1f;
                return;
            }
            if (_pausedSince < 0f)
            {
                _pausedSince = Time.realtimeSinceStartup;
                return;
            }
            if (Time.realtimeSinceStartup - _pausedSince < PausedResumeSeconds) return;
            _pausedSince = -1f;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
            Note("resumed after pause");
        }

        /// <summary>開局設定；回傳 false 表示前提不成立、不跑。</summary>
        protected abstract bool Begin(int tick);

        /// <summary>每 tick 推進情境（觸發事件、判定結束）。</summary>
        protected abstract void Step(int tick);

        protected abstract Dictionary<string, object> Sample(int tick);

        /// <summary>把模型改成免費版、開自主與紀錄（只在記憶體裡改，不寫回設定檔），並跳過「玩家先講話」的啟動門檻。</summary>
        protected bool EnableAgent(bool autonomous)
        {
            RimAgentSettings settings = RimAgentMod.Settings;
            RimAgentGameComponent agent = RimAgentGameComponent.Instance;
            if (settings == null || agent == null)
            {
                Log.Error(LogPrefix + $"not started: RimAgent settings={settings != null}, component={agent != null}");
                return false;
            }
            settings.agentModel = FreeModel;
            settings.recordRuns = true;
            settings.mode = autonomous ? AgentMode.Autonomous : AgentMode.Ask;
            if (autonomous) agent.StartAutonomousAfterPlayerMessage();
            return true;
        }

        protected static int ClearHostiles(Map map)
        {
            List<Thing> hostiles = map.attackTargetsCache.TargetsHostileToColony.Select(t => t.Thing).Where(t => t != null && !t.Destroyed).ToList();
            foreach (Thing thing in hostiles) thing.Destroy(DestroyMode.Vanish);
            return hostiles.Count;
        }

        protected void Finish(int tick, Dictionary<string, object> summary)
        {
            summary["tick"] = tick;
            summary["note"] = "summary";
            Write(summary);
            Done = true;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Log.Message(LogPrefix + "done: " + JsonSerializer.Serialize(summary));
        }

        protected void Note(string note)
        {
            Write(new Dictionary<string, object> { ["tick"] = Find.TickManager.TicksGame, ["note"] = note });
        }

        private void Write(Dictionary<string, object> line)
        {
            try
            {
                line["realSeconds"] = Time.realtimeSinceStartup - StartRealtime;
                File.AppendAllText(_timelinePath, JsonSerializer.Serialize(line) + "\n", new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Warning(LogPrefix + "write failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    /// <summary>
    /// 08 防禦：開局給 Agent 一段準備時間，再發動固定強度的襲擊，記錄殖民者倒地／死亡、徵召人數與敵人存活，
    /// 敵人清光（或時間到）就結束。標記檔根節點帶 baseline="true" 時不開 Agent，當對照組。
    /// </summary>
    public sealed class RaidDefenseExperimentComponent : EndgameExperimentBase
    {
        private const int PrepTicks = 15000;
        private const int RaidLimitTicks = 60000;
        private const int ClearHoldTicks = 2500;
        private const float RaidPoints = 400f;

        private List<Pawn> _colonists = new List<Pawn>();
        private List<Pawn> _raiders = new List<Pawn>();
        private bool _baseline;
        private int _raidTick = -1;
        private int _clearSince = -1;
        private int _maxDowned;

        public RaidDefenseExperimentComponent(Verse.Game game)
        {
        }

        protected override string MarkerFileName => "RimAgentRaidExperiment.xml";
        protected override string TimelineFileName => "RaidExperiment.jsonl";
        protected override int SampleTicks => 250;

        protected override bool Begin(int tick)
        {
            Map map = Find.CurrentMap;
            // 只看根節點屬性；註解裡提到 baseline 不算。
            _baseline = MarkerText.IndexOf("baseline=\"true\"", StringComparison.OrdinalIgnoreCase) >= 0;
            int cleared = ClearHostiles(map);
            _colonists = map.mapPawns.FreeColonistsSpawned.ToList();
            if (!EnableAgent(!_baseline)) return false;
            Note($"start: baseline={_baseline}, colonists={_colonists.Count}, cleared {cleared} hostiles");
            return true;
        }

        protected override void Step(int tick)
        {
            Map map = Find.CurrentMap;
            if (_raidTick < 0)
            {
                if (tick - StartTick >= PrepTicks) LaunchRaid(map, tick);
                return;
            }
            _maxDowned = Math.Max(_maxDowned, _colonists.Count(p => !p.Dead && p.Downed));
            int active = ActiveHostiles(map);
            if (active == 0)
            {
                if (_clearSince < 0) _clearSince = tick;
            }
            else
            {
                _clearSince = -1;
            }
            bool cleared = _clearSince >= 0 && tick - _clearSince >= ClearHoldTicks;
            if (cleared || tick - _raidTick >= RaidLimitTicks)
            {
                Finish(tick, new Dictionary<string, object>
                {
                    ["baseline"] = _baseline,
                    ["raidCleared"] = cleared,
                    ["ticksToClear"] = cleared ? _clearSince - _raidTick : -1,
                    ["colonists"] = _colonists.Count,
                    ["dead"] = _colonists.Count(p => p.Dead),
                    ["maxDowned"] = _maxDowned,
                    ["raiders"] = _raiders.Count,
                    ["raidersDead"] = _raiders.Count(p => p.Dead),
                    ["raidersDowned"] = _raiders.Count(p => !p.Dead && p.Downed),
                    ["hostilesLeft"] = active
                });
            }
        }

        private void LaunchRaid(Map map, int tick)
        {
            Faction enemy = Find.FactionManager.RandomEnemyFaction(allowNonHumanlike: false);
            var parms = new IncidentParms
            {
                target = map,
                points = RaidPoints,
                faction = enemy,
                raidStrategy = RaidStrategyDefOf.ImmediateAttack,
                raidArrivalMode = PawnsArrivalModeDefOf.EdgeWalkIn,
                forced = true
            };
            bool fired = enemy != null && IncidentDefOf.RaidEnemy.Worker.TryExecute(parms);
            _raidTick = tick;
            // 死掉的 pawn 會離開 mapPawns，開打時就記下這批襲擊者。
            _raiders = map.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(Faction.OfPlayer) && !p.IsPrisonerOfColony).ToList();
            Note($"raid fired={fired}, faction={enemy?.Name}, points={RaidPoints}");
        }

        private static int ActiveHostiles(Map map) =>
            map.mapPawns.AllPawnsSpawned.Count(p => !p.Dead && !p.Downed && p.HostileTo(Faction.OfPlayer) && !p.IsPrisonerOfColony);

        protected override Dictionary<string, object> Sample(int tick)
        {
            Map map = Find.CurrentMap;
            return new Dictionary<string, object>
            {
                ["tick"] = tick,
                ["phase"] = _raidTick < 0 ? "prep" : "raid",
                ["speed"] = Find.TickManager.CurTimeSpeed.ToString(),
                ["alive"] = _colonists.Count(p => !p.Dead),
                ["downed"] = _colonists.Count(p => !p.Dead && p.Downed),
                ["drafted"] = _colonists.Count(p => !p.Dead && p.Drafted),
                ["hostiles"] = ActiveHostiles(map),
                ["sandbags"] = map.listerThings.AllThings.Count(t => t.def.defName == "Sandbags" || t.def.entityDefToBuild?.defName == "Sandbags"),
                ["blueprints"] = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count
            };
        }
    }

    /// <summary>
    /// 11 飛船：開局完成飛船研究鏈、在殖民者旁放足材料與 AI 人格核心，以長期目標要 Agent 蓋船，
    /// 取樣時檢查船體規劃（藍圖、框架、成品合計）是否零件齊全、四方向相連、沒有被屋頂蓋住。
    /// </summary>
    public sealed class ShipLayoutExperimentComponent : EndgameExperimentBase
    {
        private const int DurationTicks = 60000;
        private const string Goal =
            "Build the starship and launch it. All ship research is finished and the materials, including an AI persona core, are already on the map near the colonists. Start by placing blueprints for every part the launch needs.";

        private static readonly string[] ShipResearch =
            { "ShipBasics", "ShipCryptosleep", "ShipComputerCore", "ShipSensorCluster", "ShipReactor", "ShipEngine" };

        private Dictionary<string, object> _lastPlan;

        public ShipLayoutExperimentComponent(Verse.Game game)
        {
        }

        protected override string MarkerFileName => "RimAgentShipExperiment.xml";
        protected override string TimelineFileName => "ShipExperiment.jsonl";
        protected override int SampleTicks => 1000;

        protected override bool Begin(int tick)
        {
            Map map = Find.CurrentMap;
            int cleared = ClearHostiles(map);
            int researched = FinishResearch();
            int stacks = SpawnMaterials(map);
            if (!EnableAgent(autonomous: true)) return false;
            bool added = RimAgentGameComponent.Instance.AddGoal(Goal);
            Note($"start: researched {researched}, spawned {stacks} stacks, goal added={added}, cleared {cleared} hostiles");
            return added;
        }

        protected override void Step(int tick)
        {
            if (tick - StartTick < DurationTicks) return;
            Dictionary<string, object> summary = _lastPlan != null ? new Dictionary<string, object>(_lastPlan) : new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> kv in EvaluatePlan(Find.CurrentMap)) summary[kv.Key] = kv.Value;
            Finish(tick, summary);
        }

        protected override Dictionary<string, object> Sample(int tick)
        {
            _lastPlan = EvaluatePlan(Find.CurrentMap);
            var line = new Dictionary<string, object>(_lastPlan) { ["tick"] = tick };
            return line;
        }

        private static int FinishResearch()
        {
            var done = new HashSet<ResearchProjectDef>();
            foreach (string name in ShipResearch)
            {
                ResearchProjectDef def = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(name);
                if (def != null) FinishWithPrerequisites(def, done);
            }
            return done.Count;
        }

        private static void FinishWithPrerequisites(ResearchProjectDef def, HashSet<ResearchProjectDef> done)
        {
            if (!done.Add(def)) return;
            foreach (ResearchProjectDef pre in def.prerequisites ?? new List<ResearchProjectDef>()) FinishWithPrerequisites(pre, done);
            foreach (ResearchProjectDef pre in def.hiddenPrerequisites ?? new List<ResearchProjectDef>()) FinishWithPrerequisites(pre, done);
            if (!def.IsFinished) Find.ResearchManager.FinishProject(def, doCompletionDialog: false, researcher: null, doCompletionLetter: false);
        }

        /// <summary>一艘最小船（一個休眠艙）材料約 1540 鋼、700 塑鋼、294 鈾；多給一些讓結構樑與多個休眠艙夠用。</summary>
        private static int SpawnMaterials(Map map)
        {
            IntVec3 near = map.mapPawns.FreeColonistsSpawned.FirstOrDefault()?.Position ?? map.Center;
            var wanted = new List<KeyValuePair<ThingDef, int>>
            {
                new KeyValuePair<ThingDef, int>(ThingDefOf.Steel, 3500),
                new KeyValuePair<ThingDef, int>(ThingDefOf.Plasteel, 1200),
                new KeyValuePair<ThingDef, int>(ThingDefOf.Uranium, 500),
                new KeyValuePair<ThingDef, int>(ThingDefOf.Gold, 150),
                new KeyValuePair<ThingDef, int>(ThingDefOf.ComponentIndustrial, 40),
                new KeyValuePair<ThingDef, int>(ThingDefOf.ComponentSpacer, 70),
                new KeyValuePair<ThingDef, int>(ThingDefOf.AIPersonaCore, 1)
            };
            int stacks = 0;
            foreach (KeyValuePair<ThingDef, int> item in wanted)
            {
                int left = item.Value;
                while (left > 0)
                {
                    Thing thing = ThingMaker.MakeThing(item.Key);
                    thing.stackCount = Math.Min(left, item.Key.stackLimit);
                    left -= thing.stackCount;
                    if (GenPlace.TryPlaceThing(thing, near, map, ThingPlaceMode.Near)) stacks++;
                }
            }
            return stacks;
        }

        /// <summary>
        /// 船體規劃：把飛船零件的藍圖、框架與成品都算進來，檢查發射所需數量（ShipUtility.RequiredParts）、
        /// 是否全部四方向連成一塊、有沒有零件格被屋頂蓋住。
        /// </summary>
        private static Dictionary<string, object> EvaluatePlan(Map map)
        {
            var parts = new List<KeyValuePair<ThingDef, CellRect>>();
            foreach (Thing t in map.listerThings.AllThings)
            {
                ThingDef def = t.def.entityDefToBuild as ThingDef ?? t.def;
                if (def.building == null || !def.building.shipPart) continue;
                parts.Add(new KeyValuePair<ThingDef, CellRect>(def, t.OccupiedRect()));
            }
            var counts = parts.GroupBy(p => p.Key.defName).ToDictionary(g => g.Key, g => g.Count());
            var missing = ShipUtility.RequiredParts()
                .Where(r => (counts.TryGetValue(r.Key.defName, out int n) ? n : 0) < r.Value)
                .Select(r => r.Key.defName).ToList();
            int roofed = parts.Sum(p => p.Value.Cells.Count(c => c.InBounds(map) && map.roofGrid.Roofed(c)));
            return new Dictionary<string, object>
            {
                ["parts"] = counts,
                ["missing"] = missing,
                ["groups"] = CountGroups(parts.Select(p => p.Value).ToList()),
                ["roofedCells"] = roofed,
                ["built"] = map.listerBuildings.allBuildingsColonist.Count(b => b.def.building?.shipPart == true),
                ["blueprints"] = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count(t => (t.def.entityDefToBuild as ThingDef)?.building?.shipPart == true)
            };
        }

        /// <summary>零件以共用邊（四方向）相連的群數；1 表示整艘船連成一塊。</summary>
        private static int CountGroups(List<CellRect> rects)
        {
            var seen = new bool[rects.Count];
            int groups = 0;
            for (int i = 0; i < rects.Count; i++)
            {
                if (seen[i]) continue;
                groups++;
                var pending = new List<int> { i };
                seen[i] = true;
                while (pending.Count > 0)
                {
                    int a = pending[pending.Count - 1];
                    pending.RemoveAt(pending.Count - 1);
                    for (int b = 0; b < rects.Count; b++)
                    {
                        if (seen[b] || !Touches(rects[a], rects[b])) continue;
                        seen[b] = true;
                        pending.Add(b);
                    }
                }
            }
            return groups;
        }

        private static bool Touches(CellRect a, CellRect b)
        {
            bool xOverlap = a.minX <= b.maxX && b.minX <= a.maxX;
            bool zOverlap = a.minZ <= b.maxZ && b.minZ <= a.maxZ;
            bool xAdjacent = a.maxX + 1 == b.minX || b.maxX + 1 == a.minX;
            bool zAdjacent = a.maxZ + 1 == b.minZ || b.maxZ + 1 == a.minZ;
            return (xAdjacent && zOverlap) || (zAdjacent && xOverlap);
        }
    }
}
