using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RimAgent.Agent;

/// <summary>一次工作優先級修改的反操作：改回舊值。</summary>
public sealed class UndoWorkPriority : IExposable
{
    public int PawnId { get; set; }
    public string WorkTypeDefName { get; set; } = string.Empty;
    public int OldPriority { get; set; }
    /// <summary>Agent 寫入的新值；復原前用來確認玩家沒有在這之後手動改過。-1 代表舊存檔沒有這個欄位。</summary>
    public int NewPriority { get; set; } = -1;

    public void ExposeData()
    {
        int pawnId = PawnId;
        string workType = WorkTypeDefName;
        int oldPriority = OldPriority;
        int newPriority = NewPriority;
        Scribe_Values.Look(ref pawnId, "pawnId");
        Scribe_Values.Look(ref workType, "workTypeDefName");
        Scribe_Values.Look(ref oldPriority, "oldPriority");
        Scribe_Values.Look(ref newPriority, "newPriority", -1);
        PawnId = pawnId;
        WorkTypeDefName = workType ?? string.Empty;
        OldPriority = oldPriority;
        NewPriority = newPriority;
    }
}

/// <summary>一個小時作息修改的反操作：改回舊指派。</summary>
public sealed class UndoTimeAssignment : IExposable
{
    public int PawnId { get; set; }
    public int Hour { get; set; }
    public string OldAssignmentDefName { get; set; } = string.Empty;
    /// <summary>Agent 寫入的新指派；復原前用來確認玩家沒有在這之後手動改過。空字串代表舊存檔沒有這個欄位。</summary>
    public string NewAssignmentDefName { get; set; } = string.Empty;

    public void ExposeData()
    {
        int pawnId = PawnId;
        int hour = Hour;
        string assignment = OldAssignmentDefName;
        string newAssignment = NewAssignmentDefName;
        Scribe_Values.Look(ref pawnId, "pawnId");
        Scribe_Values.Look(ref hour, "hour");
        Scribe_Values.Look(ref assignment, "oldAssignmentDefName");
        Scribe_Values.Look(ref newAssignment, "newAssignmentDefName", string.Empty);
        PawnId = pawnId;
        Hour = hour;
        OldAssignmentDefName = assignment ?? string.Empty;
        NewAssignmentDefName = newAssignment ?? string.Empty;
    }
}

/// <summary>
/// 一句指令改過的工作設定：送出時開框、工具記錄舊值、結束時整框推進堆疊。
/// 復原時整框倒著 replay，把每個值寫回去。
/// </summary>
public sealed class UndoFrame : IExposable
{
    public string Label { get; set; } = string.Empty;
    public List<UndoWorkPriority> Priorities { get; set; } = [];
    public List<UndoTimeAssignment> Assignments { get; set; } = [];
    /// <summary>Agent 改優先級前後的全域手動優先級設定；null 代表這一框沒有動到它（或舊存檔）。</summary>
    public bool? OldUseWorkPriorities { get; set; }
    public bool? NewUseWorkPriorities { get; set; }

    public bool IsEmpty => Priorities.Count == 0 && Assignments.Count == 0;

    public void ExposeData()
    {
        string label = Label;
        List<UndoWorkPriority> priorities = Priorities;
        List<UndoTimeAssignment> assignments = Assignments;
        bool hasOldUse = OldUseWorkPriorities.HasValue;
        bool oldUse = OldUseWorkPriorities ?? false;
        bool hasNewUse = NewUseWorkPriorities.HasValue;
        bool newUse = NewUseWorkPriorities ?? false;
        Scribe_Values.Look(ref label, "label");
        Scribe_Collections.Look(ref priorities, "priorities", LookMode.Deep);
        Scribe_Collections.Look(ref assignments, "assignments", LookMode.Deep);
        Scribe_Values.Look(ref hasOldUse, "hasOldUseWorkPriorities", false);
        Scribe_Values.Look(ref oldUse, "oldUseWorkPriorities", false);
        Scribe_Values.Look(ref hasNewUse, "hasNewUseWorkPriorities", false);
        Scribe_Values.Look(ref newUse, "newUseWorkPriorities", false);
        Label = label ?? string.Empty;
        Priorities = priorities ?? [];
        Assignments = assignments ?? [];
        OldUseWorkPriorities = hasOldUse ? oldUse : (bool?)null;
        NewUseWorkPriorities = hasNewUse ? newUse : (bool?)null;
    }
}

