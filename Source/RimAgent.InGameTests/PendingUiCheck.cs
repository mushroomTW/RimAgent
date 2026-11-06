using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimAgent.Game;
using RimAgent.Tools;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimAgent.InGameTests;

/// <summary>
/// 開出原版真正的介面（世界選點、選目標、右鍵選單、裝載、安裝、新殖民地、意識形態），
/// 確認 list_pending_choices 與 choose_option 回答得了，而且回答後原版真的收到。
/// </summary>
internal static class PendingUiCheck
{
    public static bool LandingStarted;

    private static readonly string[] ReflectedMembers =
    [
        "TilePickerValidator", "TilePickerChosen", "TilePickerTitle", "TilePickerAllowEscape", "TargeterCaster", "TilePickerStop", "WorldTargeterAction", "WorldTargeterCanSelect",
        "FloatMenuOptions", "LandingMarkerField", "ConfigureIdeoNext", "LoadTransferables", "LoadTryAccept"
    ];

    public static List<string> Run(RimWorldGameWorld world)
    {
        var failures = new List<string>();
        world.TakeNewGameErrors();
        var checks = new (string Name, Action<RimWorldGameWorld, List<string>> Body)[]
        {
            ("reflection", CheckReflection),
            ("tile picker", CheckTilePicker),
            ("world target", CheckWorldTarget),
            ("map target", CheckMapTarget),
            ("menu", CheckMenu),
            ("menu then target", CheckMenuThenTarget),
            ("ceremony", CheckLordJob),
            ("loading", CheckLoading),
            ("install", CheckInstall),
            ("visibility", CheckVisibility),
            ("new colony", CheckNewColony),
            ("ideoligion", CheckConfigureIdeo),
            ("archonexus core", CheckArchonexusCore),
            ("landing", CheckLanding)
        };
        foreach ((string name, Action<RimWorldGameWorld, List<string>> body) in checks)
        {
            int before = failures.Count;
            try
            {
                body(world, failures);
            }
            catch (Exception ex)
            {
                failures.Add(name + " threw: " + ex);
            }
            Log.Message(TestHelpers.LogPrefix + "PendingUiCheck " + name + (failures.Count == before ? " ok" : " FAILED"));
        }
        foreach (string error in world.TakeNewGameErrors()) failures.Add("game logged: " + error);
        return failures;
    }

    private static PendingChoiceInfo Pending(RimWorldGameWorld world, Func<PendingChoiceInfo, bool> match)
    {
        return world.GetPendingChoices().FirstOrDefault(match);
    }

    private static void SetFlag(string name, bool value)
    {
        AccessTools.Field(typeof(RimWorldGameWorld), name).SetValue(null, value);
    }

    private static Pawn Colonist()
    {
        return Find.CurrentMap.mapPawns.FreeColonistsSpawned.First();
    }

    private static IntVec3? FindClear(Map map, int width, int height)
    {
        IntVec3 start = Colonist().Position;
        foreach (IntVec3 cell in GenRadial.RadialCellsAround(start, 40f, false))
        {
            var rect = new CellRect(cell.x, cell.z, width, height);
            if (rect.InBounds(map) && rect.ExpandedBy(1).InBounds(map)
                && rect.Cells.All(c => !c.Fogged(map) && c.Standable(map) && c.GetEdifice(map) == null && c.GetThingList(map).All(t => t is Plant)))
            {
                return cell;
            }
        }
        return null;
    }

    private static void CheckReflection(RimWorldGameWorld world, List<string> failures)
    {
        foreach (string name in ReflectedMembers)
        {
            FieldInfo holder = AccessTools.Field(typeof(RimWorldGameWorld), name);
            if (holder == null || holder.GetValue(null) == null) failures.Add("missing game member behind " + name);
        }
        Type colony = typeof(Dialog_ChooseThingsForNewColony);
        foreach (string field in new[] { "colonists", "animals", "relics", "items", "selected", "selectedItemCount", "cancel" })
        {
            if (AccessTools.Field(colony, field) == null) failures.Add("new colony dialog has no field " + field);
        }
        if (AccessTools.Property(colony, "AcceptanceReport") == null) failures.Add("new colony dialog has no AcceptanceReport");
        if (AccessTools.Method(colony, "ConfirmArchonexusSettlementConsequences") == null) failures.Add("new colony dialog has no confirm method");
    }

