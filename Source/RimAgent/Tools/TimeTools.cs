using System;
using System.ComponentModel;
using System.Linq;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>時間流速：查詢目前速度，切換暫停與加速。</summary>
    public sealed class TimeTools
    {
        private readonly IGameWorld _world;

        public TimeTools(IGameWorld world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        [AgentTool("get_time_speed", ToolCategory.Time)]
        [Description("Shows the current game time flow speed: paused, normal, fast or superfast, with the speeds you can switch to.")]
        public string GetTimeSpeed()
        {
            TimeSpeedInfo info = _world.GetTimeSpeed();
            string current = SpeedLabel(info.Current, _world.TimeSpeeds);
            string available = string.Join(", ", _world.TimeSpeeds.Select(s => $"{s.Label} ({s.DefName})"));
            return $"Current time speed: {current}. Available: {available}.";
        }

        [AgentTool("set_time_speed", ToolCategory.Time)]
        [Description("Pauses the game or changes the time flow speed. Pause before giving orders that need a still game, speed up while waiting. Reports the previous and new speed.")]
        public string SetTimeSpeed(
            [Description("Speed: Paused (or 暫停), Normal, Fast or Superfast.")] string speed)
        {
            string want = NormalizeSpeed(speed);
            TimeSpeedInfo before = _world.GetTimeSpeed();
            string previous = SpeedLabel(before.Current, _world.TimeSpeeds);

            ResolveResult<DefInfo> match = DefResolver.Resolve(want, _world.TimeSpeeds, "time speed");
            if (!match.Success) return match.Error;

            CommandResult result = _world.SetTimeSpeed(match.Value.DefName);
            if (!result.Success) return ToolText.Error(result.Message);

            // 世界層已保證冪等（重選目前速度直接成功），這裡讀最新狀態回報。
            TimeSpeedInfo after = _world.GetTimeSpeed();
            string current = SpeedLabel(after.Current, _world.TimeSpeeds);
            string suffix = string.IsNullOrEmpty(result.Message) ? string.Empty : " " + result.Message;
            return $"Time speed {previous} -> {current}.{suffix}";
        }

        /// <summary>中文別名（大小寫不敏感）；英文循 DefResolver 的既有規則。</summary>
        internal static string NormalizeSpeed(string input)
        {
            string needle = (input ?? string.Empty).Trim();
            if (string.Equals(needle, "暫停", StringComparison.OrdinalIgnoreCase)
                || string.Equals(needle, "暂停", StringComparison.OrdinalIgnoreCase)
                || string.Equals(needle, "pause", StringComparison.OrdinalIgnoreCase))
                return "Paused";
            if (string.Equals(needle, "正常", StringComparison.OrdinalIgnoreCase)
                || string.Equals(needle, "普通", StringComparison.OrdinalIgnoreCase)
                || string.Equals(needle, "1x", StringComparison.OrdinalIgnoreCase))
                return "Normal";
            if (string.Equals(needle, "加速", StringComparison.OrdinalIgnoreCase)
                || string.Equals(needle, "快速", StringComparison.OrdinalIgnoreCase)
                || string.Equals(needle, "快", StringComparison.OrdinalIgnoreCase)
                || string.Equals(needle, "2x", StringComparison.OrdinalIgnoreCase))
                return "Fast";
            if (string.Equals(needle, "超加速", StringComparison.OrdinalIgnoreCase)
                || string.Equals(needle, "超快", StringComparison.OrdinalIgnoreCase)
                || string.Equals(needle, "超級加速", StringComparison.OrdinalIgnoreCase)
                || string.Equals(needle, "超级加速", StringComparison.OrdinalIgnoreCase)
                || string.Equals(needle, "3x", StringComparison.OrdinalIgnoreCase))
                return "Superfast";
            return needle;
        }

        private static string SpeedLabel(string defName, System.Collections.Generic.IReadOnlyList<DefInfo> speeds)
        {
            DefInfo match = speeds.FirstOrDefault(s => string.Equals(s.DefName, defName, StringComparison.OrdinalIgnoreCase));
            return match != null ? $"{match.Label} ({match.DefName})" : defName;
        }
    }
}
