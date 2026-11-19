using System;
using System.Collections.Generic;
using RimAgent.Tools;

namespace RimAgent.Game;

// IGameWorld 兩側共用的純資料型別。刻意不含任何 Verse / RimWorld 型別，
// 讓工具邏輯與測試都不需要載入遊戲組件。

/// <summary>殖民地一眼看盡的摘要。</summary>
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
    /// <summary>心情已低於輕微崩潰門檻的殖民者人數。</summary>
    public int ColonistsNearMentalBreak { get; set; }
    /// <summary>看得到的派系與對玩家的好感。</summary>
    public IReadOnlyList<string> Factions { get; set; } = Array.Empty<string>();
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
    /// <summary>帝國頭銜與榮譽；沒有是空字串。</summary>
    public string RoyalStatus { get; set; } = string.Empty;
    public int Id { get; set; }
    /// <summary>顯示用短名（綽號優先）。</summary>
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
    /// <summary>醫療等級（MedicalCareCategory 名稱，如 Best）。</summary>
    public string MedicalCare { get; set; } = string.Empty;
    public string Equipment { get; set; } = string.Empty;
    public IReadOnlyList<NeedInfo> Needs { get; set; } = Array.Empty<NeedInfo>();
    public IReadOnlyList<string> Thoughts { get; set; } = Array.Empty<string>();
    public IReadOnlyList<SkillInfo> Skills { get; set; } = Array.Empty<SkillInfo>();
    public IReadOnlyList<WorkPriorityInfo> WorkPriorities { get; set; } = Array.Empty<WorkPriorityInfo>();
    /// <summary>24 個小時各自的 TimeAssignment defName（索引即小時）。</summary>
    public IReadOnlyList<string> Timetable { get; set; } = Array.Empty<string>();
    /// <summary>因其他模組丟例外而讀不到的段落（如 <see cref="WorkPrioritiesPart"/>）；這些欄位是空的，不代表「沒有」。</summary>
    public IReadOnlyList<string> UnreadableParts { get; set; } = Array.Empty<string>();

    public const string WorkPrioritiesPart = "work priorities";
}

/// <summary>殖民者需求的遊戲原始讀數；百分比為 0 到 1。</summary>
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
    public IReadOnlyList<ResourceInfo> Items { get; set; } = Array.Empty<ResourceInfo>();
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

/// <summary>任務分頁裡的一個任務：可接受（含被拒絕的）或進行中。</summary>
public sealed class QuestInfo
{
    public const string AvailableState = "available";
    public const string ActiveState = "active";

    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>available（還沒接受）或 active（已接受、進行中）。</summary>
    public string State { get; set; } = AvailableState;
    /// <summary>玩家在任務分頁按過拒絕（可再取消）；只對 available 有意義。</summary>
    public bool Dismissed { get; set; }
    /// <summary>純文字描述（標籤已去除，含各任務段落附加的說明）。</summary>
    public string Description { get; set; } = string.Empty;
    /// <summary>還剩幾 tick 過期（只對 available）；沒有期限為 null。</summary>
    public int? ExpiresInTicks { get; set; }
    /// <summary>進行中任務的完成期限，遊戲給的原文（例如「剩 3 天」）；沒有則為空。</summary>
    public IReadOnlyList<string> Deadlines { get; set; } = Array.Empty<string>();
    /// <summary>已接受者的名字（只對 active，且該任務有接受者）。</summary>
    public string AccepterName { get; set; } = string.Empty;
    /// <summary>遊戲目前是否允許接受（只對 available）。</summary>
    public bool CanAccept { get; set; } = true;
    /// <summary>接受條件不滿足的原因，遊戲給的原文；CanAccept 為 false 時至少有一筆。</summary>
    public IReadOnlyList<string> UnmetRequirements { get; set; } = Array.Empty<string>();
    /// <summary>獎勵選項；只有一組也算一個選項。沒有獎勵選擇的任務為空。</summary>
    public IReadOnlyList<QuestRewardInfo> RewardOptions { get; set; } = Array.Empty<QuestRewardInfo>();
    /// <summary>接受時要指名接受者（沒有獎勵選項的任務看這個；有選項的任務以各選項的 RequiresAccepter 為準）。</summary>
    public bool RequiresAccepter { get; set; }
    /// <summary>目前能當接受者的殖民者（只在任務需要接受者時列出）。</summary>
    public IReadOnlyList<QuestAccepterInfo> Accepters { get; set; } = Array.Empty<QuestAccepterInfo>();
}