    private static void CheckTilePicker(RimWorldGameWorld world, List<string> failures)
    {
        PlanetTile home = Find.CurrentMap.Tile;
        PlanetTile chosen = PlanetTile.Invalid;
        string own = home.tileId.ToString();
        string other = (home.tileId + 1).ToString();
        Find.TilePicker.StartTargeting_NewTemp(t => t == home, t => chosen = t, title: "Check tile");
        try
        {
            if (Pending(world, c => c.Id == "dialog-tile") != null) failures.Add("a tile picker the player can cancel counted as pending");
            SetFlag("_agentExpectsTile", true);
            PendingChoiceInfo choice = Pending(world, c => c.Id == "dialog-tile");
            if (choice == null || string.IsNullOrEmpty(choice.InputHint))
            {
                failures.Add("tile picker is not pending with an input hint");
                return;
            }
            if (world.ChooseOption(choice.Id, 1, "not a tile").Success) failures.Add("garbage tile accepted");
            if (world.ChooseOption(choice.Id, 1, other).Success || chosen.Valid) failures.Add("tile refused by the validator went through");
            if (!Find.TilePicker.Active) failures.Add("a refused tile closed the picker");
            CommandResult ok = world.ChooseOption(choice.Id, 1, own);
            if (!ok.Success || chosen != home) failures.Add($"tile not delivered: {ok.Message}, chosen {chosen}");
            if (Find.TilePicker.Active) failures.Add("tile picker still active after answering");
        }
        finally
        {
            if (Find.TilePicker.Active) Find.TilePicker.StopTargeting();
        }
    }

    private static void CheckWorldTarget(RimWorldGameWorld world, List<string> failures)
    {
        PlanetTile home = Find.CurrentMap.Tile;
        GlobalTargetInfo got = GlobalTargetInfo.Invalid;
        Find.WorldTargeter.BeginTargeting(t =>
        {
            got = t;
            return true;
        }, true);
        SetFlag("_agentWorldTarget", true);
        try
        {
            PendingChoiceInfo choice = Pending(world, c => c.Id == "dialog-world-target");
            if (choice == null)
            {
                failures.Add("world target is not pending");
                return;
            }
            CommandResult ok = world.ChooseOption(choice.Id, 1, home.tileId.ToString());
            if (!ok.Success || got.Tile != home) failures.Add($"world target not delivered: {ok.Message}, got {got}");
            if (Find.WorldTargeter.IsTargeting) failures.Add("world targeter still active after answering");
            if (Pending(world, c => c.Id == "dialog-world-target") != null) failures.Add("world target still pending");
        }
        finally
        {
            Find.WorldTargeter.StopTargeting();
        }
    }

    private static void CheckMapTarget(RimWorldGameWorld world, List<string> failures)
    {
        Pawn colonist = Colonist();
        LocalTargetInfo got = LocalTargetInfo.Invalid;
        Find.Targeter.BeginTargeting(TargetingParameters.ForColonist(), t => got = t, caster: null, actionWhenFinished: null, mouseAttachment: null, requiresCastedSelected: false);
        SetFlag("_agentMapTarget", true);
        try
        {
            PendingChoiceInfo choice = Pending(world, c => c.Id == "dialog-map-target");
            if (choice == null)
            {
                failures.Add("map target is not pending");
                return;
            }
            CommandResult ok = world.ChooseOption(choice.Id, 1, "#" + colonist.thingIDNumber);
            if (!ok.Success || got.Thing != colonist) failures.Add($"map target not delivered: {ok.Message}, got {got}");
            if (Find.Targeter.IsTargeting) failures.Add("targeter still active after answering");
        }
        finally
        {
            Find.Targeter.StopTargeting();
        }
    }

