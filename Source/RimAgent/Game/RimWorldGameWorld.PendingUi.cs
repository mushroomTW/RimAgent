using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimAgent.Tools;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimAgent.Game;

/// <summary>
/// 把原版要玩家用滑鼠回應的介面（世界地圖選點、選目標器、右鍵選單、儀式、裝載、新殖民地、飛船著陸）
/// 通通當成 pending choice，讓模型只用 list_pending_choices 與 choose_option 就能回應，不必為每種介面加工具。
/// 這些介面的狀態多是私有欄位，缺欄位時回報失敗，不猜測。
/// </summary>
internal sealed partial class RimWorldGameWorld
{
    private const string TileChoiceId = DialogChoicePrefix + "tile";
    private const string WorldTargetChoiceId = DialogChoicePrefix + "world-target";
    private const string MapTargetChoiceId = DialogChoicePrefix + "map-target";
    private const string LandingChoiceId = DialogChoicePrefix + "landing";
    private const string WindowChoicePrefix = DialogChoicePrefix + "window-";
    private const string CancelLabel = "Cancel";
    private const string TileInputHint = "a tile id as printed by list_world_objects or get_world_tile, for example 123 (surface) or Orbit:45";

    private static readonly FieldInfo TilePickerValidator = AccessTools.Field(typeof(TilePicker), "validator");
    private static readonly FieldInfo TilePickerChosen = AccessTools.Field(typeof(TilePicker), "tileChosen");
    private static readonly FieldInfo TilePickerTitle = AccessTools.Field(typeof(TilePicker), "title");
    private static readonly FieldInfo TilePickerAllowEscape = AccessTools.Field(typeof(TilePicker), "allowEscape");
    private static readonly FieldInfo TargeterCaster = AccessTools.Field(typeof(Targeter), "caster");
    private static readonly MethodInfo TilePickerStop = AccessTools.Method(typeof(TilePicker), "StopTargetingInt");
    private static readonly FieldInfo WorldTargeterAction = AccessTools.Field(typeof(WorldTargeter), "action");
    private static readonly FieldInfo WorldTargeterCanSelect = AccessTools.Field(typeof(WorldTargeter), "canSelectTarget");
    private static readonly FieldInfo FloatMenuOptions = AccessTools.Field(typeof(FloatMenu), "options");
    private static readonly FieldInfo LandingMarkerField = AccessTools.Field(typeof(WorldComponent_GravshipController), "landingMarker");
    private static readonly FieldInfo ConfigureIdeoNext = AccessTools.Field(typeof(Dialog_ConfigureIdeo), "nextAction");
    private static readonly FieldInfo LoadTransferables = AccessTools.Field(typeof(Dialog_LoadTransporters), "transferables");
    private static readonly MethodInfo LoadTryAccept = AccessTools.Method(typeof(Dialog_LoadTransporters), "TryAccept");

    /// <summary>由工具開出來的選單、儀式與裝載畫面；玩家自己開的與自己的選目標狀態不算待回應。</summary>
    private static readonly HashSet<Window> AgentWindows = [];
    private static bool _agentWorldTarget;
    private static bool _agentMapTarget;
    /// <summary>重力飛船的選點要等發射儀式跑完才出現，不在工具呼叫當下，所以從開始儀式起就算在等。</summary>
    private static bool _agentExpectsTile;

    private static bool IsAnswerableWindow(Window window)
    {
        return window is Dialog_NodeTree or Dialog_MessageBox or FloatMenu or Dialog_BeginLordJob
            or Dialog_ConfigureIdeo or Dialog_ChooseThingsForNewColony or Dialog_LoadTransporters;
    }

    /// <summary>開場動畫、製作名單、主分頁自己會結束或無害，不是要玩家回答的視窗，不能關掉。</summary>
    private static bool RunsOnItsOwn(Window window)
    {
        return window is MainTabWindow || window.GetType().Name.StartsWith("Screen_", StringComparison.Ordinal);
    }

