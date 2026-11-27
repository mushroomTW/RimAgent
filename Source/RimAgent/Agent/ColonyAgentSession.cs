using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using RimAgent.Game;
using RimAgent.Mcp;
using RimAgent.Tools;
using RimLLM_Framework;
using RimLLM_Framework.Api;

namespace RimAgent.Agent;

/// <summary>一場遊戲期間的對話：歷史交給 Agent Framework 的 session，行動紀錄寫進 transcript，同一時間只允許一個請求在飛；多輪工具呼叫由框架的主線程包裝（inner）在單次 GetResponseAsync 內跑完，ChatClientAgent 以 UseProvidedChatClientAsIs 持有 inner，不另起工具迴圈。</summary>
public sealed partial class ColonyAgentSession
{
    public const string ModId = "greenmushroom.rimagent";
    public const float Temperature = 0.3f;
    // 高推理強度的供應商可能把推理 token 計入輸出上限；保留足夠空間給工具規劃與最終回覆。
    public const int DefaultMaxOutputTokens = 8192;
    private const int StoredHistoryMessageLimit = 60;
    private const int AutoFoldPreservedGroups = 6;
    /// <summary>手動 /compact 保留的最近組數：比自動折疊更積極，壓完只留最近一兩輪。</summary>
    private const int ManualSummaryPreservedGroups = 4;

    // 以下各層 chat client 都包著外部傳入、與框架共用的 innerClient，本類別不擁有它，
    // 所以刻意不實作 IDisposable：DelegatingChatClient.Dispose 會連內層一起釋放。
    private readonly ChatClientAgent _agent;
    private readonly RunOptionsCapture _runOptions;
    private readonly FunctionInvokingChatClient? _invoking;
    private readonly AIFunction _toolPackLoader;
    /// <summary>run_script：一步依序執行多個工具呼叫；只給主 Agent。</summary>
    private readonly AIFunction _scriptRunner;
    private readonly ImageAttachingChatClient _images;
    /// <summary>本段對話展開過的工具包；清除對話、讀檔或手動壓縮時重置。工具（主線程）與組請求（背景）都會碰，所以上鎖。</summary>
    private readonly HashSet<string> _loadedPacks = new(StringComparer.Ordinal);
#pragma warning disable MAAI001 // 同建構式：AF 1.21 壓縮 API 評估中，鎖定版本並以回歸測試保護。
    private readonly CompactionProvider _compaction;
#pragma warning restore MAAI001
    private readonly AgentToolRegistry _registry;
    private readonly SubagentTools _subagents;
    private readonly Func<ToolPermissions> _permissions;
    private readonly TokenTrackingChatClient _tokenUsage;
    private readonly Func<string, int?> _contextWindowLookup;
    private readonly Func<string, bool?> _visionLookup;
    private readonly Func<string, int?> _outputLimitLookup;
    private readonly DecisionReasoningRouter _router;

    /// <summary>摘要用的原始 client：不經工具迴圈包裝，壓縮呼叫不會觸發工具執行。</summary>
    private readonly IChatClient _summarizer;
    private readonly IGameWorld _world;
    private readonly AgentMemoryStore _memory;
    private readonly PlayerInterjections _interjections = new();
    private readonly FailureLoopGuard _loopGuard;
    private readonly Func<string?>? _instructions;
    private AgentSession? _session;
    private long _instructionTokens;
    private bool _tokenBudgetReached;
    private string? _serializedSession;
    /// <summary>每句指令結束時備妥的存檔內容；存檔直接取用，不在主線程序列化，也不因忙碌而漏存。</summary>
    private string? _saveSnapshot;
    private bool _snapshotWarningShown;
    private int _busy;
    private int _maxSteps;
    private RunLog? _activeRunLog;
    private bool _runLogWarningShown;

    /// <summary>可選的獨立執行紀錄；預設不記錄，由遊戲設定決定。</summary>
    public Func<bool>? RunLoggingEnabled { get; set; }
    public RunLogWriter? RunLogs { get; set; }

