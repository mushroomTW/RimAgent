using System.Collections.Generic;

namespace RimAgent.Agent
{
    /// <summary>一次工作優先級修改的反操作：改回舊值。</summary>
    public sealed class UndoWorkPriority
    {
        public int PawnId { get; set; }
        public string WorkTypeDefName { get; set; } = string.Empty;
        public int OldPriority { get; set; }
    }

    /// <summary>一個小時作息修改的反操作：改回舊指派。</summary>
    public sealed class UndoTimeAssignment
    {
        public int PawnId { get; set; }
        public int Hour { get; set; }
        public string OldAssignmentDefName { get; set; } = string.Empty;
    }

    /// <summary>
    /// 一句指令改過的工作設定：送出時開框、工具記錄舊值、結束時整框推進堆疊。
    /// 復原時整框倒著 replay，把每個值寫回去。
    /// </summary>
    public sealed class UndoFrame
    {
        public string Label { get; set; } = string.Empty;
        public List<UndoWorkPriority> Priorities { get; } = new List<UndoWorkPriority>();
        public List<UndoTimeAssignment> Assignments { get; } = new List<UndoTimeAssignment>();

        public bool IsEmpty => Priorities.Count == 0 && Assignments.Count == 0;
    }

    /// <summary>
    /// 工作設定復原堆疊：只記工作優先級與時間表（TODO 指定的至少範圍），
    /// 深度上限 20 框，超過丟掉最舊的。鎖保護：記錄發生在主線程的工具裡，
    /// 開框與復原發生在送指令的執行緒。
    /// </summary>
    public sealed class WorkUndoStack
    {
        public const int MaxFrames = 20;

        private readonly object _gate = new object();
        private readonly List<UndoFrame> _frames = new List<UndoFrame>();
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

        public int Depth
        {
            get
            {
                lock (_gate)
                {
                    return _frames.Count;
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
    }
}
