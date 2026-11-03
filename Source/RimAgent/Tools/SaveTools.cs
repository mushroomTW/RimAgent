using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools;

/// <summary>
/// 讀檔重來：殖民者死亡後由模型決定要不要讀回死亡前的存檔。
/// 可讀的是這一局的 RimAgent 檢查點與遊戲 autosave；次數上限與時間線規則由 <see cref="SaveLedger"/> 把關。
/// </summary>
public sealed class SaveTools
{
    public const string CheckpointPrefix = "RimAgent-";
    /// <summary>保留的檢查點份數；更舊的在存新檔時刪掉。</summary>
    public const int MaxCheckpoints = 5;
    private const int TicksPerHour = 2500;
    private const int TicksPerDay = 60000;
    private const int MaxLessonChars = 1000;

    private readonly IGameWorld _world;
    private readonly ConfirmationGate _gate;
    private readonly SaveLedger _ledger;

    public SaveTools(IGameWorld world, ConfirmationGate gate = null, SaveLedger ledger = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _gate = gate;
        _ledger = ledger ?? SaveLedger.Shared;
    }

    /// <summary>檢查點名稱：殖民地代號＋遊戲 tick，不必開檔就認得出是哪一局、哪個時間點。</summary>
    public static string CheckpointName(string colonyId, int tick)
    {
        return CheckpointPrefix + colonyId + "-" + tick.ToString(CultureInfo.InvariantCulture);
    }

    [AgentTool("list_saves", ToolCategory.Save, ToolRisk.Query)]
    [Description("Lists the saves of this colony that load_save can return to, newest first: RimAgent checkpoints (made once an in-game day while the colony is safe, or by save_game) and the game's autosaves. Each line gives the in-game time, how long ago that was, how many of its 3 reloads are used, and whether it can still be loaded. Use it after a colonist dies to decide whether to reload.")]
    public async Task<string> ListSaves()
    {
        SaveContext context = _world.GetSaveContext();
        if (context.Permadeath) return ToolText.Error("this is a commitment-mode game; it cannot be reloaded.");
        List<SaveSlotInfo> saves = await ColonySavesAsync(context);
        if (saves.Count == 0) return "No saves of this colony yet. save_game makes one; RimAgent also makes one each in-game day while the colony is safe.";

        var sb = new StringBuilder();
        sb.AppendLine("Saves of this colony (newest first):");
        foreach (SaveSlotInfo save in saves)
        {
            sb.Append("- ").Append(save.Name).Append(save.Checkpoint ? " [checkpoint]" : " [autosave]")
                .Append(": ").Append(FormatTime(save.Tick, context.Tick))
                .Append(", reloads used ").Append(_ledger.LoadsOf(save)).Append('/').Append(SaveLedger.MaxLoadsPerSave);
            string refusal = LoadRefusal(save, saves);
            sb.AppendLine(refusal == null ? ", can be loaded" : ", locked: " + refusal);
        }
        return sb.ToString().TrimEnd();
    }

    [AgentTool("save_game", ToolCategory.Save, ToolRisk.Safe)]
    [Description("Saves a checkpoint of the current game that load_save can return to, for example before a risky attack or caravan. Refused while hostiles are on the map, a colonist is downed or bleeding, or a fire burns: a checkpoint must not start in danger. Only the newest 5 checkpoints are kept.")]
    public async Task<string> SaveGame()
    {
        SaveContext context = _world.GetSaveContext();
        string refusal = SaveRefusal(context);
        if (refusal != null) return ToolText.Error(refusal);

        string name = CheckpointName(context.ColonyId, context.Tick);
        string blocked = ToolText.Blocked(_gate, $"would save checkpoint {name}.");
        if (blocked != null) return blocked;

        return await CreateCheckpointAsync(context);
    }

