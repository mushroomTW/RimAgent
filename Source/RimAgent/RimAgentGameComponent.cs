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
    /// 自主模式：設定頁選「自主」後，每 500 tick 檢查一次訊號（襲擊、心境、糧食、間隔），
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
        /// <summary>常駐指令上限（字數），跟存檔走，每句指令都附上。</summary>
        public const int MaxStandingChars = 2000;

        private ColonyAgentSession _session;
        private List<TranscriptEntryData> _loadedTranscript;
        private List<string> _goals = new List<string>();
        private string _standingInstructions = string.Empty;
        private AgentPack _pack;
        private McpManager _mcp;
        private bool _mcpWarmedUp;
        private readonly AutonomousScheduler _scheduler = new AutonomousScheduler();
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
                    RestoreTranscript(_session);
                    // MCP server 在背景先連好：行程啟動慢，別等玩家第一句才開。
                    if (!_mcpWarmedUp && RimAgentMod.Settings != null && RimAgentMod.Settings.allowMCP)
                    {
                        _mcpWarmedUp = true;
                        _ = _mcp.WarmUpAsync();
                    }
                }
                // 步數上限即時生效：重設工具迴圈的屬性即可，AF session 的歷史不受影響。
                else if (RimAgentMod.Settings != null) _session.UpdateMaxSteps(RimAgentMod.Settings.maxSteps);
                return _session;
            }
        }

        /// <summary>玩家設的長期目標（跟存檔走）；自主任務會附上並持續推進。</summary>
        public IReadOnlyList<string> Goals => _goals;

        /// <summary>玩家寫的常駐指令（跟存檔走）；每句指令的提示詞都附上，優先序最高。</summary>
        public string StandingInstructions => _standingInstructions ?? string.Empty;

        /// <summary>寫入常駐指令（超長截斷）；內容沒變回傳 false。</summary>
        public bool SetStandingInstructions(string text)
        {
            string trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length > MaxStandingChars) trimmed = trimmed.Substring(0, MaxStandingChars);
            if (trimmed == StandingInstructions) return false;
            _standingInstructions = trimmed;
            return true;
        }

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

            Scribe_Collections.Look(ref _goals, "goals", LookMode.Value);
            if (_goals == null) _goals = new List<string>();

            Scribe_Values.Look(ref _standingInstructions, "standingInstructions", string.Empty);
            if (_standingInstructions == null) _standingInstructions = string.Empty;
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
            _goals = new List<string>();
            _standingInstructions = string.Empty;
            ResetMcp();
            _scheduler.Reset();
        }

        public override void LoadedGame()
        {
            _session = null;
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
                BudgetPerWindow = settings.autonomousBudgetPerDay,
                OnRaid = settings.autoOnRaid,
                OnMentalBreak = settings.autoOnMentalBreak,
                OnFoodShortage = settings.autoOnFoodShortage
            };
        }

        private AutonomousSignals GatherSignals()
        {
            return _autoWorld.GetAutonomousSignals();
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
                string instruction = AutonomousPrompts.Build(trigger, _goals, DetectLanguage());
                await session.SendAsync(instruction, _autoCts.Token, autonomous: true).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // 玩家在同一幀搶先送了指令：這次沒跑，不算一次自主任務、不吃冷卻。
                return;
            }
            catch (Exception ex)
            {
                Log.Warning("RimAgent autonomous run failed: " + ex.Message);
            }
            // 只數這一句的工具紀錄：transcript 會裁掉最舊的紀錄，前後相減會失準。
            int used = session.Transcript.Snapshot().Count(e => e.Kind == TranscriptKind.Tool && e.InstructionId == instructionId);
            _scheduler.RecordRun(tick, used, config);
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

        private ColonyAgentSession CreateSession()
        {
            IChatClient client = RimLLMProvider.CreateChatClient(ColonyAgentSession.ModId);
            RimAgentSettings settings = RimAgentMod.Settings;
            return new ColonyAgentSession(client, new RimWorldGameWorld(), () => settings.ToPermissions(), settings.maxSteps, () => settings.mode, ComposeInstructions, _mcp);
        }

        /// <summary>
        /// 每句指令的完整提示詞：核心→內建準則→技能→常駐指令。
        /// 存檔讀進來才有常駐指令；每句現組，改完下一句生效。
        /// </summary>
        private string ComposeInstructions()
        {
            AgentPack pack = _pack;
            return AgentPromptComposer.Compose(
                AgentSystemPrompt.Text,
                pack != null ? pack.Conduct : string.Empty,
                pack != null ? pack.Skills : null,
                _standingInstructions);
        }
    }
}