    private static void AdoptOpenedWindows(HashSet<Window> before)
    {
        AgentWindows.RemoveWhere(w => !Find.WindowStack.Windows.Contains(w));
        foreach (Window window in Find.WindowStack.Windows.Where(w => !before.Contains(w) && w is FloatMenu or Dialog_BeginLordJob or Dialog_LoadTransporters))
        {
            // 預設滑鼠一離開選單就消失；模型沒有滑鼠。
            if (window is FloatMenu menu) menu.vanishIfMouseDistant = false;
            AgentWindows.Add(window);
        }
        if (Find.TilePicker.Active) _agentExpectsTile = true;
    }

    /// <summary>原版的選目標要施放者保持選取，否則下一幀就自己取消；玩家按右鍵時殖民者本來就選著。</summary>
    private static void KeepCasterSelected()
    {
        Targeter targeter = Find.Targeter;
        if (!targeter.IsTargeting) return;
        Thing caster = TargeterCaster?.GetValue(targeter) as Thing ?? targeter.targetingSource?.Caster;
        if (caster is not { Spawned: true } || Find.Selector.IsSelected(caster)) return;
        Find.Selector.ClearSelection();
        Find.Selector.Select(caster, false, false);
    }

    private static IEnumerable<PendingChoiceInfo> PendingUiChoices()
    {
        // 目標選完或被取消後旗標就失效，不能算到玩家之後自己開的選目標上。
        _agentWorldTarget &= Find.WorldTargeter.IsTargeting;
        _agentMapTarget &= Find.Targeter.IsTargeting;
        // 不能取消的選點（古樞紐搬家）只可能是任務流程，不是玩家自己開的。
        if (Find.TilePicker.Active && (_agentExpectsTile || TilePickerAllowEscape?.GetValue(Find.TilePicker) is false))
        {
            yield return TileChoice(TileChoiceId, TilePickerTitle?.GetValue(Find.TilePicker) as string ?? "Choose a tile on the world map");
        }
        if (_agentWorldTarget) yield return TileChoice(WorldTargetChoiceId, "Choose a destination on the world map");
        if (_agentMapTarget)
        {
            yield return new PendingChoiceInfo
            {
                Id = MapTargetChoiceId,
                Title = "Choose a target on the map",
                Text = DescribeActiveTarget(),
                InputHint = "#thing id or x,z, for example #1234 or 40,62",
                Options = Buttons("Use the target given as input", CancelLabel)
            };
        }
        PendingChoiceInfo landing = LandingChoice();
        if (landing != null) yield return landing;
        foreach (Window window in Find.WindowStack.Windows)
        {
            PendingChoiceInfo choice = window switch
            {
                FloatMenu menu when AgentWindows.Contains(menu) => MenuChoice(menu),
                Dialog_BeginLordJob lordJob when AgentWindows.Contains(lordJob) => LordJobChoice(lordJob),
                Dialog_ConfigureIdeo ideo => new PendingChoiceInfo
                {
                    Id = WindowChoicePrefix + ideo.ID,
                    Title = "Ideoligion of the new colony",
                    Options = Buttons("Keep the current ideoligion")
                },
                Dialog_ChooseThingsForNewColony colony => NewColonyChoice(colony),
                Dialog_LoadTransporters load when AgentWindows.Contains(load) => LoadChoice(load),
                _ => null
            };
            if (choice != null) yield return choice;
        }
    }

    private static string DescribeActiveTarget()
    {
        TargetingParameters parameters = ActiveTargetParams(Find.Targeter);
        return parameters == null ? string.Empty : "Accepts: " + DescribeTargets(parameters);
    }

    private static IReadOnlyList<ChoiceOptionInfo> Buttons(params string[] labels)
    {
        return labels.Select((label, i) => new ChoiceOptionInfo { Number = i + 1, Text = label }).ToList();
    }

    private static PendingChoiceInfo TileChoice(string id, string title)
    {
        return new PendingChoiceInfo
        {
            Id = id,
            Title = title,
            InputHint = TileInputHint,
            Options = Buttons("Use the tile given as input", CancelLabel)
        };
    }

    private static PendingChoiceInfo MenuChoice(FloatMenu menu)
    {
        if (FloatMenuOptions?.GetValue(menu) is not List<FloatMenuOption> options) return null;
        return new PendingChoiceInfo
        {
            Id = WindowChoicePrefix + menu.ID,
            Title = "Menu",
            Options = options.Select((o, i) => new ChoiceOptionInfo { Number = i + 1, Text = o.Label.StripTags(), Disabled = o.Disabled }).ToList()
        };
    }

