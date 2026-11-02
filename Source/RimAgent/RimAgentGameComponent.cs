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

namespace RimAgent;

public enum GoalPauseReason
{
    None,
    User,
    Blocked,
    TurnLimit,
    NoProgress,
    Error
}

/// <summary>
/// 持有這一場遊戲的 <see cref="ColonyAgentSession"/>。玩家看得到的對話
/// （指令、回覆、行動紀錄）與序列化後的 AF session 模型上下文皆寫進存檔，讀檔後仍在；
/// 新局時兩者都清空，避免把上一個殖民地的脈絡帶進來。RimWorld 會自動為每個 GameComponent 子類建立實例。
///
/// 自主模式：設定頁選「自主」後，每 500 tick 檢查一次訊號（襲擊、心境、糧食、急診、火災與基地狀況），
/// 嚴重度變高就起一次自主任務，條件沒解決會退避重試三次。自主任務與玩家指令走同一個 session
/// （同歷史、同權限、同閘門），差別在指令文字由排程器產生；持續 goal 另由 GameComponentUpdate 續跑。
/// </summary>
public class RimAgentGameComponent : GameComponent
{
    /// <summary>訊號檢查間隔（500 tick ≈ 遊戲內 12 分鐘），避免每幀掃全圖。</summary>
    private const int SignalCheckIntervalTicks = 500;

    /// <summary>每輪自主任務的工具呼叫上限：背景執行沒人盯著，模型打轉時靠它止血。</summary>
    private const int AutonomousRunMaxSteps = 20;

    /// <summary>長期目標上限（條數／每條字數），避免無限塞進指令。</summary>
    private const int MaxGoals = 20;
    public const int MaxGoalChars = 4000;

