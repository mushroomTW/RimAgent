using System;
using System.Collections.Generic;

namespace RimAgent.Game;

// IGameWorld 共用資料型別：工作台、帳單、區域、動物、囚犯與健康。
// 與 GameWorldModels.cs 相同：刻意不含任何 Verse / RimWorld 型別。

/// <summary>一張可接帳單的工作台（遊戲內有 BillStack 的建築）。</summary>
public sealed class WorkTableInfo
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public int X { get; set; }
    public int Z { get; set; }
    public int BillCount { get; set; }
}

/// <summary>工作台上的一張生產帳單。Index 是當次列表序號（1 起），刪單前重查。</summary>
public sealed class BillInfo
{
    public int Index { get; set; }
    public string Label { get; set; } = string.Empty;
    /// <summary>重複方式顯示，如 3x、until 10、forever。</summary>
    public string Repeat { get; set; } = string.Empty;
}

/// <summary>地圖上的一個區域（儲藏區、種植區等）。</summary>
public sealed class ZoneInfo
{
    /// <summary>ZoneManager 配發的編號（查詢工具回傳的不透明編號，指名區域用它）。</summary>
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    /// <summary>Stockpile / Growing 等。</summary>
    public string Kind { get; set; } = string.Empty;
    public int CellCount { get; set; }
    /// <summary>涵蓋區域所有格子的最小矩形。</summary>
    public CellRectSpec Extents { get; set; }
}

/// <summary>地圖上的一隻動物（殖民地或野生）。</summary>
public sealed class AnimalInfo
{
    public int Id { get; set; }
    /// <summary>顯示名（命名動物為名字，否則為物種）。</summary>
    public string Name { get; set; } = string.Empty;
    public string SpeciesDefName { get; set; } = string.Empty;
    public string Species { get; set; } = string.Empty;
    /// <summary>是否野生（非殖民地所有）。</summary>
    public bool Wild { get; set; }
    /// <summary>活動區域；空字串代表無限制。</summary>
    public string Area { get; set; } = string.Empty;
    public float HealthPercent { get; set; } = 1f;
    public bool Downed { get; set; }
}

/// <summary>一位殖民地囚犯。</summary>
public sealed class PrisonerInfo
{
    public int Id { get; set; }
    /// <summary>顯示用短名（綽號優先）。</summary>
    public string Name { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    /// <summary>剩餘抗拒值；負值代表無需勸降（可直接招募）。</summary>
    public float Resistance { get; set; } = -1f;
    public bool Recruitable { get; set; }
    /// <summary>目前互動模式 defName（如 AttemptRecruit）。</summary>
    public string InteractionMode { get; set; } = string.Empty;
    public string InteractionModeLabel { get; set; } = string.Empty;
    public float HealthPercent { get; set; } = 1f;
    public bool Downed { get; set; }
}

/// <summary>排一台手術：把一張 Bill_Medical 掛進殖民者的 BillStack，等醫生執行。</summary>
public sealed class SurgeryRequest
{
    public int PawnId { get; set; }
    public string RecipeDefName { get; set; } = string.Empty;
    /// <summary>部位顯示名；空字串由實作端選第一個適用部位（不需部位的手術忽略）。</summary>
    public string BodyPartLabel { get; set; } = string.Empty;
}

/// <summary>一條健康異常：傷口、疾病或缺失部位。</summary>
public sealed class HealthConditionInfo
{
    /// <summary>外傷（割傷、槍傷、燒傷等）。</summary>
    public const string InjuryKind = "Injury";
    /// <summary>缺失的身體部位。</summary>
    public const string MissingKind = "Missing";
    /// <summary>其他（疾病、慢性病、植入物副作用等）。</summary>
    public const string ConditionKind = "Condition";

    public string Label { get; set; } = string.Empty;
    /// <summary>部位顯示名；空字串代表全身。</summary>
    public string Part { get; set; } = string.Empty;
    public float Severity { get; set; }
    /// <summary>見 <see cref="InjuryKind"/>／<see cref="MissingKind"/>／<see cref="ConditionKind"/>。</summary>
    public string Kind { get; set; } = ConditionKind;
    public bool Bleeding { get; set; }
    public bool Tended { get; set; }
    public bool NeedsTending { get; set; }
}

/// <summary>已排上但還沒動的手術。</summary>
public sealed class SurgeryBillInfo
{
    public string Label { get; set; } = string.Empty;
    /// <summary>部位顯示名；空字串代表全身。</summary>
    public string Part { get; set; } = string.Empty;
    public bool Suspended { get; set; }
}

/// <summary>一位殖民者的健康狀況。</summary>
public sealed class ColonistHealthInfo
{
    public int PawnId { get; set; }
    public IReadOnlyList<HealthConditionInfo> Conditions { get; set; } = Array.Empty<HealthConditionInfo>();
    public IReadOnlyList<SurgeryBillInfo> PendingSurgeries { get; set; } = Array.Empty<SurgeryBillInfo>();
}