    private static PendingChoiceInfo LordJobChoice(Dialog_BeginLordJob dialog)
    {
        return new PendingChoiceInfo
        {
            Id = WindowChoicePrefix + dialog.ID,
            Title = dialog.HeaderLabel.Resolve().StripTags(),
            Text = LimitChoiceText(dialog.DescriptionLabel.Resolve().StripTags()),
            Options =
            [
                new ChoiceOptionInfo { Number = 1, Text = "Begin with the default participants", Disabled = !dialog.CanBegin, DisabledReason = LordJobBlock(dialog) },
                new ChoiceOptionInfo { Number = 2, Text = CancelLabel }
            ]
        };
    }

    /// <summary>缺人或缺條件的原因，平常只畫在視窗上；原版也靠它把開始鈕變灰。</summary>
    private static string LordJobBlock(Dialog_BeginLordJob dialog)
    {
        return (AccessTools.Property(typeof(Dialog_BeginLordJob), "WarningText")?.GetValue(dialog) as string)?.StripTags() ?? string.Empty;
    }

    private static List<Thing> NewColonyList(Dialog_ChooseThingsForNewColony dialog, string field)
    {
        return AccessTools.Field(typeof(Dialog_ChooseThingsForNewColony), field)?.GetValue(dialog) as List<Thing> ?? [];
    }

    /// <summary>序號依人、動物、聖物、物品排列，與回答時解析的順序相同。</summary>
    private static List<Thing> NewColonyCandidates(Dialog_ChooseThingsForNewColony dialog)
    {
        return [.. NewColonyList(dialog, "colonists"), .. NewColonyList(dialog, "animals"), .. NewColonyList(dialog, "relics"), .. NewColonyList(dialog, "items")];
    }

    private static PendingChoiceInfo NewColonyChoice(Dialog_ChooseThingsForNewColony dialog)
    {
        return new PendingChoiceInfo
        {
            Id = WindowChoicePrefix + dialog.ID,
            Title = "Choose what comes along to the new colony",
            Text = "At most about 5 people, 5 animals and 7 item stacks; a refused selection reports why.\n" + NumberedThings(NewColonyCandidates(dialog).Select(t => t.LabelCap)),
            InputHint = "the numbers to bring, for example 1,2,9",
            Options = Buttons("Accept the selection given as input", CancelLabel)
        };
    }

    private static string NumberedThings(IEnumerable<string> labels)
    {
        return string.Join("\n", labels.Select((label, i) => $"{i + 1}) {label.StripTags()}"));
    }

    private static PendingChoiceInfo LoadChoice(Dialog_LoadTransporters dialog)
    {
        if (LoadTransferables?.GetValue(dialog) is not List<TransferableOneWay> transferables) return null;
        return new PendingChoiceInfo
        {
            Id = WindowChoicePrefix + dialog.ID,
            Title = "Choose what to load",
            Text = NumberedThings(transferables.Select(t => $"{t.LabelCap} x{t.MaxCount}")),
            InputHint = "number=count pairs, for example 1=1,4=50; a number alone loads all of it",
            Options = Buttons("Load the selection given as input", CancelLabel)
        };
    }

    private static PendingChoiceInfo LandingChoice()
    {
        WorldComponent_GravshipController controller = Find.GravshipController;
        if (controller == null || !controller.LandingAreaConfirmationInProgress) return null;
        var marker = LandingMarkerField?.GetValue(controller) as GravshipLandingMarker;
        IntVec3 size = controller.landingMap?.Size ?? IntVec3.Zero;
        return new PendingChoiceInfo
        {
            Id = LandingChoiceId,
            Title = "Gravship landing",
            Text = (marker is { Spawned: true } ? $"The marker is at ({marker.Position.x},{marker.Position.z}). " : "The marker is not placed yet. ")
                   + $"Landing map size {size.x}x{size.z}.",
            InputHint = "x,z for option 2",
            Options =
            [
                new ChoiceOptionInfo { Number = 1, Text = "Land at the marker", Disabled = marker is not { Spawned: true }, DisabledReason = "place the marker first" },
                new ChoiceOptionInfo { Number = 2, Text = "Move the marker to the cell given as input" }
            ]
        };
    }

