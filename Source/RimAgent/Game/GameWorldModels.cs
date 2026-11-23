#nullable disable

using System;
using System.Collections.Generic;
using RimAgent.Tools;

namespace RimAgent.Game;

// IGameWorld 兩側共用的純資料型別。刻意不含任何 Verse / RimWorld 型別，
// 讓工具邏輯與測試都不需要載入遊戲組件。

public sealed class ColonyOverview
{
    public string ColonyName { get; set; } = string.Empty;
    public int ColonistCount { get; set; }
    public int Year { get; set; }
    public string Season { get; set; } = string.Empty;
    public int DayOfSeason { get; set; }
    public int Hour { get; set; }
    public string Weather { get; set; } = string.Empty;
    public float OutdoorTemperatureC { get; set; }
    public float Wealth { get; set; }
    public int HostileCount { get; set; }
    public int ColonistsNearMentalBreak { get; set; }
    public IReadOnlyList<string> Factions { get; set; } = [];
}

public sealed class SkillInfo
{
    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Level { get; set; }
    /// <summary>0 = 無熱情，1 = 小熱情，2 = 大熱情。</summary>
    public int Passion { get; set; }
    public bool Disabled { get; set; }
}

public sealed class WorkPriorityInfo
{
    public string WorkTypeDefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    /// <summary>0 = 不做；1 最高 … 4 最低。</summary>
    public int Priority { get; set; }
    public bool Disabled { get; set; }
}

public sealed class ColonistInfo
{
    public string RoyalStatus { get; set; } = string.Empty;
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public int Age { get; set; }
    public int X { get; set; }
    public int Z { get; set; }
    public float HealthPercent { get; set; }
    public bool Downed { get; set; }
    public float MoodPercent { get; set; }
    public float MinorBreakThreshold { get; set; }
    public bool InMentalState { get; set; }
    public string MentalStateLabel { get; set; } = string.Empty;
    public string CurrentActivity { get; set; } = string.Empty;
    public bool Drafted { get; set; }
    public string MedicalCare { get; set; } = string.Empty;
    public string Equipment { get; set; } = string.Empty;
    public IReadOnlyList<NeedInfo> Needs { get; set; } = [];
    public IReadOnlyList<string> Thoughts { get; set; } = [];
    public IReadOnlyList<SkillInfo> Skills { get; set; } = [];
    public IReadOnlyList<WorkPriorityInfo> WorkPriorities { get; set; } = [];
    public IReadOnlyList<string> Timetable { get; set; } = [];
    /// <summary>因其他模組丟例外而讀不到的段落（如 <see cref="WorkPrioritiesPart"/>）；這些欄位是空的，不代表「沒有」。</summary>
    public IReadOnlyList<string> UnreadableParts { get; set; } = [];

    public const string WorkPrioritiesPart = "work priorities";
}

public sealed class NeedInfo
{
    public string Label { get; set; } = string.Empty;
    public float Percent { get; set; }
}

public sealed class ResourceInfo
{
    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
    public string Category { get; set; } = string.Empty;
}

public sealed class ResourceSnapshot
{
    public IReadOnlyList<ResourceInfo> Items { get; set; } = [];
    public float TotalHumanEdibleNutrition { get; set; }
}

public sealed class AlertInfo
{
    public string Label { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
}

public sealed class LetterInfo
{
    public string Label { get; set; } = string.Empty;
}

public sealed class QuestInfo
{
    public const string AvailableState = "available";
    public const string ActiveState = "active";

    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string State { get; set; } = AvailableState;
    public bool Dismissed { get; set; }
    public string Description { get; set; } = string.Empty;
    public int? ExpiresInTicks { get; set; }
    public IReadOnlyList<string> Deadlines { get; set; } = [];
    public string AccepterName { get; set; } = string.Empty;
    public bool CanAccept { get; set; } = true;
    public IReadOnlyList<string> UnmetRequirements { get; set; } = [];
    public IReadOnlyList<QuestRewardInfo> RewardOptions { get; set; } = [];
    public bool RequiresAccepter { get; set; }
    public IReadOnlyList<QuestAccepterInfo> Accepters { get; set; } = [];
}

public sealed class QuestRewardInfo
{
    public int Number { get; set; }
    public IReadOnlyList<string> Rewards { get; set; } = [];
    public bool RequiresAccepter { get; set; }
}

public sealed class QuestAccepterInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Warning { get; set; } = string.Empty;
}

public sealed class PowerInfo
{
    public int NetworkCount { get; set; }
    public float NetWatts { get; set; }
    public float StoredEnergy { get; set; }
}

public sealed class ThreatInfo
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Faction { get; set; } = string.Empty;
    public int X { get; set; }
    public int Z { get; set; }
    public bool Downed { get; set; }
}

public sealed class TimeSpeedInfo
{
    public string Current { get; set; } = "Normal";
    public IReadOnlyList<DefInfo> Available { get; set; } = [];
}

/// <summary>玩家可按鍵切到的四檔速度目錄；Ultrafast 只留給開發模式。正式與假世界共用同一份。</summary>
public static class TimeSpeedDirectory
{
    public static readonly IReadOnlyList<DefInfo> Default =
    [
        new DefInfo("Paused", "Paused"), new DefInfo("Normal", "Normal"),
        new DefInfo("Fast", "Fast"), new DefInfo("Superfast", "Superfast")
    ];
}

public sealed class ResearchProjectInfo
{
    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public float ProgressPercent { get; set; }
    public float Cost { get; set; }
}

public sealed class ResearchInfo
{
    public ResearchProjectInfo Current { get; set; }
    public IReadOnlyList<ResearchProjectInfo> Available { get; set; } = [];
}

public sealed class CommandResult : Result<string>
{
    private CommandResult(bool success, string message) : base(success, message, message)
    {
    }

    public string Message => Value ?? string.Empty;

    /// <summary>放置失敗是因為位置；換個地方可能就放得下（研究、材料等失敗不算）。</summary>
    public bool PlacementRefused { get; private init; }

    public static CommandResult RefusePlacement(string message)
    {
        return new CommandResult(success: false, message) { PlacementRefused = true };
    }

    public static new CommandResult Ok(string message = "")
    {
        return new CommandResult(success: true, message);
    }

    public static new CommandResult Fail(string message)
    {
        return new CommandResult(success: false, message);
    }
}

public sealed class ScreenshotInfo
{
    public bool Success { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string Error { get; set; } = string.Empty;
    public MapShotLayout Layout { get; set; }
}

public sealed class SaveContext
{
    public string ColonyId { get; set; } = string.Empty;
    public int Tick { get; set; }
    public bool Permadeath { get; set; }
    public string SavingBlockedReason { get; set; }
}

public sealed class SaveSlotInfo
{
    public string Name { get; set; } = string.Empty;
    public bool Checkpoint { get; set; }
    public string ColonyId { get; set; } = string.Empty;
    public int Tick { get; set; }
    /// <summary>檔案最後寫入時間（UTC ticks）：autosave 會輪流覆寫，名稱加上它才是同一份存檔。</summary>
    public long WrittenUtcTicks { get; set; }
}
