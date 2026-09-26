using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.AI;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools
{
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
        }

        private readonly List<Entry> _tools = new List<Entry>();
        private readonly IGameWorld _world;
        private readonly ConfirmationGate _gate;
        private readonly RiskClassifier _classifier;

        public AgentToolRegistry(IGameWorld world, AgentTranscript transcript) : this(world, transcript, null, null)
        {
        }

        public AgentToolRegistry(IGameWorld world, AgentTranscript transcript, WorkUndoStack undo)
            : this(world, transcript, undo, null)
        {
        }

        public AgentToolRegistry(IGameWorld world, AgentTranscript transcript, WorkUndoStack undo, ConfirmationGate gate, Mcp.McpManager mcp = null, RiskClassifier classifier = null, AgentMemoryStore memory = null, Func<bool> useLocalSearch = null, Func<string> searxngUrl = null)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (transcript == null) throw new ArgumentNullException(nameof(transcript));
            _world = world;
            _gate = gate;
            _classifier = classifier;

            Register(new ObserveTools(world, gate), transcript);
            Register(new ChoiceTools(world, gate), transcript);
            Register(new WorkTools(world, undo ?? new WorkUndoStack(), gate), transcript);
            Register(new BuildTools(world, gate), transcript);
            Register(new GizmoTools(world, gate), transcript);
            Register(new CombatTools(world, gate), transcript);
            Register(new TradeTools(world, gate), transcript);
            Register(new MedicalTools(world, gate), transcript);
            Register(new PrisonerTools(world, gate), transcript);
            Register(new AnimalTools(world, gate), transcript);
            Register(new ZoneTools(world, gate), transcript);
            Register(new ProductionTools(world, gate), transcript);
            Register(new TimeTools(world, gate), transcript);
            Register(new McpTools(mcp ?? new Mcp.McpManager(null), gate), transcript);
            Register(new WebSearchTools(useLocalSearch: useLocalSearch, searxngUrl: searxngUrl, gate: gate), transcript);
            foreach (AIFunction function in MemoryTools.Create(memory ?? new AgentMemoryStore(), gate))
                Add(function, ToolCategory.Memory, MemoryTools.RiskOf(function.Name), transcript);
        }

        private IReadOnlyList<AITool> _allTools;

        /// <summary>全部工具，不分權限（供測試與診斷）。</summary>
        public IReadOnlyList<AITool> All => _allTools ?? (_allTools = _tools.Select(t => t.Tool).ToList());

        /// <summary>全部工具的名稱、類別與風險，依登錄順序（供測試與診斷）。</summary>
        public IReadOnlyList<AgentToolDescriptor> Descriptors =>
            _tools.Select(t => new AgentToolDescriptor(t.Name, t.Category, t.Risk)).ToList();

        /// <summary>依權限挑選完整工具清單；未指定模式時採確認模式，不套用自主模式的禁止清單。</summary>
        public IList<AITool> Select(ToolPermissions permissions)
        {
            return Select(permissions, AgentMode.Confirm);
        }

        /// <summary>依權限與模式挑選：詢問模式只留查詢工具，YOLO 模式排除黑名單工具，命令工具連 schema 都不送給模型。</summary>
        public IList<AITool> Select(ToolPermissions permissions, AgentMode mode)
        {
            if (permissions == null) throw new ArgumentNullException(nameof(permissions));
            return _tools
                .Where(t => permissions.Allows(t.Category))
                .Where(t => mode != AgentMode.Ask || t.Risk == ToolRisk.Query)
                .Where(t => mode != AgentMode.Yolo || !IsYoloForbidden(t.Name))
                .Select(t => t.Tool)
                .ToList();
        }

        private static readonly HashSet<string> YoloForbiddenToolNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "execute_trade",
            "form_caravan",
            "release_prisoner",
            "schedule_surgery",
            "set_prisoner_interaction"
        };

        /// <summary>YOLO 模式下受保護的黑名單工具（不可直接執行的重大高危操作）。</summary>
        public static bool IsYoloForbidden(string name)
        {
            return !string.IsNullOrEmpty(name) && YoloForbiddenToolNames.Contains(name);
        }

        /// <summary>工具名稱 → 靜態風險（供測試檢查標記）。</summary>
        public ToolRisk RiskOf(string toolName)
        {
            Entry entry = _tools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));
            if (entry == null) throw new ArgumentException($"unknown tool {toolName}", nameof(toolName));
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
                Tool = new LoggingAIFunction(function, transcript, _world, category, risk, _gate, _classifier)
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
}
