#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using RimAgent.Agent;
using RimLLM_Framework;
using RimWorld;
using Verse;

namespace RimAgent.UI;

#pragma warning disable S101 // RimWorld Window 類別命名慣例
public partial class Window_Agent
{
    /// <summary>
    /// 換上新一輪的 CTS。舊的若還有未完成的工作握著它，就先收進 _retiredCts（停止時一併取消）而不 Dispose；
    /// 舊工作已結束才安全釋放。
    /// </summary>
    private CancellationTokenSource BeginRunCts()
    {
        CancellationTokenSource old = _cts;
        if (old != null)
        {
            if (_observedTask == null || _observedTask.IsCompleted) old.Dispose();
            else _retiredCts.Add((old, _observedTask));
        }
        DisposeFinishedRetiredCts();
        _cts = new CancellationTokenSource();
        return _cts;
    }

    /// <summary>取消目前與已退役但仍在跑的 CTS（停止鍵、關窗、遊戲被換掉）。</summary>
    private void CancelRunCts()
    {
        _cts?.Cancel();
        foreach ((CancellationTokenSource cts, Task _) in _retiredCts) cts.Cancel();
        DisposeFinishedRetiredCts();
    }

    /// <summary>工作已結束的退役 CTS 不會再有人讀，釋放並移出清單；仍在跑的留著。</summary>
    private void DisposeFinishedRetiredCts()
    {
        _retiredCts.RemoveAll(r =>
        {
            if (!r.Work.IsCompleted) return false;
            r.Cts.Dispose();
            return true;
        });
    }

    /// <summary>
    /// 記下發射後不管的請求；它 faulted（通常是同幀被自主任務搶先導致的忙碌衝突）
    /// 才在主線程補一句忙碌提示。成功完成就靜靜丟掉。
    /// </summary>
    private void Observe(ColonyAgentSession session, Task task)
    {
        _observedSession = session;
        _observedTask = task;
    }

    private void DrainObservedTask()
    {
        Task task = _observedTask;
        if (task == null || !task.IsCompleted) return;
        _observedTask = null;
        ColonyAgentSession session = _observedSession;
        _observedSession = null;
        if (task.IsFaulted && session != null)
        {
            // 只有「同幀被自主任務搶先」的忙碌衝突才提示忙碌；其他失敗留下紀錄並顯示一般錯誤。
            Exception error = task.Exception?.GetBaseException();
            if (error is AgentBusyException)
            {
                Notice(session, "RimAgent.Slash.Busy".Translate());
            }
            else
            {
                Log.Warning("RimAgent request failed: " + error);
                Notice(session, "RimAgent.Error.Unknown".Translate(error?.Message ?? string.Empty));
            }
        }
    }

    private void Send(ColonyAgentSession session)
    {
        string text = _input.Trim();
        if (text.Length == 0) return;
        // 執行中插話；確認、壓縮等非指令的忙碌不收，輸入框內容留著等忙完再送。
        // 剛好在這瞬間跑完的照一般指令送出。
        bool interjected = TryInterject(session, text);
        if (!interjected && text[0] != '/' && !ProbeAndValidateImages(session)) return;
        _input = string.Empty;
        RememberInput(text);
        _historyIndex = -1;
        _scrollToBottom = true;
        if (interjected) return;

        List<byte[]> images = TakePastedImages();
        HandleSlashOrSend(session, text, images);
    }

    /// <summary>嘗試將輸入當執行中的揬話；回傳 true 表示已送出或已擋下，呼叫端應在清除輸入後結束。</summary>
    private static bool TryInterject(ColonyAgentSession session, string text)
    {
        if (text[0] == '/' || !session.IsBusy) return false;
        bool interjected = session.Interject(text);
        if (!interjected && session.IsBusy)
        {
            Notice(session, "RimAgent.Slash.Busy".Translate());
            return true;
        }
        return interjected;
    }

    /// <summary>探勘模型能力並檢查貼圖是否可送；若應擋下送出則回傳 false。</summary>
    private bool ProbeAndValidateImages(ColonyAgentSession session)
    {
        ProbeCapabilities();
        if (_capabilityWarning != null) return false;
        // 貼圖後換成不支援圖片的模型：圖片送出時會被丟掉，先擋下並保留輸入與圖片，讓玩家移除圖片或換回模型。
        if (_pastedImages.Count > 0 && !session.ImageInputEnabled)
        {
            Notice(session, "RimAgent.ImagesUnsupported".Translate());
            return false;
        }
        return true;
    }

    /// <summary>處理斜線指令，或一般文字送給模型。</summary>
    private void HandleSlashOrSend(ColonyAgentSession session, string text, List<byte[]> images)
    {
        if (SlashCommands.TryParse(text, out SlashCommand command, out string args))
        {
            if (command != null) RunSlash(session, command, args);
            else Notice(session, "RimAgent.Slash.Unknown".Translate(text));
            return;
        }
        string display = images.Count == 0 ? null : text + " " + "RimAgent.ImagesAttached".Translate(images.Count);
        Observe(session, session.SendAsync(text, displayText: display, images: images, cancellationToken: BeginRunCts().Token));
    }

    private void ProbeCapabilities()
    {
        _nextCapabilityProbe = DateTime.UtcNow.AddSeconds(2);
        // 框架換掉模型後，清掉 RimAgent 殘留的舊指定，免得繼續打不存在的模型。
        RimAgentSettings settings = RimAgentMod.Settings;
        try
        {
            if (settings != null && settings.ClearAgentModelIfMissing(ConfiguredModels())) TryWriteSettings(settings);
            _capabilityWarning = CheckFunctionCalling() ? null : "RimAgent.NoFunctionCalling".Translate().ToString();
        }
        catch (RimLLMException ex)
        {
            _capabilityWarning = "RimAgent.NoProvider".Translate(ex.Message);
        }
        catch (InvalidOperationException)
        {
            _capabilityWarning = "RimAgent.FrameworkNotInitialized".Translate();
        }
        catch (Exception ex) when (ex is MissingMethodException || ex is TypeLoadException)
        {
            _capabilityWarning = "RimAgent.FrameworkTooOld".Translate();
        }
        catch (Exception ex)
        {
            // 其他未預期的例外：不冒到 UI 繪製迴圈，也不誤報成「沒有供應商」；記一次警告留下原因（每兩秒重探，不洗版）。
            Log.WarningOnce("RimAgent: capability probe failed: " + ex, StringComparer.Ordinal.GetHashCode("RimAgent.ProbeCapabilities"));
            _capabilityWarning = "RimAgent.Error.Unknown".Translate(ex.Message);
        }
    }

    /// <summary>寫設定檔失敗（例如檔案被鎖）只記一次警告，不影響能力探測的結果。</summary>
    private static void TryWriteSettings(RimAgentSettings settings)
    {
        try
        {
            settings.Write();
        }
        catch (Exception ex)
        {
            Log.WarningOnce("RimAgent: could not save settings: " + ex.Message, StringComparer.Ordinal.GetHashCode("RimAgent.SettingsWrite"));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool CheckFunctionCalling()
    {
        string model = RimAgentMod.Settings?.agentModel;
        return RimLLMProvider.GetEffectiveCapabilities(string.IsNullOrWhiteSpace(model) ? null : model.Trim()).SupportsFunctionCalling;
    }
}
