using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimAgent.Agent;
using RimAgent.Game;
using RimLLM_Framework;
using Verse;

namespace RimAgent
{
    /// <summary>
    /// 持有這一場遊戲的 <see cref="ColonyAgentSession"/>。玩家看得到的對話
    /// （指令、回覆、行動紀錄）寫進存檔，讀檔後仍在；AF session 的模型上下文
    /// 照舊歸零（等框架提供版本化的 session 序列化再評估），新局時兩者都清空，
    /// 避免把上一個殖民地的脈絡帶進來。RimWorld 會自動為每個 GameComponent 子類建立實例。
    ///
    /// 自主模式：設定頁打開後，每 500 tick 檢查一次訊號（襲擊、心境、糧食、間隔），
    /// 條件成立就起一次自主任務。自主任務與玩家指令走同一個 session（同歷史、同權限、同確認閘門），
    /// 差別只在指令文字由排程器產生並附上長期目標；每日工具呼叫總量有上限。
    /// </summary>
    public class RimAgentGameComponent : GameComponent
    {
        /// <summary>訊號檢查間隔（500 tick ≈ 遊戲內 12 分鐘），避免每幀掃全圖。</summary>
        private const int SignalCheckIntervalTicks = 500;

        /// <summary>長期目標上限（條數／每條字數），避免無限塞進指令。</summary>
        private const int MaxGoals = 20;
        private const int MaxGoalChars = 200;

        private ColonyAgentSession _session;
        private List<TranscriptEntryData> _loadedTranscript;
        private List<string> _goals = new List<string>();
        private readonly AutonomousScheduler _scheduler = new AutonomousScheduler();
        private readonly RimWorldGameWorld _autoWorld = new RimWorldGameWorld();
        private string _lastAutoError;

        // RimAgent.Game 命名空間會遮蔽 Verse.Game，這裡要寫全名。
        public RimAgentGameComponent(Verse.Game game)
        {
        }

        public static RimAgentGameComponent Instance => Current.Game?.GetComponent<RimAgentGameComponent>();

        /// <summary>延遲建立：框架的 client 要等 mod 全部載入完才拿得到。</summary>
        public ColonyAgentSession Session
        {
            get
            {
                if (_session == null)
                {
                    _session = CreateSession();
                    RestoreTranscript(_session);
                }
                // 步數上限即時生效：重設工具迴圈的屬性即可，AF session 的歷史不受影響。
                else if (RimAgentMod.Settings != null) _session.UpdateMaxSteps(RimAgentMod.Settings.maxSteps);
                return _session;
            }
        }

        /// <summary>玩家設的長期目標（跟存檔走）；自主任務會附上並持續推進。</summary>
        public IReadOnlyList<string> Goals => _goals;

        public bool AddGoal(string goal)
        {
            string trimmed = (goal ?? string.Empty).Trim();
            if (trimmed.Length == 0 || _goals.Count >= MaxGoals) return false;
            if (trimmed.Length > MaxGoalChars) trimmed = trimmed.Substring(0, MaxGoalChars);
            _goals.Add(trimmed);
            return true;
        }

        public void RemoveGoalAt(int index)
        {
            if (index >= 0 && index < _goals.Count) _goals.RemoveAt(index);
        }

        /// <summary>UI 狀態列用：自主模式是否開啟中。</summary>
        public bool AutonomousEnabled => RimAgentMod.Settings != null && RimAgentMod.Settings.allowAutonomous;

        /// <summary>今日自主任務已消耗的工具呼叫次數／上限。</summary>
        public int AutonomousToolsUsed => _scheduler.ToolsUsed;

        public int AutonomousBudget => RimAgentMod.Settings != null ? RimAgentMod.Settings.autonomousBudgetPerDay : 0;

        public override void ExposeData()
        {
            base.ExposeData();
            List<TranscriptEntryData> data = null;
            // 新局與讀檔時 session 都是 null（延遲建立）；只有玩到一半存檔才有東西可寫。
            if (Scribe.mode == LoadSaveMode.Saving && _session != null)
            {
                data = TranscriptEntryData.FromTranscript(_session.Transcript);
            }
            Scribe_Collections.Look(ref data, "transcript", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) _loadedTranscript = data;

            Scribe_Collections.Look(ref _goals, "goals", LookMode.Value);
            if (_goals == null) _goals = new List<string>();
        }

        public override void StartedNewGame()
        {
            _session = null;
            _loadedTranscript = null;
            _goals = new List<string>();
            _scheduler.Reset();
        }

