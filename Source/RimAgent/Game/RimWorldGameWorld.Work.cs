using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RimAgent.Game;

internal sealed partial class RimWorldGameWorld
{
    public IReadOnlyList<WorkTableInfo> GetWorkTables()
    {
        return Map.listerBuildings.allBuildingsColonist
            .Where(b => b is IBillGiver)
            .Select(b => new WorkTableInfo
            {
                Id = b.thingIDNumber,
                Label = b.LabelCap,
                X = b.Position.x,
                Z = b.Position.z,
                BillCount = ((IBillGiver)b).BillStack.Bills.Count
            })
            .ToList();
    }

    public IReadOnlyList<BillInfo> GetBills(int tableId)
    {
        IBillGiver giver = FindBillGiver(tableId);
        if (giver == null) return [];
        var bills = new List<BillInfo>();
        for (int i = 0; i < giver.BillStack.Bills.Count; i++)
        {
            Bill bill = giver.BillStack.Bills[i];
            bills.Add(new BillInfo { Index = i + 1, Label = bill.LabelCap, Repeat = RepeatLabel(bill) });
        }
        return bills;
    }

    private static string RepeatLabel(Bill bill)
    {
        if (bill is not Bill_Production production) return string.Empty;
        if (production.repeatMode == BillRepeatModeDefOf.Forever) return "forever";
        if (production.repeatMode == BillRepeatModeDefOf.TargetCount) return $"until {production.targetCount}";
        if (production.repeatMode == BillRepeatModeDefOf.RepeatCount) return $"{production.repeatCount}x";
        return production.repeatMode?.label ?? string.Empty;
    }

    /// <summary>
    /// 只列這張台自己的配方：模組包常有好幾個配方產出同一物品、分屬不同工作台，
    /// 對全遊戲配方解析會撞名或挑到這張台做不了的那一個。
    /// </summary>
    public IReadOnlyList<DefInfo> GetTableRecipes(int tableId)
    {
        if (FindBillGiver(tableId) is not Thing table) return [];
        // 屠宰、火化、熔煉等配方沒有單一產物，改用配方名稱，否則這類工作單無法新增。
        return table.def.AllRecipes
            .Where(r => r.workerClass == null || !typeof(Recipe_Surgery).IsAssignableFrom(r.workerClass))
            .Select(r => new DefInfo(r.defName, r.ProducedThingDef?.label ?? r.label))
            .ToList();
    }

    public CommandResult AddBill(int tableId, string recipeDefName, int count)
    {
        IBillGiver giver = FindBillGiver(tableId);
        if (giver == null) return CommandResult.Fail($"no work table has id {tableId}");
        var table = (Thing)giver;
        RecipeDef recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(recipeDefName);
        if (recipe == null) return CommandResult.Fail($"unknown bill recipe {recipeDefName}");
        if (!table.def.AllRecipes.Contains(recipe))
            return CommandResult.Fail($"{table.LabelCap} cannot make {recipe.ProducedThingDef?.label ?? recipe.label}");
        if (!recipe.AvailableOnNow(table))
            return CommandResult.Fail($"{table.LabelCap} cannot make {recipe.ProducedThingDef?.label ?? recipe.label} yet: missing research or facilities");
        if (count < 1) return CommandResult.Fail($"count must be at least 1, not {count}");
        // 用 MakeNewBill 取得正確的子類別（例如 Bill_ProductionWithUft），與遊戲內視窗新增帳單一致。
        if (recipe.MakeNewBill() is not Bill_Production bill)
            return CommandResult.Fail($"{recipe.label} is not a production bill and cannot be added with a count");
        bill.repeatMode = BillRepeatModeDefOf.RepeatCount;
        bill.repeatCount = count;
        giver.BillStack.AddBill(bill);
        return CommandResult.Ok($"Added bill [{giver.BillStack.Bills.Count}] {bill.LabelCap} x{count} on {table.LabelCap}.");
    }