    public CommandResult ChooseOption(string choiceId, int option, string input = null)
    {
        string id = choiceId?.Trim() ?? string.Empty;
        var before = new HashSet<Window>(Find.WindowStack.Windows);
        bool wasMapTarget = Find.Targeter.IsTargeting;
        bool wasWorldTarget = Find.WorldTargeter.IsTargeting;
        CommandResult result = AnswerPendingUi(id, option, input) ?? AnswerDialogOrLetter(id, option);
        // 回答可能接著開下一步：新的選目標與選單交回 choose_option，回答不了的視窗收掉並回報。
        _agentMapTarget |= !wasMapTarget && Find.Targeter.IsTargeting;
        _agentWorldTarget |= !wasWorldTarget && Find.WorldTargeter.IsTargeting;
        if (_agentMapTarget) KeepCasterSelected();
        CommandResult windowFailure = CloseUnsupportedWindows(before, "the answer");
        return result.Success && windowFailure != null ? windowFailure : result;
    }

    /// <summary>不是這裡管的 id 回傳 null，交回一般的對話框與信件流程。</summary>
    private static CommandResult AnswerPendingUi(string id, int option, string input)
    {
        var watch = new MessageWatch();
        if (string.Equals(id, TileChoiceId, StringComparison.OrdinalIgnoreCase)) return AnswerTilePicker(option, input, watch);
        if (string.Equals(id, WorldTargetChoiceId, StringComparison.OrdinalIgnoreCase)) return AnswerWorldTarget(option, input, watch);
        if (string.Equals(id, MapTargetChoiceId, StringComparison.OrdinalIgnoreCase)) return AnswerMapTarget(option, input, watch);
        if (string.Equals(id, LandingChoiceId, StringComparison.OrdinalIgnoreCase)) return AnswerLanding(option, input, watch);
        if (!TryParseChoiceId(id, WindowChoicePrefix, out int windowId)) return null;
        return Find.WindowStack.Windows.FirstOrDefault(w => w.ID == windowId) switch
        {
            FloatMenu menu => AnswerMenu(menu, option, watch),
            Dialog_BeginLordJob lordJob => AnswerLordJob(lordJob, option, watch),
            Dialog_ConfigureIdeo ideo => AnswerConfigureIdeo(ideo),
            Dialog_ChooseThingsForNewColony colony => AnswerNewColony(colony, option, input, watch),
            Dialog_LoadTransporters load => AnswerLoad(load, option, input, watch),
            _ => CommandResult.Fail(ChoiceNotFound)
        };
    }

    private static bool TryParseTile(string input, out PlanetTile tile)
    {
        tile = PlanetTile.Invalid;
        string text = input?.Trim() ?? string.Empty;
        PlanetLayer layer = Find.WorldGrid.Surface;
        int colon = text.IndexOf(':');
        if (colon >= 0)
        {
            string name = text.Substring(0, colon).Trim();
            layer = Find.WorldGrid.PlanetLayers.Values.FirstOrDefault(l => string.Equals(l.Def.defName, name, StringComparison.OrdinalIgnoreCase));
            text = text.Substring(colon + 1);
        }
        if (layer == null || !int.TryParse(text.Trim(), out int id)) return false;
        tile = new PlanetTile(id, layer);
        return TileInWorld(tile);
    }

    private static CommandResult Refused(string what, MessageWatch watch)
    {
        return CommandResult.Fail(WithGameMessages(what, watch));
    }

    private static CommandResult AnswerTilePicker(int option, string input, MessageWatch watch)
    {
        if (!Find.TilePicker.Active) return CommandResult.Fail(ChoiceNotFound);
        if (option == 2)
        {
            _agentExpectsTile = false;
            Find.TilePicker.StopTargeting();
            return CommandResult.Ok(CancelLabel);
        }
        if (TilePickerValidator?.GetValue(Find.TilePicker) is not Func<PlanetTile, bool> validator
            || TilePickerChosen?.GetValue(Find.TilePicker) is not Action<PlanetTile> chosen
            || TilePickerStop == null) return CommandResult.Fail("this game version does not expose the tile picker");
        if (!TryParseTile(input, out PlanetTile tile)) return CommandResult.Fail("give " + TileInputHint);
        if (!validator(tile)) return Refused($"tile {input} was refused", watch);
        _agentExpectsTile = false;
        TilePickerStop.Invoke(Find.TilePicker, null);
        chosen(tile);
        return CommandResult.Ok(WithGameMessages("tile " + input.Trim(), watch));
    }

