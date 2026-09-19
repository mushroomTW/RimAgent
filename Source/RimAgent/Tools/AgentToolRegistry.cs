using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.AI;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>十二類工具各自的開關。權限以「有沒有放進 ChatOptions.Tools」表達，不在執行期再檢查。</summary>
    public sealed class ToolPermissions
    {
        public bool Observe { get; set; } = true;
        public bool Work { get; set; } = true;
        public bool Build { get; set; }
        public bool Combat { get; set; }
        public bool Trade { get; set; }
        public bool Medical { get; set; }
        public bool Prisoner { get; set; }
        public bool Animal { get; set; }
        public bool Zone { get; set; }
        public bool Production { get; set; }
        public bool Time { get; set; }
        public bool Mcp { get; set; }

        public bool Allows(ToolCategory category)
        {
            switch (category)
            {
                case ToolCategory.Observe: return Observe;
                case ToolCategory.Work: return Work;
                case ToolCategory.Build: return Build;
                case ToolCategory.Combat: return Combat;
                case ToolCategory.Trade: return Trade;
                case ToolCategory.Medical: return Medical;
                case ToolCategory.Prisoner: return Prisoner;
                case ToolCategory.Animal: return Animal;
                case ToolCategory.Zone: return Zone;
                case ToolCategory.Production: return Production;
                case ToolCategory.Time: return Time;
                case ToolCategory.Mcp: return Mcp;
                default: return false;
            }
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
        private readonly ConfirmationGate _gate;
        private readonly RiskClassifier _classifier;

        public AgentToolRegistry(IGameWorld world, AgentTranscript transcript) : this(world, transcript, null, null)
        {
        }

        public AgentToolRegistry(IGameWorld world, AgentTranscript transcript, WorkUndoStack undo)
            : this(world, transcript, undo, null)
        {
        }

        public AgentToolRegistry(IGameWorld world, AgentTranscript transcript, WorkUndoStack undo, ConfirmationGate gate, Mcp.McpManager mcp = null, RiskClassifier classifier = null)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (transcript == null) throw new ArgumentNullException(nameof(transcript));
            _gate = gate;
            _classifier = classifier;

            Register(new ObserveTools(world, gate), transcript);
            Register(new WorkTools(world, undo ?? new WorkUndoStack(), gate), transcript);
            Register(new BuildTools(world, gate), transcript);
            Register(new CombatTools(world, gate), transcript);
            Register(new TradeTools(world, gate), transcript);
            Register(new MedicalTools(world, gate), transcript);
            Register(new PrisonerTools(world, gate), transcript);
            Register(new AnimalTools(world, gate), transcript);
            Register(new ZoneTools(world, gate), transcript);
            Register(new ProductionTools(world, gate), transcript);
            Register(new TimeTools(world, gate), transcript);
            Register(new McpTools(mcp ?? new Mcp.McpManager(null), gate), transcript);
        }

        private IReadOnlyList<AITool> _allTools;

        /// <summary>全部工具，不分權限（供測試與診斷）。</summary>
        public IReadOnlyList<AITool> All => _allTools ?? (_allTools = _tools.Select(t => t.Tool).ToList());

        /// <summary>依權限挑選完整工具清單；未指定模式時採確認模式，不套用自主模式的禁止清單。</summary>
        public IList<AITool> Select(ToolPermissions permissions)
        {
            return Select(permissions, AgentMode.Confirm);
        }

        /// <summary>依權限與模式挑選：詢問模式只留查詢工具，命令工具連 schema 都不送給模型。</summary>
        public IList<AITool> Select(ToolPermissions permissions, AgentMode mode)
        {
            if (permissions == null) throw new ArgumentNullException(nameof(permissions));
            return _tools
                .Where(t => permissions.Allows(t.Category))
                .Where(t => mode != AgentMode.Ask || t.Risk == ToolRisk.Query)
                .Where(t => mode != AgentMode.Autonomous || !IsAutonomousForbidden(t.Name))
                .Select(t => t.Tool)
                .ToList();
        }

        /// <summary>工具名稱 → 靜態風險（供測試檢查標記）。</summary>
        public ToolRisk RiskOf(string toolName)
        {
            Entry entry = _tools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));
            if (entry == null) throw new ArgumentException($"unknown tool {toolName}", nameof(toolName));
            return entry.Risk;
        }

        private static bool IsAutonomousForbidden(string name)
        {
            return string.Equals(name, "execute_trade", StringComparison.Ordinal)
                   || string.Equals(name, "form_caravan", StringComparison.Ordinal)
                   || string.Equals(name, "release_prisoner", StringComparison.Ordinal);
        }

        private void Register(object target, AgentTranscript transcript)
        {
            foreach (MethodInfo method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var attribute = method.GetCustomAttribute<AgentToolAttribute>();
                if (attribute == null) continue;

                // 說明交給 AIFunctionFactory 從 [Description] 讀取（description 傳 null）。
                AIFunction function = AIFunctionFactory.Create(method, target, attribute.Name, null, null);
                _tools.Add(new Entry
                {
                    Name = attribute.Name,
                    Category = attribute.Category,
                    Risk = attribute.Risk,
                    Tool = new LoggingAIFunction(function, transcript, attribute.Category, attribute.Risk, _gate, _classifier)
                });
            }
        }
    }
}
