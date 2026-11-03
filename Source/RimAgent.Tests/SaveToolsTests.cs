using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests;

/// <summary>讀檔重來：存檢查點、列出這一局的存檔、讀檔次數上限與時間線規則。</summary>
[TestFixture]
public class SaveToolsTests
{
    private FakeGameWorld _world;
    private SaveLedger _ledger;
    private SaveTools _tools;

    [SetUp]
    public void SetUp()
    {
        _world = new FakeGameWorld();
        _ledger = new SaveLedger();
        _tools = new SaveTools(_world, ledger: _ledger);
    }

    private SaveSlotInfo AddSave(string name, int tick, bool checkpoint = false, string colonyId = "colony1")
    {
        var save = new SaveSlotInfo { Name = name, Tick = tick, Checkpoint = checkpoint, ColonyId = colonyId, WrittenUtcTicks = tick };
        _world.SaveFiles.Add(save);
        return save;
    }

    [Test]
    public async Task ListShowsOnlyThisColonyNewestFirst()
    {
        AddSave("Autosave-1", 120000);
        AddSave("RimAgent-colony1-240000", 240000, checkpoint: true);
        AddSave("Autosave-2", 200000, colonyId: "other");
        AddSave("Autosave-3", 180000, colonyId: string.Empty);

        string text = await _tools.ListSaves();

        Assert.That(text, Does.Contain("RimAgent-colony1-240000 [checkpoint]").And.Contain("Autosave-1 [autosave]"));
        Assert.That(text, Does.Not.Contain("Autosave-2").And.Not.Contain("Autosave-3"));
        Assert.That(text.IndexOf("RimAgent-colony1-240000"), Is.LessThan(text.IndexOf("Autosave-1")));
        Assert.That(text, Does.Contain("reloads used 0/3, can be loaded"));
    }

    [Test]
    public async Task SaveIsRefusedInDanger()
    {
        _world.Overview.HostileCount = 2;

        Assert.That(await _tools.SaveGame(), Does.StartWith("Error:").And.Contain("hostiles"));
        Assert.That(_world.Calls, Has.None.StartsWith("SaveGame"));
    }

    [Test]
    public async Task SaveNamesCheckpointByColonyAndTickAndKeepsNewestFive()
    {
        for (int i = 1; i <= 6; i++) AddSave("RimAgent-colony1-" + i * 1000, i * 1000, checkpoint: true);

        string text = await _tools.SaveGame();

        Assert.That(text, Is.EqualTo("Saved checkpoint RimAgent-colony1-300000."));
        string[] left = _world.SaveFiles.Where(s => s.Checkpoint).Select(s => s.Name).ToArray();
        Assert.That(left, Has.Length.EqualTo(SaveTools.MaxCheckpoints));
        Assert.That(left, Does.Contain("RimAgent-colony1-300000").And.Contain("RimAgent-colony1-3000"));
        Assert.That(left, Does.Not.Contain("RimAgent-colony1-2000"));
    }

    [Test]
    public async Task PermadeathCannotSaveOrLoad()
    {
        _world.SaveContext.Permadeath = true;
        AddSave("Autosave-1", 1000);

        Assert.That(await _tools.SaveGame(), Does.StartWith("Error:").And.Contain("commitment"));
        Assert.That(await _tools.LoadSave("Autosave-1", "lesson"), Does.StartWith("Error:").And.Contain("commitment"));
        Assert.That(_world.LoadedSave, Is.Null);
    }

    [Test]
    public async Task LoadNeedsALessonAndAKnownSave()
    {
        AddSave("Autosave-1", 1000);

        Assert.That(await _tools.LoadSave("Autosave-1", " "), Does.StartWith("Error:").And.Contain("lesson"));
        Assert.That(await _tools.LoadSave("Autosave-9", "x"), Does.StartWith("Error:").And.Contain("Autosave-1"));
        Assert.That(_world.LoadedSave, Is.Null);
    }

    [Test]
    public async Task LoadRecordsTheAttemptAndHandsTheLessonOver()
    {
        AddSave("Autosave-1", 1000);

        string text = await _tools.LoadSave("Autosave-1", "raid from the east; build walls first");

        Assert.That(text, Does.Contain("attempt 1/3"));
        Assert.That(_world.LoadedSave, Is.EqualTo("Autosave-1"));
        Assert.That(_ledger.TakePending("other"), Is.Null, "別局讀到的不是這次讀檔");
    }

