using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>
    /// 任務分頁：古樞紐、旅程邀約、皇家飛升等結局路線都從任務開始。
    /// 列出可接受與進行中的任務，照分頁的接受鈕接受，或照拒絕鈕收起來。
    /// </summary>
    public sealed class QuestTools
    {
        private const int MaxListDescriptionChars = 300;
        private const int MaxDetailDescriptionChars = 1500;
        private const float TicksPerHour = 2500f;
        private readonly IGameWorld _world;
        private readonly ConfirmationGate _gate;

        public QuestTools(IGameWorld world, ConfirmationGate gate = null)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _gate = gate;
        }

        [AgentTool("list_quests", ToolCategory.Observe, ToolRisk.Query)]
        [Description("Lists the quests of the Quests tab: available ones (not yet accepted) and active ones. Each shows its id, name, time left to accept or to finish, description, reward choices, whether it can be accepted now and the game's reason when not (for example a required noble title), and which colonists can accept it. Pass a quest id for the full description of one quest. Accept with accept_quest.")]
        public string ListQuests(
            [Description("Quest id for the full detail of one quest; 0 lists all.")] int questId = 0,
            [Description("Also list quests that were dismissed, so dismiss_quest can restore them.")] bool includeDismissed = false)
        {
            IReadOnlyList<QuestInfo> quests = _world.GetQuests();
            if (questId != 0)
            {
                QuestInfo one = quests.FirstOrDefault(q => q.Id == questId);
                return one == null ? NotFound(questId) : Format(one, MaxDetailDescriptionChars);
            }

            List<QuestInfo> shown = quests.Where(q => includeDismissed || !q.Dismissed)
                .OrderBy(q => q.State == QuestInfo.AvailableState ? 0 : 1).ThenBy(q => q.Id).ToList();
            int hidden = quests.Count - shown.Count;
            string hiddenNote = hidden == 0 ? string.Empty : $"{hidden} dismissed quest(s) are hidden; pass includeDismissed to list them.";
            if (shown.Count == 0) return "No available or active quests." + (hidden == 0 ? string.Empty : " " + hiddenNote);
            return string.Join("\n\n", shown.Select(q => Format(q, MaxListDescriptionChars))) + (hidden == 0 ? string.Empty : "\n\n" + hiddenNote);
        }

        [AgentTool("accept_quest", ToolCategory.Observe, ToolRisk.Confirm)]
        [Description("Accepts an available quest, exactly like pressing Accept in the Quests tab. Acceptance cannot be undone and starts the quest's obligations and deadlines. Refuses with the game's reason when the requirements are unmet or the quest expired. A quest with several reward choices needs the reward number; a quest that needs an accepter (for example royal favor goes to that colonist) needs a colonist from list_quests.")]
        public string AcceptQuest(
            [Description("Quest id from list_quests.")] int questId,
            [Description("Reward choice number from list_quests; only needed when the quest offers several choices.")] int reward = 0,
            [Description("Accepter colonist name, nickname or id from list_quests; only needed when the quest needs an accepter. With a single eligible colonist it is picked automatically.")] string accepter = null)
        {
            QuestInfo quest = _world.GetQuests().FirstOrDefault(q => q.Id == questId);
            if (quest == null) return NotFound(questId);
            if (quest.State != QuestInfo.AvailableState)
                return ToolText.Error($"quest \"{quest.Name}\" [{quest.Id}] is already accepted.");
            if (!quest.CanAccept)
                return ToolText.Error($"quest \"{quest.Name}\" [{quest.Id}] cannot be accepted now: {Requirements(quest)}");

            QuestRewardInfo option = null;
            if (quest.RewardOptions.Count > 0)
            {
                option = quest.RewardOptions.Count == 1 && reward <= 1
                    ? quest.RewardOptions[0]
                    : quest.RewardOptions.FirstOrDefault(o => o.Number == reward);
                if (option == null)
                {
                    return ToolText.Error($"quest \"{quest.Name}\" [{quest.Id}] needs a reward choice. Choices: " + FormatRewardOptions(quest));
                }
            }
            else if (reward > 1)
            {
                return ToolText.Error($"quest \"{quest.Name}\" [{quest.Id}] has no reward choices.");
            }

            QuestAccepterInfo picked = null;
            if (option?.RequiresAccepter ?? quest.RequiresAccepter)
            {
                Result<QuestAccepterInfo> resolved = ResolveAccepter(quest, accepter);
                if (!resolved.Success) return resolved.Error;
                picked = resolved.Value;
            }

            string preview = $"would accept quest \"{quest.Name}\" [{quest.Id}]" + RewardClause(option) + AccepterClause(picked) + ". This cannot be undone.";
            string blocked = ToolText.Blocked(_gate, preview);
            if (blocked != null) return blocked;

            CommandResult r = _world.AcceptQuest(quest.Id, option?.Number ?? 0, picked?.Id ?? 0);
            if (!r.Success) return ToolText.Error(r.Message);
            string summary = $"Accepted quest \"{quest.Name}\" [{quest.Id}]" + RewardClause(option) + AccepterClause(picked) + ".";
            return string.IsNullOrEmpty(picked?.Warning) ? summary : summary + " Warning from the game: " + picked.Warning;
        }

        [AgentTool("dismiss_quest", ToolCategory.Observe, ToolRisk.Safe)]
        [Description("Dismisses an available quest, like the dismiss button in the Quests tab, or restores a dismissed one. The quest still expires on schedule; it is only hidden from the available list (list_quests with includeDismissed shows it). Accepted quests cannot be dismissed.")]
        public string DismissQuest(
            [Description("Quest id from list_quests.")] int questId,
            [Description("True dismisses the quest, false restores a dismissed one.")] bool dismissed = true)
        {
            QuestInfo quest = _world.GetQuests().FirstOrDefault(q => q.Id == questId);
            if (quest == null) return NotFound(questId);
            // 只有尚未接受的任務能被拒絕；但進行中的任務若曾被收起來，仍可取消。
            if (dismissed && quest.State != QuestInfo.AvailableState)
                return ToolText.Error($"quest \"{quest.Name}\" [{quest.Id}] is already accepted; only available quests can be dismissed.");
            string verb = dismissed ? "dismissed" : "restored";
            if (quest.Dismissed == dismissed) return $"Quest \"{quest.Name}\" [{quest.Id}] is already {(dismissed ? "dismissed" : "not dismissed")}.";

            string blocked = ToolText.Blocked(_gate, $"would mark quest \"{quest.Name}\" [{quest.Id}] as {verb}.");
            if (blocked != null) return blocked;

            CommandResult r = _world.SetQuestDismissed(quest.Id, dismissed);
            if (!r.Success) return ToolText.Error(r.Message);
            return $"Quest \"{quest.Name}\" [{quest.Id}] {verb}.";
        }

        private static string NotFound(int questId)
        {
            return ToolText.Error($"no available or active quest with id {questId}. Call list_quests for the current ids.");
        }

        private static Result<QuestAccepterInfo> ResolveAccepter(QuestInfo quest, string accepter)
        {
            if (quest.Accepters.Count == 0)
            {
                return Result<QuestAccepterInfo>.Fail(ToolText.Error($"quest \"{quest.Name}\" [{quest.Id}] needs an accepter, but no colonist can accept it right now."));
            }
            if (string.IsNullOrWhiteSpace(accepter))
            {
                if (quest.Accepters.Count == 1) return Result<QuestAccepterInfo>.Ok(quest.Accepters[0]);
                return Result<QuestAccepterInfo>.Fail(ToolText.Error($"quest \"{quest.Name}\" [{quest.Id}] needs an accepter. Colonists who can accept: " + FormatAccepters(quest.Accepters)));
            }
            return TargetResolver.ResolveEntity(accepter, quest.Accepters, a => a.Id, AccepterNames, a => $"[{a.Id}] {a.FullName}", "colonist");
        }

        private static IEnumerable<string> AccepterNames(QuestAccepterInfo a)
        {
            if (!string.IsNullOrEmpty(a.Name)) yield return a.Name;
            if (!string.IsNullOrEmpty(a.FullName)) yield return a.FullName;
        }

        private static string RewardClause(QuestRewardInfo option)
        {
            if (option == null || option.Rewards.Count == 0) return string.Empty;
            return " for reward " + option.Number + " (" + string.Join("; ", option.Rewards) + ")";
        }

        private static string AccepterClause(QuestAccepterInfo accepter)
        {
            if (accepter == null) return string.Empty;
            string clause = " with " + accepter.Name + " as the accepter";
            return string.IsNullOrEmpty(accepter.Warning) ? clause : clause + " (game warning: " + accepter.Warning + ")";
        }

        private static string FormatAccepters(IReadOnlyList<QuestAccepterInfo> accepters)
        {
            return string.Join("; ", accepters.Select(a => $"[{a.Id}] {a.Name}" + (string.IsNullOrEmpty(a.Warning) ? string.Empty : " (warning: " + a.Warning + ")")));
        }

        private static string FormatRewardOptions(QuestInfo quest)
        {
            return string.Join("; ", quest.RewardOptions.Select(o => o.Number + ") " + (o.Rewards.Count == 0 ? "(nothing listed)" : string.Join(", ", o.Rewards))
                + (o.RequiresAccepter ? " [needs an accepter]" : string.Empty)));
        }

        private static string Requirements(QuestInfo quest)
        {
            return quest.UnmetRequirements.Count == 0
                ? "the game does not allow accepting it right now."
                : string.Join("; ", quest.UnmetRequirements);
        }

        private static string Time(int ticks)
        {
            float hours = Math.Max(0, ticks) / TicksPerHour;
            return hours < 24f ? ToolText.Num(hours) + " h" : ToolText.Num(hours / 24f) + " days";
        }

        private static string Format(QuestInfo q, int descriptionLimit)
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(q.Id).Append("] ").Append(string.IsNullOrEmpty(q.Name) ? "(unnamed)" : q.Name).Append(" (").Append(q.State);
            if (q.Dismissed) sb.Append(", dismissed");
            if (q.ExpiresInTicks.HasValue) sb.Append(", expires in ").Append(Time(q.ExpiresInTicks.Value));
            if (!string.IsNullOrEmpty(q.AccepterName)) sb.Append(", accepted by ").Append(q.AccepterName);
            sb.Append(')');
            foreach (string deadline in q.Deadlines) sb.AppendLine().Append("Deadline: ").Append(deadline);
            if (!string.IsNullOrEmpty(q.Description))
                sb.AppendLine().Append(ToolText.TruncateField(q.Description.Replace('\n', ' ').Replace("\r", string.Empty), descriptionLimit));
            if (q.State == QuestInfo.AvailableState)
            {
                sb.AppendLine().Append(q.CanAccept ? "Can accept: yes" : "Can accept: NO - " + Requirements(q));
            }
            if (q.RewardOptions.Count > 0)
            {
                sb.AppendLine().Append(q.State == QuestInfo.AvailableState && q.RewardOptions.Count > 1 ? "Reward choices: " : "Rewards: ").Append(FormatRewardOptions(q));
            }
            if (q.State == QuestInfo.AvailableState && q.Accepters.Count > 0)
            {
                sb.AppendLine().Append("Colonists who can accept: ").Append(FormatAccepters(q.Accepters));
            }
            return sb.ToString();
        }
    }
}
