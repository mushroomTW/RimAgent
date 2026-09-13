using System;
using System.ComponentModel;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>醫療等級調整。健康查詢與手術排程在後續 issue 補上。</summary>
    public sealed class MedicalTools
    {
        private readonly IGameWorld _world;

        public MedicalTools(IGameWorld world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        [AgentTool("set_medical_care", ToolCategory.Medical)]
        [Description("Sets one colonist's medical care level: how good the medicine doctors may use on them. Higher care consumes better medicine. Reports the previous and new value.")]
        public string SetMedicalCare(
            [Description("Colonist name, nickname or id.")] string colonist,
            [Description("Care level: NoCare, NoMeds, HerbalOrWorse, NormalOrWorse or Best.")] string level)
        {
            ResolveResult<ColonistInfo> pawn = TargetResolver.ResolveColonist(colonist, _world.GetColonists());
            if (!pawn.Success) return pawn.Error;

            ResolveResult<DefInfo> care = DefResolver.Resolve(level, _world.MedicalCareLevels, "medical care level");
            if (!care.Success) return care.Error;

            // 舊值要在命令之前抓：轉接層回傳的物件可能就是之後被改動的那一個。
            string previous = CareLabel(pawn.Value.MedicalCare);
            CommandResult result = _world.SetMedicalCare(pawn.Value.Id, care.Value.DefName);
            if (!result.Success) return ToolText.Error(result.Message);

            return $"Set {pawn.Value.Name}'s medical care {previous} -> {care.Value.Label}.";
        }

        private string CareLabel(string defName)
        {
            foreach (DefInfo d in _world.MedicalCareLevels)
            {
                if (string.Equals(d.DefName, defName, StringComparison.OrdinalIgnoreCase)) return d.Label;
            }
            return defName;
        }
    }
}
