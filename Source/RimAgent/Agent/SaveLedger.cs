using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimAgent.Game;

namespace RimAgent.Agent;

public sealed class ReloadRecord
{
    public string ColonyId { get; set; } = string.Empty;
    public string SaveName { get; set; } = string.Empty;
    public int Attempt { get; set; }
    public string Lesson { get; set; } = string.Empty;
}

/// <summary>讀檔會換掉整場遊戲（連同存檔裡的對話與記憶），所以放在遊戲行程內的共用實例，跨讀檔存活；重開遊戲就歸零。同一份存檔（名稱＋寫入時間：autosave 會輪流覆寫）最多讀 <see cref="MaxLoadsPerSave"/> 次；用完之後，同一局不比它新的存檔也鎖住——不再往更早退，接受損失繼續玩。</summary>
public sealed class SaveLedger
{
    public const int MaxLoadsPerSave = 3;

    public static SaveLedger Shared { get; } = new();

    private readonly System.Threading.Lock _gate = new();
    private readonly Dictionary<string, int> _loads = new(StringComparer.Ordinal);
    /// <summary>讀回較早的存檔時，比它新的存檔屬於被放棄的那條時間線，不能再讀。</summary>
    private readonly HashSet<string> _abandoned = new(StringComparer.Ordinal);
    private readonly List<ReloadRecord> _history = [];
    private ReloadRecord? _pending;
    private readonly List<SaveSlotInfo> _toDiscard = [];

    private static string KeyOf(SaveSlotInfo save) => save.Name + "@" + save.WrittenUtcTicks.ToString(CultureInfo.InvariantCulture);

    public int LoadsOf(SaveSlotInfo save)
    {
        lock (_gate) return _loads.TryGetValue(KeyOf(save), out int n) ? n : 0;
    }

    public bool IsAbandoned(SaveSlotInfo save)
    {
        lock (_gate) return _abandoned.Contains(KeyOf(save));
    }

    public SaveSlotInfo? ExhaustedBlocker(SaveSlotInfo save, IEnumerable<SaveSlotInfo> colonySaves)
    {
        lock (_gate)
        {
            return colonySaves
                .Where(s => s.Tick >= save.Tick && LoadsUnlocked(s) >= MaxLoadsPerSave)
                .OrderBy(s => s.Tick)
                .FirstOrDefault();
        }
    }

    /// <summary>記下一次讀檔並回傳這是第幾次；比它新的存檔標為放棄，並留給讀檔後的新遊戲接手。</summary>
    public int RecordLoad(SaveSlotInfo save, IEnumerable<SaveSlotInfo> colonySaves, string? colonyId, string? lesson)
    {
        lock (_gate)
        {
            int attempt = LoadsUnlocked(save) + 1;
            _loads[KeyOf(save)] = attempt;
            foreach (SaveSlotInfo newer in colonySaves.Where(s => s.Tick > save.Tick)) _abandoned.Add(KeyOf(newer));
            var record = new ReloadRecord
            {
                ColonyId = colonyId ?? string.Empty,
                SaveName = save.Name,
                Attempt = attempt,
                Lesson = lesson ?? string.Empty
            };
            _toDiscard.AddRange(colonySaves.Where(s => s.Checkpoint && s.Tick > save.Tick));
            _history.Add(record);
            _pending = record;
            return attempt;
        }
    }

    /// <summary>讀檔完成後取走剛才那次讀檔；別局（玩家中途自己讀了別的檔）的紀錄直接丟掉。</summary>
    public ReloadRecord? TakePending(string? colonyId)
    {
        lock (_gate)
        {
            ReloadRecord? pending = _pending;
            _pending = null;
            return pending != null && string.Equals(pending.ColonyId, colonyId, StringComparison.Ordinal) ? pending : null;
        }
    }

    /// <summary>
    /// 不論讀進哪一局都要取走：只交給同一局處理的話，讀到別局時清單就遺失，被標為放棄的檔案永遠留在磁碟上。
    /// 玩家改讀的那一局已經走到（tick 不超過目前）的檢查點仍是現行時間線，解除放棄標記、保留下來。
    /// </summary>
    public IReadOnlyList<SaveSlotInfo> TakeCheckpointsToDiscard(string? colonyId, int currentTick)
    {
        lock (_gate)
        {
            var discard = new List<SaveSlotInfo>();
            foreach (SaveSlotInfo checkpoint in _toDiscard)
            {
                if (string.Equals(checkpoint.ColonyId, colonyId, StringComparison.Ordinal) && checkpoint.Tick <= currentTick)
                    _abandoned.Remove(KeyOf(checkpoint));
                else
                    discard.Add(checkpoint);
            }
            _toDiscard.Clear();
            return discard;
        }
    }

    public IReadOnlyList<ReloadRecord> HistoryOf(string? colonyId)
    {
        lock (_gate) return [.. _history.Where(r => string.Equals(r.ColonyId, colonyId, StringComparison.Ordinal))];
    }

    private int LoadsUnlocked(SaveSlotInfo save) => _loads.TryGetValue(KeyOf(save), out int n) ? n : 0;
}