    [AgentTool("load_save", ToolCategory.Save, ToolRisk.Confirm)]
    [Description("Reloads one of this colony's saves from list_saves to undo a disaster such as a colonist's death. Everything after that save is lost, including this conversation and memory written since: only the lesson is carried over, so state what went wrong and what to do differently. Saves newer than the loaded one are discarded. Each save can be reloaded 3 times; once one is used up, it and every older save are locked and the loss stands.")]
    public async Task<string> LoadSave(
        [Description("Save name from list_saves.")] string save,
        [Description("What killed or endangered the colonists and what to do differently after the reload.")] string lesson)
    {
        string trimmedLesson = (lesson ?? string.Empty).Trim();
        if (trimmedLesson.Length == 0) return ToolText.Error("lesson is required: say what went wrong and what to do differently.");
        if (trimmedLesson.Length > MaxLessonChars) trimmedLesson = trimmedLesson.Substring(0, MaxLessonChars);

        SaveContext context = _world.GetSaveContext();
        if (context.Permadeath) return ToolText.Error("this is a commitment-mode game; it cannot be reloaded.");
        List<SaveSlotInfo> saves = await ColonySavesAsync(context);
        SaveSlotInfo target = saves.FirstOrDefault(s => string.Equals(s.Name, (save ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            string known = saves.Count == 0 ? "none" : string.Join(", ", saves.Select(s => s.Name));
            return ToolText.Error($"no save of this colony named '{save}'. Saves: {known}.");
        }
        string refusal = LoadRefusal(target, saves);
        if (refusal != null) return ToolText.Error($"{target.Name} cannot be loaded: {refusal}.");

        int attempt = _ledger.LoadsOf(target) + 1;
        string blocked = ToolText.Blocked(_gate,
            $"would reload {target.Name} ({FormatTime(target.Tick, context.Tick)}), attempt {attempt}/{SaveLedger.MaxLoadsPerSave}; everything after it is lost.");
        if (blocked != null) return blocked;

        // 比它新的檢查點屬於要放棄的時間線：自己做的檔自己刪；autosave 留著，由紀錄擋住。
        foreach (SaveSlotInfo newer in saves.Where(s => s.Checkpoint && s.Tick > target.Tick))
        {
            _world.DeleteCheckpoint(newer.Name);
        }
        attempt = _ledger.RecordLoad(target, saves, context.ColonyId, trimmedLesson);
        CommandResult result = _world.LoadGame(target.Name);
        if (!result.Success) return ToolText.Error(result.Message);
        return $"Reloading {target.Name} (attempt {attempt}/{SaveLedger.MaxLoadsPerSave}). This run ends now; the lesson is handed to the reloaded game.";
    }

    /// <summary>存一份檢查點並刪掉超出份數的舊檢查點；每日自動存檔與 save_game 共用。呼叫端已確認可以存。</summary>
    internal async Task<string> CreateCheckpointAsync(SaveContext context)
    {
        string name = CheckpointName(context.ColonyId, context.Tick);
        CommandResult result = _world.SaveGame(name);
        if (!result.Success) return ToolText.Error(result.Message);

        // 新檔下一幀才寫入，清單裡還沒有它：舊檢查點只留 MaxCheckpoints - 1 份。
        List<SaveSlotInfo> checkpoints = (await ColonySavesAsync(context))
            .Where(s => s.Checkpoint && !string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (SaveSlotInfo old in checkpoints.Skip(MaxCheckpoints - 1))
        {
            _world.DeleteCheckpoint(old.Name);
        }
        return $"Saved checkpoint {name}.";
    }

    /// <summary>現在不能存檢查點的原因；可以存時回傳 null。</summary>
    internal string SaveRefusal(SaveContext context)
    {
        if (context.Permadeath) return "this is a commitment-mode game; the game manages its only save";
        if (context.SavingBlockedReason != null) return "the game cannot save right now: " + context.SavingBlockedReason;
        if (_world.GetHostileCount() > 0) return "hostiles are on the map";
        if (_world.GetColonistsInMedicalEmergency().Count > 0) return "a colonist is downed or bleeding";
        if (_world.GetFireCount() > 0) return "a fire is burning";
        return null;
    }

    private string LoadRefusal(SaveSlotInfo save, IReadOnlyList<SaveSlotInfo> colonySaves)
    {
        SaveSlotInfo blocker = _ledger.ExhaustedBlocker(save, colonySaves);
        if (blocker == null) return null;
        return ReferenceEquals(blocker, save)
            ? $"all {SaveLedger.MaxLoadsPerSave} reloads used; accept the loss and keep playing"
            : $"{blocker.Name} (newer) used all {SaveLedger.MaxLoadsPerSave} reloads; going further back is not allowed";
    }

    /// <summary>這一局、沒被放棄的存檔，新到舊。</summary>
    private async Task<List<SaveSlotInfo>> ColonySavesAsync(SaveContext context)
    {
        IReadOnlyList<SaveSlotInfo> all = await _world.GetSaveFilesAsync();
        return all
            .Where(s => s.ColonyId.Length > 0 && string.Equals(s.ColonyId, context.ColonyId, StringComparison.Ordinal))
            .Where(s => !_ledger.IsAbandoned(s))
            .OrderByDescending(s => s.Tick)
            .ToList();
    }

    private static string FormatTime(int tick, int now)
    {
        int day = tick / TicksPerDay;
        int hour = tick % TicksPerDay / TicksPerHour;
        string when = $"game day {day.ToString(CultureInfo.InvariantCulture)} {hour.ToString(CultureInfo.InvariantCulture)}h";
        int ago = now - tick;
        return ago >= 0 ? $"{when} ({ToolText.Num(ago / (float)TicksPerHour)} in-game hours ago)" : when;
    }
}
