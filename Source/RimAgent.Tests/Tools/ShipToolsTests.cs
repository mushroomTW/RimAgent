using NUnit.Framework;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests;

[TestFixture]
public class ShipToolsTests
{
    private FakeGameWorld _world;
    private ShipTools _tools;

    [SetUp]
    public void SetUp()
    {
        _world = new FakeGameWorld();
        _world.RequiredShipParts.Add(new ShipPartRequirement { DefName = "Ship_ComputerCore", Label = "computer core", Count = 1 });
        _tools = new ShipTools(_world);
    }

    private ShipPartInfo Add(string defName, string label, int minX, int minZ, int maxX, int maxZ, ShipPartState state = ShipPartState.Blueprint)
    {
        var part = new ShipPartInfo
        {
            Id = _world.ShipParts.Count + 1,
            DefName = defName,
            Label = label,
            State = state,
            Rect = new CellRectSpec(minX, minZ, maxX, maxZ)
        };
        _world.ShipParts.Add(part);
        return part;
    }

    private ShipPartInfo Core(int minX = 10, int minZ = 10) => Add("Ship_ComputerCore", "computer core", minX, minZ, minX + 1, minZ + 1);

    [Test]
    public void NoPartsListsWhatIsRequired()
    {
        Assert.That(_tools.CheckShipLayout(), Is.EqualTo("No ship parts on this map. Required: 1 computer core."));
    }

    [Test]
    public void PartsSharingAnEdgeAreOneGroupEvenAsBlueprints()
    {
        Core();
        Add("Ship_Beam", "ship beam", 12, 11, 13, 16);

        string text = _tools.CheckShipLayout();

        Assert.That(text, Does.Contain("Layout OK"));
        Assert.That(text, Does.Not.Contain("Group 2"));
        Assert.That(text, Does.Contain("2 blueprint(s)"));
    }

    [Test]
    public void ConnectionPassesThroughIntermediateParts()
    {
        Core();
        Add("Ship_Beam", "ship beam", 12, 10, 13, 15);
        Add("Ship_Engine", "ship engine", 14, 15, 16, 18);

        Assert.That(_tools.CheckShipLayout(), Does.Contain("Layout OK"));
    }

    [Test]
    public void OneCellGapReportsDistanceAndDirection()
    {
        Core();
        Add("Ship_Engine", "ship engine", 13, 10, 15, 13);

        string text = _tools.CheckShipLayout();

        Assert.That(text, Does.Contain("Group 2 is not connected: ship engine (13,10)-(15,13) and computer core (10,10)-(11,11) have 1 empty cell(s)"));
        Assert.That(text, Does.Contain("Moving the former 1 west"));
    }

    [Test]
    public void CornerContactDoesNotConnect()
    {
        Core();
        Add("Ship_Engine", "ship engine", 12, 12, 14, 15);

        string text = _tools.CheckShipLayout();

        Assert.That(text, Does.Contain("touch only at a corner"));
        Assert.That(text, Does.Contain("Moving the former 1 west"));
    }

    [Test]
    public void DiagonalGapGivesAShiftThatActuallyTouches()
    {
        Core();
        Add("Ship_Engine", "ship engine", 14, 13, 16, 16);

        string text = _tools.CheckShipLayout();

        // 往西 3 格後 x 範圍 11-13 與核心重疊，往南 1 格後 z 從 12 開始，正好貼住核心上緣。
        Assert.That(text, Does.Contain("diagonal, 2 cell(s) apart along x and 1 along z"));
        Assert.That(text, Does.Contain("Moving the former 3 west and 1 south"));
    }

    [Test]
    public void MainGroupIsTheOneWithTheComputerCoreEvenWhenSmaller()
    {
        Add("Ship_Beam", "ship beam", 30, 30, 31, 35);
        Add("Ship_Engine", "ship engine", 32, 30, 34, 33);
        Core();

        string text = _tools.CheckShipLayout();

        Assert.That(text, Does.Contain("Group 1: computer core"));
        Assert.That(text, Does.Contain("Group 2:"));
    }

    [Test]
    public void RequiredPartsOutsideTheMainGroupStillCountAsMissing()
    {
        _world.RequiredShipParts.Add(new ShipPartRequirement { DefName = "Ship_Engine", Label = "ship engine", Count = 3 });
        Core();
        Add("Ship_Engine", "ship engine", 12, 10, 14, 13);
        Add("Ship_Engine", "ship engine", 50, 50, 52, 53);

        Assert.That(_tools.CheckShipLayout(), Does.Contain("Missing from group 1: 2 ship engine."));
    }

    [Test]
    public void CasketEntryCoveredByBeamBlueprintIsReported()
    {
        Core();
        Add("Ship_Beam", "ship beam", 12, 10, 13, 15);
        ShipPartInfo casket = Add("Ship_CryptosleepCasket", "cryptosleep casket", 11, 12, 11, 13);
        casket.InteractionCell = (12, 12);

        string text = _tools.CheckShipLayout();

        Assert.That(text, Does.Contain("colonists enter from (12,12), which is blocked by ship beam (12,10)-(13,15)"));
        Assert.That(text, Does.Not.Contain("Layout OK"));
    }

    [Test]
    public void CasketEntryBlockedByWorldThingIsReported()
    {
        Core();
        ShipPartInfo casket = Add("Ship_CryptosleepCasket", "cryptosleep casket", 12, 10, 12, 11);
        casket.InteractionCell = (13, 10);
        casket.InteractionBlockedBy = "granite wall";

        Assert.That(_tools.CheckShipLayout(), Does.Contain("blocked by granite wall"));
    }

    [Test]
    public void FreeCasketEntryIsNotAProblem()
    {
        Core();
        ShipPartInfo casket = Add("Ship_CryptosleepCasket", "cryptosleep casket", 12, 10, 12, 11);
        casket.InteractionCell = (13, 10);

        Assert.That(_tools.CheckShipLayout(), Does.Contain("Layout OK"));
    }

    [Test]
    public void RemoveBlueprintReportsWhatWasCancelled()
    {
        _world.RemovableBlueprints.Add("ship engine (13,10)-(15,13)");
        var build = new BuildTools(_world);

        string text = build.RemoveBlueprint(15, 13, 13, 10);

        Assert.That(text, Is.EqualTo("Cancelled 1: ship engine (13,10)-(15,13)."));
        Assert.That(_world.LastRemovedArea.ToString(), Is.EqualTo("(13,10)-(15,13)"));
    }

    [Test]
    public void RemoveBlueprintWithNothingThereIsAnError()
    {
        Assert.That(new BuildTools(_world).RemoveBlueprint(1, 1, 2, 2), Does.Contain("no blueprints or frames"));
    }
}
