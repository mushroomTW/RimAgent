using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests;

[TestFixture]
public class QuestToolsTests
{
    private FakeGameWorld _world;
    private QuestTools _tools;

    [SetUp]
    public void SetUp()
    {
        _world = new FakeGameWorld();
        _world.Quests.Add(new QuestInfo
        {
            Id = 7,
            Name = "Hospitality request",
            Description = "A group asks to stay for a while.",
            ExpiresInTicks = 150000,
            RewardOptions = new[]
            {
                new QuestRewardInfo { Number = 1, Rewards = new[] { "Silver x300" } },
                new QuestRewardInfo { Number = 2, Rewards = new[] { "Goodwill +10 with Outlanders" } }
            }
        });
        _world.Quests.Add(new QuestInfo
        {
            Id = 9,
            Name = "Ascent",
            Description = "Reach the title of Baron.",
            CanAccept = false,
            UnmetRequirements = new[] { "Requires a colonist with the title Count" }
        });
        _world.Quests.Add(new QuestInfo
        {
            Id = 11,
            Name = "Favor for the empire",
            RequiresAccepter = true,
            RewardOptions = new[] { new QuestRewardInfo { Number = 1, Rewards = new[] { "Favor +4" }, RequiresAccepter = true } },
            Accepters = new[]
            {
                new QuestAccepterInfo { Id = 101, Name = "Ali", FullName = "Alice Smith" },
                new QuestAccepterInfo { Id = 102, Name = "Bob", FullName = "Bob Jones", Warning = "Bob is conceited." }
            }
        });
        _world.Quests.Add(new QuestInfo
        {
            Id = 12,
            Name = "Single accepter",
            RequiresAccepter = true,
            Accepters = new[] { new QuestAccepterInfo { Id = 101, Name = "Ali", FullName = "Alice Smith" } }
        });
        _world.Quests.Add(new QuestInfo
        {
            Id = 20,
            Name = "Defend the ship",
            State = QuestInfo.ActiveState,
            AccepterName = "Ali",
            Deadlines = new[] { "Ends in 3 days" }
        });
        _world.Quests.Add(new QuestInfo { Id = 30, Name = "Old offer", Dismissed = true });
        _tools = new QuestTools(_world);
    }

    [Test]
    public void ListShowsAvailableThenActiveWithExpiryRewardsAndRequirements()
    {
        string text = _tools.ListQuests();

        Assert.That(text, Does.StartWith("[7] Hospitality request (available, expires in 2.5 days)"));
        Assert.That(text, Does.Contain("A group asks to stay for a while."));
        Assert.That(text, Does.Contain("Can accept: yes"));
        Assert.That(text, Does.Contain("Reward choices: 1) Silver x300; 2) Goodwill +10 with Outlanders"));
        Assert.That(text, Does.Contain("[9] Ascent (available)"));
        Assert.That(text, Does.Contain("Can accept: NO - Requires a colonist with the title Count"));
        Assert.That(text, Does.Contain("Colonists who can accept: [101] Ali; [102] Bob (warning: Bob is conceited.)"));
        Assert.That(text, Does.Contain("[20] Defend the ship (active, accepted by Ali)").And.Contain("Deadline: Ends in 3 days"));
        Assert.That(text.IndexOf("[20]"), Is.GreaterThan(text.IndexOf("[11]")), "進行中的排在可接受之後");
    }

    [Test]
    public void ListHidesDismissedUnlessAskedAndSaysSo()
    {
        string text = _tools.ListQuests();
        Assert.That(text, Does.Not.Contain("Old offer"));
        Assert.That(text, Does.Contain("1 dismissed quest(s) are hidden"));

        Assert.That(_tools.ListQuests(includeDismissed: true), Does.Contain("[30] Old offer (available, dismissed)"));
    }

    [Test]
    public void ListWithIdShowsFullDescriptionAndUnknownIdIsAnError()
    {
        _world.Quests[0].Description = new string('x', 800);

        Assert.That(_tools.ListQuests(questId: 7), Does.Contain(new string('x', 800)));
        Assert.That(_tools.ListQuests(), Does.Not.Contain(new string('x', 400)), "清單裡的描述會截短");
        Assert.That(_tools.ListQuests(questId: 99), Does.StartWith("Error:").And.Contain("list_quests"));
    }

    [Test]
    public void ListReportsNoneWhenEmpty()
    {
        _world.Quests.Clear();
        Assert.That(_tools.ListQuests(), Is.EqualTo("No available or active quests."));
    }

    [Test]
    public void AcceptWithRewardChoiceCallsTheWorld()
    {
        string text = _tools.AcceptQuest(7, 2);

        Assert.That(text, Is.EqualTo("Accepted quest \"Hospitality request\" [7] for reward 2 (Goodwill +10 with Outlanders)."));
        Assert.That(_world.Calls, Is.EqualTo(["AcceptQuest(7,2,0)"]));
        Assert.That(_world.Quests[0].State, Is.EqualTo(QuestInfo.ActiveState));
    }

