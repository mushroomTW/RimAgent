#nullable disable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools;

public sealed class TimeTools
{
    private const int TicksPerHour = 2500;
    private const int MaxWaitHours = 24;
    /// <summary>等待分段的長度（0.1 遊戲小時）：段與段之間檢查敵人。</summary>
    private const int WaitChunkTicks = 250;

    private readonly IGameWorld _world;
    private readonly ConfirmationGate _gate;
    private readonly PlayerInterjections _interjections;
    private readonly AgentTranscript _transcript;

    public TimeTools(IGameWorld world, ConfirmationGate gate = null, PlayerInterjections interjections = null, AgentTranscript transcript = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _gate = gate;
        _interjections = interjections;
        _transcript = transcript;
    }

    /// <summary>
    /// 有插話等著送給「這次呼叫的」Agent。插話只附在主 Agent 的工具結果（見 LoggingAIFunction），
    /// 子代理的等待若也提早結束，話送不到它，只會讓它每次等 0.1 小時就被打斷。
    /// </summary>
    private bool PlayerMessageWaiting =>
        _interjections?.HasPending == true && (_transcript == null || _transcript.CurrentAgent.Length == 0);

    [AgentTool("set_time_speed", ToolCategory.Time, ToolRisk.Safe)]
    [Description("Pauses the game or changes the time speed. Pause before a batch of orders that needs a still game; speed up while waiting. Reports the previous and new speed.")]
    public string SetTimeSpeed(
        [Description("Paused, Normal, Fast or Superfast.")] string speed)
    {
        string want = NormalizeSpeed(speed);
        TimeSpeedInfo before = _world.GetTimeSpeed();
        string previous = SpeedLabel(before.Current, _world.TimeSpeeds);

        Result<DefInfo> match = DefResolver.Resolve(want, _world.TimeSpeeds, "time speed");
        if (!match.Success) return match.Error;

        string blocked = ToolText.Blocked(_gate, $"would set time speed {previous} -> {match.Value.Label} ({match.Value.DefName}).");
        if (blocked != null) return blocked;

        CommandResult result = _world.SetTimeSpeed(match.Value.DefName);
        if (!result.Success) return ToolText.Error(result.Message);

        // 世界層已保證冪等（重選目前速度直接成功），這裡讀最新狀態回報。
        TimeSpeedInfo after = _world.GetTimeSpeed();
        string current = SpeedLabel(after.Current, _world.TimeSpeeds);
        string suffix = string.IsNullOrEmpty(result.Message) ? string.Empty : " " + result.Message;
        return $"Time speed {previous} -> {current}.{suffix}";
    }

    [AgentTool("wait_for_time", ToolCategory.Time, ToolRisk.Safe)]
    [Description("Lets in-game time pass while colonists carry out orders (building, hauling, harvesting, research), then returns the colony overview. Use this to wait for progress instead of calling query tools over and over. The game must be unpaused (see set_time_speed). Stops early when the game gets paused, the current map changes, new hostiles appear, a colonist goes down or starts bleeding badly (not from surgery under anesthesia), a fire starts, or the player sends a message.")]
    public async Task<string> WaitForTime(
        [Description("In-game hours to wait, 1-24.")] int hours,
        CancellationToken cancellationToken)
    {
        if (hours < 1 || hours > MaxWaitHours) return ToolText.Error($"hours must be 1-{MaxWaitHours}.");

        string blocked = ToolText.Blocked(_gate, string.Create(CultureInfo.InvariantCulture, $"would let {hours} in-game hour(s) pass."));
        if (blocked != null) return blocked;

        // 分段等：每段之間看一次敵人、倒地流血與火災，任一變多就先回來，不讓模型悶著頭等完整段時間。
        // 等待佔著整句指令，期間排程器也不會起急診或火災任務，只能靠這裡把模型叫回來處理。
        int hostilesBefore = _world.GetHostileCount();
        // 急診比對的是人而不是人數：有人恢復的同時另一人倒下，人數不變也要叫回來。
        // 已知名單跟著縮小，恢復後再次倒下的人也算新的。
        var knownEmergencies = new HashSet<int>(_world.GetColonistsInMedicalEmergency());
        int firesBefore = _world.GetFireCount();
        int total = hours * TicksPerHour;
        int waited = 0;
        string stopReason = null;
        while (waited < total)
        {
            int chunk = Math.Min(WaitChunkTicks, total - waited);
            CommandResult result = await _world.WaitTicksAsync(chunk, cancellationToken);
            if (!result.Success)
            {
                stopReason = result.Message;
                break;
            }
            waited += chunk;
            stopReason = InterruptionReason(hostilesBefore, knownEmergencies, firesBefore);
            if (stopReason != null) break;
        }
        // 等待期間遊戲（或其他模組）記下的錯誤不是這次呼叫造成的，丟掉，免得包裝層把它們附在結果後面誤導模型。
        _world.TakeNewGameErrors();

        if (stopReason != null && waited == 0)
        {
            return ToolText.Error($"cannot wait: {stopReason}. Unpause with set_time_speed first, or tell the player.");
        }
        string head = stopReason == null
            ? string.Create(CultureInfo.InvariantCulture, $"Waited {hours} in-game hour(s).")
            : string.Create(CultureInfo.InvariantCulture, $"Stopped waiting after about {ToolText.Num(waited / (float)TicksPerHour)} of {hours} in-game hour(s): {stopReason}.");
        return head + "\n" + ObserveTools.FormatOverview(_world);
    }

