using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RimAgent.Game;

internal sealed partial class RimWorldGameWorld
{
    #pragma warning disable S1168 // null 代表未在地圖上找到該實體
    public string? GetInspectText(int thingId)
    {
        Thing? thing = FindThing(thingId);
        if (thing == null || thing.Fogged()) return string.Empty;
        return LimitChoiceText(thing.GetInspectString()?.StripTags().Trim());
    }

    public IReadOnlyList<GizmoInfo>? GetGizmos(int thingId)
    {
        Thing? thing = FindThing(thingId);
        if (thing == null || thing.Fogged()) return null;
        List<Command> commands = VisibleCommands(thing);
        var result = new List<GizmoInfo>(commands.Count);
        for (int i = 0; i < commands.Count; i++)
        {
            Command command = commands[i];
            string kind = DetermineGizmoKind(command, out string targetHint);
            result.Add(new GizmoInfo
            {
                Number = i + 1,
                Label = CommandLabel(command),
                Description = LimitChoiceText(command.Desc),
                Kind = kind,
                TargetHint = targetHint,
                On = (command as Command_Toggle)?.isActive?.Invoke() ?? false,
                Disabled = command.Disabled,
                DisabledReason = command.disabledReason ?? string.Empty
            });
        }
        return result;
    }

    #pragma warning restore S1168

    /// <summary>
    /// 按鈕種類。Command_Action 可能在按下後才開始選目標（互動物件、虛空巨石柱、使用物品都是），
    /// 光看型別分不出來，一律算 action，按下去才知道（見 UseGizmo）。
    /// </summary>
    private static string DetermineGizmoKind(Command command, out string targetHint)
    {
        targetHint = string.Empty;
        if (command is Designator_Install)
        {
            targetHint = "a cell";
            return GizmoInfo.TargetKind;
        }
        if (command is Command_Toggle) return GizmoInfo.ToggleKind;
        if (command is Command_Action or Command_LoadToTransporter) return GizmoInfo.ActionKind;
        if (command is Command_Ability abilityCommand)
        {
            AbilityDef? def = abilityCommand.Ability?.def;
            // 世界地圖選目標（傳送到別處）沒有地圖格子可指，不支援。
            if (def == null || def.targetWorldCell) return GizmoInfo.UnsupportedKind;
            // 不需要目標的能力按下去就生效，和一般 action 一樣。
            if (!def.targetRequired) return GizmoInfo.ActionKind;
            return TargetKindOf(abilityCommand.Ability?.verb, out targetHint);
        }
        if (command is Command_VerbTarget verbCommand) return TargetKindOf(verbCommand.verb, out targetHint);
        if (command is Command_Target targetCommand) return TargetKindOf(targetCommand.targetingParams, out targetHint);
        return GizmoInfo.UnsupportedKind;
    }

    private static string TargetKindOf(Verb? source, out string targetHint)
    {
        targetHint = string.Empty;
        // 不可選目標的 verb（對自己施放）按下去就直接執行。
        if (source == null) return GizmoInfo.UnsupportedKind;
        if (!source.Targetable) return GizmoInfo.ActionKind;
        return TargetKindOf(source.targetParams, out targetHint);
    }

    private static string TargetKindOf(TargetingParameters? parameters, out string targetHint)
    {
        targetHint = parameters == null ? string.Empty : DescribeTargets(parameters);
        return parameters == null ? GizmoInfo.UnsupportedKind : GizmoInfo.TargetKind;
    }

    private static string DescribeTargets(TargetingParameters p)
    {
        var kinds = new List<string>();
        if (p.canTargetLocations) kinds.Add("a cell");
        if (p.canTargetPawns) kinds.Add(DescribePawnTargets(p));
        if (p.canTargetBuildings) kinds.Add("a building");
        if (p.canTargetItems) kinds.Add("an item");
        if (p.canTargetPlants) kinds.Add("a plant");
        if (p.canTargetFires) kinds.Add("a fire");
        if (p.canTargetCorpses || p.onlyTargetCorpses) kinds.Add("a corpse");
        return kinds.Count == 0 ? "see the game's targeting rules" : string.Join(", ", kinds);
    }

    private static string DescribePawnTargets(TargetingParameters p)
    {
        if (p.onlyTargetColonists) return "a colonist";
        if (p.onlyTargetPrisonersOfColony) return "a prisoner of the colony";
        if (p.onlyTargetColonistsOrPrisoners) return "a colonist or prisoner";
        if (p.onlyTargetColonistsOrPrisonersOrSlaves || p.onlyTargetColonistsOrPrisonersOrSlavesAllowMinorMentalBreaks) return "a colonist, prisoner or slave";
        if (p.onlyTargetControlledPawns) return "a controlled pawn";
        return "a pawn";
    }