/// <summary>任務的一組獎勵選項（接受時選其一）。</summary>
public sealed class QuestRewardInfo
{
    /// <summary>1 起的序號。</summary>
    public int Number { get; set; }
    public IReadOnlyList<string> Rewards { get; set; } = Array.Empty<string>();
    /// <summary>選了這一組之後，接受時是否要指名接受者。</summary>
    public bool RequiresAccepter { get; set; }
}

/// <summary>可以接受某個任務的殖民者。</summary>
public sealed class QuestAccepterInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    /// <summary>遊戲在確認框提醒的風險（個性、社交能力等），沒有則為空。</summary>
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
    /// <summary>HostilePawn / HostileAnimal / Manhunter / Mechanoid / Entity / Fire。</summary>
    public string Kind { get; set; } = string.Empty;
    public string Faction { get; set; } = string.Empty;
    public int X { get; set; }
    public int Z { get; set; }
    public bool Downed { get; set; }
}

/// <summary>遊戲時間流速：目前速度與可切換的速度目錄。</summary>
public sealed class TimeSpeedInfo
{
    /// <summary>目前速度 defName（Paused／Normal／Fast／Superfast）。</summary>
    public string Current { get; set; } = "Normal";
    /// <summary>可切換的速度目錄。</summary>
    public IReadOnlyList<DefInfo> Available { get; set; } = Array.Empty<DefInfo>();
}

/// <summary>玩家可按鍵切到的四檔速度目錄；Ultrafast 只留給開發模式。正式與假世界共用同一份。</summary>
public static class TimeSpeedDirectory
{
    public static readonly IReadOnlyList<DefInfo> Default = new[]
    {
        new DefInfo("Paused", "Paused"), new DefInfo("Normal", "Normal"),
        new DefInfo("Fast", "Fast"), new DefInfo("Superfast", "Superfast")
    };
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
    /// <summary>null 代表目前沒有進行中的專案。</summary>
    public ResearchProjectInfo Current { get; set; }
    public IReadOnlyList<ResearchProjectInfo> Available { get; set; } = Array.Empty<ResearchProjectInfo>();
}

/// <summary>命令型操作的結果。失敗時 Message 說明原因，供工具轉述給模型。</summary>
public sealed class CommandResult : Result<string>
{
    private CommandResult(bool success, string message) : base(success, message, message)
    {
    }

    public string Message => Value ?? string.Empty;

    public static new CommandResult Ok(string message = "")
    {
        return new CommandResult(true, message);
    }

    public static new CommandResult Fail(string message)
    {
        return new CommandResult(false, message);
    }
}

/// <summary>遊戲截圖結果資料。</summary>
public sealed class ScreenshotInfo
{
    public bool Success { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string Error { get; set; } = string.Empty;
    /// <summary>地圖範圍截圖的版面（每格像素、格線間距）；截玩家畫面時為 null。</summary>
    public MapShotLayout Layout { get; set; }
}

/// <summary>存讀檔工具需要的這一局狀態。</summary>
public sealed class SaveContext
{
    /// <summary>這一局的 RimAgent 殖民地代號（寫在每份存檔裡，用來認出同一局的 autosave）。</summary>
    public string ColonyId { get; set; } = string.Empty;
    public int Tick { get; set; }
    /// <summary>死亡模式（只有一份會被覆寫的存檔）：不能存讀檔重來。</summary>
    public bool Permadeath { get; set; }
    /// <summary>現在不能存檔的原因（選地塊、對話框禁止存檔、過場中）；可以存時為 null。</summary>
    public string SavingBlockedReason { get; set; }
}

/// <summary>一份可能屬於這一局的存檔：RimAgent 檢查點或遊戲 autosave。</summary>
public sealed class SaveSlotInfo
{
    /// <summary>存檔名（不含副檔名），讀檔用。</summary>
    public string Name { get; set; } = string.Empty;
    public bool Checkpoint { get; set; }
    /// <summary>存檔裡記的殖民地代號；沒有代號（本功能之前的存檔）為空字串。</summary>
    public string ColonyId { get; set; } = string.Empty;
    /// <summary>存檔時的遊戲 tick。</summary>
    public int Tick { get; set; }
    /// <summary>檔案最後寫入時間（UTC ticks）：autosave 會輪流覆寫，名稱加上它才是同一份存檔。</summary>
    public long WrittenUtcTicks { get; set; }
}
