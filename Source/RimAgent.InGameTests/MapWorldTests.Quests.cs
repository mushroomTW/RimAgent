using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DevTools.Testing;
using RimAgent.Game;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace RimAgent.InGameTests;

internal sealed partial class MapWorldTests
{
    /// <summary>
    /// 用原版產生一個需要玩家接受的任務（強盜營地或交易請求；物資藏匿點等 autoAccept 任務生成即接受，不適用），
    /// 走過列出、拒絕、取消拒絕、接受，再以原版欄位讀回；結束後移除任務。
    /// </summary>
    [Test]
    public void QuestListDismissAndAccept()
    {
        if (TestHelpers.SkipWithoutMap(nameof(QuestListDismissAndAccept))) return;
        Quest quest = GenerateManualQuest("OpportunitySite_BanditCamp", "TradeRequest");
        if (quest == null)
        {
            TestHelpers.Skip(nameof(QuestListDismissAndAccept), "no manually accepted quest could be generated on this world");
            return;
        }
        var failures = new List<string>();
        try
        {
            QuestInfo info = _world.GetQuests().FirstOrDefault(q => q.Id == quest.id);
            if (info == null) failures.Add("generated quest is missing from GetQuests");
            else CheckQuestInfo(info, quest, failures);

            CommandResult dismiss = _world.SetQuestDismissed(quest.id, dismissed: true);
            if (!dismiss.Success || !quest.dismissed) failures.Add("dismiss did not set quest.dismissed: " + dismiss.Error);
            CommandResult restore = _world.SetQuestDismissed(quest.id, dismissed: false);
            if (!restore.Success || quest.dismissed) failures.Add("restore did not clear quest.dismissed: " + restore.Error);

            TryAcceptQuest(quest, failures);
        }
        finally
        {
            if (!quest.Historical) quest.End(QuestEndOutcome.Unknown, sendLetter: false, playSound: false);
            Find.QuestManager.Remove(quest);
        }
        TestHelpers.AssertNone(failures, "quests");
    }

    /// <summary>檢查從 GetQuests 讀回的任務資訊是否與原版一致。</summary>
    private static void CheckQuestInfo(QuestInfo info, Quest quest, List<string> failures)
    {
        if (info.State != QuestInfo.AvailableState) failures.Add("state is " + info.State + ", expected available");
        if (string.IsNullOrEmpty(info.Name)) failures.Add("empty name");
        if (info.CanAccept != QuestUtility.CanAcceptQuest(quest).Accepted) failures.Add("CanAccept differs from vanilla");
    }

    /// <summary>當任務可接受時執行接受，並驗證狀態與重複接受被擋下；否則記錄無法接受的原因。</summary>
    private void TryAcceptQuest(Quest quest, List<string> failures)
    {
        if (!QuestUtility.CanAcceptQuest(quest).Accepted)
        {
            failures.Add("item stash quest cannot be accepted: " + QuestUtility.CanAcceptQuest(quest).Reason);
            return;
        }
        int accepter = quest.RequiresAccepter
            ? PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoSuspended
                .FirstOrDefault(p => QuestUtility.CanPawnAcceptQuest(p, quest))?.thingIDNumber ?? 0
            : 0;
        // 有獎勵選項的任務只能「以此獎勵接受」，選第一組。
        int reward = quest.PartsListForReading.OfType<QuestPart_Choice>().Any() ? 1 : 0;
        CommandResult accept = _world.AcceptQuest(quest.id, reward, accepter);
        if (!accept.Success) failures.Add("accept failed: " + accept.Error);
        else if (quest.State != QuestState.Ongoing) failures.Add("after accept the quest state is " + quest.State);
        if (_world.AcceptQuest(quest.id, reward, accepter).Success) failures.Add("accepting twice succeeded");
        if (!_world.GetQuests().Any(q => q.Id == quest.id && q.State == QuestInfo.ActiveState))
            failures.Add("accepted quest is not listed as active");
    }