    private static CommandResult AnswerWorldTarget(int option, string input, MessageWatch watch)
    {
        WorldTargeter targeter = Find.WorldTargeter;
        if (!targeter.IsTargeting) return CommandResult.Fail(ChoiceNotFound);
        if (option == 2)
        {
            targeter.StopTargeting();
            return CommandResult.Ok(CancelLabel);
        }
        if (WorldTargeterAction?.GetValue(targeter) is not Func<GlobalTargetInfo, bool> action)
            return CommandResult.Fail("this game version does not expose the world targeter");
        if (!TryParseTile(input, out PlanetTile tile)) return CommandResult.Fail("give " + TileInputHint);
        // 同原版點世界地圖：格子上有世界物件就是點到物件。
        WorldObject worldObject = Find.WorldObjects.ObjectsAt(tile).FirstOrDefault();
        GlobalTargetInfo target = worldObject != null ? new GlobalTargetInfo(worldObject) : new GlobalTargetInfo(tile);
        var canSelect = WorldTargeterCanSelect?.GetValue(targeter) as Func<GlobalTargetInfo, bool>;
        if ((canSelect != null && !canSelect(target)) || !action(target)) return Refused($"tile {input} was refused", watch);
        targeter.StopTargeting();
        return CommandResult.Ok(WithGameMessages("tile " + input.Trim(), watch));
    }

    private static CommandResult AnswerMapTarget(int option, string input, MessageWatch watch)
    {
        Targeter targeter = Find.Targeter;
        if (!targeter.IsTargeting) return CommandResult.Fail(ChoiceNotFound);
        if (option == 2)
        {
            targeter.StopTargeting();
            return CommandResult.Ok(CancelLabel);
        }
        if (!TryParseMapTarget(input, out MapTarget target) || !TryResolveTarget(target, out LocalTargetInfo local, out string error))
        {
            return CommandResult.Fail("give #thing id or x,z");
        }
        try
        {
            CommandResult applied = ApplyTarget(targeter, "target", local, watch);
            return applied.Success ? CommandResult.Ok(string.IsNullOrEmpty(applied.Message) ? "target " + input.Trim() : applied.Message) : applied;
        }
        finally
        {
            targeter.StopTargeting();
        }
    }