    /// <summary>右鍵 → 選單 → 選目標：要隔一幀還活著（原版會檢查施放者有沒有選取），才等得到模型回答。</summary>
    private static void CheckMenuThenTarget(RimWorldGameWorld world, List<string> failures)
    {
        Pawn colonist = Colonist();
        Find.Selector.ClearSelection();
        LocalTargetInfo got = LocalTargetInfo.Invalid;
        var snapshot = new HashSet<Window>(Find.WindowStack.Windows);
        var menu = new FloatMenu([new FloatMenuOption("Check target", () => Find.Targeter.BeginTargeting(TargetingParameters.ForColonist(), t => got = t, caster: colonist))]);
        Find.WindowStack.Add(menu);
        try
        {
            AccessTools.Method(typeof(RimWorldGameWorld), "AdoptOpenedWindows").Invoke(null, [snapshot]);
            CommandResult chosen = world.ChooseOption(Pending(world, c => c.Title == "Menu").Id, 1);
            AccessTools.Method(typeof(Targeter), "ConfirmStillValid").Invoke(Find.Targeter, null);
            PendingChoiceInfo target = Pending(world, c => c.Id == "dialog-map-target");
            if (!chosen.Success || target == null)
            {
                failures.Add("target after a menu did not survive a frame: " + chosen.Message);
                return;
            }
            CommandResult ok = world.ChooseOption(target.Id, 1, "#" + colonist.thingIDNumber);
            if (!ok.Success || got.Thing != colonist) failures.Add("target after a menu not delivered: " + ok.Message);
        }
        finally
        {
            Find.Targeter.StopTargeting();
            Find.Selector.ClearSelection();
        }
    }

    private static void CheckMenu(RimWorldGameWorld world, List<string> failures)
    {
        bool ran = false;
        var snapshot = new HashSet<Window>(Find.WindowStack.Windows);
        var menu = new FloatMenu([new FloatMenuOption("Check run", () => ran = true), new FloatMenuOption("Check disabled", null)]);
        Find.WindowStack.Add(menu);
        try
        {
            AccessTools.Method(typeof(RimWorldGameWorld), "AdoptOpenedWindows").Invoke(null, [snapshot]);
            if (menu.vanishIfMouseDistant) failures.Add("adopted menu would vanish without a mouse");
            PendingChoiceInfo choice = Pending(world, c => c.Title == "Menu");
            if (choice == null || choice.Options.Count != 2 || !choice.Options[1].Disabled)
            {
                failures.Add("menu is not pending with its options");
                return;
            }
            string reply = new ChoiceTools(world).ChooseOption(choice.Id, 1);
            if (!ran) failures.Add("menu option did not run: " + reply);
            if (Find.WindowStack.Windows.Contains(menu)) failures.Add("menu stayed open");
        }
        finally
        {
            if (Find.WindowStack.Windows.Contains(menu)) menu.Close(false);
        }
    }

    private sealed class CheckLordJobDialog : Dialog_BeginLordJob
    {
        public int Started;
        public bool Refuse;
        public bool AskFirst;

        public CheckLordJobDialog()
            : base(null)
        {
        }

        public override TaggedString HeaderLabel => "Check ceremony";

        public override bool CanBegin => !Refuse;

        protected override IEnumerable<string> BlockingIssues()
        {
            if (Refuse) yield return "Check blocked";
        }

        public override void DoWindowContents(UnityEngine.Rect inRect)
        {
        }

        protected override void Start()
        {
            if (AskFirst)
            {
                // 同 Dialog_BeginRitual：確認框答完才真的開始，這段時間視窗還開著。
                AskFirst = false;
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("Check confirm", Start));
                return;
            }
            Started++;
            Close();
        }

