#nullable disable

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
    public FloatMenuInfo GetFloatMenuOptions(int colonistId, MapTarget target)
    {
        Pawn pawn = FindColonist(colonistId);
        if (pawn == null) return new FloatMenuInfo { Error = ColonistNotFound };
        var info = new FloatMenuInfo { PawnName = pawn.LabelShort, Drafted = pawn.Drafted };
        if (!TryResolveTarget(target, out LocalTargetInfo resolved, out string error))
        {
            info.Error = error;
            return info;
        }
        info.TargetLabel = DescribeTarget(resolved);
        List<FloatMenuOption> options = BuildFloatMenu(pawn, resolved, out error);
        if (options == null)
        {
            info.Error = error;
            return info;
        }
        info.Options = options.Select((o, i) => new FloatMenuOptionInfo { Number = i + 1, Label = o.Label.StripTags(), Disabled = o.Disabled }).ToList();
        return info;
    }

    public CommandResult UseFloatMenuOption(int colonistId, MapTarget target, int optionNumber, string label)
    {
        Pawn pawn = FindColonist(colonistId);
        if (pawn == null) return CommandResult.Fail(ColonistNotFound);
        if (!TryResolveTarget(target, out LocalTargetInfo resolved, out string error)) return CommandResult.Fail(error);
        List<FloatMenuOption> options = BuildFloatMenu(pawn, resolved, out error);
        if (options == null || options.Count == 0) return CommandResult.Fail(error);

        FloatMenuOption option = optionNumber >= 1 && optionNumber <= options.Count ? options[optionNumber - 1] : null;
        if (option == null || !string.Equals(option.Label.StripTags(), label, StringComparison.OrdinalIgnoreCase))
            return CommandResult.Fail($"{pawn.LabelShort}'s right-click menu changed; call list_right_click_menu again");
        if (option.Disabled) return CommandResult.Fail($"\"{label}\" is disabled");

        StopAllTargeting();
        var before = new HashSet<Window>(Find.WindowStack.Windows);
        var watch = new MessageWatch();
        try
        {
            option.Chosen(colonistOrdering: true, null);
        }
        catch (Exception ex)
        {
            Log.Warning("RimAgent: right-click option \"" + label + "\" threw: " + ex);
            return CommandResult.Fail($"\"{label}\" failed in the game: {ex.Message}");
        }

        // 選項若接著要再選目標就留著，由 choose_option 回應；回答不了的視窗才收掉並回報。
        SetTargetingState(Find.Targeter.IsTargeting, Find.WorldTargeter.IsTargeting);
        KeepCasterSelected();
        CommandResult windowFailure = CloseUnsupportedWindows(before, label);
        if (windowFailure != null)
        {
            StopAllTargeting();
            return windowFailure;
        }
        return CommandResult.Ok(WithGameMessages(string.Empty, watch));
    }

    /// <summary>
    /// 照原版右鍵（Selector.HandleMapClicks）的路徑：FloatMenuMakerMap.GetOptions 以點擊位置產生選項，
    /// 不需要滑鼠或框選，只要給殖民者與世界座標；排序同 FloatMenu 建構子（優先度高者在前）。
    /// 殖民者不能下令時原版只丟一則訊息，這裡先問 ShouldGenerateFloatMenuForPawn 取得原因。
    /// </summary>
    private static List<FloatMenuOption> BuildFloatMenu(Pawn pawn, LocalTargetInfo target, out string error)
    {
        error = null;
        AcceptanceReport allowed = FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn(pawn);
        if (!allowed.Accepted)
        {
            error = allowed.Reason.NullOrEmpty() ? $"{pawn.LabelShort} cannot be given orders right now" : allowed.Reason.StripTags();
            return [];
        }
        if (!pawn.CanTakeOrder)
        {
            error = $"{pawn.LabelShort} cannot take orders";
            return [];
        }
        // 點擊位置：pawn 用繪製位置（原版先以 DrawPos 距離找 pawn），其他物件用所在格的中心。
        UnityEngine.Vector3 clickPosition;
        if (target.HasThing)
        {
            clickPosition = target.Thing is Pawn targetPawn ? targetPawn.DrawPos : target.Thing.Position.ToVector3Shifted();
        }
        else
        {
            clickPosition = target.Cell.ToVector3Shifted();
        }
        try
        {
            // GetOptions 會改動傳入的清單，所以給一份新的。
            List<FloatMenuOption> options = FloatMenuMakerMap.GetOptions([pawn], clickPosition, out FloatMenuContext _) ?? [];
            // 原版只在多選時問這一份，有些東西（古樞紐核心）的選項只放在這裡，只選一人就看不到。
            var extras = clickPosition.ToIntVec3().GetThingList(pawn.Map).SelectMany(t => t.GetMultiSelectFloatMenuOptions([pawn]));
            extras.Where(e => options.All(o => o.Label != e.Label)).ToList().ForEach(e => options.Add(e));
            return [.. (options ?? [])
                .OrderByDescending(o => o.Priority).ThenByDescending(o => o.orderInPriority)];
        }
        catch (Exception ex)
        {
            Log.Warning("RimAgent: building the right-click menu failed: " + ex);
            error = "the game could not build the right-click menu: " + ex.Message;
            return [];
        }
    }

    /// <summary>選取時會畫出來的指令按鈕；純狀態顯示的 gizmo（護盾條之類）與開發者模式按鈕不算。</summary>
    private static List<Command> VisibleCommands(Thing thing)
    {
        // 安裝鈕的 Visible 要有選取才成立；這裡沒有選取，一律列出，按下時才選。
        return [.. thing.GetGizmos().OfType<Command>().Where(c => (c is Designator_Install || c.Visible) && !IsDevLabel(CommandLabel(c)))];
    }

    private static string CommandLabel(Command command)
    {
        // 原版的安裝鈕標籤要看目前選取，這裡沒有選取，固定一個才不會按前按後不同。
        if (command is Designator_Install) return "Install or reinstall";
        return command.LabelCap ?? string.Empty;
    }

    private static bool IsDevLabel(string label)
    {
        return label.StartsWith("DEV:", StringComparison.OrdinalIgnoreCase) || label.StartsWith("Debug:", StringComparison.OrdinalIgnoreCase);
    }
}