    private string InterruptionReason(int hostilesBefore, HashSet<int> knownEmergencies, int firesBefore)
    {
        if (_world.GetHostileCount() > hostilesBefore) return "new hostiles appeared";
        IReadOnlyList<int> emergencies = _world.GetColonistsInMedicalEmergency();
        if (emergencies.Any(id => !knownEmergencies.Contains(id))) return "a colonist went down or started bleeding badly";
        knownEmergencies.IntersectWith(emergencies);
        if (_world.GetFireCount() > firesBefore) return "a fire started";
        // 插話要等工具結束才會附在結果送給模型：長等待不先回來，玩家要等好幾個遊戲小時才有反應。
        return PlayerMessageWaiting ? "the player sent a message" : null;
    }

    internal static string NormalizeSpeed(string input)
    {
        string needle = (input ?? string.Empty).Trim();
        if (string.Equals(needle, "暫停", StringComparison.OrdinalIgnoreCase)
            || string.Equals(needle, "暂停", StringComparison.OrdinalIgnoreCase)
            || string.Equals(needle, "pause", StringComparison.OrdinalIgnoreCase))
        {
            return "Paused";
        }
        if (string.Equals(needle, "正常", StringComparison.OrdinalIgnoreCase)
            || string.Equals(needle, "普通", StringComparison.OrdinalIgnoreCase)
            || string.Equals(needle, "1x", StringComparison.OrdinalIgnoreCase))
        {
            return "Normal";
        }
        if (string.Equals(needle, "加速", StringComparison.OrdinalIgnoreCase)
            || string.Equals(needle, "快速", StringComparison.OrdinalIgnoreCase)
            || string.Equals(needle, "快", StringComparison.OrdinalIgnoreCase)
            || string.Equals(needle, "2x", StringComparison.OrdinalIgnoreCase))
        {
            return "Fast";
        }
        if (string.Equals(needle, "超加速", StringComparison.OrdinalIgnoreCase)
            || string.Equals(needle, "超快", StringComparison.OrdinalIgnoreCase)
            || string.Equals(needle, "超級加速", StringComparison.OrdinalIgnoreCase)
            || string.Equals(needle, "超级加速", StringComparison.OrdinalIgnoreCase)
            || string.Equals(needle, "3x", StringComparison.OrdinalIgnoreCase))
        {
            return "Superfast";
        }
        return needle;
    }

    internal static string SpeedLabel(string defName, System.Collections.Generic.IReadOnlyList<DefInfo> speeds)
    {
        DefInfo match = speeds.FirstOrDefault(s => string.Equals(s.DefName, defName, StringComparison.OrdinalIgnoreCase));
        return match != null ? $"{match.Label} ({match.DefName})" : defName;
    }
}
