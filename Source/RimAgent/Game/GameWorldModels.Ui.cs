using System;
using System.Collections.Generic;

namespace RimAgent.Game;

// IGameWorld 共用資料型別：待處理選擇、Gizmo 與浮動選單。
// 與 GameWorldModels.cs 相同：刻意不含任何 Verse / RimWorld 型別。

/// <summary>等玩家回應的抉擇：還沒處理的抉擇信件，或開著的對話框（id 以 letter-／dialog- 開頭）。</summary>
public sealed class PendingChoiceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    /// <summary>信件還剩幾 tick 過期；不會過期為 null。</summary>
    public int? ExpiresInTicks { get; set; }
    public IReadOnlyList<ChoiceOptionInfo> Options { get; set; } = Array.Empty<ChoiceOptionInfo>();
    /// <summary>回答時要附的輸入格式（世界格子、地圖目標、選取項目）；不用輸入是空字串。</summary>
    public string InputHint { get; set; } = string.Empty;
}

/// <summary>選取物件時畫面下方的指令按鈕（gizmo）。</summary>
public sealed class GizmoInfo
{
    /// <summary>action（按一下）、toggle（開關）、target（要在地圖上選目標）或 unsupported（要從選單挑或需要其他輸入）。</summary>
    public const string ActionKind = "action";
    public const string ToggleKind = "toggle";
    /// <summary>按下後要在地圖上選目標（物件或格子）；可接受的目標見 <see cref="TargetHint"/>。</summary>
    public const string TargetKind = "target";
    public const string UnsupportedKind = "unsupported";

    /// <summary>1 起的序號，照遊戲裡的排列。</summary>
    public int Number { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Kind { get; set; } = ActionKind;
    /// <summary>開關目前是否開著（只對 toggle 有意義）。</summary>
    public bool On { get; set; }
    /// <summary>target 按鈕可接受的目標種類（遊戲的 TargetingParameters 白話），例如「a cell, a colonist」。</summary>
    public string TargetHint { get; set; } = string.Empty;
    public bool Disabled { get; set; }
    public string DisabledReason { get; set; } = string.Empty;
}

/// <summary>命令指向的地圖目標：一個物件（ThingId）或一個格子（X、Z）。</summary>
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

/// <summary>右鍵選單（FloatMenu）的一個選項。</summary>
public sealed class FloatMenuOptionInfo
{
    /// <summary>1 起的序號，照原版選單排序（優先度高者在前，停用者在最後）。</summary>
    public int Number { get; set; }
    /// <summary>選項文字；停用的選項原因就寫在文字裡（原版慣例，例如「Use X (no path)」）。</summary>
    public string Label { get; set; } = string.Empty;
    public bool Disabled { get; set; }
}

/// <summary>某位殖民者對某個目標按右鍵會得到的選單。</summary>
public sealed class FloatMenuInfo
{
    /// <summary>非空代表選單做不出來（殖民者無法下令、目標不存在…），內容是遊戲給的原因。</summary>
    public string Error { get; set; } = string.Empty;
    public string PawnName { get; set; } = string.Empty;
    /// <summary>目標的白話描述（物件標籤或格子座標）。</summary>
    public string TargetLabel { get; set; } = string.Empty;
    /// <summary>殖民者目前是否徵召：徵召與否會換一批選項。</summary>
    public bool Drafted { get; set; }
    public IReadOnlyList<FloatMenuOptionInfo> Options { get; set; } = Array.Empty<FloatMenuOptionInfo>();
}

public sealed class ChoiceOptionInfo
{
    /// <summary>1 起的選項序號（資訊卡連結不列出，序號仍照遊戲裡的位置）。</summary>
    public int Number { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool Disabled { get; set; }
    public string DisabledReason { get; set; } = string.Empty;
}
