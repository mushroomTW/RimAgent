using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.AI;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools;

/// <summary>十二類工具各自的開關，以 <see cref="ToolCategory"/> 順序存成陣列。權限以「有沒有放進 ChatOptions.Tools」表達，不在執行期再檢查。</summary>
public sealed class ToolPermissions
{
    private static readonly int CategoryCount = Enum.GetValues(typeof(ToolCategory)).Length;
    private readonly bool[] _flags = new bool[CategoryCount];

    /// <summary>所有工具類別權限皆開啟的執行個體。</summary>
    public static ToolPermissions All
    {
        get
        {
            var permissions = new ToolPermissions();
            for (int i = 0; i < CategoryCount; i++) permissions._flags[i] = true;
            return permissions;
        }
    }

    /// <summary>只開啟指定類別的執行個體（取代舊的物件初始器寫法）。</summary>
    public static ToolPermissions For(params ToolCategory[] enabled)
    {
        var permissions = new ToolPermissions();
        if (enabled == null) return permissions;
        foreach (ToolCategory category in enabled) permissions.Set(category, true);
        return permissions;
    }

    public bool Allows(ToolCategory category)
    {
        return _flags[(int)category];
    }

    public void Set(ToolCategory category, bool value)
    {
        _flags[(int)category] = value;
    }
}

/// <summary>
/// 以反射收集標了 <see cref="AgentToolAttribute"/> 的方法，透過 AIFunctionFactory 建成 MEAI 工具，
/// 並包上行動紀錄與閘門通知。建構時建好一次，之後依權限與模式挑選。
/// </summary>
public sealed class AgentToolRegistry
{
    private sealed class Entry
    {
        public string Name;
        public ToolCategory Category;
        public ToolRisk Risk;
        public AITool Tool;
        /// <summary>所屬的工具包；常駐工具為 null。</summary>
        public string Pack;
    }

    private readonly List<Entry> _tools = [];
    private readonly IGameWorld _world;
    private readonly AgentTranscript _transcript;
    private readonly ConfirmationGate _gate;
    private readonly IRiskChecker _classifier;
    private readonly PlayerInterjections _interjections;
    private readonly AgentActivityTracker _activity;

    public AgentToolRegistry(IGameWorld world, AgentTranscript transcript) : this(world, transcript, null, null)
    {
    }

    public AgentToolRegistry(IGameWorld world, AgentTranscript transcript, WorkUndoStack undo)
        : this(world, transcript, undo, null)
    {
    }

#pragma warning disable S107 // Tool registry aggregates all domain tool implementations
    public AgentToolRegistry(IGameWorld world, AgentTranscript transcript, WorkUndoStack undo, ConfirmationGate gate, Mcp.McpManager mcp = null, IRiskChecker classifier = null, AgentMemoryStore memory = null, Func<bool> useLocalSearch = null, Func<string> searxngUrl = null, Func<IReadOnlyList<AgentSkill>> skills = null, PlayerInterjections interjections = null, AgentActivityTracker activity = null)
#pragma warning restore S107
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        if (transcript == null) throw new ArgumentNullException(nameof(transcript));
        _world = world;
        _transcript = transcript;
        _gate = gate;
        _classifier = classifier;
        _interjections = interjections;
        _activity = activity;