        protected override void Cancel()
        {
            Close();
        }
    }

    private static void CheckLordJob(RimWorldGameWorld world, List<string> failures)
    {
        var dialog = new CheckLordJobDialog { Refuse = true, AskFirst = true };
        var snapshot = new HashSet<Window>(Find.WindowStack.Windows);
        Find.WindowStack.Add(dialog);
        try
        {
            if (Pending(world, c => c.Title == "Check ceremony") != null) failures.Add("a ceremony the player opened counted as pending");
            AccessTools.Method(typeof(RimWorldGameWorld), "AdoptOpenedWindows").Invoke(null, [snapshot]);
            PendingChoiceInfo choice = Pending(world, c => c.Title == "Check ceremony");
            if (choice == null)
            {
                failures.Add("ceremony is not pending");
                return;
            }
            CommandResult refused = world.ChooseOption(choice.Id, 1);
            if (refused.Success || dialog.Started != 0 || !refused.Message.Contains("Check blocked")) failures.Add("a blocked ceremony: " + refused.Message);
            if (!Find.WindowStack.Windows.Contains(dialog)) failures.Add("a blocked ceremony closed the window");
            dialog.Refuse = false;
            CommandResult asked = world.ChooseOption(choice.Id, 1);
            if (!asked.Success || dialog.Started != 0) failures.Add("a ceremony asking for confirmation: " + asked.Message);
            if (world.ChooseOption(choice.Id, 1).Success) failures.Add("a second confirmation box was stacked");
            PendingChoiceInfo box = Pending(world, c => c.Text.Contains("Check confirm"));
            CommandResult ok = box == null ? CommandResult.Fail("no confirmation box") : world.ChooseOption(box.Id, 1);
            if (!ok.Success || dialog.Started != 1) failures.Add("ceremony did not start: " + ok.Message);
        }
        finally
        {
            if (Find.WindowStack.Windows.Contains(dialog)) dialog.Close(false);
        }
    }

    private static void CheckLoading(RimWorldGameWorld world, List<string> failures)
    {
        Map map = Find.CurrentMap;
        IntVec3? cell = FindClear(map, 7, 7);
        if (cell == null)
        {
            failures.Add("no room for a transport pod");
            return;
        }
        // 運輸艙要接在燃料台的補給格上，裝載鈕才能按。
        ThingDef launcherDef = DefDatabase<ThingDef>.GetNamed("PodLauncher");
        var launcher = (Building)ThingMaker.MakeThing(launcherDef, launcherDef.MadeFromStuff ? GenStuff.DefaultStuffFor(launcherDef) : null);
        launcher.SetFaction(Faction.OfPlayer);
        GenSpawn.Spawn(launcher, cell.Value + new IntVec3(3, 0, 3), map, Rot4.North);
        launcher.TryGetComp<CompRefuelable>()?.Refuel(500f);
        Thing pod = ThingMaker.MakeThing(ThingDefOf.TransportPod);
        pod.SetFaction(Faction.OfPlayer);
        GenSpawn.Spawn(pod, FuelingPortUtility.GetFuelingPortCell(launcher), map);
        try
        {
            // 測試環境可能是中文，靠原版翻譯的前綴找裝載鈕。
            string loadLabel = "CommandLoadTransporter".Translate(0).ToString();
            GizmoInfo load = world.GetGizmos(pod.thingIDNumber).FirstOrDefault(g => loadLabel.StartsWith(g.Label.Substring(0, 2), StringComparison.Ordinal));
            if (load == null || load.Kind != GizmoInfo.ActionKind)
            {
                failures.Add("load button is not an action: " + string.Join(", ", world.GetGizmos(pod.thingIDNumber).Select(g => g.Label + "/" + g.Kind)));
                return;
            }
            CommandResult pressed = world.UseGizmo(pod.thingIDNumber, load.Number, load.Label, null);
            PendingChoiceInfo choice = Pending(world, c => c.Title == "Choose what to load");
            if (!pressed.Success || choice == null)
            {
                failures.Add($"load screen is not pending: {pressed.Message}");
                return;
            }
            Log.Message(TestHelpers.LogPrefix + "load screen:\n" + choice.Text);
            CommandResult ok = world.ChooseOption(choice.Id, 1, "1=1");
            if (!ok.Success) failures.Add("loading refused: " + ok.Message);
            if (ok.Success && !pod.TryGetComp<CompTransporter>().LoadingInProgressOrReadyToLaunch) failures.Add("loading did not start");
            if (Pending(world, c => c.Title == "Choose what to load") != null) failures.Add("load screen stayed open");
        }
        finally
        {
            foreach (Dialog_LoadTransporters open in Find.WindowStack.Windows.OfType<Dialog_LoadTransporters>().ToList()) open.Close(false);
            pod.TryGetComp<CompTransporter>()?.CancelLoad();
            pod.Destroy();
            launcher.Destroy();
        }
    }

    private static void CheckInstall(RimWorldGameWorld world, List<string> failures)
    {
        Map map = Find.CurrentMap;
        ThingDef def = DefDatabase<ThingDef>.AllDefs.FirstOrDefault(d => d.Minifiable && !d.MadeFromStuff && d.size == IntVec2.One);
        IntVec3? itemCell = FindClear(map, 1, 1);
        if (def == null || itemCell == null)
        {
            failures.Add("no minifiable def or no room");
            return;
        }
        MinifiedThing mini = ((Building)ThingMaker.MakeThing(def)).MakeMinified();
        GenSpawn.Spawn(mini, itemCell.Value, map);
        IntVec3? target = GenRadial.RadialCellsAround(itemCell.Value, 15f, false)
            .Where(c => c.InBounds(map) && !c.Fogged(map) && GenConstruct.CanPlaceBlueprintAt(def, c, Rot4.North, map).Accepted)
            .Select(c => (IntVec3?)c).FirstOrDefault();
        try
        {
            GizmoInfo install = world.GetGizmos(mini.thingIDNumber).FirstOrDefault(g => g.Kind == GizmoInfo.TargetKind);
            if (install == null || target == null)
            {
                failures.Add("no install button: " + string.Join(", ", world.GetGizmos(mini.thingIDNumber).Select(g => g.Label + "/" + g.Kind)));
                return;
            }
            MapTarget cell = TargetResolver.ResolveMapTarget(-1, target.Value.x, target.Value.z).Value;
            CommandResult ok = world.UseGizmo(mini.thingIDNumber, install.Number, install.Label, cell);
            if (!ok.Success) failures.Add("install refused: " + ok.Message);
            Blueprint_Install blueprint = target.Value.GetThingList(map).OfType<Blueprint_Install>().FirstOrDefault();
            if (blueprint == null) failures.Add("install placed no blueprint");
            blueprint?.Destroy();
        }
        finally
        {
            Find.Selector.ClearSelection();
            if (!mini.Destroyed) mini.Destroy();
        }
    }

    private static void CheckVisibility(RimWorldGameWorld world, List<string> failures)
    {
        if (world.GetOverview().Factions.Count == 0) failures.Add("overview lists no factions");
        Pawn colonist = Colonist();
        Log.Message(TestHelpers.LogPrefix + "inspect text: " + world.GetInspectText(colonist.thingIDNumber));
        if (!ModsConfig.RoyaltyActive || colonist.royalty == null) return;
        Faction empire = Faction.OfEmpire;
        if (empire == null) return;
        int honor = colonist.royalty.GetFavor(empire);
        colonist.royalty.SetFavor(empire, 7);
        ColonistInfo info = world.GetColonists().FirstOrDefault(c => c.Id == colonist.thingIDNumber);
        // 原版給榮譽時可能自動升頭銜，數字不一定還是 7。
        if (info == null || !info.RoyalStatus.Contains("honor ")) failures.Add("royal status: " + info?.RoyalStatus);
        colonist.royalty.SetFavor(empire, honor);
    }

    private static void CheckNewColony(RimWorldGameWorld world, List<string> failures)
    {
        if (!ModsConfig.IdeologyActive) return;
        List<Thing> accepted = null;
        bool cancelled = false;
        var dialog = new Dialog_ChooseThingsForNewColony(list => accepted = list, cancel: () => cancelled = true);
        Find.WindowStack.Add(dialog);
        try
        {
            PendingChoiceInfo choice = Pending(world, c => c.Title == "Choose what comes along to the new colony");
            if (choice == null)
            {
                failures.Add("new colony selection is not pending");
                return;
            }
            Log.Message(TestHelpers.LogPrefix + "new colony screen:\n" + choice.Text);
            if (world.ChooseOption(choice.Id, 1, "99999").Success) failures.Add("an unlisted number was accepted");
            CommandResult ok = world.ChooseOption(choice.Id, 1, "1");
            if (!ok.Success) failures.Add("selection refused: " + ok.Message);
            // 後果確認框也要走得通。
            PendingChoiceInfo warning = Pending(world, c => c.Id.StartsWith("dialog-", StringComparison.Ordinal) && c.Title != choice.Title);
            if (accepted == null && warning != null) world.ChooseOption(warning.Id, 1);
            if (accepted == null || accepted.Count != 1 || accepted[0] is not Pawn) failures.Add("selection not delivered: " + (accepted == null ? "null" : accepted.Count.ToString()));
            if (cancelled) failures.Add("selection cancelled");
        }
        finally
        {
            if (Find.WindowStack.Windows.Contains(dialog)) dialog.Close(false);
        }
    }

    private static void CheckConfigureIdeo(RimWorldGameWorld world, List<string> failures)
    {
        if (!ModsConfig.IdeologyActive) return;
        bool next = false;
        var dialog = new Dialog_ConfigureIdeo(Find.CurrentMap.mapPawns.FreeColonistsSpawned, () => next = true, true);
        Find.WindowStack.Add(dialog);
        try
        {
            PendingChoiceInfo choice = Pending(world, c => c.Title == "Ideoligion of the new colony");
            if (choice == null)
            {
                failures.Add("ideoligion screen is not pending");
                return;
            }
            CommandResult ok = world.ChooseOption(choice.Id, 1);
            if (!ok.Success || !next) failures.Add("ideoligion screen did not move on: " + ok.Message);
            if (Find.WindowStack.Windows.Contains(dialog)) failures.Add("ideoligion screen stayed open");
        }
        finally
        {
            if (Find.WindowStack.Windows.Contains(dialog)) dialog.Close(false);
        }
    }

    /// <summary>照原版抵達既有地圖的順序開出著陸確認，移動標記再確認著陸。放最後跑：著陸動畫會接手畫面。</summary>
    private static void CheckLanding(RimWorldGameWorld world, List<string> failures)
    {
        if (!ModsConfig.OdysseyActive) return;
        Map map = Find.CurrentMap;
        ThingDef engineDef = DefDatabase<ThingDef>.GetNamed("GravEngine");
        IntVec3? corner = FindClear(map, 9, 9);
        if (corner == null)
        {
            failures.Add("no room for a gravship");
            return;
        }
        TerrainDef substructure = DefDatabase<TerrainDef>.GetNamed("Substructure");
        foreach (IntVec3 c in new CellRect(corner.Value.x, corner.Value.z, 9, 9)) map.terrainGrid.SetFoundation(c, substructure);
        Thing made = ThingMaker.MakeThing(engineDef);
        made.SetFaction(Faction.OfPlayer);
        var engine = (Building_GravEngine)GenSpawn.Spawn(made, corner.Value + new IntVec3(4, 0, 4), map);
        // 飛船沒有駕駛台就算不出起飛方向；發射資訊平常由發射儀式填入。
        ThingDef consoleDef = DefDatabase<ThingDef>.GetNamed("PilotConsole");
        Thing console = ThingMaker.MakeThing(consoleDef, consoleDef.MadeFromStuff ? GenStuff.DefaultStuffFor(consoleDef) : null);
        console.SetFaction(Faction.OfPlayer);
        GenSpawn.Spawn(console, corner.Value + new IntVec3(1, 0, 1), map, Rot4.North);
        engine.launchInfo = new LaunchInfo();
        Gravship gravship = GravshipUtility.GenerateGravship(engine);
        gravship.destinationTile = map.Tile;
        // 省略飛行，但抵達時原版會把世界物件移除，所以要先放上世界。
        gravship.Tile = map.Tile;
        Find.WorldObjects.Add(gravship);
        GravshipUtility.ArriveExistingMap(gravship);

        PendingChoiceInfo choice = Pending(world, c => c.Id == "dialog-landing");
        if (choice == null)
        {
            failures.Add("landing is not pending");
            return;
        }
        Log.Message(TestHelpers.LogPrefix + "landing: " + choice.Text + " | " + string.Join("; ", choice.Options.Select(o => o.Number + ") " + o.Text + (o.Disabled ? " (disabled)" : string.Empty))));
        if (world.ChooseOption(choice.Id, 2, "not a cell").Success) failures.Add("garbage landing cell accepted");
        Designator_MoveGravship designator = Find.GravshipController.MoveDesignator();
        IntVec3 target = map.AllCells.Where(c => designator.CanDesignateCell(c).Accepted).OrderBy(c => c.DistanceTo(map.Center)).FirstOrDefault();
        CommandResult moved = world.ChooseOption(choice.Id, 2, target.x + "," + target.z);
        if (!moved.Success) failures.Add($"marker refused at {target}: {moved.Message}");
        PendingChoiceInfo after = Pending(world, c => c.Id == "dialog-landing");
        if (after == null || after.Options[0].Disabled) failures.Add("landing still cannot be confirmed after placing the marker");
        CommandResult landed = world.ChooseOption(choice.Id, 1);
        if (!landed.Success) failures.Add("landing refused: " + landed.Message);
        if (Find.GravshipController.LandingAreaConfirmationInProgress) failures.Add("landing confirmation still open");
        LandingStarted = landed.Success;
    }

    private static void CheckArchonexusCore(RimWorldGameWorld world, List<string> failures)
    {
        if (!ModsConfig.IdeologyActive) return;
        ThingDef def = DefDatabase<ThingDef>.AllDefs.FirstOrDefault(d => d.thingClass == typeof(Building_ArchonexusCore));
        if (def == null)
        {
            failures.Add("no archonexus core def");
            return;
        }
        Map map = Find.CurrentMap;
        IntVec3? cell = FindClear(map, def.size.x, def.size.z);
        if (cell == null)
        {
            // 隨機地圖可能放不下，這不是待驗的行為。
            Log.Warning(TestHelpers.LogPrefix + "PendingUiCheck archonexus core SKIP: no room");
            return;
        }
        Thing core = GenSpawn.Spawn(ThingMaker.MakeThing(def), cell.Value + new IntVec3(def.size.x / 2, 0, def.size.z / 2), map);
        try
        {
            Pawn colonist = Colonist();
            MapTarget target = TargetResolver.ResolveMapTarget(core.thingIDNumber, -1, -1).Value;
            FloatMenuInfo menu = world.GetFloatMenuOptions(colonist.thingIDNumber, target);
            string expected = core.GetMultiSelectFloatMenuOptions([colonist]).FirstOrDefault()?.Label;
            Log.Message(TestHelpers.LogPrefix + "core menu: " + string.Join(", ", menu.Options.Select(o => o.Label)));
            if (expected == null || menu.Options.All(o => o.Label != expected.StripTags())) failures.Add($"multi-select option \"{expected}\" missing for one colonist: {menu.Error}");
        }
        finally
        {
            // 古樞紐核心是不可摧毀的建築，只能移出地圖。
            core.DeSpawn();
        }
    }
}

