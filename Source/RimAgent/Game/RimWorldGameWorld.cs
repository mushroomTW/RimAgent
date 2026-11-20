#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimAgent.Game;

/// <summary>
/// <see cref="IGameWorld"/> 的正式實作：對 Find.CurrentMap 的薄轉接層。
/// 只做 RimWorld API 呼叫與純資料轉換，不含決策邏輯；假設呼叫者已在 Unity 主線程
/// （由框架的 AsMainThreadFunctionInvokingClient 保證）。這一層只能在遊戲內驗證。
/// </summary>
internal sealed partial class RimWorldGameWorld : IGameWorld
{
    private const string MineDefName = "Mine";
    private const string ColonistNotFound = "colonist not found";

    /// <summary>綁定的遊戲；不為 null 時，換成別的遊戲（讀檔）後一律不可執行工具。</summary>
    private readonly Verse.Game _boundGame;

    /// <summary>遊戲截圖資料夾；截圖時（主線程）記下，供背景執行緒讀截圖檔使用。還沒截過圖時為 null。</summary>
    private volatile string _screenshotFolder;

    public RimWorldGameWorld(Verse.Game boundGame = null)
    {
        _boundGame = boundGame;
    }

    /// <summary>
    /// 反射目標在遊戲改版後可能消失；啟動時只警告一次，讓 log 看得出是哪個功能降級，
    /// 而不是等到玩家按下按鈕才得到模糊的「不支援」。
    /// </summary>
    static RimWorldGameWorld()
    {
        WarnMissingReflection(ActiveAlertsField, "AlertsReadout.activeAlerts");
        WarnMissingReflection(DiaOptionTextField, "DiaOption.text");
        WarnMissingReflection(DiaOptionActivateMethod, "DiaOption.Activate");
        WarnMissingReflection(NodeTreeCurNodeField, "Dialog_NodeTree.curNode");
        WarnMissingReflection(NodeTreeTitleField, "Dialog_NodeTree.title");
        WarnMissingReflection(TargeterActionField, "Targeter.action");
        WarnMissingReflection(TargeterParamsField, "Targeter.targetParams");
        WarnMissingReflection(TargeterValidatorField, "Targeter.targetValidator");
        WarnMissingReflection(LiveMessagesField, "Messages.liveMessages");
    }

    private static void WarnMissingReflection(object member, string name)
    {
        if (member == null)
        {
            Log.Warning("RimAgent: RimWorld reflection target " + name +
                " was not found; the related feature will report itself as unsupported. " +
                "This usually means the game changed; update RimAgent.");
        }
    }

    private IReadOnlyList<DefInfo> _workTypes;
    private IReadOnlyList<DefInfo> _buildables;
    private IReadOnlyList<DefInfo> _stuffs;
    private IReadOnlyList<DefInfo> _timeAssignments;
    private IReadOnlyList<DefInfo> _tradeGoods;
    private IReadOnlyList<DefInfo> _medicalCareLevels;
    private IReadOnlyList<DefInfo> _surgeryRecipes;
    private IReadOnlyList<DefInfo> _prisonerInteractionModes;
    private IReadOnlyList<DefInfo> _animalTraining;
    private IReadOnlyList<DefInfo> _storageFilterables;
    private IReadOnlyList<DefInfo> _growablePlants;

    private static Map Map => Find.CurrentMap;

    public IReadOnlyList<ColonistInfo> GetColonists()
    {
        return [.. Map.mapPawns.FreeColonistsSpawned.Select(ToColonist)];
    }

    public IReadOnlyList<MapInfo> GetMaps()
    {
        Map current = Map;
        return [.. Find.Maps.Select(m => new MapInfo
        {
            Id = m.uniqueID,
            Label = m.Parent?.LabelCap ?? ("Map " + m.uniqueID.ToString(CultureInfo.InvariantCulture)),
            Tile = m.Tile,
            ColonistCount = m.mapPawns.FreeColonistsSpawnedCount,
            IsCurrent = m == current
        })];
    }

    public CommandResult SetCurrentMap(int mapId)
    {
        Map map = Find.Maps.FirstOrDefault(m => m.uniqueID == mapId);
        if (map == null) return CommandResult.Fail(string.Create(CultureInfo.InvariantCulture, $"no map has id {mapId}"));
        string label = map.Parent?.LabelCap ?? ("Map " + map.uniqueID.ToString(CultureInfo.InvariantCulture));
        if (map == Map) return CommandResult.Ok($"{label} is already the current map.");
        Current.Game.CurrentMap = map;
        // 讓畫面跟著切換：TryJump 會離開世界地圖並把鏡頭移到該地圖。
        CameraJumper.TryJump(map.Center, map);
        return CommandResult.Ok(string.Create(CultureInfo.InvariantCulture, $"Switched to {label} [{mapId}]. Later tools now operate on this map."));
    }

