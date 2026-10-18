using System.Linq;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests;

[TestFixture]
public class CombatToolsTests
{
    private FakeGameWorld _world;
    private CombatTools _tools;

    [SetUp]
    public void SetUp()
    {
        _world = new FakeGameWorld();
        _world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
        _world.Colonists.Add(FakeGameWorld.MakeColonist(102, "Bob", "", "Jones"));
        _world.Colonists.Add(FakeGameWorld.MakeColonist(103, "Cid", "", "Lee"));
        _world.Colonists[2].Downed = true;
        _world.Threats.Add(new ThreatInfo { Id = 900, Label = "grizzly bear", Kind = "Manhunter", X = 20, Z = 20 });
        _tools = new CombatTools(_world);
    }

    [Test]
    public void DraftAllSkipsDowned()
    {
        string text = _tools.Draft("all", true);
        Assert.That(text, Does.StartWith("Drafted 2: Ali, Bob.").And.Contain("Cid is downed"));
        Assert.That(_world.Colonists[0].Drafted, Is.True);
        Assert.That(_world.Colonists[2].Drafted, Is.False);
    }

    [Test]
    public void UndraftReportsVerb()
    {
        _world.Colonists[1].Drafted = true;
        Assert.That(_tools.Draft("Bob", false), Is.EqualTo("Undrafted 1: Bob."));
        Assert.That(_world.Colonists[1].Drafted, Is.False);
    }

    [Test]
    public void MoveDraftsFirstAndOrdersMove()
    {
        _world.Colonists[0].Drafted = true;
        string text = _tools.OrderMove("Ali, Bob", 25, 30);
        Assert.That(text, Is.EqualTo("Ordered Ali, Bob to move to (25,30). Drafted first: Bob."));
        Assert.That(_world.Calls, Is.EqualTo(["OrderMoveTo(101,25,30)", "SetDrafted(102,True)", "OrderMoveTo(102,25,30)"]));
    }

    [Test]
    public void MoveOutsideMapIsRejectedBeforeAnything()
    {
        Assert.That(_tools.OrderMove("all", 500, 30), Does.StartWith("Error:").And.Contain("outside"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void MoveToUnwalkableCellReportsGameReason()
    {
        _world.UnwalkableCells.Add((25, 30));
        Assert.That(_tools.OrderMove("Bob", 25, 30), Does.StartWith("Error:").And.Contain("not walkable"));
    }

    [Test]
    public void AttackKnownThreat()
    {
        string text = _tools.OrderAttack("all", 900);
        Assert.That(text, Does.StartWith("Ordered Ali, Bob to attack grizzly bear [900].").And.Contain("Cid is downed"));
        Assert.That(_world.Calls, Does.Contain("OrderAttack(101,900)").And.Contain("SetDrafted(101,True)"));
    }

    [Test]
    public void AttackUnknownTargetIsAnError()
    {
        Assert.That(_tools.OrderAttack("all", 1234), Does.StartWith("Error:").And.Contain("list_threats"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void AttackFallsBackToPawnsFromFindThings()
    {
        _world.Things.Add(FakeGameWorld.MakeThing(77, "Deer", "deer", ThingKind.Pawn, 1, 1));
        Assert.That(_tools.OrderAttack("Bob", 77), Does.StartWith("Ordered Bob to attack deer [77]."));
    }

    [Test]
    public void BlockedDraftReturnsPreviewWithoutTouchingWorld()
    {
        var gated = new CombatTools(_world, new ConfirmationGate(() => AgentMode.Confirm));

        string text = gated.Draft("all", true);

        Assert.That(text, Does.StartWith("Needs confirmation:").And.Contain("draft"));
        Assert.That(_world.Calls, Is.Empty);
        Assert.That(_world.Colonists.All(c => !c.Drafted), Is.True);
    }

    [Test]
    public void OrderJobPassesCanonicalJobToWorld()
    {
        string text = _tools.OrderJob("Ali", " Capture ", 900);

        Assert.That(text, Is.EqualTo("Ordered Ali to capture [900]: capture [900]."));
        Assert.That(_world.Calls, Is.EqualTo(["OrderJob(101,capture,900)"]));
    }

    [Test]
    public void OrderJobRefusesUnknownJobAndReportsWorldFailure()
    {
        Assert.That(_tools.OrderJob("Ali", "butcher", 900), Does.StartWith("Error:").And.Contain("rescue, capture, tend, strip, equip or enter_casket"));
        Assert.That(_world.Calls, Is.Empty);

        Assert.That(_tools.OrderJob("Bob", "rescue", 4242), Is.EqualTo("Error: Bob cannot rescue [4242]: target not found on the map"));
    }

    [Test]
    public void OrderJobAcceptsEnterCasket()
    {
        _world.Things.Add(FakeGameWorld.MakeThing(500, "CryptosleepCasket", "cryptosleep casket", ThingKind.Building, 30, 30, playerOwned: true));

        Assert.That(_tools.OrderJob("Bob", "Enter_Casket", 500), Is.EqualTo("Ordered Bob to enter_casket [500]: enter_casket [500]."));
        Assert.That(_world.Calls, Is.EqualTo(["OrderJob(102,enter_casket,500)"]));
    }

    [Test]
    public void BlockedOrderJobReturnsPreviewWithoutTouchingWorld()
    {
        var gated = new CombatTools(_world, new ConfirmationGate(() => AgentMode.Confirm));

        Assert.That(gated.OrderJob("Bob", "rescue", 103), Does.StartWith("Needs confirmation:").And.Contain("rescue [103]"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void BlockedAttackReturnsPreviewWithoutTouchingWorld()
    {
        var gated = new CombatTools(_world, new ConfirmationGate(() => AgentMode.Confirm));

        string text = gated.OrderAttack("Bob", 900);

        Assert.That(text, Does.StartWith("Needs confirmation:").And.Contain("grizzly bear"));
        Assert.That(_world.Calls, Is.Empty);
    }
}