        Register(new ObserveTools(world, gate), transcript);
        Register(new MapGridTools(world), transcript);
        Register(new ChoiceTools(world, gate), transcript);
        Register(new QuestTools(world, gate), transcript);
        Register(new WorkTools(world, undo ?? new WorkUndoStack(), gate), transcript);
        Register(new BuildTools(world, gate), transcript);
        Register(new GizmoTools(world, gate), transcript);
        Register(new FloatMenuTools(world, gate), transcript);
        Register(new CombatTools(world, gate), transcript);
        Register(new TradeTools(world, gate), transcript);
        Register(new CaravanTools(world, gate), transcript);
        Register(new WorldTools(world, gate), transcript);
        Register(new MedicalTools(world, gate), transcript);
        Register(new PrisonerTools(world, gate), transcript);
        Register(new AnimalTools(world, gate), transcript);
        Register(new ZoneTools(world, gate), transcript);
        Register(new ProductionTools(world, gate), transcript);
        Register(new TimeTools(world, gate), transcript);
        Register(new SaveTools(world, gate), transcript);
        Register(new McpTools(mcp ?? new Mcp.McpManager(null), gate), transcript);
        Register(new WebSearchTools(useLocalSearch: useLocalSearch, searxngUrl: searxngUrl, gate: gate), transcript);
        foreach (AIFunction function in MemoryTools.Create(memory ?? new AgentMemoryStore(), gate))
            Add(function, ToolCategory.Memory, MemoryTools.RiskOf(function.Name), transcript);
        if (skills != null) Register(new SkillTools(skills), transcript);
        Register(new ToolOutputTools(Outputs), transcript);
        _outputReader = _tools.Single(t => t.Name == ToolOutputTools.ToolName);
    }

    private readonly Entry _outputReader;

    /// <summary>主 Agent 被截斷的工具結果原文；子代理各自用自己的 store，避免讀到別人的輸出。</summary>
    internal ToolOutputStore Outputs { get; } = new ToolOutputStore();

    private IReadOnlyList<AITool> _allTools;
    private IReadOnlyList<AgentToolDescriptor> _descriptors;

    /// <summary>全部工具，不分權限（供測試與診斷）。</summary>
    public IReadOnlyList<AITool> All => _allTools ?? (_allTools = _tools.Select(t => t.Tool).ToList());

    /// <summary>全部工具的名稱、類別與風險，依登錄順序（供測試與診斷）。</summary>
    public IReadOnlyList<AgentToolDescriptor> Descriptors =>
        _descriptors ?? (_descriptors = _tools.Select(t => new AgentToolDescriptor(t.Name, t.Category, t.Risk)).ToList());

    /// <summary>依權限挑選完整工具清單；模式只管怎麼執行，不影響清單。</summary>
    public IList<AITool> Select(ToolPermissions permissions)
    {
        return [.. WithOutputReader(Allowed(permissions)).Select(t => t.Tool)];
    }

    /// <summary>主 Agent 一次請求的工具，依權限一次分好。</summary>
    internal sealed class MainAgentTools
    {
        /// <summary>放進 ChatOptions.Tools 公告給模型的：常駐工具加上已展開的包。</summary>
        public readonly List<AITool> Advertised = [];
        /// <summary>每個包可用的工具（含已展開的）；未公告的仍可直接執行。</summary>
        public readonly Dictionary<string, List<AITool>> PackTools = new(StringComparer.Ordinal);
        /// <summary>至少有一個可用工具的包，依 <see cref="ToolPacks.All"/> 的順序。</summary>
        public IReadOnlyList<ToolPacks.Pack> Packs = Array.Empty<ToolPacks.Pack>();
    }

    internal MainAgentTools SelectForMainAgent(ToolPermissions permissions, ICollection<string> loadedPacks)
    {
        var result = new MainAgentTools();
        foreach (Entry t in WithOutputReader(Allowed(permissions)))
        {
            if (t.Pack != null)
            {
                if (!result.PackTools.TryGetValue(t.Pack, out List<AITool> tools))
                {
                    tools = [];
                    result.PackTools[t.Pack] = tools;
                }
                tools.Add(t.Tool);
                if (loadedPacks == null || !loadedPacks.Contains(t.Pack)) continue;
            }
            result.Advertised.Add(t.Tool);
        }
        result.Packs = ToolPacks.All.Where(p => result.PackTools.ContainsKey(p.Id)).ToList();
        return result;
    }

    /// <summary>
    /// read_tool_output 只讀已允許工具被截斷的輸出：清單裡有其他工具就一併給，不看類別開關；
    /// 否則關掉觀察類（或子代理沒宣告觀察類）時，截斷說明會指向不存在的工具。
    /// 子代理的儲存由呼叫流程上的 AsyncLocal 決定，工具實例本身仍只有一個。
    /// </summary>
    private List<Entry> WithOutputReader(IEnumerable<Entry> entries)
    {
        List<Entry> list = [.. entries];
        if (list.Count > 0 && !list.Contains(_outputReader)) list.Add(_outputReader);
        return list;
    }

    private IEnumerable<Entry> Allowed(ToolPermissions permissions)
    {
        if (permissions == null) throw new ArgumentNullException(nameof(permissions));
        return _tools.Where(t => permissions.Allows(t.Category));
    }

    /// <summary>
    /// 子代理的工具：玩家權限照 <see cref="Select(ToolPermissions)"/>，
    /// 再限縮到子代理宣告的類別；唯讀子代理只拿查詢工具。
    /// </summary>
    /// <summary>子代理專用版本；輸出儲存由 <see cref="SubagentTools"/> 在每次任務的呼叫流程上指定。</summary>
    public IList<AITool> SelectForSubagent(ToolPermissions permissions, IReadOnlyList<ToolCategory> categories, bool readOnly)
    {
        if (permissions == null) throw new ArgumentNullException(nameof(permissions));
        var scoped = new ToolPermissions();
        foreach (ToolCategory category in categories ?? Array.Empty<ToolCategory>())
        {
            scoped.Set(category, permissions.Allows(category));
        }
        // 讀取器在唯讀過濾之後才補：沒有其他可用工具的子代理仍視為不可用。
        return [.. WithOutputReader(Allowed(scoped).Where(t => !readOnly || t.Risk == ToolRisk.Query)).Select(t => t.Tool)];
    }

    private static readonly HashSet<string> HighRiskBlacklistedToolNames = new(StringComparer.Ordinal)
    {
        "execute_trade",
        "form_caravan",
        "release_prisoner",
        "schedule_surgery",
        "send_caravan",
        "set_prisoner_interaction",
        "settle_caravan"
    };

    /// <summary>高危黑名單（重大不可逆操作）：自動模式直接拒絕，也不能記成「本局不再問」。</summary>
    public static bool IsHighRiskBlacklisted(string name)
    {
        return !string.IsNullOrEmpty(name) && HighRiskBlacklistedToolNames.Contains(name);
    }

    /// <summary>
    /// 通用按鈕通道：它們只是「按下某個按鈕／某個右鍵選項／回答某個對話」的通道，
    /// 同一個工具可能執行無害操作，也可能啟動飛船、自毀或吞噬神經強化器；不能因工具名稱固定就整場放行。
    /// </summary>
    private static readonly HashSet<string> GenericActionToolNames = new(StringComparer.Ordinal)
    {
        "use_gizmo",
        "use_right_click_option",
        "choose_option"
    };

    /// <summary>是否為通用按鈕通道；這類工具不能記成「本局不再問」。</summary>
    public static bool IsGenericActionTool(string name)
    {
        return !string.IsNullOrEmpty(name) && GenericActionToolNames.Contains(name);
    }

    /// <summary>新的一句指令開始時清空分類器的句內判定快取（查詢快取由閘門的額度計數一併清空）。</summary>
    internal void ClearClassifierCache()
    {
        if (_classifier is RiskClassifier rc) rc.ClearCache();
        else if (_classifier is DecisionRiskChecker drc) drc.ClearCache();
    }

    /// <summary>工具名稱 → 靜態風險（供測試檢查標記）。</summary>
    public ToolRisk RiskOf(string toolName)
    {
        Entry entry = _tools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal)) ?? throw new ArgumentException($"unknown tool {toolName}", nameof(toolName));
        return entry.Risk;
    }

    private void Register(object target, AgentTranscript transcript)
    {
        foreach (MethodInfo method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var attribute = method.GetCustomAttribute<AgentToolAttribute>();
            if (attribute == null) continue;

            // 說明交給 AIFunctionFactory 從 [Description] 讀取（description 傳 null）。
            AIFunction function = AIFunctionFactory.Create(method, target, attribute.Name, null, null);
            Add(function, attribute.Category, attribute.Risk, transcript);
        }
    }

    private void Add(AIFunction function, ToolCategory category, ToolRisk risk, AgentTranscript transcript)
    {
        _tools.Add(new Entry
        {
            Name = function.Name,
            Category = category,
            Risk = risk,
            Pack = ToolPacks.PackOf(function.Name, category),
            Tool = new LoggingAIFunction(function, transcript, _world, category, risk, _gate, _classifier, Outputs, _interjections, activity: _activity)
        });
    }
}

/// <summary>一個工具的靜態描述：名稱、類別、風險。</summary>
public sealed class AgentToolDescriptor
{
    public AgentToolDescriptor(string name, ToolCategory category, ToolRisk risk)
    {
        Name = name;
        Category = category;
        Risk = risk;
    }

    public string Name { get; }
    public ToolCategory Category { get; }
    public ToolRisk Risk { get; }
}
