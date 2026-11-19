using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;
using Verse;

namespace RimAgent;

public partial class RimAgentGameComponent
{
    /// <summary>
    /// 剛讀檔重來就先把教訓交給 agent；有殖民者死亡就讓 agent 決定要不要讀檔（需開啟存檔權限）。
    /// 主動照顧關閉時讀檔重來只留通知，死亡不起任務。
    /// </summary>
    private void TryStartRecoveryRun()
    {
        if (_pendingReload == null && _pendingDeaths.Count == 0) return;
        if (_autoRunning || _goalRunning || Find.CurrentMap == null) return;
        RimAgentSettings settings = RimAgentMod.Settings;
        try
        {
            ColonyAgentSession session = Session;
            if (session.IsBusy || session.HasPendingConfirmation) return;
            bool care = settings != null && settings.autonomousCare;
            if (_pendingReload != null)
            {
                ReloadRecord reload = _pendingReload;
                _pendingReload = null;
                session.Transcript.Add(new TranscriptEntry
                {
                    Kind = TranscriptKind.Notice,
                    Text = "Reloaded " + reload.SaveName + " (attempt " + reload.Attempt + "/" + SaveLedger.MaxLoadsPerSave + "): " + reload.Lesson
                });
                if (!care) return;
                _autoRunning = true;
                _ = RunAutonomousGuardedAsync(session, AutonomousTrigger.Reloaded, Find.TickManager.TicksGame, ToConfig(settings), null,
                    DescribeReloads(SaveLedger.Shared.HistoryOf(ColonyId)));
                return;
            }
            if (!care || !settings.GetAllow(ToolCategory.Save))
            {
                _pendingDeaths.Clear();
                return;
            }
            string died = "Died: " + string.Join(", ", _pendingDeaths) + ".";
            _pendingDeaths.Clear();
            _autoRunning = true;
            _ = RunAutonomousGuardedAsync(session, AutonomousTrigger.ColonistDied, Find.TickManager.TicksGame, ToConfig(settings), null, died);
        }
        catch (Exception ex)
        {
            WarnOnce("RimAgent recovery run could not start: " + ex.Message);
        }
    }

    /// <summary>這一局到目前為止每次讀檔重來的教訓，由舊到新。</summary>
    private static string DescribeReloads(IReadOnlyList<ReloadRecord> reloads)
    {
        var sb = new System.Text.StringBuilder("Reloads of this colony so far (oldest first):");
        foreach (ReloadRecord r in reloads)
        {
            sb.Append("\n- ").Append(r.SaveName).Append(", attempt ").Append(r.Attempt).Append('/').Append(SaveLedger.MaxLoadsPerSave)
                .Append(": ").Append(r.Lesson);
        }
        return sb.ToString();
    }

    /// <summary>點名殖民者：上次還活著、這次不在名單且確實死亡的記下來。離開地圖、被綁架等不算。</summary>
    private void TrackColonistDeaths()
    {
        _pendingDeaths.AddRange(_trackedColonists.Values.Where(t => t.Dead).Select(t => t.LabelShort));
        _trackedColonists.Clear();
        foreach (Pawn pawn in RimWorld.PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoSuspended)
        {
            _trackedColonists[pawn.thingIDNumber] = pawn;
        }
    }

    /// <summary>每個遊戲日存一份檢查點，只在安全時存：檢查點不能一開始就身陷危險。</summary>
    private void TryCheckpoint(int tick)
    {
        if (tick - _lastCheckpointTick < CheckpointIntervalTicks || _pendingDeaths.Count > 0 || _autoRunning) return;
        var tools = new SaveTools(_autoWorld);
        SaveContext context = _autoWorld.GetSaveContext();
        if (tools.SaveRefusal(context) != null) return;
        _lastCheckpointTick = tick;
        _ = CheckpointGuardedAsync(tools, context);
    }

    private async Task CheckpointGuardedAsync(SaveTools tools, SaveContext context)
    {
        try
        {
            string result = await tools.CreateCheckpointAsync(context);
            if (result.StartsWith(ToolText.ErrorPrefix, StringComparison.Ordinal)) WarnOnce("RimAgent checkpoint failed: " + result);
        }
        catch (Exception ex)
        {
            WarnOnce("RimAgent checkpoint failed: " + ex.Message);
        }
    }
}
