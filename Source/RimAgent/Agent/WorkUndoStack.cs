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

    public void ExposeData()
    {
        int pawnId = PawnId;
        string workType = WorkTypeDefName;
        int oldPriority = OldPriority;
        Scribe_Values.Look(ref pawnId, "pawnId");
        Scribe_Values.Look(ref workType, "workTypeDefName");
        Scribe_Values.Look(ref oldPriority, "oldPriority");
        PawnId = pawnId;
        WorkTypeDefName = workType ?? string.Empty;
        OldPriority = oldPriority;
    }
}

/// <summary>一個小時作息修改的反操作：改回舊指派。</summary>
public sealed class UndoTimeAssignment : IExposable
{
    public int PawnId { get; set; }
    public int Hour { get; set; }
    public string OldAssignmentDefName { get; set; } = string.Empty;

    public void ExposeData()
    {
        int pawnId = PawnId;
        int hour = Hour;
        string assignment = OldAssignmentDefName;
        Scribe_Values.Look(ref pawnId, "pawnId");
        Scribe_Values.Look(ref hour, "hour");
        Scribe_Values.Look(ref assignment, "oldAssignmentDefName");
        PawnId = pawnId;
        Hour = hour;
        OldAssignmentDefName = assignment ?? string.Empty;
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

    public bool IsEmpty => Priorities.Count == 0 && Assignments.Count == 0;

    public void ExposeData()
    {
        string label = Label;
        List<UndoWorkPriority> priorities = Priorities;
        List<UndoTimeAssignment> assignments = Assignments;
        Scribe_Values.Look(ref label, "label");
        Scribe_Collections.Look(ref priorities, "priorities", LookMode.Deep);
        Scribe_Collections.Look(ref assignments, "assignments", LookMode.Deep);
        Label = label ?? string.Empty;
        Priorities = priorities ?? [];
        Assignments = assignments ?? [];
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

    public void RecordPriority(int pawnId, string workTypeDefName, int oldPriority)
    {
        lock (_gate)
        {
            if (_open == null) return;
            _open.Priorities.Add(new UndoWorkPriority
            {
                PawnId = pawnId,
                WorkTypeDefName = workTypeDefName,
                OldPriority = oldPriority
            });
        }
    }

    public void RecordAssignment(int pawnId, int hour, string oldAssignmentDefName)
    {
        lock (_gate)
        {
            if (_open == null) return;
            _open.Assignments.Add(new UndoTimeAssignment
            {
                PawnId = pawnId,
                Hour = hour,
                OldAssignmentDefName = oldAssignmentDefName
            });
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
            return [.. _frames.Select(Clone)];
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
            Priorities = [.. frame.Priorities.Select(p => new UndoWorkPriority
            {
                PawnId = p.PawnId, WorkTypeDefName = p.WorkTypeDefName, OldPriority = p.OldPriority
            })],
            Assignments = [.. frame.Assignments.Select(a => new UndoTimeAssignment
            {
                PawnId = a.PawnId, Hour = a.Hour, OldAssignmentDefName = a.OldAssignmentDefName
            })]
        };
    }
}