    public ToolUsageStats? ToolUsage { get; set; }

    /// <summary>單句指令（含子代理）的 token 上限；0 為不限。</summary>
    public Func<long>? InstructionTokenBudget { get; set; }

    /// <param name="innerClient">框架交出的 IChatClient（測試時為假 client）；這裡再包上工具迴圈。</param>
    /// <param name="permissions">每次送出時讀取的權限，讓設定頁的變更下一句立即生效。</param>
    /// <param name="mode">執行模式；每次送出時讀取，null 時採確認模式。</param>
    /// <param name="instructions">每句送出時組裝的完整系統提示詞；null 則沿用核心提示詞。</param>
    /// <param name="mcp">MCP 管理器；null 則用停用的預設管理器。</param>
    /// <param name="classifierClient">自動模式分類器用的 client；null 則與 innerClient 共用（測試可另給假 client）。</param>
    /// <param name="classifierModel">自動模式分類器指定使用的模型識別碼 provider；null 則沿用框架預設。</param>
    /// <param name="classifierPrompt">分類器的系統提示詞（AgentPack/Prompts/RiskClassifier.md）；null 則分類器 fail-closed。</param>
    /// <param name="contextWindowLookup">依模型識別碼查詢上下文視窗上限的委派；null 則使用預設值。</param>
    /// <param name="memory">記憶儲存庫；null 則建立新的預設實例。</param>
    /// <param name="subagentsProvider">AgentPack/Agents 載入的子代理；null 或空清單則不提供委派工具。</param>
    /// <param name="routing">分類器、/compact 摘要與子代理的模型槽；null 則全部沿用框架備援鏈與 Auto 思考強度。</param>
    /// <param name="decisionRiskCriteria">自動模式決策模型的判準檔內容（AgentPack/Prompts/DecisionRisk.md）；null 則不啟用決策模型。</param>
    /// <param name="decisionService">測試用決策模型服務；null 則透過 DecisionServiceFactory 建立。</param>
    /// <param name="router">測試用決策模型分流器；null 則透過 DecisionServiceFactory 建立。</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "DI constructor with optional dependencies for testing")]
    public ColonyAgentSession(IChatClient innerClient, IGameWorld world, Func<ToolPermissions> permissions, int maxSteps, Func<AgentMode>? mode = null, Func<string?>? instructions = null, McpManager? mcp = null, IChatClient? classifierClient = null, Func<string?>? classifierModel = null, Func<string?>? classifierPrompt = null, Func<string, int?>? contextWindowLookup = null, AgentMemoryStore? memory = null, Func<bool>? useLocalSearch = null, Func<string>? searxngUrl = null, Func<IReadOnlyList<AgentSkill>>? skillsProvider = null, Func<IReadOnlyList<AgentSubagent>>? subagentsProvider = null, ModelRouting? routing = null, Func<string?>? decisionRiskCriteria = null, IDecisionService? decisionService = null, DecisionReasoningRouter? router = null, Func<string, bool?>? visionLookup = null, Func<string, int?>? outputLimitLookup = null)
    {
        ArgumentNullException.ThrowIfNull(innerClient);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(permissions);
        _permissions = permissions;

        Transcript = new AgentTranscript { Observer = RecordTranscriptEntry };
        Undo = new WorkUndoStack();
        ConfirmGate = new ConfirmationGate(mode);
        _world = world;
        _memory = memory ?? new AgentMemoryStore();
        _instructions = instructions;
        IRiskChecker classifier = CreateRiskChecker(
            classifierClient ?? innerClient, classifierModel, classifierPrompt, routing, decisionRiskCriteria, decisionService, () => History);

        _router = router ?? new DecisionReasoningRouter(
            decisionService,
            () => DecisionServiceFactory.Create(),
            () => RimAgentMod.Settings,
            () => History);

        _loopGuard = new FailureLoopGuard(NotifyStuck);

        _registry = new AgentToolRegistry(world, Transcript, Undo, ConfirmGate, mcp, classifier, _memory, useLocalSearch, searxngUrl, skillsProvider, _interjections, Activity,
            layoutsEnabled: () => RimAgentMod.Settings?.layouts ?? true, loopGuard: _loopGuard);
        // 子代理用原始 client：它的用量不該蓋掉標題列顯示的主模型輸入 token。
        _subagents = new SubagentTools(new ThinkingActivityClient(innerClient, Activity, Transcript), _registry, Transcript, subagentsProvider, _permissions,
            routing?.SubagentModel, routing?.SubagentReasoning, () => ConfirmGate.RemainingToolBudget, RecordModelResponse, MaxOutputFor);
        _tokenUsage = new TokenTrackingChatClient(innerClient);
        _contextWindowLookup = contextWindowLookup ?? RimLLMProvider.GetContextWindow;
        _visionLookup = visionLookup ?? RimLLMProvider.SupportsVision;
        _outputLimitLookup = outputLimitLookup ?? RimLLMProvider.GetMaxOutputTokens;
        _tokenUsage.ResponseObserved = response => RecordModelResponse(string.Empty, response);
        // 附圖在 FICC 之內：每次模型呼叫前都看得到這一輪最新的工具結果。讀圖要排到主線程。
        _images = new ImageAttachingChatClient(
            new ThinkingActivityClient(_tokenUsage, Activity, Transcript),
            () => ImageInputEnabled,
            path => LoadScreenshotForModelAsync(world, path));
        _compaction = CreateCompaction();
#pragma warning disable MAAI001 // AF 1.21 將壓縮 API 標記為評估中；已鎖定相依版本並以回歸測試保護。
        IChatClient compacting = new ChatClientBuilder(_images).UseAIContextProviders(_compaction).Build();
#pragma warning restore MAAI001
        _runOptions = new RunOptionsCapture(new MessageIsolatingChatClient(compacting), EndRunIfOverTokenBudget);
        var invokingClient = _runOptions.AsMainThreadFunctionInvokingClient(maxSteps);
        _invoking = invokingClient as FunctionInvokingChatClient;
        if (_invoking != null) _runOptions.RecordToolResults(_invoking);
        _toolPackLoader = CreateToolPackLoader(world);
        _scriptRunner = CreateScriptRunner();
        _summarizer = new ModelSlotChatClient(innerClient, routing?.CompactModel, routing?.CompactReasoning);
        _maxSteps = NormalizeSteps(maxSteps);
        _agent = CreateAgent(invokingClient);
    }