    private ColonyAgentSession _session;
    private string _loadedAgentSession;
    private List<TranscriptEntryData> _loadedTranscript;
    private List<UndoFrame> _loadedUndoFrames;
    private List<string> _goals = [];
    /// <summary>局內記憶：跟著這份存檔序列化，新局清空。</summary>
    private AgentMemoryStore _saveMemory = new();
    /// <summary>全域記憶：跨存檔，放在 RimAgent 資料夾；null 代表還沒從磁碟載入。</summary>
    private AgentMemoryStore _globalMemory;
    private List<AgentMemoryEntry> _pendingGlobalMigration = [];
    /// <summary>agent 實際使用的記憶：兩個範圍的合成視圖。</summary>
    private ScopedMemoryStore _scopedMemory;
    private AgentPack _pack;
    /// <summary>DLC 啟用狀況；模組清單要重開遊戲才會變，建立會話時在主線程算一次。</summary>
    private string _expansions;
    private McpManager _mcp;
    private bool _mcpWarmedUp;
    private readonly AutonomousScheduler _scheduler = new();
    private AutonomousScheduler _loadedScheduler;
    private List<AutonomousWatch> _schedulerWatch = [];
    private readonly RimWorldGameWorld _autoWorld = new();
    private string _lastAutoError;
    private CancellationTokenSource _autoCts;
    /// <summary>新局／讀檔時遞增；舊的自主任務藉此確認自己已不屬於目前這份遊戲。</summary>
    private int _autoGeneration;
    /// <summary>
    /// 自主任務從起跑到 RecordRun 記完為止。session 的 busy 旗標在背景執行緒就放開，
    /// 早於冷卻記錄；只看 busy 的話，中間落下的訊號檢查會拿舊冷卻再起一輪。
    /// </summary>
    private volatile bool _autoRunning;
    // 自主排程須由玩家在目前這次自主模式中送出對話後才啟動。
    private bool _autonomousConversationStarted;
    /// <summary>
    /// 自主任務因金鑰、額度（402）、供應商離線等設定問題失敗後，暫停到這個 tick 為止。
    /// 這類錯誤幾秒內不會自己好，照常重觸發只會連續失敗；不存檔，讀檔即恢復。
    /// </summary>
    private int _autoHoldUntilTick;
    private const int ConfigurationErrorHoldTicks = 60000;
    /// <summary>見過的抉擇 id：只在出現新抉擇時起一輪，模型沒處理掉的不會每幾秒重觸發、白燒 API。</summary>
    private readonly HashSet<string> _seenChoices = [];
    private DateTime _nextChoiceCheckAt;
    private const double ChoiceCheckSeconds = 2;
    private CancellationTokenSource _goalCts;
    private bool _goalRunning;
    private bool _goalPaused;
    private int _goalTurns;
    private int _goalBlockedTurns;
    private int _goalIdleTurns;
    private string _goalBlockerKey;
    private string _goalLastProgress;
    private string _lastCompletedGoal;
    private GoalPauseReason _goalPauseReason;
    private bool _goalNeedsVerification;
    private string _goalAuditNote;
    private DecisionGoalAuditor _goalAuditor;
    internal DecisionGoalAuditor GoalAuditor
    {
        get => _goalAuditor ??= new DecisionGoalAuditor(
            decisionService: null,
            criteriaProvider: () => _pack?.DecisionGoalCriteria,
            serviceFactory: () => DecisionServiceFactory.Create(
                () => RimAgentMod.Settings?.decisionProvider,
                () => RimAgentMod.Settings?.decisionModel));
        set => _goalAuditor = value;
    }
    private DateTime _goalNextRunAt;
    private int _goalGeneration;
    private RunLogWriter _runLogWriter;

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
            // 模型、思考強度與自動壓縮即時生效，AF session 的歷史不受影響。
            else if (RimAgentMod.Settings != null)
            {
                _session.Reasoning = RimAgentMod.Settings.reasoning;
                _session.PreferredModel = RimAgentMod.Settings.agentModel;
                _session.SyncAutoCompact(RimAgentMod.Settings.autoCompact, RimAgentMod.Settings.autoCompactThreshold);
            }
            return _session;
        }
    }

    /// <summary>玩家設的持續目標（跟存檔走），由獨立執行流程依序推進。</summary>
    public IReadOnlyList<string> Goals => _goals;
    public bool GoalPaused => _goalPaused;
    public bool IsGoalRunning => _goalRunning;
    public GoalPauseReason CurrentGoalPauseReason => _goalPauseReason;
    public int GoalTurns => _goalTurns;
    public int GoalBlockedTurns => _goalBlockedTurns;
    public int GoalIdleTurns => _goalIdleTurns;
    public string GoalLastProgress => _goalLastProgress;
    public string LastCompletedGoal => _lastCompletedGoal;
    public bool GoalNeedsVerification => _goalNeedsVerification;
    public string GoalAuditNote => _goalAuditNote;
    public string GoalBlockerKey => _goalBlockerKey;

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

    /// <summary>全域記憶檔：跨存檔共用，玩家可直接檢視、備份或手動編輯。</summary>
    internal static string GlobalMemoryPath
    {
        get { return Path.Combine(GenFilePaths.SaveDataFolderPath, "RimAgent", "Memory", GlobalMemoryFile.FileName); }
    }

    /// <summary>全域記憶；第一次取用時從磁碟載入，讀不了的檔案改名保留再從空白開始。</summary>
    private AgentMemoryStore GlobalMemory
    {
        get
        {
            if (_globalMemory == null)
            {
                _globalMemory = GlobalMemoryFile.Load(GlobalMemoryPath, out string warning);
                if (warning != null) Log.Warning("RimAgent " + warning);
                if (_globalMemory.DroppedOnLoad > 0)
                    Log.Warning("RimAgent discarded " + _globalMemory.DroppedOnLoad + " global memory file(s) that exceed the current limits.");
            }
            return _globalMemory;
        }
    }

    /// <summary>agent 看到的一組記憶：以檔名把局內記憶與全域記憶分到兩個範圍。</summary>
    private ScopedMemoryStore Memory
    {
        get
        {
            if (_scopedMemory == null) _scopedMemory = new ScopedMemoryStore(_saveMemory, GlobalMemory, SaveGlobalMemory);
            return _scopedMemory;
        }
    }

    /// <summary>開新局或讀檔時忘掉全域記憶快取：玩家可能改過檔案，別的存檔也可能剛寫過。</summary>
    private void ResetMemory()
    {
        _globalMemory = null;
        _scopedMemory = null;
    }

    /// <summary>寫回全域記憶；寫不了就讓工具回報失敗，不假裝記住了。</summary>
    private void SaveGlobalMemory()
    {
        AgentMemoryStore global = _globalMemory ?? GlobalMemory;
        try
        {
            GlobalMemoryFile.Save(GlobalMemoryPath, global.Snapshot());
            _pendingGlobalMigration.Clear();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            throw new InvalidOperationException("global memory could not be saved: " + ex.Message, ex);
        }
    }

    /// <summary>把先前版本留在存檔裡的全域記憶併入全域記憶（同名者以磁碟上的為準）；回傳被捨棄的筆數。</summary>
    private int MergeIntoGlobalMemory(List<AgentMemoryEntry> migrated)
    {
        List<AgentMemoryEntry> merged = GlobalMemory.Snapshot();
        merged.AddRange(migrated);
        _globalMemory = new AgentMemoryStore(merged);
        try
        {
            SaveGlobalMemory();
        }
        catch (InvalidOperationException ex)
        {
            // 讀檔不能因為寫不回磁碟而失敗：這批記憶留在記憶體裡，下次寫入再試。
            Log.Warning("RimAgent could not persist migrated global memory: " + ex.Message);
        }
        return _globalMemory.DroppedOnLoad;
    }

    public bool AddGoal(string goal)
    {
        string trimmed = (goal ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxGoalChars || _goals.Count >= MaxGoals) return false;
        _goals.Add(trimmed);
        if (_goals.Count == 1)
        {
            _goalPaused = false;
            _goalTurns = 0;
            _goalBlockedTurns = 0;
            _goalIdleTurns = 0;
            _goalBlockerKey = null;
            _goalNeedsVerification = false;
            _goalAuditNote = null;
            _goalLastProgress = null;
            _goalPauseReason = GoalPauseReason.None;
            _goalNextRunAt = DateTime.MinValue;
        }
        return true;
    }

    public void RemoveGoalAt(int index)
    {
        if (index < 0 || index >= _goals.Count) return;
        if (index == 0)
        {
            _goalCts?.Cancel();
            // 讓正在等待審計的舊回合失效；新回合要等舊回合放開 session 才會開始。
            _goalGeneration++;
            _goalRunning = false;
        }
        _goals.RemoveAt(index);
        if (index == 0)
        {
            _goalPaused = false;
            _goalTurns = 0;
            _goalBlockedTurns = 0;
            _goalIdleTurns = 0;
            _goalBlockerKey = null;
            _goalNeedsVerification = false;
            _goalAuditNote = null;
            _goalLastProgress = null;
            _goalPauseReason = GoalPauseReason.None;
            _goalNextRunAt = DateTime.MinValue;
        }
    }

    public bool EditGoalAt(int index, string objective)
    {
        string trimmed = (objective ?? string.Empty).Trim();
        if (index < 0 || index >= _goals.Count || trimmed.Length == 0 || trimmed.Length > MaxGoalChars) return false;
        if (index == 0)
        {
            _goalCts?.Cancel();
            // 同一個字串也算新目標：舊審計結果不能套用到剛編輯過的目標。
            _goalGeneration++;
            _goalRunning = false;
        }
        _goals[index] = trimmed;
        if (index == 0)
        {
            _goalTurns = 0;
            _goalBlockedTurns = 0;
            _goalIdleTurns = 0;
            _goalBlockerKey = null;
            _goalNeedsVerification = false;
            _goalAuditNote = null;
            _goalLastProgress = null;
            _goalNextRunAt = DateTime.MinValue;
            if (_goalPauseReason != GoalPauseReason.User)
            {
                _goalPaused = false;
                _goalPauseReason = GoalPauseReason.None;
            }
        }
        return true;
    }

    public void ClearGoals()
    {
        _goalCts?.Cancel();
        _goalGeneration++;
        _goalRunning = false;
        _goals.Clear();
        _goalPaused = false;
        _goalPauseReason = GoalPauseReason.None;
        _goalTurns = 0;
        _goalBlockedTurns = 0;
        _goalIdleTurns = 0;
        _goalBlockerKey = null;
        _goalNeedsVerification = false;
        _goalAuditNote = null;
        _goalLastProgress = null;
        _lastCompletedGoal = null;
        _goalNextRunAt = DateTime.MinValue;
    }

    public void PauseGoal()
    {
        if (_goals.Count == 0) return;
        _goalPaused = true;
        _goalPauseReason = GoalPauseReason.User;
        _goalCts?.Cancel();
    }

    public void ResumeGoal()
    {
        if (_goals.Count == 0) return;
        _goalPaused = false;
        _goalPauseReason = GoalPauseReason.None;
        _goalBlockedTurns = 0;
        _goalIdleTurns = 0;
        _goalBlockerKey = null;
        _goalNeedsVerification = false;
        _goalAuditNote = null;
        _goalTurns = 0;
        _goalNextRunAt = DateTime.MinValue;
    }

    /// <summary>UI 狀態列用：自主模式是否開啟中。</summary>
    public static bool AutonomousEnabled => RimAgentMod.Settings != null && RimAgentMod.Settings.mode == AgentMode.Autonomous;

    public bool AutonomousConversationStarted => _autonomousConversationStarted;

    public void StartAutonomousAfterPlayerMessage()
    {
        _autonomousConversationStarted = true;
    }

    public void ResetAutonomousConversation()
    {
        _autonomousConversationStarted = false;
        StopAutonomousRun();
    }

    /// <summary>今日自主任務已消耗的工具呼叫次數。</summary>
    public int AutonomousToolsUsed => _scheduler.ToolsUsed;

    #pragma warning disable S3776 // RimWorld ExposeData serialization requires sequential save/load branches
    public override void ExposeData()
    {
        base.ExposeData();
        List<TranscriptEntryData> data = Scribe.mode == LoadSaveMode.Saving && _session != null
            ? TranscriptEntryData.FromTranscript(_session.Transcript) : _loadedTranscript;
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
        if (_goals == null) _goals = [];
        List<AgentMemoryEntry> memories = Scribe.mode == LoadSaveMode.Saving
            ? _saveMemory.Snapshot() : null;
        if (memories != null) memories.AddRange(_pendingGlobalMigration);
        Scribe_Collections.Look(ref memories, "agentMemories", LookMode.Deep);
        if (Scribe.mode == LoadSaveMode.LoadingVars)
        {
            // 舊存檔可能把全域記憶存在存檔裡：先挑出來交還全域記憶，存檔才真的只留局內記憶。
            List<AgentMemoryEntry> migrated = MemoryScopes.TakeGlobalEntries(memories);
            _saveMemory = new AgentMemoryStore(memories);
            int dropped = _saveMemory.DroppedOnLoad;
            ResetMemory();
            _pendingGlobalMigration = migrated;
            if (migrated.Count > 0) dropped += MergeIntoGlobalMemory(migrated);
            if (dropped > 0)
                Log.Warning("RimAgent discarded " + dropped + " saved memory file(s) that exceed the current limits.");
        }
        Scribe_Values.Look(ref _goalPaused, "goalPaused", false);
        Scribe_Values.Look(ref _goalTurns, "goalTurns", 0);
        Scribe_Values.Look(ref _goalBlockedTurns, "goalBlockedTurns", 0);
        Scribe_Values.Look(ref _goalIdleTurns, "goalIdleTurns", 0);
        Scribe_Values.Look(ref _goalNeedsVerification, "goalNeedsVerification", false);
        Scribe_Values.Look(ref _goalAuditNote, "goalAuditNote");
        Scribe_Values.Look(ref _goalBlockerKey, "goalBlockerKey");
        Scribe_Values.Look(ref _goalLastProgress, "goalLastProgress");
        Scribe_Values.Look(ref _lastCompletedGoal, "lastCompletedGoal");
        Scribe_Values.Look(ref _goalPauseReason, "goalPauseReason", GoalPauseReason.None);
        Scribe_Values.Look(ref _autonomousConversationStarted, "autonomousConversationStarted", false);

        // 重試表住在唯讀陣列裡，Scribe 過不了：存檔前攤平成清單，讀檔後灌回排程器。
        if (Scribe.mode == LoadSaveMode.Saving) _schedulerWatch = [.. _scheduler.CaptureWatch()];
        Scribe_Collections.Look(ref _schedulerWatch, "autonomousWatch", LookMode.Deep);
        AutonomousScheduler schedulerState = Scribe.mode == LoadSaveMode.Saving ? _scheduler : _loadedScheduler;
        Scribe_Deep.Look(ref schedulerState, "autonomousScheduler");
        if (Scribe.mode == LoadSaveMode.LoadingVars) _loadedScheduler = schedulerState;

        if (Scribe.mode == LoadSaveMode.Saving && _mcp != null)
        {
            // 存檔時順手關掉閒置的 MCP server，不阻塞。
            _mcp.SweepIdle();
        }
    }
    #pragma warning restore S3776

    public override void StartedNewGame()
    {
        _autonomousConversationStarted = false;
        _session = null;
        _loadedTranscript = null;
        _loadedUndoFrames = null;
        _loadedAgentSession = null;
        _goals = [];
        _saveMemory = new AgentMemoryStore();
        _pendingGlobalMigration.Clear();
        ResetMemory();
        _goalCts?.Cancel();
        _goalGeneration++;
        _goalRunning = false;
        // 舊的自主任務還握著上一個存檔的 session；先取消，再用 generation 擋住它的收尾與排程器寫入。
        _autoCts?.Cancel();
        _autoGeneration++;
        _autoRunning = false;
        _goalPaused = false;
        _goalTurns = 0;
        _goalBlockedTurns = 0;
        _goalIdleTurns = 0;
        _goalBlockerKey = null;
        _goalNeedsVerification = false;
        _goalAuditNote = null;
        _goalLastProgress = null;
        _lastCompletedGoal = null;
        _goalPauseReason = GoalPauseReason.None;
        _goalNextRunAt = DateTime.MinValue;
        ResetMcp();
        _scheduler.Reset();
        _schedulerWatch = [];
    }

    public override void LoadedGame()
    {
        _session = null;
        // LoadingVars 已重新載入全域記憶；保留未成功寫入的遷移資料。
        _goalCts?.Cancel();
        _goalGeneration++;
        _goalRunning = false;
        _goalNextRunAt = DateTime.MinValue;
        // 讀檔後舊自主任務的 session 指向舊遊戲；取消並讓它的收尾失效，避免操作到新存檔。
        _autoCts?.Cancel();
        _autoGeneration++;
        _autoRunning = false;
        if (_loadedScheduler != null)
        {
            // 順序要緊：CopyTo 會整片覆蓋 _watch，所以另外 Scribe 的重試表必須套在它之後，
            // 否則剛灌進去的層級與重試次數會被清掉。
            _loadedScheduler.CopyTo(_scheduler);
            _scheduler.ApplyWatch(_schedulerWatch);
            _loadedScheduler = null;
        }
        _schedulerWatch = [];
        ResetMcp();
    }

    /// <summary>關掉 MCP server 並丟掉擴充包快取（開新局／讀檔時重抓）。</summary>
    private void ResetMcp()
    {
        _mcp?.Shutdown();
        _mcp = null;
        _mcpWarmedUp = false;
        _pack = null;
    }

    public override void GameComponentUpdate()
    {
        base.GameComponentUpdate();
        // 抉擇先於持續目標：暫停遊戲的對話框不處理，目標也推進不了。
        TryStartChoiceRun();
        TryStartGoal();
    }

    /// <summary>
    /// 自主模式下出現新的抉擇信件或對話框就起一輪自主任務。放在 Update 而不是 Tick：
    /// 對話框會暫停遊戲，暫停時 GameComponentTick 不跑，只看 tick 會一路卡到玩家回來。
    /// 也不走排程器的冷卻：暫停中 tick 不前進，冷卻永遠等不完。
    /// </summary>
    private void TryStartChoiceRun()
    {
        RimAgentSettings settings = RimAgentMod.Settings;
        if (settings == null || settings.mode != AgentMode.Autonomous || !_autonomousConversationStarted || DateTime.UtcNow < _nextChoiceCheckAt) return;
        _nextChoiceCheckAt = DateTime.UtcNow.AddSeconds(ChoiceCheckSeconds);
        if (_autoRunning || _goalRunning || Find.CurrentMap == null) return;
        if (Find.TickManager.TicksGame < _autoHoldUntilTick) return;
        try
        {
            ColonyAgentSession session = Session;
            if (session.IsBusy || session.HasPendingConfirmation) return;
            IReadOnlyList<PendingChoiceInfo> choices = _autoWorld.GetPendingChoices();
            bool fresh = false;
            foreach (PendingChoiceInfo choice in choices) fresh |= _seenChoices.Add(choice.Id);
            _seenChoices.RemoveWhere(id => choices.All(c => c.Id != id));
            if (!fresh) return;
            _autoRunning = true;
            _ = RunAutonomousGuardedAsync(session, AutonomousTrigger.Choice, Find.TickManager.TicksGame, ToConfig(settings), null);
        }
        catch (Exception ex)
        {
            WarnOnce("RimAgent choice check failed: " + ex.Message);
        }
    }

    private void TryStartGoal()
    {
        if (_goals.Count == 0 || _goalPaused || _goalRunning || _autoRunning || Find.CurrentMap == null
            || DateTime.UtcNow < _goalNextRunAt) return;
        try
        {
            ColonyAgentSession session = Session;
            if (session.IsBusy || session.HasPendingConfirmation) return;
            _goalRunning = true;
            _ = RunGoalAsync(session, _goals[0], _goalGeneration);
        }
        catch (Exception ex)
        {
            _goalRunning = false;
            _goalNextRunAt = DateTime.UtcNow.AddSeconds(30);
            WarnOnce("RimAgent goal run could not start: " + ex.Message);
        }
    }

    private async Task RunGoalAsync(ColonyAgentSession session, string objective, int generation)
    {
        var cts = new CancellationTokenSource();
        _goalCts = cts;
        bool exclusive = false;
        try
        {
            int instructionId = session.Transcript.CurrentInstructionId + 1;
            string confirmationReport = session.PeekGoalConfirmationReport(objective);
            string reply = await session.SendAsync(
                GoalProgress.BuildPrompt(objective, confirmationReport, _goalNeedsVerification, _goalAuditNote), cts.Token,
                goalRun: true, displayText: objective);
            if (reply != null && confirmationReport != null) session.TakeGoalConfirmationReport(objective);
            if (!GoalRunIsCurrent(objective, generation, cts)) return;

            // SendAsync 返回前已放開忙碌旗標；設置與審計之間不能有 await，
            // 先把 session 留在手上，避免玩家指令或自主任務插進審計。
            if (session.IsBusy || !session.TryBeginExclusiveWork()) return;
            exclusive = true;

            // 本輪已將 auditNote 送給模型，清空備忘避免跨輪殘留
            _goalAuditNote = null;

            IReadOnlyList<TranscriptEntry> entries = session.Transcript.Snapshot();
            bool hasToolEvidence = GoalProgress.HasVerificationEvidence(entries, instructionId);
            bool usedTool = HasActiveGoalToolUsage(entries, instructionId);
            GoalTurnDecision decision = GoalProgress.AfterTurn(
                _goalTurns, _goalBlockedTurns, reply, session.HasPendingConfirmation, hasToolEvidence,
                _goalBlockerKey, _goalIdleTurns, usedTool);

            GoalAuditResult audit = null;
            // 審計只能否決完成：沒有工具證據的「完成」在 AfterTurn 就不算完成，也不送審計升級。
            // 有待確認的命令時目標還沒走完，稽核只會看到半截的結果：不稽核。
            if (reply != null && !session.HasPendingConfirmation && decision.Complete)
            {
                DecisionGoalAuditor auditor = GoalAuditor;
                if (auditor != null)
                {
                    string toolEvidence = GoalProgress.BuildAuditEvidence(entries, instructionId);
                    // 稽核回來後要繼續在主線程處理目標狀態；切到執行緒池會讓後續的存檔／UI 更新離開主線程。
                    audit = await auditor.AuditAsync(objective, reply, cts.Token, toolEvidence);
                }
            }

            // 等待審計期間玩家可能編輯、刪除或暫停目標；即使是同一個字串也不能沿用舊結果。
            if (!GoalRunIsCurrent(objective, generation, cts)) return;

            if (audit != null)
            {
                GoalAuditVerdict verdict = GoalProgress.CombineAudit(
                    audit.AllDoneProbability, audit.RemainingProbability, audit.NeedsUserProbability);
                HandleAuditedGoalDecision(session, objective, reply, decision, audit, verdict);
            }
            else
            {
                HandleGoalDecision(session, objective, reply, decision);
            }
        }
        catch (Exception ex)
        {
            if (generation != _goalGeneration || cts.IsCancellationRequested) return;
            _goalBlockedTurns = _goalBlockerKey == "model-error" ? _goalBlockedTurns + 1 : 1;
            _goalBlockerKey = "model-error";
            _goalIdleTurns = 0;
            if (_goalBlockedTurns >= GoalProgress.MaxConsecutiveBlocks)
            {
                _goalPaused = true;
                _goalPauseReason = GoalPauseReason.Error;
                session.Transcript.Add(new TranscriptEntry
                {
                    Kind = TranscriptKind.Notice,
                    Text = "Goal paused after repeated errors. Use /goal resume to continue: " + objective
                });
            }
            WarnOnce("RimAgent goal run failed: " + ex.Message);
        }
        finally
        {
            if (exclusive) session.EndExclusiveWork();
            if (ReferenceEquals(_goalCts, cts)) _goalCts = null;
            cts.Dispose();
            if (generation == _goalGeneration)
            {
                _goalRunning = false;
                _goalNextRunAt = DateTime.UtcNow.AddSeconds(1);
            }
        }
    }

    private bool GoalRunIsCurrent(string objective, int generation, CancellationTokenSource cts)
    {
        return generation == _goalGeneration
            && !cts.IsCancellationRequested
            && _goals.Count > 0
            && string.Equals(_goals[0], objective, StringComparison.Ordinal)
            && !_goalPaused;
    }

    private static bool HasActiveGoalToolUsage(IEnumerable<TranscriptEntry> entries, int instructionId)
    {
        // 記憶與網頁搜尋不碰殖民地，不算推進目標；否則每輪讀 preferences.md 就能躲過閒置暫停。
        // 委派本身也不算，子代理實際跑的工具另有紀錄。
        return entries.Any(e => e.InstructionId == instructionId && e.Kind == TranscriptKind.Tool
            && e.ToolCategory != ToolCategory.Memory && e.ToolCategory != ToolCategory.Web
            && e.ToolCategory != ToolCategory.Agents
            && !e.Failed && !(e.Result ?? string.Empty).StartsWith(ToolText.ConfirmationPrefix, StringComparison.Ordinal));
    }

    internal void HandleGoalDecision(ColonyAgentSession session, string objective, string reply, GoalTurnDecision decision)
    {
        _goalTurns = decision.Turns;
        _goalBlockedTurns = decision.BlockedTurns;
        _goalIdleTurns = decision.IdleTurns;
        _goalBlockerKey = decision.BlockerKey;
        _goalNeedsVerification = reply != null && GoalProgress.ParseStatus(reply) == GoalTurnStatus.Complete
            && !decision.Complete;
        if (reply != null && !session.HasPendingConfirmation)
        {
            _goalLastProgress = TranscriptEntryData.Truncate(GoalProgress.VisibleReply(reply));
        }
        if (decision.Complete)
        {
            _lastCompletedGoal = objective;
            _goals.RemoveAt(0);
            _goalTurns = 0;
            _goalBlockedTurns = 0;
            _goalIdleTurns = 0;
            _goalBlockerKey = null;
            _goalNeedsVerification = false;
            _goalAuditNote = null;
            _goalLastProgress = null;
            _goalPauseReason = GoalPauseReason.None;
            session.Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Notice,
                Text = "Goal completed and verified: " + objective
            });
        }
        else if (decision.Pause)
        {
            _goalPaused = true;
            DetermineGoalPauseDetails(decision, reply, objective, out _goalPauseReason, out string noticeText);
            session.Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Notice,
                Text = noticeText
            });
        }
    }

    internal void HandleAuditedGoalDecision(
        ColonyAgentSession session,
        string objective,
        string reply,
        GoalTurnDecision decision,
        GoalAuditResult audit,
        GoalAuditVerdict verdict)
    {
        _goalTurns = decision.Turns;
        _goalIdleTurns = decision.IdleTurns;

        if (reply != null && !session.HasPendingConfirmation)
        {
            _goalLastProgress = TranscriptEntryData.Truncate(GoalProgress.VisibleReply(reply));
        }

        // 審計只能否決完成，不能把沒有工具證據的回合升級成完成。
        if (verdict == GoalAuditVerdict.Complete && !decision.Complete)
        {
            verdict = GoalAuditVerdict.Continue;
        }

        switch (verdict)
        {
            case GoalAuditVerdict.Blocked:
                _goalPaused = true;
                _goalPauseReason = GoalPauseReason.Blocked;
                _goalBlockedTurns = 1;
                _goalBlockerKey = "audit-needs-user";
                _goalNeedsVerification = false;
                _goalAuditNote = null;
                session.Transcript.Add(new TranscriptEntry
                {
                    Kind = TranscriptKind.Notice,
                    Text = FormatNotice("RimAgent.Goal.AuditBlocked", "Goal paused by decision audit (player input required): {0}", objective)
                });
                break;

            case GoalAuditVerdict.Complete:
                _lastCompletedGoal = objective;
                _goals.RemoveAt(0);
                _goalTurns = 0;
                _goalBlockedTurns = 0;
                _goalIdleTurns = 0;
                _goalBlockerKey = null;
                _goalNeedsVerification = false;
                _goalAuditNote = null;
                _goalLastProgress = null;
                _goalPauseReason = GoalPauseReason.None;
                session.Transcript.Add(new TranscriptEntry
                {
                    Kind = TranscriptKind.Notice,
                    Text = FormatNotice("RimAgent.Goal.AuditCompleted", "Goal completed and verified by decision audit: {0}", objective)
                });
                break;

            case GoalAuditVerdict.Continue:
            default:
                // 稽核否決完成不等於把原本的暫停條件吃掉：回合上限、連續阻礙、連續沒有
                // 有效工具行動都要照原樣暫停，否則目標可能一直空轉。
                _goalBlockedTurns = decision.BlockedTurns;
                _goalBlockerKey = decision.BlockerKey;
                _goalNeedsVerification = true;
                _goalAuditNote = audit.Reason;
                if (decision.Pause)
                {
                    _goalPaused = true;
                    DetermineGoalPauseDetails(decision, reply, objective, out _goalPauseReason, out string pauseNotice);
                    session.Transcript.Add(new TranscriptEntry
                    {
                        Kind = TranscriptKind.Notice,
                        Text = pauseNotice
                    });
                }
                else
                {
                    _goalPaused = false;
                    _goalPauseReason = GoalPauseReason.None;
                    session.Transcript.Add(new TranscriptEntry
                    {
                        Kind = TranscriptKind.Notice,
                        Text = FormatNotice("RimAgent.Goal.AuditContinuing", "Decision audit determined the goal is not yet finished; continuing: {0}", objective)
                    });
                }
                break;
        }
    }

    private static string FormatNotice(string key, string fallback, string arg)
    {
        try
        {
            if (key.CanTranslate())
            {
                return key.Translate(arg).ToString();
            }
        }
        catch
        {
            // Translate may fail off-main-thread or outside game loop
        }
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, fallback, arg);
    }

    private static void DetermineGoalPauseDetails(
        GoalTurnDecision decision,
        string reply,
        string objective,
        out GoalPauseReason reason,
        out string noticeText)
    {
        if (decision.Blocked)
        {
            if (reply == null)
            {
                reason = GoalPauseReason.Error;
                noticeText = "Goal paused after repeated errors. Use /goal resume to continue: " + objective;
            }
            else
            {
                reason = GoalPauseReason.Blocked;
                noticeText = "Goal blocked by the same condition for three turns. Use /goal resume after it changes: " + objective;
            }
        }
        else if (decision.NoProgress)
        {
            reason = GoalPauseReason.NoProgress;
            noticeText = "Goal paused after three turns without a successful tool action. Use /goal resume to continue: " + objective;
        }
        else
        {
            reason = GoalPauseReason.TurnLimit;
            noticeText = "Goal paused at the turn limit. Use /goal resume to continue: " + objective;
        }
    }

    public override void GameComponentTick()
    {
        base.GameComponentTick();
        RimAgentSettings settings = RimAgentMod.Settings;
        if (settings == null || settings.mode != AgentMode.Autonomous || !_autonomousConversationStarted) return;
        int tick = Find.TickManager.TicksGame;
        if (tick % SignalCheckIntervalTicks != 0) return;
        if (Find.CurrentMap == null) return;
        if (tick < _autoHoldUntilTick) return;

        try
        {
            ColonyAgentSession session = Session;
            // 玩家還在忙、持續目標正在跑或審計，或有預覽等確認就不搶。
            if (_autoRunning || _goalRunning || session.IsBusy || session.HasPendingConfirmation) return;
            AutonomousConfig config = ToConfig(settings);
            AutonomousSignals signals = GatherSignals();
            AutonomousTrigger trigger = _scheduler.Evaluate(tick, signals, config);
            ReportGiveUps(session);
            if (trigger == AutonomousTrigger.None) return;
            _autoRunning = true;
            _ = RunAutonomousGuardedAsync(session, trigger, tick, config, signals);
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

    /// <summary>
    /// 重試用完而放棄的條件要讓玩家知道，否則會以為 agent 還在處理。
    /// 條件沒解除前不再重試；解除後排程器會重新計次。
    /// </summary>
    private void ReportGiveUps(ColonyAgentSession session)
    {
        IReadOnlyList<AutonomousTrigger> giveUps = _scheduler.TakeGiveUps();
        for (int i = 0; i < giveUps.Count; i++)
        {
            WarnOnce("RimAgent autonomous task gave up after " + AutonomousScheduler.MaxAttempts + " attempts: " + giveUps[i]);
            session.Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Notice,
                Text = "Autonomous task stopped retrying after " + AutonomousScheduler.MaxAttempts +
                       " attempts: " + DescribeGiveUp(giveUps[i]) +
                       ". It will pick this up again once the situation clears."
            });
        }
    }

    /// <summary>放棄通知裡的人話名稱；與自主任務提示詞同一套說法。</summary>
    private static string DescribeGiveUp(AutonomousTrigger trigger)
    {
        switch (trigger)
        {
            case AutonomousTrigger.Fire: return "the fire";
            case AutonomousTrigger.EmergencyMedical: return "the downed or bleeding colonist";
            case AutonomousTrigger.MentalBreak: return "the colonist near a mental break";
            case AutonomousTrigger.FoodShortage: return "the food shortage";
            case AutonomousTrigger.Cold: return "the dangerous temperatures";
            case AutonomousTrigger.BedShortage: return "the colonists without beds";
            case AutonomousTrigger.OutdoorsBed: return "the beds outdoors";
            case AutonomousTrigger.PowerOutage: return "the power outage";
            case AutonomousTrigger.Research: return "the missing research project";
            case AutonomousTrigger.LowMood: return "the low moods";
            case AutonomousTrigger.Raid: return "the ongoing raid";
            case AutonomousTrigger.RaidEnded: return "the raid cleanup";
            default: return trigger.ToString();
        }
    }

    private AutonomousConfig ToConfig(RimAgentSettings settings)
    {
        return new AutonomousConfig
        {
            Enabled = true,
            // 定期巡檢的節奏是設計決定，不隨設定變。
            IntervalTicks = AutonomousScheduler.HeartbeatTicks,
            WindowTicks = 60000,
            CooldownTicks = UnityEngine.Mathf.RoundToInt(settings.autonomousCooldownMinutes * (2500f / 60f)),
            OnRaid = settings.autoOnRaid,
            OnMentalBreak = settings.autoOnMentalBreak,
            OnFoodShortage = settings.autoOnFoodShortage,
            OnEmergencyMedical = settings.autoOnEmergencyMedical,
            OnFire = settings.autoOnFire,
            OnUpkeep = settings.autoOnUpkeep
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

    /// <summary>UI 的停止鍵：取消進行中的自主任務（玩家指令另有自己的 token）。收尾憑 token 壓住重試，見 RunAutonomousAsync。</summary>
    public void StopAutonomousRun()
    {
        _autoCts?.Cancel();
    }

    private async Task RunAutonomousGuardedAsync(ColonyAgentSession session, AutonomousTrigger trigger, int tick, AutonomousConfig config, AutonomousSignals? signals)
    {
        // 收尾只准清掉自己那一代；讀檔後新的一輪可能已經開始，舊的 finally 不能把它的旗標關掉。
        int generation = _autoGeneration;
        try
        {
            await RunAutonomousAsync(session, trigger, tick, config, signals, generation);
        }
        finally
        {
            if (generation == _autoGeneration) _autoRunning = false;
        }
    }

    private async Task RunAutonomousAsync(ColonyAgentSession session, AutonomousTrigger trigger, int tick, AutonomousConfig config, AutonomousSignals? signals, int generation)
    {
        // 起跑前遊戲已被換掉（例如排程器在同一幀觸發後玩家立刻讀檔）：不建立 CTS，也不碰任何狀態。
        if (generation != _autoGeneration) return;
        _autoCts?.Dispose();
        _autoCts = new CancellationTokenSource();
        CancellationTokenSource cts = _autoCts;
        // 送出前先算好這句會拿到的編號：busy 旗標保證成功送出的那句取的是下一個號；
        // 送完才讀 CurrentInstructionId 的話，玩家在旗標釋放的瞬間送指令會讓我們數到別句。
        int instructionId = session.Transcript.CurrentInstructionId + 1;
        try
        {
            AutonomousPack pack = _pack?.Autonomous;
            if (pack != null && !AutonomousPrompts.HasTask(pack, trigger) && AutonomousPrompts.HasTask(pack, AutonomousTrigger.Interval))
            {
                // 章節缺失（通常是拼字錯誤）時 Build 會靜默退回 Interval：警告一次讓問題可見。
                WarnOnce("RimAgent autonomous task missing section for " + trigger + ", falling back to Interval");
            }
            string instruction = AutonomousPrompts.Build(pack, trigger);
            if (string.IsNullOrWhiteSpace(instruction))
            {
                // 自主任務文案缺檔：送空話只會燒 API，直接跳過本輪（沿用錯誤節流，只記一種一次）。
                WarnOnce("RimAgent autonomous run skipped: Prompts/Autonomous.en.md is missing or has no task for " + trigger);
                return;
            }
            // 不加 ConfigureAwait(false)：收尾的 RecordRun 要回主線程，
            // 免得與主線程的 Evaluate 同時改排程器狀態（起跑空窗另由 _autoRunning 擋住）。
            await session.SendAsync(
                instruction,
                cts.Token,
                autonomous: true,
                maxToolSteps: AutonomousRunMaxSteps,
                reasoningOverride: ReasoningLevel.Low,
                autonomousInfo: RimAgentMod.Settings?.recordRuns == true ? BuildRunInfo(trigger, tick, signals) : null);
        }
        catch (InvalidOperationException)
        {
            // 玩家在同一幀搶先送了指令：這次沒跑，不算一次自主任務、不吃冷卻。
            return;
        }
        catch (OperationCanceledException)
        {
            // 玩家主動停止自主任務（點擊停止或 /stop）：收尾統一壓住重試。
        }
        catch (Exception ex)
        {
            Log.Warning("RimAgent autonomous run failed: " + ex.Message);
        }
        // 讀檔／新局已把這個任務作廢：不要用舊遊戲的結果改新遊戲的排程器或冷卻。
        if (generation != _autoGeneration) return;
        // 冷卻、定期巡視與重試退避都從任務結束起算：一輪在高速下常跑掉十幾個遊戲小時，
        // 從起跑算的話一結束就全部到期，上一輪的效果還沒反映到訊號上就接著再跑。
        // 已回到主線程（上面的 await 沒加 ConfigureAwait(false)），讀得到結束當下的 tick；
        // 任務期間玩家退回主選單時沒有遊戲可讀，這個排程器也跟著舊遊戲作廢，沿用起跑 tick 即可。
        int finishTick = Current.Game != null ? Find.TickManager.TicksGame : tick;
        // 玩家按了停止就壓住該條件的重試（解除前不再跑）：憑 token 判斷，
        // SendAsync 吃掉取消沒擲 OCE 時同樣生效；沒消耗工具配額就不吃冷卻（其他條件不受影響）。
        bool stopped = cts.IsCancellationRequested;
        if (stopped) _scheduler.Dismiss(trigger, finishTick);
        // 只數這一句的工具紀錄：transcript 會裁掉最舊的紀錄，前後相減會失準。
        int used = session.Transcript.Snapshot().Count(e =>
            e.Kind == TranscriptKind.Tool && e.InstructionId == instructionId && e.BudgetConsumed);
        if (used > 0 || !stopped)
        {
            _scheduler.RecordRun(finishTick, used, config);
        }
        _scheduler.RecordRunFinished(trigger, finishTick);
        HoldAfterConfigurationError(session, instructionId, tick);
    }

    /// <summary>執行紀錄用的起跑脈絡；在主線程起跑前組好，嘗試次數才是這一次的。</summary>
    private AutonomousRunInfo BuildRunInfo(AutonomousTrigger trigger, int tick, AutonomousSignals? signals)
    {
        int attempt = _scheduler.AttemptOf(trigger);
        return new AutonomousRunInfo
        {
            Trigger = trigger.ToString(),
            Attempt = attempt > 0 ? attempt : (int?)null,
            GameTick = tick,
            Signals = signals
        };
    }

    private void HoldAfterConfigurationError(ColonyAgentSession session, int instructionId, int tick)
    {
        if (session.Transcript.ErrorKindOf(instructionId) != AgentErrorKind.Configuration) return;
        _autoHoldUntilTick = tick + ConfigurationErrorHoldTicks;
        WarnOnce("RimAgent autonomous tasks paused for one in-game day after a provider configuration error.");
        session.Transcript.Add(new TranscriptEntry
        {
            Kind = TranscriptKind.Notice,
            Text = "Autonomous tasks are paused for one in-game day: the model provider reported a key, balance or availability problem. " +
                   "Fix it in the RimLLM Framework settings; player instructions still work."
        });
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
            int.MaxValue,
            () => settings.mode,
            ComposeInstructions,
            _mcp,
            classifierClient: null,
            classifierModel: () => settings?.ResolveClassifierModel(_session?.CurrentAgentModel),
            classifierPrompt: () => _pack != null ? _pack.ClassifierPrompt : string.Empty,
            decisionRiskCriteria: () => _pack != null ? _pack.DecisionRiskCriteria : string.Empty,
            askNote: () => _pack != null ? _pack.AskModeNote : string.Empty,
            memory: Memory,
            useLocalSearch: () => settings.useLocalSearch,
            searxngUrl: () => settings.searxngUrl,
            skillsProvider: () => _pack?.Skills,
            subagentsProvider: () => _pack?.Subagents,
            routing: new ModelRouting
            {
                ClassifierReasoning = () => settings.classifierReasoning,
                CompactModel = () => settings.ResolveModelSlot(settings.compactModel, _session?.CurrentAgentModel),
                CompactReasoning = () => settings.compactReasoning,
                SubagentModel = name => settings.ResolveSubagentModel(name, _session?.CurrentAgentModel),
                SubagentReasoning = () => settings.subagentReasoning
            });
        session.Reasoning = settings.reasoning;
        session.PreferredModel = settings.agentModel;
        _expansions = ExpansionsLine();
        session.SubagentExpansions = _expansions;
        session.SyncAutoCompact(settings.autoCompact, settings.autoCompactThreshold);
        session.RunLoggingEnabled = () => settings.recordRuns;
        session.RunLogSink = record =>
        {
            if (!settings.recordRuns) return;
            if (_runLogWriter == null)
            {
                _runLogWriter = new RunLogWriter(Path.Combine(GenFilePaths.SaveDataFolderPath, "RimAgent"));
            }
            _runLogWriter.Append(record);
        };
        return session;
    }

    /// <summary>每句指令的完整提示詞：前情提要→DLC 啟用狀況→玩家自留地→行為準則→技能索引（全文按需讀）。每句現組，AgentPack 變更後下一句生效。</summary>
    private string ComposeInstructions()
    {
        AgentPack pack = _pack;
        string preamble = pack != null && !string.IsNullOrWhiteSpace(pack.Preamble)
            ? pack.Preamble
            : AgentPromptComposer.FallbackPreamble;
        return AgentPromptComposer.ComposeWithSkillIndex(
            preamble,
            pack != null ? pack.PlayerNotes : string.Empty,
            pack != null ? pack.Conduct : string.Empty,
            pack?.Skills,
            _expansions);
    }

    private static string ExpansionsLine()
    {
        return AgentPromptComposer.FormatExpansions(ModLister.AllExpansions
            .Select(e => (e.defName, e.isCore, e.Status == RimWorld.ExpansionStatus.Active))
            .ToList());
    }
}
