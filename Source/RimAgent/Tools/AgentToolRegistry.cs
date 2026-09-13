using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.AI;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>九類工具各自的開關。權限以「有沒有放進 ChatOptions.Tools」表達，不在執行期再檢查。</summary>
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
                default: return false;
            }
        }
    }

    /// <summary>
    /// 以反射收集標了 <see cref="AgentToolAttribute"/> 的方法，透過 AIFunctionFactory 建成 MEAI 工具，
    /// 並包上行動紀錄。建構時建好一次，之後依權限挑選。
    /// </summary>
    public sealed class AgentToolRegistry
    {
        private sealed class Entry
        {
            public ToolCategory Category;
            public AITool Tool;
        }

        private readonly List<Entry> _tools = new List<Entry>();

        public AgentToolRegistry(IGameWorld world, AgentTranscript transcript)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (transcript == null) throw new ArgumentNullException(nameof(transcript));

            Register(new ObserveTools(world), transcript);
            Register(new WorkTools(world), transcript);
            Register(new BuildTools(world), transcript);
            Register(new CombatTools(world), transcript);
            Register(new TradeTools(world), transcript);
            Register(new MedicalTools(world), transcript);
            Register(new PrisonerTools(world), transcript);
            Register(new AnimalTools(world), transcript);
            Register(new ZoneTools(world), transcript);
        }

        /// <summary>全部工具，不分權限（供測試與診斷）。</summary>
        public IReadOnlyList<AITool> All => _tools.Select(t => t.Tool).ToList();

        public IList<AITool> Select(ToolPermissions permissions)
        {
            if (permissions == null) throw new ArgumentNullException(nameof(permissions));
            return _tools.Where(t => permissions.Allows(t.Category)).Select(t => t.Tool).ToList();
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
                    Category = attribute.Category,
                    Tool = new LoggingAIFunction(function, transcript, attribute.Category)
                });
            }
        }
    }
}