    /// <summary>風險分類器；有判準檔時外包一層決策模型，決策模型不可用時退回分類器。</summary>
    private static IRiskChecker CreateRiskChecker(
        IChatClient classifierClient,
        Func<string?>? classifierModel,
        Func<string?>? classifierPrompt,
        ModelRouting? routing,
        Func<string?>? decisionRiskCriteria,
        IDecisionService? decisionService,
        Func<IReadOnlyList<ChatMessage>> history)
    {
        // 分類器自己設模型（classifierModel），思考強度由模型槽包裝補上。
        var fallbackClassifier = new RiskClassifier(
            new ModelSlotChatClient(classifierClient, model: null, routing?.ClassifierReasoning),
            classifierModel, classifierPrompt, routing?.ClassifierPinned);

        // 判準檔在才可能有決策模型路徑；決策模型本身在每次判定時才決定要不要用
        // （玩家可能進遊戲後才去框架設定好，那時下一個命令就該走決策模型）。
        if (string.IsNullOrWhiteSpace(decisionRiskCriteria?.Invoke())) return fallbackClassifier;
        return new DecisionRiskChecker(
            decisionService,
            fallbackClassifier,
            decisionRiskCriteria,
            () => DecisionServiceFactory.Create(),
            history);
    }

#pragma warning disable MAAI001 // AF 1.21 將壓縮 API 標記為評估中；已鎖定相依版本並以回歸測試保護。
    /// <summary>折到門檻以下一段距離才停：AF 預設剛好低於門檻就停，之後每幾步又折一次，每次都改動前綴而失去提示快取。只折疊送出請求裡較舊的工具輸出。掛在工具迴圈內側（ChatClientBuilder 層）才會每一步送出前都折疊；掛在 ChatClientAgentOptions 只在指令邊界執行，工具迴圈內不會觸發（見 AF Agent_Step18 說明）。只改送出的內容，不動 session 內存的歷史；觸發條件讀即時設定，設定頁調整下一句生效。</summary>
    private CompactionProvider CreateCompaction()
    {
        return new CompactionProvider(new ToolResultCompactionStrategy(
            ShouldAutoCompact,
            AutoFoldPreservedGroups,
            AutoCompactTargetReached)
        {
            ToolCallFormatter = FoldToolGroup
        });
    }