    /// <summary>依序嘗試產生任務，取第一個生成後仍待玩家接受的；生成失敗或自動接受的會移除。</summary>
    internal static Quest GenerateManualQuest(params string[] scripts)
    {
        foreach (string name in scripts)
        {
            QuestScriptDef script = DefDatabase<QuestScriptDef>.GetNamedSilentFail(name);
            if (script == null || script.autoAccept) continue;
            Quest quest;
            try
            {
                quest = QuestUtility.GenerateQuestAndMakeAvailable(script, 500f);
            }
            catch (Exception ex)
            {
                Log.Message($"{TestHelpers.LogPrefix}{name} could not be generated: {ex.Message}");
                continue;
            }
            if (quest != null && quest.State == QuestState.NotYetAccepted) return quest;
            if (quest != null) Find.QuestManager.Remove(quest);
        }
        return null;
    }

    /// <summary>
    /// 任務地點與任務的關聯：產生一個會在世界地圖放地點的任務，清單裡該地點要連到這個任務；結束後移除任務與新增的世界物件。
    /// </summary>
    [Test]
    public void QuestSiteIsLinkedToItsQuest()
    {
        if (TestHelpers.SkipWithoutMap(nameof(QuestSiteIsLinkedToItsQuest))) return;
        var worldObjectsBefore = new HashSet<WorldObject>(Find.WorldObjects.AllWorldObjects);
        Quest quest = GenerateManualQuest("OpportunitySite_BanditCamp", "TradeRequest");
        if (quest == null)
        {
            TestHelpers.Skip(nameof(QuestSiteIsLinkedToItsQuest), "no manually accepted quest could be generated on this world");
            return;
        }
        var failures = new List<string>();
        try
        {
            // 營地這類據點要等任務接受（QuestPart_SpawnWorldObject 收到訊號）才出現在世界上；接受前查詢略過它是對的。
            List<WorldObject> unspawned = [.. QuestTargets(quest).Where(o => !o.Spawned)];
            if (_world.GetWorldSurvey(-1).Objects.Any(o => unspawned.Any(u => u.ID == o.Id)))
                failures.Add("the survey lists a quest site that is not on the world yet");
            Pawn accepter = quest.RequiresAccepter
                ? PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoSuspended.FirstOrDefault(p => QuestUtility.CanPawnAcceptQuest(p, quest))
                : null;
            // 有獎勵選項時原版要先選定再接受，否則會記錯誤並自動選第一項。
            QuestPart_Choice choice = quest.PartsListForReading.OfType<QuestPart_Choice>().FirstOrDefault();
            if (choice != null && choice.choices.Count > 0) choice.Choose(choice.choices[0]);
            quest.Accept(accepter);
            List<WorldObject> targets = [.. QuestTargets(quest).Where(o => o.Spawned)];
            if (targets.Count == 0)
            {
                TestHelpers.Skip(nameof(QuestSiteIsLinkedToItsQuest), "the accepted quest has no world object on the world");
                return;
            }
            WorldSurveyInfo survey = _world.GetWorldSurvey(-1);
            foreach (WorldObject target in targets)
            {
                WorldObjectInfo info = survey.Objects.FirstOrDefault(o => o.Id == target.ID);
                if (info == null) failures.Add(string.Create(CultureInfo.InvariantCulture, $"quest target {target.LabelCap} [{target.ID}] is not in the survey"));
                else if (!info.Quests.Any(q => q.QuestId == quest.id && q.Active)) failures.Add(string.Create(CultureInfo.InvariantCulture, $"{target.LabelCap} is not linked to the accepted quest {quest.id}"));
            }
        }
        finally
        {
            CleanupQuest(quest, worldObjectsBefore);
        }
        TestHelpers.AssertNone(failures, "quest links");
    }

    private static void CleanupQuest(Quest quest, HashSet<WorldObject> worldObjectsBefore)
    {
        if (!quest.Historical) quest.End(QuestEndOutcome.Unknown, sendLetter: false, playSound: false);
        Find.QuestManager.Remove(quest);
        foreach (WorldObject created in Find.WorldObjects.AllWorldObjects.Where(o => !worldObjectsBefore.Contains(o)).ToList())
            if (!created.Destroyed) created.Destroy();
    }

    private static List<WorldObject> QuestTargets(Quest quest) =>
        [.. quest.QuestLookTargets.Where(t => t.HasWorldObject).Select(t => t.WorldObject).Where(o => o != null).Distinct()];
}