        public override void LoadedGame()
        {
            _session = null;
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            RimAgentSettings settings = RimAgentMod.Settings;
            if (settings == null || !settings.allowAutonomous) return;
            int tick = Find.TickManager.TicksGame;
            if (tick % SignalCheckIntervalTicks != 0) return;
            if (Find.CurrentMap == null) return;

            try
            {
                ColonyAgentSession session = Session;
                // 玩家還在忙（上一句沒做完、或有預覽等確認）就不搶。
                if (session.IsBusy || session.HasPendingConfirmation) return;
                AutonomousConfig config = ToConfig(settings);
                AutonomousTrigger trigger = _scheduler.Evaluate(tick, GatherSignals(), config);
                if (trigger == AutonomousTrigger.None) return;
                _ = RunAutonomousAsync(session, trigger, tick, config);
                _lastAutoError = null;
            }
            catch (Exception ex)
            {
                // 框架沒設好（沒供應商、版本太舊）時每 500 tick 就會來一次，只記一種一次。
                if (_lastAutoError != ex.Message)
                {
                    _lastAutoError = ex.Message;
                    Log.Warning("RimAgent autonomous run skipped: " + ex.Message);
                }
            }
        }

        private AutonomousConfig ToConfig(RimAgentSettings settings)
        {
            return new AutonomousConfig
            {
                Enabled = true,
                IntervalTicks = settings.autonomousIntervalHours * 2500,
                WindowTicks = 60000,
                BudgetPerWindow = settings.autonomousBudgetPerDay,
                OnRaid = settings.autoOnRaid,
                OnMentalBreak = settings.autoOnMentalBreak,
                OnFoodShortage = settings.autoOnFoodShortage
            };
        }

        private AutonomousSignals GatherSignals()
        {
            ColonyOverview overview = _autoWorld.GetOverview();
            ResourceSnapshot resources = _autoWorld.GetResources();
            return new AutonomousSignals
            {
                HostileCount = overview.HostileCount,
                NearMentalBreakCount = overview.ColonistsNearMentalBreak,
                FoodNutrition = resources.TotalHumanEdibleNutrition,
                ColonistCount = overview.ColonistCount
            };
        }

        private async Task RunAutonomousAsync(ColonyAgentSession session, AutonomousTrigger trigger, int tick, AutonomousConfig config)
        {
            int before = CountToolEntries(session);
            try
            {
                string instruction = AutonomousPrompts.Build(trigger, _goals, DetectLanguage());
                await session.SendAsync(instruction).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning("RimAgent autonomous run failed: " + ex.Message);
            }
            finally
            {
                int used = CountToolEntries(session) - before;
                _scheduler.RecordRun(tick, used < 0 ? 0 : used, config);
            }
        }

        private static int CountToolEntries(ColonyAgentSession session)
        {
            return session.Transcript.Snapshot().Count(e => e.Kind == TranscriptKind.Tool);
        }

        private static AutoLanguage DetectLanguage()
        {
            try
            {
                string folder = LanguageDatabase.activeLanguage?.folderName ?? string.Empty;
                if (folder.IndexOf("Simplified", StringComparison.OrdinalIgnoreCase) >= 0) return AutoLanguage.Simplified;
                if (folder.IndexOf("Traditional", StringComparison.OrdinalIgnoreCase) >= 0) return AutoLanguage.Traditional;
            }
            catch (Exception)
            {
                // 語言偵測失敗就用英文，自主任務本身不受影響。
            }
            return AutoLanguage.English;
        }

        /// <summary>讀檔後第一次開分頁時，把存檔裡的對話倒回 transcript，並把指令編號推過舊紀錄。</summary>
        private void RestoreTranscript(ColonyAgentSession session)
        {
            List<TranscriptEntryData> loaded = _loadedTranscript;
            _loadedTranscript = null;
            if (loaded == null || loaded.Count == 0) return;
            int maxId = 0;
            foreach (TranscriptEntryData d in loaded)
            {
                if (d == null) continue;
                session.Transcript.Add(d.ToEntry());
                if (d.instructionId > maxId) maxId = d.instructionId;
            }
            session.Transcript.EnsureInstructionIdAtLeast(maxId);
        }

        private static ColonyAgentSession CreateSession()
        {
            IChatClient client = RimLLMProvider.CreateChatClient(ColonyAgentSession.ModId);
            RimAgentSettings settings = RimAgentMod.Settings;
            return new ColonyAgentSession(client, new RimWorldGameWorld(), () => settings.ToPermissions(), settings.maxSteps, () => settings.requireConfirmation);
        }
    }
}
