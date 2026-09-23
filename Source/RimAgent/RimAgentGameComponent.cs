using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Mcp;
using RimAgent.Tools;
using RimLLM_Framework;
using Verse;

namespace RimAgent
{
    /// <summary>
    /// 持有這一場遊戲的 <see cref="ColonyAgentSession"/>。玩家看得到的對話
    /// （指令、回覆、行動紀錄）與序列化後的 AF session 模型上下文皆寫進存檔，讀檔後仍在；
    /// 新局時兩者都清空，避免把上一個殖民地的脈絡帶進來。RimWorld 會自動為每個 GameComponent 子類建立實例。
    ///
    /// 自主模式：設定頁選「自主」後，每 500 tick 檢查一次訊號（襲擊、心境、糧食、急診、火災與間隔），
    /// 條件成立就起一次自主任務。自主任務與玩家指令走同一個 session（同歷史、同權限、同閘門），
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
        private string _loadedAgentSession;
        private List<TranscriptEntryData> _loadedTranscript;
        private List<UndoFrame> _loadedUndoFrames;
        private List<string> _goals = new List<string>();
        private AgentPack _pack;
        private McpManager _mcp;
        private bool _mcpWarmedUp;
        private readonly AutonomousScheduler _scheduler = new AutonomousScheduler();
        private AutonomousScheduler _loadedScheduler;
        private readonly RimWorldGameWorld _autoWorld = new RimWorldGameWorld();
        private string _lastAutoError;
        private CancellationTokenSource _autoCts;

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
                    if (_pack == null) _pack = AgentPackLoader.Load(PackRoot);
                    if (_mcp == null) _mcp = new McpManager(McpConfigPath);
                    _session = CreateSession();
                    if (!string.IsNullOrWhiteSpace(_loadedAgentSession))
                    {
                        _session.RestoreSerializedSession(_loadedAgentSession);
                        _loadedAgentSession = null;
                    }
                    RestoreTranscript(_session);
                    _session.Undo.Restore(_loadedUndoFrames);
                    _loadedUndoFrames = null;
                    // MCP server 在背景先連好：行程啟動慢，別等玩家第一句才開。
                    if (!_mcpWarmedUp && RimAgentMod.Settings != null && RimAgentMod.Settings.GetAllow(ToolCategory.Mcp))
                    {
                        _mcpWarmedUp = true;
                        _ = _mcp.WarmUpAsync();
                    }
                }
                // 步數上限、思考強度與自動壓縮即時生效：重設屬性即可，AF session 的歷史不受影響。
                else if (RimAgentMod.Settings != null)
                {
                    _session.UpdateMaxSteps(RimAgentMod.Settings.maxSteps);
                    _session.Reasoning = RimAgentMod.Settings.reasoning;
                    _session.SyncAutoCompact(RimAgentMod.Settings.autoCompact, RimAgentMod.Settings.autoCompactThreshold);
                }
                return _session;
            }
        }

        /// <summary>玩家設的長期目標（跟存檔走）；自主任務會附上並持續推進。</summary>
        public IReadOnlyList<string> Goals => _goals;

        /// <summary>出貨的 AgentPack 資料夾；測試或缺 mod 資料時為 null。</summary>
        private static string PackRoot
        {
            get
            {
                // Content 是基底 Mod 的成員，不自藏。
                RimAgentMod mod = LoadedModManager.GetMod<RimAgentMod>();
                return mod != null ? Path.Combine(mod.Content.RootDir, "AgentPack") : null;
            }
        }

        private static string McpConfigPath
        {
            get
            {
                string root = PackRoot;
                return root != null ? Path.Combine(root, "MCP", "servers.json") : null;
            }
        }

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
        public static bool AutonomousEnabled => RimAgentMod.Settings != null && RimAgentMod.Settings.mode == AgentMode.Autonomous;

        /// <summary>今日自主任務已消耗的工具呼叫次數／上限。</summary>
        public int AutonomousToolsUsed => _scheduler.ToolsUsed;

        public static int AutonomousBudget => RimAgentMod.Settings != null ? RimAgentMod.Settings.autonomousBudgetPerDay : 0;

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
            // Scribe_Collections 只在 LoadingVars 階段填 list（PostLoadInit 對 Deep 清單什麼都不做），要在這裡接住。
            if (Scribe.mode == LoadSaveMode.LoadingVars) _loadedTranscript = data;

            List<UndoFrame> undoFrames = Scribe.mode == LoadSaveMode.Saving && _session != null
                ? _session.Undo.Snapshot() : _loadedUndoFrames;
            Scribe_Collections.Look(ref undoFrames, "undoFrames", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars) _loadedUndoFrames = undoFrames;

            string agentSession = Scribe.mode == LoadSaveMode.Saving && _session != null
                ? _session.SerializeForSave() : _loadedAgentSession;
            Scribe_Values.Look(ref agentSession, "agentSession");
            if (Scribe.mode == LoadSaveMode.LoadingVars) _loadedAgentSession = agentSession;

            Scribe_Collections.Look(ref _goals, "goals", LookMode.Value);
            if (_goals == null) _goals = new List<string>();

            AutonomousScheduler schedulerState = Scribe.mode == LoadSaveMode.Saving ? _scheduler : _loadedScheduler;
            Scribe_Deep.Look(ref schedulerState, "autonomousScheduler");
            if (Scribe.mode == LoadSaveMode.LoadingVars) _loadedScheduler = schedulerState;

            if (Scribe.mode == LoadSaveMode.Saving && _mcp != null)
            {
                // 存檔時順手關掉閒置的 MCP server，不阻塞。
                _mcp.SweepIdle();
            }
        }

        public override void StartedNewGame()
        {
            _session = null;
            _loadedTranscript = null;
            _loadedUndoFrames = null;
            _loadedAgentSession = null;
            _goals = new List<string>();
            ResetMcp();
            _scheduler.Reset();
        }

        public override void LoadedGame()
        {
            _session = null;
            if (_loadedScheduler != null)
            {
                _loadedScheduler.CopyTo(_scheduler);
                _loadedScheduler = null;
            }
            ResetMcp();
        }

        /// <summary>關掉 MCP server 並丟掉擴充包快取（開新局／讀檔時重抓）。</summary>
        private void ResetMcp()
        {
            if (_mcp != null)
            {
                _mcp.Shutdown();
                _mcp = null;
            }
            _mcpWarmedUp = false;
            _pack = null;
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            RimAgentSettings settings = RimAgentMod.Settings;
            if (settings == null || settings.mode != AgentMode.Autonomous) return;
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
                BudgetPerWindow = 0,
                UnlimitedBudget = true,
                CooldownTicks = UnityEngine.Mathf.RoundToInt(settings.autonomousCooldownMinutes * (2500f / 60f)),
                OnRaid = settings.autoOnRaid,
                OnMentalBreak = settings.autoOnMentalBreak,
                OnFoodShortage = settings.autoOnFoodShortage,
                OnEmergencyMedical = settings.autoOnEmergencyMedical,
                OnFire = settings.autoOnFire
            };
        }

        private AutonomousSignals GatherSignals()
        {
            return _autoWorld.GetAutonomousSignals();
        }

        /// <summary>持續性問題只警告一種一次（缺文案之類），免得每 500 tick 洗一次除錯記錄。</summary>
        private void WarnOnce(string message)
        {
            if (_lastAutoError != message)
            {
                _lastAutoError = message;
                Log.Warning(message);
            }
        }

        /// <summary>UI 的停止鍵：取消進行中的自主任務（玩家指令另有自己的 token）。</summary>
        public void StopAutonomousRun()
        {
            _autoCts?.Cancel();
        }

        private async Task RunAutonomousAsync(ColonyAgentSession session, AutonomousTrigger trigger, int tick, AutonomousConfig config)
        {
            _autoCts?.Dispose();
            _autoCts = new CancellationTokenSource();
            // 送出前先算好這句會拿到的編號：busy 旗標保證成功送出的那句取的是下一個號；
            // 送完才讀 CurrentInstructionId 的話，玩家在旗標釋放的瞬間送指令會讓我們數到別句。
            int instructionId = session.Transcript.CurrentInstructionId + 1;
            try
            {
                AutonomousPack pack = _pack != null ? _pack.Autonomous : null;
                if (pack != null && !AutonomousPrompts.HasTask(pack, trigger) && AutonomousPrompts.HasTask(pack, AutonomousTrigger.Interval))
                {
                    // 章節缺失（通常是拼字錯誤）時 Build 會靜默退回 Interval：警告一次讓問題可見。
                    WarnOnce("RimAgent autonomous task missing section for " + trigger + ", falling back to Interval");
                }
                string instruction = AutonomousPrompts.Build(pack, trigger, _goals);
                if (string.IsNullOrWhiteSpace(instruction))
                {
                    // 自主任務文案缺檔：送空話只會燒 API，直接跳過本輪（沿用錯誤節流，只記一種一次）。
                    WarnOnce("RimAgent autonomous run skipped: Autonomous.en.md is missing or has no task for " + trigger);
                    return;
                }
                int remainingBudget = _scheduler.RemainingBudget(config);
                int maxSteps = RimAgentMod.Settings?.maxSteps ?? RimAgentSettings.DefaultMaxSteps;
                int steps = config.UnlimitedBudget ? maxSteps : Math.Min(maxSteps, remainingBudget);
                await session.SendAsync(
                    instruction,
                    _autoCts.Token,
                    autonomous: true,
                    maxToolSteps: steps).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // 玩家在同一幀搶先送了指令：這次沒跑，不算一次自主任務、不吃冷卻。
                return;
            }
            catch (OperationCanceledException)
            {
                // 玩家主動停止自主任務（點擊停止或 /stop）：若尚未消耗任何工具配額，不吃冷卻。
            }
            catch (Exception ex)
            {
                Log.Warning("RimAgent autonomous run failed: " + ex.Message);
            }
            // 只數這一句的工具紀錄：transcript 會裁掉最舊的紀錄，前後相減會失準。
            int used = session.Transcript.Snapshot().Count(e =>
                e.Kind == TranscriptKind.Tool && e.InstructionId == instructionId && e.BudgetConsumed);
            if (used > 0 || _autoCts == null || !_autoCts.IsCancellationRequested)
            {
                _scheduler.RecordRun(tick, used, config);
            }
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

        private ColonyAgentSession CreateSession()
        {
            IChatClient client = RimLLMProvider.CreateChatClient(ColonyAgentSession.ModId);
            RimAgentSettings settings = RimAgentMod.Settings;
            var session = new ColonyAgentSession(
                client,
                new RimWorldGameWorld(),
                () => settings.ToPermissions(),
                settings.maxSteps,
                () => settings.mode,
                ComposeInstructions,
                _mcp,
                classifierClient: null,
                classifierModel: () => settings?.classifierModel,
                classifierPrompt: () => _pack != null ? _pack.ClassifierPrompt : string.Empty,
                askNote: () => _pack != null ? _pack.AskModeNote : string.Empty);
            session.Reasoning = settings.reasoning;
            session.SyncAutoCompact(settings.autoCompact, settings.autoCompactThreshold);
            return session;
        }

        /// <summary>每句指令的完整提示詞：前情提要→玩家自留地→行為準則→技能。每句現組，AgentPack 變更後下一句生效。</summary>
        private string ComposeInstructions()
        {
            AgentPack pack = _pack;
            string preamble = pack != null && !string.IsNullOrWhiteSpace(pack.Preamble)
                ? pack.Preamble
                : AgentSystemPrompt.FallbackPreamble;
            return AgentPromptComposer.Compose(
                preamble,
                pack != null ? pack.PlayerNotes : string.Empty,
                pack != null ? pack.Conduct : string.Empty,
                pack != null ? pack.Skills : null);
        }
    }
}
