using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using RimAgent.Game;

namespace RimAgent.Tests;

internal sealed partial class FakeGameWorld
{
    public bool UseWorkPriorities { get; set; }

    public void SetUseWorkPriorities(bool value)
    {
        Calls.Add($"SetUseWorkPriorities({value})");
        UseWorkPriorities = value;
    }

    public CommandResult SetWorkPriority(int pawnId, string workTypeDefName, int priority)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"SetWorkPriority({pawnId},{workTypeDefName},{priority})"));
        ColonistInfo pawn = Colonists.FirstOrDefault(c => c.Id == pawnId);
        WorkPriorityInfo work = pawn?.WorkPriorities.FirstOrDefault(w => w.WorkTypeDefName == workTypeDefName);
        if (work == null) return CommandResult.Fail("pawn or work type not found");
        work.Priority = priority;
        return CommandResult.Ok();
    }

    public CommandResult SetTimeAssignment(int pawnId, int hour, string assignmentDefName)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"SetTimeAssignment({pawnId},{hour},{assignmentDefName})"));
        ColonistInfo pawn = Colonists.FirstOrDefault(c => c.Id == pawnId);
        if (pawn == null) return CommandResult.Fail("pawn not found");
        var table = pawn.Timetable.ToList();
        table[hour] = assignmentDefName;
        pawn.Timetable = table;
        return CommandResult.Ok();
    }
}