    public IReadOnlyList<DefInfo> TimeSpeeds => TimeSpeedDirectory.Default;

    public TimeSpeedInfo GetTimeSpeed()
    {
        return new TimeSpeedInfo { Current = Find.TickManager.CurTimeSpeed.ToString(), Available = TimeSpeedDirectory.Default };
    }

    public CommandResult SetTimeSpeed(string speedDefName)
    {
        object parsed;
        try
        {
            parsed = Enum.Parse<TimeSpeed>((speedDefName ?? string.Empty).Trim(), ignoreCase: true);
        }
        catch (ArgumentException)
        {
            return CommandResult.Fail($"unknown time speed {speedDefName}");
        }
        var speed = (TimeSpeed)parsed;
        if (!Enum.IsDefined<TimeSpeed>(speed)) return CommandResult.Fail($"unknown time speed {speedDefName}");
        if (speed == TimeSpeed.Ultrafast) return CommandResult.Fail("Ultrafast is only for development; use Paused, Normal, Fast or Superfast");
        TimeSpeed previous = Find.TickManager.CurTimeSpeed;
        if (previous == speed) return CommandResult.Ok($"Time speed is already {speed}.");
        Find.TickManager.CurTimeSpeed = speed;
        return CommandResult.Ok($"Time speed set to {speed}.");
    }

    public string GetNotReadyReason()
    {
        // 讀檔期間 ProgramState 會先變成 MapInitializing，這一條已經擋住；不看長事件，
        // 自動存檔排入的那一幀與背景生成新地圖期間當前地圖都還能碰，擋了只會誤殺指令。
        if (Current.Game == null || Current.ProgramState != ProgramState.Playing) return "no game is being played";
        // 讀檔後舊對話可能還有模型呼叫在路上：它的工具不能動到換上來的新遊戲。
        if (_boundGame != null && !ReferenceEquals(Current.Game, _boundGame)) return "the game was reloaded; this conversation belonged to the previous game";
        if (Find.CurrentMap == null) return "there is no current map";
        return null;
    }

    private const int WaitPollMilliseconds = 100;

    public async System.Threading.Tasks.Task<CommandResult> WaitTicksAsync(int ticks, System.Threading.CancellationToken cancellationToken)
    {
        Verse.Game game = Current.Game;
        Map map = Find.CurrentMap;
        int target = Find.TickManager.TicksGame + Math.Max(0, ticks);
        while (true)
        {
            string notReady = GetNotReadyReason();
            // 等待期間讀了別的檔：tick 已換一條時間軸，目標不再有意義。
            if (notReady != null || Current.Game != game) return CommandResult.Fail(notReady ?? "a different game was loaded");
            // 工具都作用在當前地圖：玩家切到別張地圖後，等待中的檢查與之後的指令都會換成那張圖，先回來讓模型知道。
            if (Find.CurrentMap != map) return CommandResult.Fail("the current map changed");
            if (Find.TickManager.TicksGame >= target) return CommandResult.Ok(string.Empty);
            if (Find.TickManager.Paused) return CommandResult.Fail("the game is paused");
            // 不加 ConfigureAwait(false)：續行要回主線程才能再讀 TickManager。
            await System.Threading.Tasks.Task.Delay(WaitPollMilliseconds, cancellationToken);
        }
    }

    private LogMessage _lastSeenLog;
    private int _lastSeenLogRepeats;

    public IReadOnlyList<string> TakeNewGameErrors()
    {
        // Log 把連續相同訊息合併成一則並累加 repeats，所以「新錯誤」有兩種：上次看過的那則又重複了，
        // 以及排在它後面的新訊息。上次那則已被清掉或擠出佇列時，整個佇列都算新的。
        var errors = new List<string>();
        // Log 的鎖是私有的，背景執行緒隨時可能 Log.Error；列舉撞上修改就當這次沒有新錯誤，不能讓指令因此失敗。
        List<LogMessage> messages;
        try
        {
            messages = [.. Log.Messages];
        }
        catch (InvalidOperationException)
        {
            return errors;
        }
        bool afterLastSeen = _lastSeenLog == null || !messages.Contains(_lastSeenLog);
        LogMessage last = null;
        foreach (LogMessage message in messages)
        {
            last = message;
            if (!afterLastSeen)
            {
                if (!ReferenceEquals(message, _lastSeenLog)) continue;
                afterLastSeen = true;
                if (message.type == LogMessageType.Error && message.repeats > _lastSeenLogRepeats)
                {
                    errors.Add(FormatLogError(message, message.repeats - _lastSeenLogRepeats));
                }
                continue;
            }
            if (message.type == LogMessageType.Error) errors.Add(FormatLogError(message, message.repeats));
        }
        _lastSeenLog = last;
        _lastSeenLogRepeats = last?.repeats ?? 0;
        return errors;
    }