    private static bool TryParseMapTarget(string input, out MapTarget target)
    {
        target = null;
        string text = input?.Trim().TrimStart('#') ?? string.Empty;
        string[] parts = text.Split(',');
        Result<MapTarget> parsed;
        if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int x) && int.TryParse(parts[1].Trim(), out int z)) parsed = TargetResolver.ResolveMapTarget(-1, x, z);
        else if (parts.Length == 1 && int.TryParse(text, out int id)) parsed = TargetResolver.ResolveMapTarget(id, -1, -1);
        else return false;
        target = parsed.Success ? parsed.Value : null;
        return target != null;
    }

    private static CommandResult AnswerLanding(int option, string input, MessageWatch watch)
    {
        WorldComponent_GravshipController controller = Find.GravshipController;
        if (controller == null || !controller.LandingAreaConfirmationInProgress) return CommandResult.Fail(ChoiceNotFound);
        if (LandingMarkerField?.GetValue(controller) is not GravshipLandingMarker marker)
            return CommandResult.Fail("this game version does not expose the landing marker");
        if (option == 1)
        {
            if (!marker.Spawned) return CommandResult.Fail("place the marker first (option 2)");
            if (Find.DesignatorManager.SelectedDesignator is Designator_MoveGravship) Find.DesignatorManager.Deselect();
            // 同原版「確認著陸」按鈕。
            marker.BeginLanding(controller);
            LandingMarkerField.SetValue(controller, null);
            return CommandResult.Ok("landing");
        }
        string[] parts = (input ?? string.Empty).Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), out int x) || !int.TryParse(parts[1].Trim(), out int z))
            return CommandResult.Fail("give the cell as x,z");
        var cell = new IntVec3(x, 0, z);
        Designator_MoveGravship designator = controller.MoveDesignator();
        AcceptanceReport report = designator.CanDesignateCell(cell);
        if (!report.Accepted) return CommandResult.Fail(report.Reason.NullOrEmpty() ? "the gravship cannot land there" : report.Reason.StripTags());
        designator.DesignateSingleCell(cell);
        return CommandResult.Ok(WithGameMessages($"marker at ({x},{z})", watch));
    }

    /// <summary>安裝與重新安裝：原版靠目前選取判斷要裝哪一個，所以先選取，再照點地圖的順序驗證與放置。</summary>
    private static CommandResult UseInstallButton(Thing thing, Designator_Install install, string label, LocalTargetInfo target)
    {
        if (!target.IsValid || target.HasThing) return CommandResult.Fail($"\"{label}\" needs a cell: pass targetX and targetZ");
        if (thing.Map != Find.CurrentMap) return CommandResult.Fail($"\"{label}\": switch to the map of that thing first");
        // 安裝完把玩家原本的選取放回去。
        List<object> playerSelection = [.. Find.Selector.SelectedObjects];
        try
        {
            Find.Selector.ClearSelection();
            Find.Selector.Select(thing, false, false);
            AcceptanceReport report = install.CanDesignateCell(target.Cell);
            if (!report.Accepted) return CommandResult.Fail(report.Reason.NullOrEmpty() ? "it cannot be placed there" : report.Reason.StripTags());
            InstallBlueprintUtility.CancelBlueprintsFor(thing);
            install.DesignateSingleCell(target.Cell);
            return CommandResult.Ok($"a builder will place it at ({target.Cell.x},{target.Cell.z})");
        }
        finally
        {
            Find.Selector.ClearSelection();
            foreach (object selected in playerSelection.Where(o => o is not Thing { Spawned: false })) Find.Selector.Select(selected, false, false);
        }
    }

    private static CommandResult AnswerMenu(FloatMenu menu, int option, MessageWatch watch)
    {
        if (FloatMenuOptions?.GetValue(menu) is not List<FloatMenuOption> options) return CommandResult.Fail("this game version does not expose menus");
        FloatMenuOption picked = option >= 1 && option <= options.Count ? options[option - 1] : null;
        if (picked == null) return CommandResult.Fail($"option {option} does not exist");
        if (picked.Disabled) return CommandResult.Fail($"\"{picked.Label.StripTags()}\" is disabled");
        string label = picked.Label.StripTags();
        // 同 FloatMenu 被點到：先跑選項，再關選單。
        picked.Chosen(menu.givesColonistOrders, menu);
        Find.WindowStack.TryRemove(menu, false);
        return CommandResult.Ok(WithGameMessages(label, watch));
    }

    private static CommandResult AnswerLordJob(Dialog_BeginLordJob dialog, int option, MessageWatch watch)
    {
        if (option == 2)
        {
            dialog.Close(false);
            return CommandResult.Ok(CancelLabel);
        }
        // 同開始鈕：條件不符時原版鈕是灰的（Enter 鍵不檢查，所以要自己擋）；通過後仍可能丟訊息、視窗留著。
        if (!dialog.CanBegin) return CommandResult.Fail("it cannot begin yet: " + LordJobBlock(dialog));
        if (Find.WindowStack.Windows.Any(w => w is Dialog_MessageBox)) return CommandResult.Fail("answer the open confirmation first");
        var before = new HashSet<Window>(Find.WindowStack.Windows);
        dialog.OnAcceptKeyPressed();
        _agentExpectsTile = true;
        if (Find.WindowStack.Windows.Any(w => w is Dialog_MessageBox && !before.Contains(w))) return CommandResult.Ok(WithGameMessages("it asks for confirmation first", watch));
        if (Find.WindowStack.Windows.Contains(dialog))
        {
            _agentExpectsTile = false;
            return Refused("it cannot begin yet", watch);
        }
        return CommandResult.Ok(WithGameMessages("begun", watch));
    }

    private static CommandResult AnswerConfigureIdeo(Dialog_ConfigureIdeo dialog)
    {
        if (ConfigureIdeoNext?.GetValue(dialog) is not Action next) return CommandResult.Fail("this game version does not expose this dialog");
        // 同「下一步」按鈕；沒改任何選擇時它只做這兩件事。
        dialog.Close();
        next();
        return CommandResult.Ok("kept the current ideoligion");
    }

    private static CommandResult AnswerNewColony(Dialog_ChooseThingsForNewColony dialog, int option, string input, MessageWatch watch)
    {
        Type type = typeof(Dialog_ChooseThingsForNewColony);
        if (option == 2)
        {
            (AccessTools.Field(type, "cancel")?.GetValue(dialog) as Action)?.Invoke();
            dialog.Close();
            return CommandResult.Ok(CancelLabel);
        }
        var selected = AccessTools.Field(type, "selected")?.GetValue(dialog) as HashSet<Thing>;
        FieldInfo itemCount = AccessTools.Field(type, "selectedItemCount");
        PropertyInfo acceptance = AccessTools.Property(type, "AcceptanceReport");
        MethodInfo confirm = AccessTools.Method(type, "ConfirmArchonexusSettlementConsequences");
        if (selected == null || itemCount == null || acceptance == null || confirm == null) return CommandResult.Fail("this game version does not expose this dialog");

        List<Thing> candidates = NewColonyCandidates(dialog);
        var picked = new List<Thing>();
        foreach (string part in (input ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part.Trim(), out int number) || number < 1 || number > candidates.Count) return CommandResult.Fail($"\"{part.Trim()}\" is not a listed number");
            if (!picked.Contains(candidates[number - 1])) picked.Add(candidates[number - 1]);
        }
        selected.Clear();
        selected.UnionWith(picked);
        List<Thing> items = NewColonyList(dialog, "items");
        itemCount.SetValue(dialog, picked.Count(items.Contains));
        var report = (AcceptanceReport)acceptance.GetValue(dialog);
        if (!report.Accepted) return CommandResult.Fail(report.Reason.NullOrEmpty() ? "the selection is not allowed" : report.Reason.StripTags());
        bool onlySlaves = picked.OfType<Pawn>().Any(p => p.IsColonist) && picked.OfType<Pawn>().Where(p => p.IsColonist).All(p => p.IsSlaveOfColony);
        // 同「接受」按鈕：後果確認框留給 choose_option。
        confirm.Invoke(dialog, [picked, onlySlaves]);
        return CommandResult.Ok(WithGameMessages($"selected {picked.Count}", watch));
    }

    private static CommandResult AnswerLoad(Dialog_LoadTransporters dialog, int option, string input, MessageWatch watch)
    {
        if (option == 2)
        {
            dialog.Close();
            return CommandResult.Ok(CancelLabel);
        }
        if (LoadTransferables?.GetValue(dialog) is not List<TransferableOneWay> transferables || LoadTryAccept == null)
            return CommandResult.Fail("this game version does not expose this dialog");
        // 先全部驗過，再從零開始套用：上一次失敗留下的數量不能混進這次。
        var counts = new List<(TransferableOneWay Row, int Count)>();
        foreach (string part in (input ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=');
            if (!int.TryParse(pair[0].Trim(), out int number) || number < 1 || number > transferables.Count) return CommandResult.Fail($"\"{part.Trim()}\" is not a listed number");
            TransferableOneWay transferable = transferables[number - 1];
            int count = transferable.MaxCount;
            if (pair.Length > 1 && !int.TryParse(pair[1].Trim(), out count)) return CommandResult.Fail($"\"{part.Trim()}\" has no valid count");
            AcceptanceReport report = transferable.CanAdjustTo(count);
            if (!report.Accepted) return CommandResult.Fail($"{transferable.LabelCap}: " + (report.Reason.NullOrEmpty() ? "that count is not allowed" : report.Reason.StripTags()));
            counts.Add((transferable, count));
        }
        foreach (TransferableOneWay row in transferables) row.AdjustTo(0);
        foreach ((TransferableOneWay row, int rowCount) in counts) row.AdjustTo(rowCount);
        // TryAccept 檢查失敗時丟訊息並回傳 false，視窗留著。
        if (!(bool)LoadTryAccept.Invoke(dialog, null)) return Refused("the loading was refused", watch);
        dialog.Close(false);
        return CommandResult.Ok(WithGameMessages("loading started", watch));
    }
}
