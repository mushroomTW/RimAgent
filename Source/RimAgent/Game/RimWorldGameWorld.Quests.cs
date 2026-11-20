#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RimAgent.Game;

internal sealed partial class RimWorldGameWorld
{
    private const string QuestNotFound = "no available or active quest with that id. Call list_quests for the current ids.";

    private static readonly Regex MarkupTags = new("<[^>]+>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>任務分頁會列出的任務：可接受與進行中，不含已結束、已過期與隱藏的。</summary>
    public IReadOnlyList<QuestInfo> GetQuests()
    {
        var result = new List<QuestInfo>();
        foreach (Quest quest in Find.QuestManager.QuestsListForReading)
        {
            if (quest.hidden || quest.hiddenInUI) continue;
            if (quest.State != QuestState.NotYetAccepted && quest.State != QuestState.Ongoing) continue;
            result.Add(ToQuestInfo(quest));
        }
        return result;
    }

    private static QuestInfo ToQuestInfo(Quest quest)
    {
        bool available = quest.State == QuestState.NotYetAccepted;
        var info = new QuestInfo
        {
            Id = quest.id,
            Name = PlainText(quest.name),
            State = available ? QuestInfo.AvailableState : QuestInfo.ActiveState,
            Dismissed = quest.dismissed,
            Description = QuestDescription(quest),
            AccepterName = quest.AccepterPawnLabelCap ?? string.Empty,
            RequiresAccepter = quest.RequiresAccepter,
            RewardOptions = QuestRewardOptions(quest)
        };
        if (!available)
        {
            info.Deadlines = quest.PartsListForReading.OfType<QuestPartActivable>()
                .Where(p => p.State == QuestPartState.Enabled && !p.ExpiryInfoPart.NullOrEmpty())
                .OrderBy(p => p.EnableTick)
                .Select(p => PlainText(p.ExpiryInfoPart)).ToList();
            return info;
        }
        if (quest.TicksUntilExpiry > 0) info.ExpiresInTicks = quest.TicksUntilExpiry;
        AcceptanceReport report = QuestUtility.CanAcceptQuest(quest);
        info.CanAccept = report.Accepted;
        if (!report.Accepted) info.UnmetRequirements = UnmetQuestRequirements(quest, report);
        if (quest.RequiresAccepter || info.RewardOptions.Any(o => o.RequiresAccepter))
        {
            info.Accepters = QuestAccepterCandidates(quest)
                .Select(p => new QuestAccepterInfo
                {
                    Id = p.thingIDNumber,
                    Name = p.LabelShort,
                    FullName = p.Name?.ToStringFull ?? p.LabelShort,
                    Warning = AccepterWarning(quest, p)
                }).ToList();
        }
        return info;
    }

    /// <summary>照任務分頁的說明區：任務描述，接著是已啟用的各段落補充（依啟用時間）。</summary>
    private static string QuestDescription(Quest quest)
    {
        var sb = new StringBuilder();
        if (!quest.description.RawText.NullOrEmpty()) sb.Append(quest.description.Resolve());
        foreach (QuestPart part in quest.PartsListForReading.OrderBy(p => p is QuestPartActivable a ? a.EnableTick : 0))
        {
            if (part is QuestPartActivable activable && activable.State != QuestPartState.Enabled) continue;
            string text = part.DescriptionPart;
            if (text.NullOrEmpty()) continue;
            if (sb.Length != 0) sb.Append("\n\n");
            sb.Append(text);
        }
        return PlainText(sb.ToString());
    }

    private static List<string> UnmetQuestRequirements(Quest quest, AcceptanceReport overall)
    {
        var reasons = new List<string>();
        foreach (QuestPart_RequirementsToAccept part in quest.PartsListForReading.OfType<QuestPart_RequirementsToAccept>())
        {
            AcceptanceReport report = part.CanAccept();
            string reason = PlainText(report.Reason);
            if (!report.Accepted && reason.Length > 0 && !reasons.Contains(reason)) reasons.Add(reason);
        }
        string fallback = PlainText(overall.Reason);
        if (reasons.Count == 0 && fallback.Length > 0) reasons.Add(fallback);
        return reasons;
    }

    /// <summary>獎勵選項：照任務分頁「以此獎勵接受」的每一組；描述用遊戲自己的獎勵說明文字。</summary>
    private static List<QuestRewardInfo> QuestRewardOptions(Quest quest)
    {
        var result = new List<QuestRewardInfo>();
        QuestPart_Choice choice = quest.PartsListForReading.OfType<QuestPart_Choice>().FirstOrDefault();
        if (choice == null) return result;
        for (int i = 0; i < choice.choices.Count; i++)
        {
            result.Add(new QuestRewardInfo
            {
                Number = i + 1,
                Rewards = choice.choices[i].rewards.Select(RewardText).Where(t => t.Length > 0).ToList(),
                RequiresAccepter = ChoiceRequiresAccepter(quest, choice, choice.choices[i])
            });
        }
        return result;
    }

    private static string RewardText(Reward reward)
    {
        try
        {
            return PlainText(reward.GetDescription(default(RewardsGeneratorParams)));
        }
        catch (Exception)
        {
            return reward.GetType().Name;
        }
    }

    /// <summary>選了這一組獎勵之後，留下的任務段落有沒有要接受者（與任務分頁相同的算法：扣掉只屬於其他選項的段落）。</summary>
    private static bool ChoiceRequiresAccepter(Quest quest, QuestPart_Choice choice, QuestPart_Choice.Choice picked)
    {
        var remaining = new List<QuestPart>(quest.PartsListForReading);
        foreach (QuestPart part in choice.choices.Where(c => c != picked).SelectMany(c => c.questParts))
        {
            remaining.Remove(part);
        }
        return remaining.Any(p => p.RequiresAccepter);
    }

    private static IEnumerable<Pawn> QuestAccepterCandidates(Quest quest)
    {
        return PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoSuspended
            .Where(p => QuestUtility.CanPawnAcceptQuest(p, quest));
    }

    /// <summary>任務分頁在給接受者皇家恩惠前會跳的確認框內容（社交能力、自負個性、影響通靈連結的個性）；沒有則回傳空字串。</summary>
    private static string AccepterWarning(Quest quest, Pawn pawn)
    {
        QuestPart_GiveRoyalFavor favor = quest.PartsListForReading.OfType<QuestPart_GiveRoyalFavor>().FirstOrDefault();
        if (favor == null || !favor.giveToAccepter) return string.Empty;
        NamedArgument pawnArg = pawn.Named("PAWN");
        NamedArgument factionArg = favor.faction.Named("FACTION");
        var warnings = new List<string>();
        if (pawn.skills?.GetSkill(SkillDefOf.Social)?.TotallyDisabled == true)
            warnings.Add("RoyalIncapableOfSocial".Translate(pawnArg, factionArg).Resolve());
        IEnumerable<Trait> conceited = RoyalTitleUtility.GetConceitedTraits(pawn);
        if (conceited.Any())
            warnings.Add("RoyalWithConceitedTrait".Translate(pawnArg, factionArg, conceited.Select(t => t.Label).ToCommaList(useAnd: true)).Resolve());
        IEnumerable<Trait> psylinkTraits = RoyalTitleUtility.GetTraitsAffectingPsylinkNegatively(pawn);
        if (!pawn.HasPsylink && psylinkTraits.Any())
            warnings.Add("RoyalWithTraitAffectingPsylinkNegatively".Translate(pawnArg, factionArg, psylinkTraits.Select(t => t.Label).ToCommaList(useAnd: true)).Resolve());
        return PlainText(string.Join(" ", warnings));
    }

    private static string PlainText(string text)
    {
        return string.IsNullOrEmpty(text) ? string.Empty : MarkupTags.Replace(text, string.Empty).Trim();
    }

    private static Quest FindQuest(int questId)
    {
        return Find.QuestManager.QuestsListForReading.FirstOrDefault(q => q.id == questId && !q.hidden && !q.hiddenInUI);
    }

    /// <summary>照任務分頁接受鈕：先過接受條件，有獎勵選項的先選定，需要接受者的再核對人選，最後 Quest.Accept。</summary>
    public CommandResult AcceptQuest(int questId, int rewardNumber, int accepterPawnId)
    {
        Quest quest = FindQuest(questId);
        if (quest == null) return CommandResult.Fail(QuestNotFound);
        if (quest.State == QuestState.Ongoing) return CommandResult.Fail("the quest is already accepted");
        if (quest.State != QuestState.NotYetAccepted) return CommandResult.Fail("the quest is no longer available: it expired or ended");
        AcceptanceReport report = QuestUtility.CanAcceptQuest(quest);
        if (!report.Accepted)
        {
            List<string> reasons = UnmetQuestRequirements(quest, report);
            return CommandResult.Fail("the game does not allow accepting this quest: "
                + (reasons.Count > 0 ? string.Join("; ", reasons) : "MessageCannotAcceptQuest".Translate().Resolve()));
        }

        QuestPart_Choice choice = quest.PartsListForReading.OfType<QuestPart_Choice>().FirstOrDefault();
        QuestPart_Choice.Choice picked = null;
        bool requiresAccepter = quest.RequiresAccepter;
        if (choice != null)
        {
            picked = SelectRewardChoice(choice, rewardNumber);
            if (picked == null) return CommandResult.Fail($"pick a reward choice from 1 to {choice.choices.Count}");
            requiresAccepter = ChoiceRequiresAccepter(quest, choice, picked);
        }

        Pawn accepter = SelectAccepter(quest, requiresAccepter, accepterPawnId);
        if (requiresAccepter && accepter == null) return CommandResult.Fail("the accepter is not a colonist who can accept this quest");

        if (picked != null) choice.Choose(picked);
        quest.Accept(accepter);
        if (accepter != null)
        {
            Messages.Message("MessageQuestAccepted".Translate(accepter, quest.name), accepter, MessageTypeDefOf.TaskCompletion, historical: false);
        }
        return CommandResult.Ok(quest.name);
    }

    private static QuestPart_Choice.Choice SelectRewardChoice(QuestPart_Choice choice, int rewardNumber)
    {
        if (choice == null) return null;
        if (rewardNumber >= 1 && rewardNumber <= choice.choices.Count) return choice.choices[rewardNumber - 1];
        if (rewardNumber == 0 && choice.choices.Count == 1) return choice.choices[0];
        return null;
    }

    private static Pawn SelectAccepter(Quest quest, bool requiresAccepter, int accepterPawnId)
    {
        if (!requiresAccepter) return null;
        return QuestAccepterCandidates(quest).FirstOrDefault(p => p.thingIDNumber == accepterPawnId);
    }

    /// <summary>照任務分頁的拒絕鈕：切換 dismissed，子任務一起跟著。已結束的任務該鈕是刪除，這裡不做。</summary>
    public CommandResult SetQuestDismissed(int questId, bool dismissed)
    {
        Quest quest = FindQuest(questId);
        if (quest == null) return CommandResult.Fail(QuestNotFound);
        if (quest.Historical) return CommandResult.Fail("the quest has already expired or ended");
        // 任務分頁只在「可接」與「進行中」列出可拒絕的任務；隱藏或已從介面收起的任務玩家看不到鈕。
        if (quest.hidden || quest.hiddenInUI) return CommandResult.Fail(QuestNotFound);
        if (dismissed && quest.State != QuestState.NotYetAccepted && quest.State != QuestState.Ongoing)
            return CommandResult.Fail("only available or ongoing quests can be dismissed");
        quest.dismissed = dismissed;
        foreach (Quest subquest in quest.GetSubquests())
        {
            subquest.dismissed = dismissed;
        }
        return CommandResult.Ok();
    }
}