/// <summary>
/// 工作設定復原堆疊：只記工作優先級與時間表（規格說明的範圍），
/// 深度上限 20 框，超過丟掉最舊的。鎖保護：記錄發生在主線程的工具裡，
/// 開框與復原發生在送指令的執行緒。
/// </summary>
public sealed class WorkUndoStack
{
    public const int MaxFrames = 20;

    private readonly object _gate = new();
    private readonly List<UndoFrame> _frames = [];
    private UndoFrame _open;

    public bool CanUndo
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count > 0;
            }
        }
    }

    public void BeginFrame()
    {
        lock (_gate)
        {
            _open = new UndoFrame();
        }
    }

    public void RecordPriority(int pawnId, string workTypeDefName, int oldPriority, int newPriority)
    {
        lock (_gate)
        {
            if (_open == null) return;
            _open.Priorities.Add(new UndoWorkPriority
            {
                PawnId = pawnId,
                WorkTypeDefName = workTypeDefName,
                OldPriority = oldPriority,
                NewPriority = newPriority
            });
        }
    }

    public void RecordAssignment(int pawnId, int hour, string oldAssignmentDefName, string newAssignmentDefName)
    {
        lock (_gate)
        {
            if (_open == null) return;
            _open.Assignments.Add(new UndoTimeAssignment
            {
                PawnId = pawnId,
                Hour = hour,
                OldAssignmentDefName = oldAssignmentDefName,
                NewAssignmentDefName = newAssignmentDefName
            });
        }
    }

    /// <summary>記下這框造成的全域手動優先級變化；同一框只保留第一次的舊值與最後一次的新值。</summary>
    public void RecordUseWorkPriorities(bool oldValue, bool newValue)
    {
        lock (_gate)
        {
            if (_open == null) return;
            _open.OldUseWorkPriorities ??= oldValue;
            _open.NewUseWorkPriorities = newValue;
        }
    }

    /// <summary>收框：有記錄才推進堆疊，空框直接丟掉。</summary>
    public void EndFrame(string label)
    {
        lock (_gate)
        {
            UndoFrame frame = _open;
            _open = null;
            if (frame == null || frame.IsEmpty) return;
            frame.Label = label ?? string.Empty;
            _frames.Add(frame);
            while (_frames.Count > MaxFrames) _frames.RemoveAt(0);
        }
    }

    public UndoFrame Pop()
    {
        lock (_gate)
        {
            if (_frames.Count == 0) return null;
            UndoFrame frame = _frames[_frames.Count - 1];
            _frames.RemoveAt(_frames.Count - 1);
            return frame;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _frames.Clear();
            _open = null;
        }
    }

    public List<UndoFrame> Snapshot()
    {
        lock (_gate)
        {
            var snapshot = _frames.Select(Clone).ToList();
            if (_open != null && !_open.IsEmpty)
            {
                UndoFrame pending = Clone(_open);
                pending.Label = "執行中的指令";
                snapshot.Add(pending);
                if (snapshot.Count > MaxFrames) snapshot.RemoveAt(0);
            }
            return snapshot;
        }
    }

    public void Restore(IEnumerable<UndoFrame> frames)
    {
        lock (_gate)
        {
            _frames.Clear();
            if (frames == null) return;
            foreach (UndoFrame frame in frames.Where(f => f != null && !f.IsEmpty).Take(MaxFrames))
            {
                _frames.Add(Clone(frame));
            }
        }
    }

    private static UndoFrame Clone(UndoFrame frame)
    {
        return new UndoFrame
        {
            Label = frame.Label,
            OldUseWorkPriorities = frame.OldUseWorkPriorities,
            NewUseWorkPriorities = frame.NewUseWorkPriorities,
            Priorities = [.. frame.Priorities.Select(p => new UndoWorkPriority
            {
                PawnId = p.PawnId, WorkTypeDefName = p.WorkTypeDefName, OldPriority = p.OldPriority,
                NewPriority = p.NewPriority
            })],
            Assignments = [.. frame.Assignments.Select(a => new UndoTimeAssignment
            {
                PawnId = a.PawnId, Hour = a.Hour, OldAssignmentDefName = a.OldAssignmentDefName,
                NewAssignmentDefName = a.NewAssignmentDefName
            })]
        };
    }
}
