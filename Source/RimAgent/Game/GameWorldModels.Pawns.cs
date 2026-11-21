using System;
using System.Collections.Generic;

namespace RimAgent.Game;


public sealed class WorkTableInfo
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public int X { get; set; }
    public int Z { get; set; }
    public int BillCount { get; set; }
}

public sealed class BillInfo
{
    public int Index { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Repeat { get; set; } = string.Empty;
}

public sealed class ZoneInfo
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int CellCount { get; set; }
    public CellRectSpec Extents { get; set; }
}

public sealed class AnimalInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SpeciesDefName { get; set; } = string.Empty;
    public string Species { get; set; } = string.Empty;
    public bool Wild { get; set; }
    public string Area { get; set; } = string.Empty;
    public float HealthPercent { get; set; } = 1f;
    public bool Downed { get; set; }
}

public sealed class PrisonerInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    /// <summary>剩餘抗拒值；負值代表無需勸降（可直接招募）。</summary>
    public float Resistance { get; set; } = -1f;
    public bool Recruitable { get; set; }
    public string InteractionMode { get; set; } = string.Empty;
    public string InteractionModeLabel { get; set; } = string.Empty;
    public float HealthPercent { get; set; } = 1f;
    public bool Downed { get; set; }
}

public sealed class SurgeryRequest
{
    public int PawnId { get; set; }
    public string RecipeDefName { get; set; } = string.Empty;
    /// <summary>部位顯示名；空字串由實作端選第一個適用部位（不需部位的手術忽略）。</summary>
    public string BodyPartLabel { get; set; } = string.Empty;
}

public sealed class HealthConditionInfo
{
    public const string InjuryKind = "Injury";
    public const string MissingKind = "Missing";
    public const string ConditionKind = "Condition";

    public string Label { get; set; } = string.Empty;
    public string Part { get; set; } = string.Empty;
    public float Severity { get; set; }
    public string Kind { get; set; } = ConditionKind;
    public bool Bleeding { get; set; }
    public bool Tended { get; set; }
    public bool NeedsTending { get; set; }
}

public sealed class SurgeryBillInfo
{
    public string Label { get; set; } = string.Empty;
    public string Part { get; set; } = string.Empty;
    public bool Suspended { get; set; }
}

public sealed class ColonistHealthInfo
{
    public int PawnId { get; set; }
    public IReadOnlyList<HealthConditionInfo> Conditions { get; set; } = [];
    public IReadOnlyList<SurgeryBillInfo> PendingSurgeries { get; set; } = [];
}
