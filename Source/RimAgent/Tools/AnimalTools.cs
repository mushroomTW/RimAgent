using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>動物：查詢、馴養、訓練開關、區域指派。馴養與訓練走遊戲原生機制。</summary>
    public sealed class AnimalTools
    {
        /// <summary>無限制區域的關鍵字（大小寫不敏感）。</summary>
        internal const string UnrestrictedKeyword = "unrestricted";

        private readonly IGameWorld _world;
        private readonly ConfirmationGate _gate;

        public AnimalTools(IGameWorld world, ConfirmationGate gate = null)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _gate = gate;
        }

        [AgentTool("list_animals", ToolCategory.Animal, ToolRisk.Query)]
        [Description("Lists colony animals with id, name, allowed area and health, and counts the wild animals on the map by species. Check it before taming, training or area changes.")]
        public string ListAnimals()
        {
            List<AnimalInfo> animals = _world.GetAnimals().ToList();
            List<AnimalInfo> tame = animals.Where(a => !a.Wild).ToList();
            List<AnimalInfo> wild = animals.Where(a => a.Wild).ToList();

            var sb = new StringBuilder();
            if (tame.Count == 0) sb.Append("The colony has no animals.");
            else
            {
                sb.Append(tame.Count == 1 ? "1 colony animal:" : tame.Count + " colony animals:").AppendLine();
                foreach (AnimalInfo a in tame)
                {
                    sb.Append("[").Append(a.Id).Append("] ").Append(a.Name);
                    sb.Append(" (area: ").Append(string.IsNullOrEmpty(a.Area) ? UnrestrictedKeyword : a.Area).Append(')');
                    sb.Append(" (health ").Append(ToolText.Pct(a.HealthPercent)).Append(')');
                    if (a.Downed) sb.Append(" DOWNED");
                    sb.AppendLine();
                }
                sb.Length -= Environment.NewLine.Length;
            }
            if (wild.Count > 0)
            {
                sb.AppendLine();
                sb.Append("Wild on the map: ").Append(string.Join(", ",
                    wild.GroupBy(a => a.Species).Select(g => g.Count() + " " + g.Key).ToArray())).Append('.');
            }
            return sb.ToString().Trim();
        }

        [AgentTool("tame_animal", ToolCategory.Animal, ToolRisk.Safe)]
        [Description("Marks a wild animal for taming; a handler attempts it when available with the game's success chances.")]
        public string TameAnimal(
            [Description("Wild animal's species (e.g. \"muffalo\") or id; when several match, the error lists their ids.")] string animal)
        {
            Result<AnimalInfo> target = ResolveAnimal(animal, _world.GetAnimals());
            if (!target.Success) return target.Error;
            if (!target.Value.Wild) return ToolText.Error($"{target.Value.Name} is already tame.");

            string blocked = ToolText.Blocked(_gate, $"would mark {target.Value.Name} [{target.Value.Id}] for taming.");
            if (blocked != null) return blocked;

            CommandResult result = _world.TameAnimal(target.Value.Id);
            if (!result.Success) return ToolText.Error(result.Message);
            return result.Message;
        }

        [AgentTool("train_animal", ToolCategory.Animal, ToolRisk.Safe)]
        [Description("Turns one training (obedience, release, rescue or haul) on or off for a colony animal. Animals that cannot learn it are reported.")]
        public string TrainAnimal(
            [Description("Colony animal name or id.")] string animal,
            [Description("Training name, e.g. \"obedience\".")] string training,
            [Description("true to train it, false to stop.")] bool enable)
        {
            Result<AnimalInfo> target = ResolveAnimal(animal, _world.GetAnimals());
            if (!target.Success) return target.Error;
            if (target.Value.Wild) return ToolText.Error($"{target.Value.Name} is wild; tame it first.");

            Result<DefInfo> def = DefResolver.Resolve(training, _world.AnimalTraining, "animal training");
            if (!def.Success) return def.Error;

            string blocked = ToolText.Blocked(_gate, $"would {(enable ? "start" : "stop")} training {target.Value.Name} in {def.Value.Label}.");
            if (blocked != null) return blocked;

            CommandResult result = _world.SetAnimalTraining(target.Value.Id, def.Value.DefName, enable);
            if (!result.Success) return ToolText.Error(result.Message);
            return result.Message;
        }

        [AgentTool("set_animal_area", ToolCategory.Animal, ToolRisk.Safe)]
        [Description("Restricts a colony animal to one allowed area, or \"unrestricted\" to let it roam.")]
        public string SetAnimalArea(
            [Description("Colony animal name or id.")] string animal,
            [Description("Area name, or \"unrestricted\".")] string area)
        {
            Result<AnimalInfo> target = ResolveAnimal(animal, _world.GetAnimals());
            if (!target.Success) return target.Error;
            if (target.Value.Wild) return ToolText.Error($"{target.Value.Name} is wild; tame it first.");

            string label = (area ?? string.Empty).Trim();
            if (string.Equals(label, UnrestrictedKeyword, StringComparison.OrdinalIgnoreCase)) label = string.Empty;
            else
            {
                Result<DefInfo> def = DefResolver.Resolve(label, _world.AnimalAreas, "animal area");
                if (!def.Success) return def.Error;
                label = def.Value.Label;
            }

            string blocked = ToolText.Blocked(_gate, $"would set {target.Value.Name}'s allowed area to {(label.Length == 0 ? UnrestrictedKeyword : label)}.");
            if (blocked != null) return blocked;

            CommandResult result = _world.SetAnimalArea(target.Value.Id, label);
            if (!result.Success) return ToolText.Error(result.Message);
            return result.Message;
        }

        /// <summary>動物解析：id 精確命中；名字／物種可模糊比對，歧義即報錯並列候選。</summary>
        internal static Result<AnimalInfo> ResolveAnimal(string input, IReadOnlyList<AnimalInfo> animals)
        {
            return TargetResolver.ResolveEntity(
                input,
                animals,
                a => a.Id,
                Names,
                a => $"[{a.Id}] {a.Name}",
                "animal");
        }

        private static IEnumerable<string> Names(AnimalInfo a)
        {
            if (!string.IsNullOrEmpty(a.Name)) yield return a.Name;
            if (!string.IsNullOrEmpty(a.Species)) yield return a.Species;
        }
    }
}