    private static string FormatLogError(LogMessage message, int occurrences)
    {
        string text = (message.text ?? string.Empty).Trim();
        int newline = text.IndexOf('\n', StringComparison.Ordinal);
        if (newline >= 0) text = text.Substring(0, newline);
        return occurrences > 1 ? string.Create(CultureInfo.InvariantCulture, $"(x{occurrences}) {text}") : text;
    }

    public IReadOnlyList<DefInfo> WorkTypes =>
        _workTypes ?? (_workTypes = [.. DefDatabase<WorkTypeDef>.AllDefsListForReading
            .Where(w => w.visible)
            .Select(w => new DefInfo(w.defName, w.labelShort ?? w.label))]);

    public IReadOnlyList<DefInfo> Buildables =>
        _buildables ?? (_buildables =
        [
            .. DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.BuildableByPlayer).Select(d => new DefInfo(d.defName, d.label)),
            .. DefDatabase<TerrainDef>.AllDefsListForReading.Where(d => d.BuildableByPlayer).Select(d => new DefInfo(d.defName, d.label)),
        ]);

    public IReadOnlyList<DefInfo> Stuffs =>
        _stuffs ?? (_stuffs = [.. DefDatabase<ThingDef>.AllDefsListForReading
            .Where(d => d.IsStuff)
            .Select(d => new DefInfo(d.defName, d.label))]);

    public IReadOnlyList<DefInfo> TimeAssignments =>
        _timeAssignments ?? (_timeAssignments = [.. DefDatabase<TimeAssignmentDef>.AllDefsListForReading.Select(d => new DefInfo(d.defName, d.label))]);

    public IReadOnlyList<DefInfo> TradeGoods =>
        _tradeGoods ?? (_tradeGoods = [.. DefDatabase<ThingDef>.AllDefsListForReading
            .Where(d => d.category == ThingCategory.Item)
            .Select(d => new DefInfo(d.defName, d.label))]);

    // ---- 轉換 ----

    private static ColonistInfo ToColonist(Pawn pawn)
    {
        var triple = pawn.Name as NameTriple;
        string nick = triple?.Nick ?? string.Empty;
        string first = triple?.First ?? pawn.Name?.ToStringShort ?? pawn.LabelShort;
        bool hasDistinctNick = !string.IsNullOrEmpty(nick) && nick != first;
        var unreadable = new List<string>();

        return new ColonistInfo
        {
            Id = pawn.thingIDNumber,
            Name = pawn.LabelShort,
            Nickname = hasDistinctNick ? nick : string.Empty,
            FullName = pawn.Name?.ToStringFull ?? pawn.LabelShort,
            RoyalStatus = RoyalStatusOf(pawn),
            Age = pawn.ageTracker?.AgeBiologicalYears ?? 0,
            X = pawn.Position.x,
            Z = pawn.Position.z,
            HealthPercent = pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f,
            Downed = pawn.Downed,
            MoodPercent = pawn.needs?.mood?.CurLevelPercentage ?? 0f,
            MinorBreakThreshold = pawn.mindState?.mentalBreaker?.BreakThresholdMinor ?? 0f,
            InMentalState = pawn.InMentalState,
            MentalStateLabel = pawn.MentalStateDef?.label ?? string.Empty,
            CurrentActivity = SafeReport(pawn),
            Drafted = pawn.Drafted,
            MedicalCare = pawn.playerSettings != null ? pawn.playerSettings.medCare.ToString() : string.Empty,
            // 以下各段會跑到其他模組的程式（武器、需求、想法、技能、工作類型），各自隔離：
            // 一段丟例外只讓那段變空，殖民者仍列得出來、叫得動。
            Equipment = ReadOrDefault(pawn, unreadable, "equipment", () => pawn.equipment?.Primary?.LabelCap ?? string.Empty, string.Empty),
            Needs = ReadOrDefault(pawn, unreadable, "needs", () => pawn.needs?.AllNeeds.Select(n => new NeedInfo
            {
                Label = n.LabelCap,
                Percent = n.CurLevelPercentage
            }).ToList() ?? (IReadOnlyList<NeedInfo>)[], []),
            Thoughts = ReadOrDefault(pawn, unreadable, "thoughts", () => pawn.needs?.mood?.thoughts?.memories?.Memories
                .Select(t => t.LabelCap).Where(t => !string.IsNullOrEmpty(t)).Distinct(StringComparer.Ordinal).Take(12).ToList()
                ?? (IReadOnlyList<string>)[], []),
            Skills = ReadOrDefault(pawn, unreadable, "skills", () => pawn.skills?.skills.Select(s => new SkillInfo
            {
                DefName = s.def.defName,
                Label = s.def.label,
                Level = s.Level,
                Passion = (int)s.passion,
                Disabled = s.TotallyDisabled
            }).ToList() ?? (IReadOnlyList<SkillInfo>)[], []),
            WorkPriorities = pawn.workSettings == null
                ? (IReadOnlyList<WorkPriorityInfo>)[]
                : ReadOrDefault<IReadOnlyList<WorkPriorityInfo>>(pawn, unreadable, ColonistInfo.WorkPrioritiesPart, () => [.. DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(w => w.visible).Select(w => ToWorkPriority(pawn, w))],
                    []),
            Timetable = pawn.timetable == null
                ? (IReadOnlyList<string>)[]
                : [.. Enumerable.Range(0, 24).Select(h => pawn.timetable.GetAssignment(h)?.defName ?? string.Empty)],
            // 初始設定式依序求值，放最後才收得到上面每一段的失敗。
            UnreadableParts = unreadable
        };
    }

    /// <summary>
    /// 讀一段可能跑到其他模組程式的資料；丟例外就回傳預設值、把段名記進 <paramref name="unreadable"/>
    /// （工具據此告訴模型「讀不到」而非「沒有」），並對這個 pawn 的這一段只警告一次。
    /// </summary>
    private static T ReadOrDefault<T>(Pawn pawn, List<string> unreadable, string part, Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            WarnModError(pawn, part, ex);
            unreadable.Add(part);
            return fallback;
        }
    }

    private static void WarnModError(Pawn pawn, string part, Exception ex)
    {
        // ThingID 只是 defName + 編號，不會再跑進出錯的模組程式。
        string who = pawn?.ThingID ?? "a pawn";
        Log.WarningOnce($"RimAgent: could not read {part} of {who}, most likely because of another mod; skipped it. {ex}",
StringComparer.Ordinal.GetHashCode(("RimAgent:" + part + ":" + who)));
    }

    private static string SafeReport(Pawn pawn)
    {
        try
        {
            return pawn.jobs?.curDriver?.GetReport() ?? pawn.CurJob?.def.label ?? string.Empty;
        }
        catch (Exception)
        {
            // 某些 JobDriver 的 GetReport 會在狀態不完整時丟例外；活動描述只是輔助資訊。
            return pawn.CurJob?.def.label ?? string.Empty;
        }
    }

    private static bool IsNearMentalBreak(Pawn pawn)
    {
        Need_Mood mood = pawn.needs?.mood;
        MentalBreaker breaker = pawn.mindState?.mentalBreaker;
        return mood != null && breaker != null && mood.CurLevel < breaker.BreakThresholdMinor;
    }

    private static ThingKind? KindOf(Thing thing)
    {
        if (thing is Pawn) return ThingKind.Pawn;
        if (thing is Plant) return ThingKind.Plant;
        if (thing.def.mineable) return ThingKind.Mineable;
        if (thing is Building) return ThingKind.Building;
        if (thing.def.category == ThingCategory.Item) return ThingKind.Item;
        return null;
    }

    private static string RoyalStatusOf(Pawn pawn)
    {
        Faction empire = Faction.OfEmpire;
        if (pawn.royalty == null || empire == null) return string.Empty;
        RoyalTitleDef title = pawn.royalty.GetCurrentTitle(empire);
        int honor = pawn.royalty.GetFavor(empire);
        if (title == null && honor == 0) return string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{empire.Name}: {title?.GetLabelFor(pawn) ?? "no title"}, honor {honor}");
    }

    private static ThingInfo ToThing(Thing thing, Map map)
    {
        var designations = map.designationManager.AllDesignationsOn(thing).Select(d => d.def.defName).ToList();
        if (thing.def.mineable)
        {
            designations.AddRange(map.designationManager.AllDesignationsAt(thing.Position).Select(d => d.def.defName));
        }
        return new ThingInfo
        {
            Id = thing.thingIDNumber,
            DefName = thing.def.defName,
            Label = thing.LabelCap,
            Kind = KindOf(thing) ?? (thing is Blueprint ? ThingKind.Building : ThingKind.Any),
            X = thing.Position.x,
            Z = thing.Position.z,
            StackCount = thing.stackCount,
            PlayerOwned = thing.Faction == Faction.OfPlayer,
            Harvestable = (thing as Plant)?.HarvestableNow ?? false,
            Wild = thing is Pawn pawn && IsWildAnimal(pawn),
            Forbidden = thing.IsForbidden(Faction.OfPlayer),
            Designations = [.. designations.Distinct(StringComparer.Ordinal)]
        };
    }

    private static Pawn FindColonist(int id)
    {
        return Map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.thingIDNumber == id);
    }

    private static Thing FindThing(int id)
    {
        // 迷霧中的東西玩家看不到，不能被指名操作。
        return Map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == id && t.Spawned && !t.Fogged());
    }
}