    // 選目標器（Targeter）的目標回呼與目標規則都是私有欄位：玩家按下按鈕後它保存「點到目標時做什麼」。
    // 沒有滑鼠可點，就讀出這些欄位照同一套規則自己「點」；找不到欄位時回報不支援，避免版本差異中斷 agent。
    #pragma warning disable S3011 // RimWorld 私有欄位存取為 Mod 運作必需
    private static readonly FieldInfo? TargeterActionField =
        typeof(Targeter).GetField("action", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? TargeterParamsField =
        typeof(Targeter).GetField("targetParams", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? TargeterValidatorField =
        typeof(Targeter).GetField("targetValidator", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? LiveMessagesField =
        typeof(Messages).GetField("liveMessages", BindingFlags.Static | BindingFlags.NonPublic);

    #pragma warning restore S3011
    private const int MaxGameMessages = 3;

    private const int MaxGameMessageChars = 200;

    /// <summary>
    /// 記下開始時螢幕上的遊戲訊息，之後取出新增的：原版把「不能這樣做的原因」都以訊息（Messages.Message）顯示，
    /// 沒有別的管道，所以拿它當原因回報給模型。同樣文字的訊息還在畫面上時遊戲不新增，只重置計時
    /// （startingFrame 會變），所以新增或被重置的都算。
    /// </summary>
    private sealed class MessageWatch
    {
        private readonly Dictionary<Message, int> _before = Live().ToDictionary(m => m, m => m.startingFrame);

        private static List<Message> Live()
        {
            return (LiveMessagesField?.GetValue(null) as List<Message>)?.ToList() ?? [];
        }

        public List<string> NewTexts()
        {
            return [.. Live().Where(m => !string.IsNullOrEmpty(m.text) && (!_before.TryGetValue(m, out int frame) || frame != m.startingFrame))
                .Select(m => m.text.StripTags().Flatten().Trim())
                .Where(t => t.Length > 0)
                .Select(t => t.Length <= MaxGameMessageChars ? t : t[..MaxGameMessageChars] + "…")
                .Distinct(StringComparer.Ordinal).Take(MaxGameMessages)];
        }
    }

    private static string WithGameMessages(string text, MessageWatch watch)
    {
        List<string> messages = watch.NewTexts();
        if (messages.Count == 0) return text;
        string clause = "game message: " + string.Join(" / ", messages);
        return string.IsNullOrEmpty(text) ? clause : text + "; " + clause;
    }

    private static string DescribeTarget(LocalTargetInfo target)
    {
        return target.HasThing ? string.Create(CultureInfo.InvariantCulture, $"{target.Thing.LabelShort} [{target.Thing.thingIDNumber}]") : string.Create(CultureInfo.InvariantCulture, $"cell ({target.Cell.x},{target.Cell.z})");
    }

    private static bool TryResolveTarget(MapTarget target, out LocalTargetInfo resolved, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        resolved = LocalTargetInfo.Invalid;
        error = null;
        if (target.IsCell)
        {
            var cell = new IntVec3(target.X, 0, target.Z);
            if (!cell.InBounds(Map))
            {
                error = string.Create(CultureInfo.InvariantCulture, $"cell ({target.X},{target.Z}) is outside the map");
                return false;
            }
            resolved = new LocalTargetInfo(cell);
            return true;
        }
        Thing? thing = FindThing(target.ThingId);
        if (thing == null || thing.Fogged())
        {
            error = string.Create(CultureInfo.InvariantCulture, $"target [{target.ThingId}] not found on the map");
            return false;
        }
        resolved = new LocalTargetInfo(thing);
        return true;
    }

    public CommandResult UseGizmo(int thingId, int gizmoNumber, string label, MapTarget? target)
    {
        Thing? thing = FindThing(thingId);
        if (thing == null || thing.Fogged()) return CommandResult.Fail("thing not found on the map");
        List<Command> commands = VisibleCommands(thing);
        CommandResult? validation = ValidateGizmoCommand(thing, gizmoNumber, label, commands, out Command? command);
        if (validation != null || command == null) return validation ?? CommandResult.Fail("command missing");

        var localTarget = LocalTargetInfo.Invalid;
        if (target != null && !TryResolveTarget(target, out localTarget, out string? targetError)) return CommandResult.Fail(targetError);
        if (command is Designator_Install install) return UseInstallButton(thing, install, label, localTarget);

        // 先收掉殘留的選目標狀態，之後 IsTargeting 才代表「這顆按鈕開始選目標」。
        StopAllTargeting();
        var before = new HashSet<Window>(Find.WindowStack.Windows);
        var watch = new MessageWatch();
        try
        {
            command.ProcessInput(ev: null);
        }
        catch (Exception ex)
        {
            StopAllTargeting();
            CloseUnsupportedWindows(before, label);
            return CommandResult.Fail("the button failed: " + ex.Message);
        }
        CommandResult? targeting = FinishTargeting(label, target == null ? (LocalTargetInfo?)null : localTarget, watch, out bool askedForTarget);
        CommandResult? windowFailure = CloseUnsupportedWindows(before, label);
        if (targeting != null && !targeting.Success) return targeting;
        if (windowFailure != null) return windowFailure;

        if (askedForTarget && targeting != null) return targeting;
        string suffix = GetToggleCommandResultSuffix(command);
        if (target != null) suffix = "the button did not ask for a target, so " + DescribeTarget(localTarget) + " was not used";
        return CommandResult.Ok(WithGameMessages(suffix, watch));
    }

    /// <summary>
    /// 按鈕按下後若開始選目標，就把給的目標「點」下去（照原版 Targeter.ProcessInputEvents 的順序驗證），
    /// 沒給目標就取消並回報要什麼；無論如何都收掉選目標狀態。按鈕沒有開始選目標時回傳 null。
    /// </summary>
    private static CommandResult? FinishTargeting(string label, LocalTargetInfo? target, MessageWatch watch, out bool askedForTarget)
    {
        askedForTarget = false;
        if (Find.WorldTargeter.IsTargeting)
        {
            // 留著等 choose_option 給世界地圖的格子。
            askedForTarget = true;
            _agentWorldTarget = true;
            return CommandResult.Ok(WithGameMessages($"\"{label}\" waits for a destination", watch));
        }
        Targeter targeter = Find.Targeter;
        if (!targeter.IsTargeting) return null;
        askedForTarget = true;
        try
        {
            if (!target.HasValue)
            {
                TargetingParameters? parameters = ActiveTargetParams(targeter);
                string hint = parameters == null ? "a thing or a cell" : DescribeTargets(parameters);
                return CommandResult.Fail($"\"{label}\" asks for a target ({hint}); pass targetId (a thing id) or targetX and targetZ (a cell). Nothing was done");
            }
            return ApplyTarget(targeter, label, target.Value, watch);
        }
        finally
        {
            // 失敗或成功都要收掉，否則玩家的畫面會一直停在選目標狀態。
            targeter.StopTargeting();
        }
    }

    private static TargetingParameters? ActiveTargetParams(Targeter targeter)
    {
        return targeter.targetingSource?.targetParams ?? TargeterParamsField?.GetValue(targeter) as TargetingParameters;
    }

    private static CommandResult ApplyTarget(Targeter targeter, string label, LocalTargetInfo target, MessageWatch watch)
    {
        ITargetingSource? source = targeter.targetingSource;
        TargetingParameters? parameters = ActiveTargetParams(targeter);
        if (parameters == null) return CommandResult.Fail($"\"{label}\" started a kind of targeting that cannot be driven without the mouse");
        string described = DescribeTarget(target);

        string? untargetable = UntargetableReason(parameters, source, target);
        if (untargetable != null) return CommandResult.Fail($"{described} cannot be targeted by \"{label}\": {untargetable}");

        if (source == null) return HandleNoSource(targeter, label, described, target, watch);
        if (source.DestinationSelector != null) return CommandResult.Fail($"\"{label}\" needs a second target (a destination) after {described}, which is not supported yet");
        if (target.HasThing && target.Thing == source.Caster && !parameters.canTargetSelf) return CommandResult.Fail($"\"{label}\" cannot target its own source");
        if (!source.ValidateTarget(target, showMessages: false)) return HandleTargetRejected(targeter, label, described, target);
        if (target.Thing is not Pawn && !source.IsMeleeAttack && !source.CanHitTarget(target)) return CommandResult.Fail($"{described} cannot be hit by \"{label}\"" + RangeNote(source, target));

        return ExecuteTargetOrder(targeter, target, label, described, watch);
    }

    private static CommandResult HandleNoSource(Targeter targeter, string label, string described, LocalTargetInfo target, MessageWatch watch)
    {
        if (TargeterActionField?.GetValue(targeter) is not Action<LocalTargetInfo> action) return CommandResult.Fail($"\"{label}\" started a kind of targeting that cannot be driven without the mouse");
        if (TargeterValidatorField?.GetValue(targeter) is Func<LocalTargetInfo, bool> validator && !validator(target)) return CommandResult.Fail($"{described} was rejected by the game's target check for \"{label}\"");
        action(target);
        return CommandResult.Ok(WithGameMessages("targeted " + described, watch));
    }

    private static CommandResult HandleTargetRejected(Targeter targeter, string label, string described, LocalTargetInfo target)
    {
        ITargetingSource source = targeter.targetingSource;
        var reasonWatch = new MessageWatch();
        source.ValidateTarget(target, showMessages: true);
        string? reason = reasonWatch.NewTexts().FirstOrDefault();
        return CommandResult.Fail($"the game rejected {described} for \"{label}\"" + (reason == null ? " (it gave no reason)" : ": " + reason));
    }

    private static CommandResult ExecuteTargetOrder(Targeter targeter, LocalTargetInfo target, string label, string described, MessageWatch watch)
    {
        ITargetingSource source = targeter.targetingSource;
        if (source.CasterIsPawn)
        {
            source.OrderForceTarget(target);
        }
        else if (source.Caster is Building_Turret turret && turret.Map == Find.CurrentMap)
        {
            turret.OrderAttack(target);
            if (turret.ForcedTarget != target)
                return CommandResult.Fail(WithGameMessages($"{turret.LabelShort} did not take {described} as its target", watch));
        }
        else
        {
            return CommandResult.Fail($"the game's targeter would not issue an order for \"{label}\"");
        }
        return CommandResult.Ok(WithGameMessages("targeted " + described, watch));
    }

    private static string? UntargetableReason(TargetingParameters parameters, ITargetingSource? source, LocalTargetInfo target)
    {
        string accepts = $"it accepts {DescribeTargets(parameters)}";
        if (target.HasThing)
        {
            Thing thing = target.Thing;
            if (!parameters.CanTarget(thing, source)) return accepts;
            // GenUI.ThingsUnderMouse / TargetsAt 對隱形或躲起來的 pawn 的過濾。
            if (thing is Pawn pawn)
            {
                if (pawn.IsHiddenFromPlayer()) return "that pawn is hidden";
                Thing? caster = source?.Caster;
                if (pawn.IsPsychologicallyInvisible() && caster != null && caster.Faction != pawn.Faction) return "that pawn is not visible";
            }
            return null;
        }
        IntVec3 cell = target.Cell;
        if (!cell.InBounds(Map, parameters.mapBoundsContractedBy)) return "that cell is too close to the map edge";
        return parameters.CanTarget(new TargetInfo(cell, Map), source) ? null : accepts;
    }

    private static string RangeNote(ITargetingSource source, LocalTargetInfo target)
    {
        Verb verb = source.GetVerb;
        if (verb == null) return string.Empty;
        float distance = (target.Cell - source.Caster.Position).LengthHorizontal;
        float min = verb.verbProps.EffectiveMinRange(target, source.Caster);
        return string.Create(CultureInfo.InvariantCulture, $" (distance {distance:0.#}, range {min:0.#} to {verb.EffectiveRange:0.#}, line of sight may also be blocked)");
    }

    private static void StopAllTargeting()
    {
        if (Find.Targeter.IsTargeting) Find.Targeter.StopTargeting();
        if (Find.WorldTargeter.IsTargeting) Find.WorldTargeter.StopTargeting();
        _agentWorldTarget = false;
        _agentMapTarget = false;
    }

    private static void SetTargetingState(bool mapTarget, bool worldTarget)
    {
        _agentMapTarget = mapTarget;
        _agentWorldTarget = worldTarget;
    }

    private static CommandResult? ValidateGizmoCommand(Thing thing, int gizmoNumber, string label, List<Command> commands, out Command? command)
    {
        command = gizmoNumber >= 1 && gizmoNumber <= commands.Count ? commands[gizmoNumber - 1] : null;
        if (command == null || !string.Equals(CommandLabel(command), label, StringComparison.OrdinalIgnoreCase))
            return CommandResult.Fail($"{thing.LabelShort}'s button list changed; call list_gizmos again");
        if (command.Disabled)
            return CommandResult.Fail($"\"{label}\" is disabled" + (command.disabledReason.NullOrEmpty() ? string.Empty : ": " + command.disabledReason));
        if (DetermineGizmoKind(command, out _) == GizmoInfo.UnsupportedKind)
            return CommandResult.Fail($"\"{label}\" needs a menu choice or another kind of input that use_gizmo cannot give");
        return null;
    }

    private static CommandResult? CloseUnsupportedWindows(HashSet<Window> beforeWindows, string label)
    {
        AdoptOpenedWindows(beforeWindows);
        List<Window> opened = [.. Find.WindowStack.Windows.Where(w => !beforeWindows.Contains(w))];
        List<Window> unsupported = [.. opened.Where(w => !IsAnswerableWindow(w) && !RunsOnItsOwn(w))];
        foreach (Window window in unsupported) window.Close(doCloseSound: false);
        if (unsupported.Count > 0)
        {
            return CommandResult.Fail($"\"{label}\" opened a {(unsupported[0] is FloatMenu ? "menu" : "window")} to pick from, which is not supported yet; it was closed");
        }
        return null;
    }

    private static string GetToggleCommandResultSuffix(Command command)
    {
        if (command is Command_Toggle toggle)
        {
            return toggle.isActive() ? "now on" : "now off";
        }
        return string.Empty;
    }
}