    private const int FoldedArgumentChars = 120;
    private const int FoldedResultChars = 300;

    [GeneratedRegex(@"full output saved as [^\s,]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SavedOutput { get; }

    /// <summary>AF 1.28 的預設格式把完整結果原文照搬成文字，折疊後大小幾乎不變，所以自訂並截短。</summary>
    internal static string FoldToolGroup(CompactionMessageGroup group)
    {
        Dictionary<string, string> results = new(StringComparer.Ordinal);
        foreach (FunctionResultContent result in group.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>())
        {
            if (result.CallId != null) results[result.CallId] = result.Result?.ToString() ?? string.Empty;
        }
        var sb = new StringBuilder("[Tool Calls] (older results shortened; look things up again if the details matter, but do not repeat commands that already ran)");
        foreach (FunctionCallContent call in group.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
        {
            string arguments = call.Arguments == null ? string.Empty
                : string.Join(", ", call.Arguments.Select(a => a.Key + "=" + a.Value));
            string result = call.CallId != null && results.TryGetValue(call.CallId, out string? found) ? found : string.Empty;
            string shown = ToolText.TruncateField(result, FoldedResultChars);
            sb.Append('\n').Append(call.Name).Append('(').Append(ToolText.TruncateField(arguments, FoldedArgumentChars)).Append("): ").Append(shown);
            Match saved = SavedOutput.Match(result);
            if (saved.Success && !shown.Contains(saved.Value, StringComparison.Ordinal)) sb.Append(" [").Append(saved.Value).Append(", read it with read_tool_output]");
        }
        return sb.ToString();
    }
#pragma warning restore MAAI001

    /// <summary>經行動紀錄包裝：展開會出現在對話裡，也能順帶送達玩家插話；不碰遊戲世界，不扣自主模式的工具額度。</summary>
    private LoggingAIFunction CreateToolPackLoader(IGameWorld world)
    {
        var tools = new ToolPackTools(LoadToolPack);
        return new LoggingAIFunction(
            AIFunctionFactory.Create(tools.LoadToolpack, ToolPackTools.ToolName),
            Transcript, world, ToolCategory.Observe, ToolRisk.Query, ConfirmGate, classifier: null, _registry.Outputs, _interjections,
            consumesBudget: false, activity: Activity);
    }

    /// <summary>腳本工具：每步呼叫登錄表裡的原工具（含未公告的工具包），照常受權限、模式、閘門與紀錄管控。</summary>
    private AIFunction CreateScriptRunner()
    {
        var tools = new ScriptTools(() => _registry.Select(_permissions()), _registry.Outputs, _interjections);
        return AIFunctionFactory.Create(tools.RunScript, ScriptTools.ToolName);
    }

    /// <summary>建構時不綁 tools（每輪經 RunOptions 傳，權限變更下一句生效）。提示詞只走 run 層 Instructions：AF 1.28 的 ChatClientAgent 會把 agent 層與 run 層 Instructions 以換行串接而非覆寫，agent 層再放保底前情會讓它每句重複一次。</summary>
    private static ChatClientAgent CreateAgent(IChatClient invokingClient)
    {
        return new ChatClientAgent(invokingClient, new ChatClientAgentOptions
        {
            Name = "ColonyAgent",
            UseProvidedChatClientAsIs = true
        });
    }

    public string? CurrentAgentModel => _tokenUsage.LastModel;

    public AgentTranscript Transcript { get; }

    public AgentActivityTracker Activity { get; } = new AgentActivityTracker();

    public Func<bool>? ConfirmAutonomousMajor { get; set; }

    public WorkUndoStack Undo { get; }

    public ConfirmationGate ConfirmGate { get; }

    public AgentMode Mode => ConfirmGate.Mode;

    private string? _pendingConfirmation;
    private string? _pendingGoalObjective;
    private string? _confirmedGoalObjective;
    private string? _goalConfirmationReport;

    public bool HasPendingConfirmation => _pendingConfirmation != null;

    /// <summary>等待中的確認是否由目標執行器提出，供目標指令避免取消玩家自己的命令。</summary>
    public bool HasPendingGoalConfirmation => _pendingConfirmation != null && _pendingGoalObjective != null;

    /// <summary>查看確認結果但先保留，避免模型請求失敗時遺失。</summary>
    public string? PeekGoalConfirmationReport(string? objective)
    {
        return _confirmedGoalObjective == objective ? _goalConfirmationReport : null;
    }

    public string? TakeGoalConfirmationReport(string? objective)
    {
        if (PeekGoalConfirmationReport(objective) == null) return null;
        string? report = _goalConfirmationReport;
        _confirmedGoalObjective = null;
        _goalConfirmationReport = null;
        return report;
    }

    public bool IsBusy => Volatile.Read(ref _busy) == 1;

    /// <summary>
    /// 目標審計等「送出後還要處理結果」的工作，用同一個忙碌旗標把 session 留到收尾，
    /// 避免玩家指令或自主任務在審計途中插入。
    /// </summary>
    internal bool TryBeginExclusiveWork()
    {
        return Interlocked.CompareExchange(ref _busy, 1, 0) == 0;
    }

    internal void EndExclusiveWork()
    {
        Volatile.Write(ref _busy, 0);
    }

    /// <summary>模型思考強度；每句指令組請求時讀取，改完下一句生效。由 GameComponent 從設定同步。</summary>
    public ReasoningLevel Reasoning { get; set; } = ReasoningLevel.Auto;

    public bool ReasoningRouting { get; set; }

    public DecisionReports DecisionReports { get; } = new();

    /// <summary>
    /// 優先使用的模型（"供應商:模型"）；空白代表交給框架備援鏈。每句指令組請求時讀取，由 GameComponent 從設定同步。
    /// 玩家指令、自主任務與 <c>/compact</c> 摘要共用它；風險分類器另走自己的設定。
    /// </summary>
    private string? _preferredModel;
    public string? PreferredModel
    {
        get => _preferredModel;
        set
        {
            if (string.Equals(_preferredModel, value, StringComparison.Ordinal)) return;
            _preferredModel = value;
            _tokenUsage?.ResetModel();
        }
    }

    /// <summary>本局 DLC 啟用狀況的一行說明，交給子代理提示詞；主提示詞由 GameComponent 自己組。建立會話時在主線程設定一次。</summary>
    public string? SubagentExpansions
    {
        get => _subagents.Expansions;
        set => _subagents.Expansions = value;
    }

    /// <summary>工具迴圈的迭代上限；正式遊戲傳入 int.MaxValue，僅測試或單輪預算另行縮小。</summary>
    public int MaxSteps => Volatile.Read(ref _maxSteps);

    /// <summary>自動壓縮開關。設定頁變更後由 GameComponent 經 <see cref="SyncAutoCompact"/> 同步，觸發條件每輪讀取即時值。</summary>
    public bool AutoCompactEnabled { get; set; } = true;

    /// <summary>自動壓縮門檻，為模型上下文上限的百分比。</summary>
    public int AutoCompactThreshold { get; set; } = RimAgentSettings.DefaultAutoCompactThreshold;
}