    [Test]
    public void AcceptWithSeveralRewardChoicesNeedsOne()
    {
        string text = _tools.AcceptQuest(7);

        Assert.That(text, Does.StartWith("Error:").And.Contain("needs a reward choice").And.Contain("1) Silver x300; 2) Goodwill"));
        Assert.That(_tools.AcceptQuest(7, 3), Does.StartWith("Error:").And.Contain("needs a reward choice"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void AcceptWithSingleRewardChoiceNeedsNoNumber()
    {
        _world.Quests[0].RewardOptions = new[] { new QuestRewardInfo { Number = 1, Rewards = new[] { "Silver x300" } } };

        Assert.That(_tools.AcceptQuest(7), Does.StartWith("Accepted quest").And.Contain("for reward 1"));
        Assert.That(_world.Calls, Is.EqualTo(["AcceptQuest(7,1,0)"]));
    }

    [Test]
    public void AcceptRefusesWhenRequirementsAreNotMetWithTheGameReason()
    {
        string text = _tools.AcceptQuest(9);

        Assert.That(text, Does.StartWith("Error:").And.Contain("cannot be accepted now").And.Contain("title Count"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void AcceptRefusesWithoutAGameReasonStillExplains()
    {
        _world.Quests[1].UnmetRequirements = new string[0];

        Assert.That(_tools.AcceptQuest(9), Does.StartWith("Error:").And.Contain("does not allow accepting"));
    }

    [Test]
    public void AcceptRefusesUnknownExpiredOrAlreadyAcceptedQuests()
    {
        // 過期的任務不在任務分頁的清單裡，與不存在的 id 同樣處理。
        Assert.That(_tools.AcceptQuest(99), Does.StartWith("Error:").And.Contain("list_quests"));
        Assert.That(_tools.AcceptQuest(20), Does.StartWith("Error:").And.Contain("already accepted"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void AcceptNeedingAnAccepterAsksWhichColonistWhenSeveralQualify()
    {
        string text = _tools.AcceptQuest(11);

        Assert.That(text, Does.StartWith("Error:").And.Contain("needs an accepter").And.Contain("[101] Ali").And.Contain("[102] Bob"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void AcceptUsesTheNamedAccepter()
    {
        _world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));

        string text = _tools.AcceptQuest(11, accepter: "alice smith");

        Assert.That(text, Is.EqualTo("Accepted quest \"Favor for the empire\" [11] for reward 1 (Favor +4) with Ali as the accepter."));
        Assert.That(_world.Calls, Is.EqualTo(["AcceptQuest(11,1,101)"]));
        Assert.That(_world.Quests[2].AccepterName, Is.EqualTo("Ali"));
    }

    [Test]
    public void AcceptRepeatsTheGameWarningOfTheChosenAccepter()
    {
        string text = _tools.AcceptQuest(11, accepter: "102");

        Assert.That(text, Does.Contain("with Bob as the accepter").And.Contain("Warning from the game: Bob is conceited."));
        Assert.That(_world.Calls, Is.EqualTo(["AcceptQuest(11,1,102)"]));
    }

    [Test]
    public void AcceptRefusesAnAccepterWhoCannotAccept()
    {
        string text = _tools.AcceptQuest(11, accepter: "Zed");

        Assert.That(text, Does.StartWith("Error:").And.Contain("no colonist matches").And.Contain("[101]"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void AcceptPicksTheOnlyEligibleColonistAutomatically()
    {
        string text = _tools.AcceptQuest(12);

        Assert.That(text, Does.Contain("with Ali as the accepter"));
        Assert.That(_world.Calls, Is.EqualTo(["AcceptQuest(12,0,101)"]));
    }

    [Test]
    public void AcceptWithNoEligibleColonistIsRefused()
    {
        _world.Quests[3].Accepters = new QuestAccepterInfo[0];

        Assert.That(_tools.AcceptQuest(12), Does.StartWith("Error:").And.Contain("no colonist can accept"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void BlockedAcceptReturnsPreviewWithoutTouchingWorld()
    {
        var gated = new QuestTools(_world, new ConfirmationGate(() => AgentMode.Confirm));

        string text = gated.AcceptQuest(7, 1);

        Assert.That(text, Does.StartWith("Needs confirmation:").And.Contain("Hospitality request").And.Contain("Silver x300").And.Contain("cannot be undone"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void DismissAndRestoreToggleTheFlag()
    {
        Assert.That(_tools.DismissQuest(7), Is.EqualTo("Quest \"Hospitality request\" [7] dismissed."));
        Assert.That(_world.Quests[0].Dismissed, Is.True);
        Assert.That(_tools.DismissQuest(7, false), Is.EqualTo("Quest \"Hospitality request\" [7] restored."));
        Assert.That(_world.Quests[0].Dismissed, Is.False);
        Assert.That(_world.Calls, Is.EqualTo(["SetQuestDismissed(7,True)", "SetQuestDismissed(7,False)"]));
    }

    [Test]
    public void DismissIsIdempotentAndRefusesAcceptedOrUnknownQuests()
    {
        Assert.That(_tools.DismissQuest(30), Does.Contain("already dismissed"));
        Assert.That(_tools.DismissQuest(7, false), Does.Contain("already not dismissed"));
        Assert.That(_tools.DismissQuest(20), Does.StartWith("Error:").And.Contain("already accepted"));
        Assert.That(_tools.DismissQuest(99), Does.StartWith("Error:").And.Contain("list_quests"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void BlockedDismissReturnsPreviewWithoutTouchingWorld()
    {
        var gated = new QuestTools(_world, new ConfirmationGate(() => AgentMode.Confirm));

        Assert.That(gated.DismissQuest(7), Does.StartWith("Needs confirmation:").And.Contain("Hospitality request"));
        Assert.That(_world.Calls, Is.Empty);
    }
}
