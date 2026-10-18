using System.Linq;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Tools;

namespace RimAgent.Tests;

[TestFixture]
public class WorkToolsTests
{
    private FakeGameWorld _world;
    private WorkTools _tools;

    [SetUp]
    public void SetUp()
    {
        _world = new FakeGameWorld();
        _world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
        _world.Colonists.Add(FakeGameWorld.MakeColonist(102, "Bob", "", "Jones"));
        _tools = new WorkTools(_world);
    }

    [Test]
    public void SetPriorityChangesWorldAndReportsOldAndNew()
    {
        string text = _tools.SetWorkPriorities("Ali:doctor=1");
        Assert.That(text, Is.EqualTo("Set Ali's doctor priority 3 -> 1."));
        Assert.That(_world.Colonists[0].WorkPriorities.First(w => w.WorkTypeDefName == "Doctor").Priority, Is.EqualTo(1));
        Assert.That(_world.Calls, Does.Contain("SetWorkPriority(101,Doctor,1)"));
    }

    [Test]
    public void UnreadablePrioritiesAreNotChangedSoUndoStaysCorrect()
    {
        // 其他模組讓優先級讀不到時記不了原值；照改的話 /undo 會寫回預設的 3。
        _world.Colonists[0].UnreadableParts = new[] { Game.ColonistInfo.WorkPrioritiesPart };

        Assert.That(_tools.SetWorkPriorities("Ali:doctor=1"), Does.StartWith("Error:").And.Contain("could not be read"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void PriorityOutOfRangeIsRejectedWithoutTouchingWorld()
    {
        Assert.That(_tools.SetWorkPriorities("Ali:doctor=5"), Does.StartWith("Error:").And.Contain("0"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void IncapableWorkIsRefused()
    {
        _world.Colonists[1].WorkPriorities.First(w => w.WorkTypeDefName == "Growing").Disabled = true;
        string text = _tools.SetWorkPriorities("Bob:grow=2");
        Assert.That(text, Does.StartWith("Error:").And.Contain("incapable"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void UnknownColonistListsCandidates()
    {
        Assert.That(_tools.SetWorkPriorities("Zed:doctor=1"), Does.StartWith("Error:").And.Contain("[101]").And.Contain("[102]"));
    }

    [Test]
    public void UnknownWorkTypeIsAnError()
    {
        Assert.That(_tools.SetWorkPriorities("Ali:piloting=1"), Does.StartWith("Error:").And.Contain("work type"));
    }

    [Test]
    public void ListAllShowsEveryColonist()
    {
        string text = _tools.ListWorkPriorities("all");
        Assert.That(text, Does.Contain("[101] Ali: ").And.Contain("[102] Bob: ").And.Contain("doctor=3"));
    }

    [Test]
    public void ScheduleWrapsAroundMidnight()
    {
        string text = _tools.SetSchedule("all", 22, 6, "sleep");
        Assert.That(text, Does.Contain("8 hours").And.Contain("sleep").And.Contain("Ali").And.Contain("Bob"));
        var table = _world.Colonists[0].Timetable;
        Assert.That(table[22], Is.EqualTo("Sleep"));
        Assert.That(table[5], Is.EqualTo("Sleep"));
        Assert.That(table[6], Is.EqualTo("Anything"));
        Assert.That(table[21], Is.EqualTo("Anything"));
        Assert.That(_world.Calls.Count(c => c.StartsWith("SetTimeAssignment(")), Is.EqualTo(16));
    }

    [Test]
    public void ScheduleSameHourIsAnError()
    {
        Assert.That(_tools.SetSchedule("all", 8, 8, "work"), Does.StartWith("Error:"));
        Assert.That(_tools.SetSchedule("all", 8, 24, "work"), Does.StartWith("Error:"));
    }

    [Test]
    public void ScheduleUnknownAssignmentIsAnError()
    {
        Assert.That(_tools.SetSchedule("Ali", 8, 12, "party"), Does.StartWith("Error:").And.Contain("time assignment"));
    }

    [Test]
    public void BlockedPriorityReturnsPreviewWithoutTouchingWorld()
    {
        var gated = new WorkTools(_world, null, new ConfirmationGate(() => AgentMode.Confirm));

        string text = gated.SetWorkPriorities("Ali:doctor=1");

        Assert.That(text, Does.StartWith("Needs confirmation:").And.Contain("Ali's doctor priority 3 -> 1"));
        Assert.That(_world.Calls, Is.Empty);
        Assert.That(_world.Colonists[0].WorkPriorities.First(w => w.WorkTypeDefName == "Doctor").Priority, Is.EqualTo(3));
    }

    [Test]
    public void BlockedScheduleReturnsPreviewWithoutTouchingWorld()
    {
        var gated = new WorkTools(_world, null, new ConfirmationGate(() => AgentMode.Confirm));

        string text = gated.SetSchedule("Ali", 22, 23, "sleep");

        Assert.That(text, Does.StartWith("Needs confirmation:").And.Contain("sleep"));
        Assert.That(_world.Calls, Is.Empty);
    }
}