    public CommandResult DeleteBill(int tableId, int index)
    {
        IBillGiver giver = FindBillGiver(tableId);
        if (giver == null) return CommandResult.Fail($"no work table has id {tableId}");
        var table = (Thing)giver;
        if (index < 1 || index > giver.BillStack.Bills.Count)
            return CommandResult.Fail($"bill [{index}] does not exist on {table.LabelCap}. List the bills again to get valid numbers.");
        Bill bill = giver.BillStack.Bills[index - 1];
        string label = bill.LabelCap;
        giver.BillStack.Delete(bill);
        return CommandResult.Ok($"Deleted bill {label} from {table.LabelCap}. Materials already gathered stay where they are.");
    }

    private static IBillGiver FindBillGiver(int id)
    {
        return Map.listerBuildings.allBuildingsColonist
            .Where(b => b is IBillGiver)
            .Cast<IBillGiver>()
            .FirstOrDefault(g => ((Thing)g).thingIDNumber == id);
    }

    public CommandResult SetWorkPriority(int pawnId, string workTypeDefName, int priority)
    {
        Pawn pawn = FindColonist(pawnId);
        if (pawn == null) return CommandResult.Fail(ColonistNotFound);
        if (pawn.workSettings == null) return CommandResult.Fail($"{pawn.LabelShort} has no work settings");
        WorkTypeDef workType = DefDatabase<WorkTypeDef>.GetNamedSilentFail(workTypeDefName);
        if (workType == null) return CommandResult.Fail($"unknown work type {workTypeDefName}");
        if (pawn.WorkTypeIsDisabled(workType)) return CommandResult.Fail($"{pawn.LabelShort} is incapable of {workType.label}");

        string note = string.Empty;
        bool needsManualPriorities = priority != 0 && priority != 3;
        if (needsManualPriorities && !UseWorkPriorities)
        {
            // 1/2/4 只有在手動優先級開啟時才有意義；否則遊戲只認勾選／不勾選。
            SetUseWorkPriorities(true);
            note = "Manual work priorities were turned on for the colony.";
        }
        pawn.workSettings.SetPriority(workType, priority);
        return CommandResult.Ok(note);
    }

    /// <summary>殖民地目前是否使用手動工作優先級；遊戲尚未載入時視為關閉。</summary>
    public bool UseWorkPriorities => Current.Game?.playSettings?.useWorkPriorities ?? false;

    /// <summary>切換手動工作優先級；值沒變就不通知，避免每次復原都重算全體殖民者。</summary>
    public void SetUseWorkPriorities(bool value)
    {
        if (Current.Game?.playSettings == null || Current.Game.playSettings.useWorkPriorities == value) return;
        Current.Game.playSettings.useWorkPriorities = value;
        NotifyUseWorkPrioritiesChanged();
    }

    /// <summary>照原版工作分頁的勾選框：切換手動優先級後，每個玩家 pawn 的工作順序快取都要重建。</summary>
    internal static void NotifyUseWorkPrioritiesChanged()
    {
        foreach (Pawn pawn in PawnsFinder.AllMapsWorldAndTemporary_Alive)
        {
            if (pawn.Faction == Faction.OfPlayer && pawn.workSettings != null) pawn.workSettings.Notify_UseWorkPrioritiesChanged();
        }
    }

    public CommandResult SetTimeAssignment(int pawnId, int hour, string assignmentDefName)
    {
        Pawn pawn = FindColonist(pawnId);
        if (pawn == null) return CommandResult.Fail(ColonistNotFound);
        if (pawn.timetable == null) return CommandResult.Fail($"{pawn.LabelShort} has no schedule");
        TimeAssignmentDef assignment = DefDatabase<TimeAssignmentDef>.GetNamedSilentFail(assignmentDefName);
        if (assignment == null) return CommandResult.Fail($"unknown time assignment {assignmentDefName}");
        if (hour < 0 || hour > 23) return CommandResult.Fail("hour must be 0-23");
        pawn.timetable.SetAssignment(hour, assignment);
        return CommandResult.Ok();
    }

    private static WorkPriorityInfo ToWorkPriority(Pawn pawn, WorkTypeDef w)
    {
        bool disabled = pawn.WorkTypeIsDisabled(w);
        return new WorkPriorityInfo
        {
            WorkTypeDefName = w.defName,
            Label = w.labelShort ?? w.label,
            Priority = disabled ? 0 : pawn.workSettings.GetPriority(w),
            Disabled = disabled
        };
    }
}
