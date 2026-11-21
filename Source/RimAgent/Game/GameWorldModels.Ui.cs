using System;
using System.Collections.Generic;

namespace RimAgent.Game;


public sealed class PendingChoiceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public int? ExpiresInTicks { get; set; }
    public IReadOnlyList<ChoiceOptionInfo> Options { get; set; } = [];
    public string InputHint { get; set; } = string.Empty;
}

public sealed class GizmoInfo
{
    public const string ActionKind = "action";
    public const string ToggleKind = "toggle";
    public const string TargetKind = "target";
    public const string UnsupportedKind = "unsupported";

    public int Number { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Kind { get; set; } = ActionKind;
    public bool On { get; set; }
    public string TargetHint { get; set; } = string.Empty;
    public bool Disabled { get; set; }
    public string DisabledReason { get; set; } = string.Empty;
}

public sealed class MapTarget
{
    public int ThingId { get; set; }
    public int X { get; set; }
    public int Z { get; set; }
    public bool IsCell { get; set; }

    public static MapTarget OfThing(int thingId)
    {
        return new MapTarget { ThingId = thingId };
    }

    public static MapTarget OfCell(int x, int z)
    {
        return new MapTarget { IsCell = true, X = x, Z = z };
    }

    public override string ToString()
    {
        return IsCell ? "cell (" + X + "," + Z + ")" : "[" + ThingId + "]";
    }
}

public sealed class FloatMenuOptionInfo
{
    /// <summary>1 起的序號，照原版選單排序（優先度高者在前，停用者在最後）。</summary>
    public int Number { get; set; }
    /// <summary>選項文字；停用的選項原因就寫在文字裡（原版慣例，例如「Use X (no path)」）。</summary>
    public string Label { get; set; } = string.Empty;
    public bool Disabled { get; set; }
}

public sealed class FloatMenuInfo
{
    public string Error { get; set; } = string.Empty;
    public string PawnName { get; set; } = string.Empty;
    public string TargetLabel { get; set; } = string.Empty;
    /// <summary>殖民者目前是否徵召：徵召與否會換一批選項。</summary>
    public bool Drafted { get; set; }
    public IReadOnlyList<FloatMenuOptionInfo> Options { get; set; } = [];
}

public sealed class ChoiceOptionInfo
{
    /// <summary>1 起的選項序號（資訊卡連結不列出，序號仍照遊戲裡的位置）。</summary>
    public int Number { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool Disabled { get; set; }
    public string DisabledReason { get; set; } = string.Empty;
}