/// <summary>隔離環境 Config 有標記檔時，進圖後跑一次，結果以 "[RimAgent.InGameTests] PendingUiCheck PASS/FAIL" 警告輸出。</summary>
public sealed class PendingUiCheckComponent : GameComponent
{
    private const string MarkerFileName = "RimAgentPendingUiCheck.xml";
    private static bool _done;

    public PendingUiCheckComponent(Verse.Game game)
    {
    }

    public override void GameComponentUpdate()
    {
        // 著陸動畫跑完才算真的落地。
        if (PendingUiCheck.LandingStarted && Current.Game.Gravship == null)
        {
            PendingUiCheck.LandingStarted = false;
            Log.Warning(TestHelpers.LogPrefix + "PendingUiCheck landing finished; engine on the map: " + Find.CurrentMap.listerBuildings.allBuildingsColonist.Any(b => b is Building_GravEngine));
        }
        if (_done || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) return;
        _done = true;
        if (!File.Exists(Path.Combine(GenFilePaths.ConfigFolderPath, MarkerFileName))) return;
        string prefix = TestHelpers.LogPrefix + "PendingUiCheck ";
        try
        {
            List<string> failures = PendingUiCheck.Run(new RimWorldGameWorld(Current.Game));
            Log.Warning(prefix + (failures.Count == 0 ? "PASS" : $"FAIL ({failures.Count}): " + string.Join(" | ", failures)));
        }
        catch (Exception ex)
        {
            Log.Warning(prefix + "FAIL exception: " + ex);
        }
    }
}