    [Test]
    public async Task PendingReloadGoesToTheSameColonyOnly()
    {
        AddSave("Autosave-1", 1000);
        await _tools.LoadSave("Autosave-1", "lesson one");

        ReloadRecord pending = _ledger.TakePending("colony1");

        Assert.That(pending.SaveName, Is.EqualTo("Autosave-1"));
        Assert.That(pending.Lesson, Is.EqualTo("lesson one"));
        Assert.That(_ledger.TakePending("colony1"), Is.Null, "只交接一次");
        Assert.That(_ledger.HistoryOf("colony1"), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ASaveCanBeReloadedThreeTimes()
    {
        AddSave("Autosave-1", 1000);
        for (int i = 1; i <= SaveLedger.MaxLoadsPerSave; i++)
        {
            Assert.That(await _tools.LoadSave("Autosave-1", "try " + i), Does.Contain($"attempt {i}/3"));
        }

        Assert.That(await _tools.LoadSave("Autosave-1", "again"), Does.StartWith("Error:").And.Contain("accept the loss"));
        Assert.That(await _tools.ListSaves(), Does.Contain("reloads used 3/3, locked"));
    }

    [Test]
    public async Task OlderSavesLockOnceANewerOneIsUsedUp()
    {
        AddSave("Autosave-1", 1000);
        AddSave("Autosave-2", 2000);
        for (int i = 0; i < SaveLedger.MaxLoadsPerSave; i++) await _tools.LoadSave("Autosave-2", "try");

        string text = await _tools.LoadSave("Autosave-1", "go further back");

        Assert.That(text, Does.StartWith("Error:").And.Contain("going further back is not allowed"));
        Assert.That(_world.LoadedSave, Is.EqualTo("Autosave-2"));
    }

    [Test]
    public async Task LoadingAnOlderSaveDiscardsTheNewerTimeline()
    {
        AddSave("Autosave-1", 1000);
        AddSave("RimAgent-colony1-2000", 2000, checkpoint: true);
        AddSave("Autosave-2", 3000);

        await _tools.LoadSave("Autosave-1", "lesson");

        Assert.That(_world.Calls, Does.Contain("DeleteCheckpoint(RimAgent-colony1-2000)"));
        Assert.That(await _tools.ListSaves(), Does.Not.Contain("Autosave-2"), "被放棄的時間線上的 autosave 不能再讀");
    }

    [Test]
    public async Task ConfirmModePreviewsALoadWithoutLoading()
    {
        AddSave("Autosave-1", 1000);
        var gate = new ConfirmationGate(() => AgentMode.Confirm);
        var tools = new SaveTools(_world, gate, _ledger);

        string text = await tools.LoadSave("Autosave-1", "lesson");

        Assert.That(text, Does.StartWith(ToolText.ConfirmationPrefix).And.Contain("attempt 1/3"));
        Assert.That(_world.LoadedSave, Is.Null);
        Assert.That(_ledger.LoadsOf(_world.SaveFiles[0]), Is.EqualTo(0));
    }

    [Test]
    public void SaveToolsAreOffUnlessTheSaveCategoryIsAllowed()
    {
        var registry = new AgentToolRegistry(_world, new AgentTranscript(), null, null);

        Assert.That(registry.Select(ToolPermissions.For(ToolCategory.Time)).Select(t => t.Name), Has.None.EqualTo("load_save"));
        Assert.That(registry.Select(ToolPermissions.For(ToolCategory.Save)).Select(t => t.Name),
            Is.SupersetOf(new[] { "list_saves", "save_game", "load_save" }));
    }
}

/// <summary>從存檔讀殖民地代號與 tick，不載入整份存檔。</summary>
[TestFixture]
public class SaveFileScannerTests
{
    private string _path;

    [SetUp]
    public void SetUp()
    {
        _path = Path.Combine(Path.GetTempPath(), "RimAgentScan-" + System.Guid.NewGuid().ToString("N") + ".rws");
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Test]
    public void ReadsTheColonyIdAndTheTickManagersTicks()
    {
        File.WriteAllText(_path,
            "<savegame><meta><gameVersion>1.7</gameVersion></meta><game>" +
            "<strikes><li><ticksGame>5</ticksGame></li></strikes>" +
            "<tickManager><ticksGame>123456</ticksGame><settleTick>1</settleTick></tickManager>" +
            "<components><li Class=\"RimAgent.RimAgentGameComponent\"><goals /><rimAgentColonyId>ab12cd34</rimAgentColonyId></li></components>" +
            "</game></savegame>");

        SaveFileScanner.Scan scan = SaveFileScanner.Read(_path, 1);

        Assert.That(scan.ColonyId, Is.EqualTo("ab12cd34"));
        Assert.That(scan.Tick, Is.EqualTo(123456));
    }

    [Test]
    public void FindsAdjacentFieldsRightAfterEachOther()
    {
        // 讀完一個欄位時讀取器已停在下一個節點：緊鄰的另一個欄位不能被跳過。
        File.WriteAllText(_path,
            "<savegame><game><tickManager><ticksGame>42</ticksGame></tickManager><rimAgentColonyId>id1</rimAgentColonyId></game></savegame>");

        SaveFileScanner.Scan scan = SaveFileScanner.Read(_path, 1);

        Assert.That(scan.ColonyId, Is.EqualTo("id1"));
        Assert.That(scan.Tick, Is.EqualTo(42));
    }

    [Test]
    public void ABrokenFileHasNoColony()
    {
        File.WriteAllText(_path, "<savegame><game><tickManager>");

        Assert.That(SaveFileScanner.Read(_path, 1).ColonyId, Is.Empty);
    }
}
