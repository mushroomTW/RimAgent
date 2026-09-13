using System;

namespace RimAgent.Tools
{
    public enum ToolCategory
    {
        Observe,
        Work,
        Build,
        Combat,
        Trade,
        Medical,
        Prisoner,
        Animal,
        Zone,
        Production
    }

    /// <summary>
    /// 標記一個公開方法為 agent 工具。<see cref="AgentToolRegistry"/> 以反射收集，
    /// 工具說明與參數說明則交給 <see cref="System.ComponentModel.DescriptionAttribute"/>，
    /// 由 MEAI 的 AIFunctionFactory 讀進 schema。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class AgentToolAttribute : Attribute
    {
        public AgentToolAttribute(string name, ToolCategory category)
        {
            Name = name;
            Category = category;
        }

        /// <summary>送給模型的工具名稱（snake_case）。</summary>
        public string Name { get; }
        public ToolCategory Category { get; }
    }
}
