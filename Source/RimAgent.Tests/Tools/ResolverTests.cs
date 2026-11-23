using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests;

[TestFixture]
public class DefResolverTests
{
    private static readonly DefInfo[] Defs =
    [
        new("Wall", "wall"),
        new("Door", "door"),
        new("SolarGenerator", "solar generator"),
        new("WindTurbine", "wind turbine"),
        new("Bed", "bed"),
        new("DoubleBed", "double bed")
    ];

    [Test]
    public void ExactDefNameWins_CaseInsensitive()
    {
        Result<DefInfo> r = DefResolver.Resolve("solargenerator", Defs, "buildable");
        Assert.That(r.Success, Is.True);
        Assert.That(r.Value.DefName, Is.EqualTo("SolarGenerator"));
    }

    [Test]
    public void ExactLabelMatches()
    {
        Result<DefInfo> r = DefResolver.Resolve("Wind Turbine", Defs, "buildable");
        Assert.That(r.Success, Is.True);
        Assert.That(r.Value.DefName, Is.EqualTo("WindTurbine"));
    }

    [Test]
    public void PrefixMatchesWhenUnique()
    {
        Result<DefInfo> r = DefResolver.Resolve("sol", Defs, "buildable");
        Assert.That(r.Success, Is.True);
        Assert.That(r.Value.DefName, Is.EqualTo("SolarGenerator"));
    }

    [Test]
    public void ExactMatchBeatsPrefixAmbiguity()
    {
        // "bed" 同時是 Bed 的 defName 與 DoubleBed 的 label 子字串；精確比對優先。
        Result<DefInfo> r = DefResolver.Resolve("bed", Defs, "buildable");
        Assert.That(r.Success, Is.True);
        Assert.That(r.Value.DefName, Is.EqualTo("Bed"));
    }

    [Test]
    public void ModDefWithoutLabelDoesNotBreakContainsMatching()
    {
        // 模組 def 常不填 label（Def.label 預設 null）；走到包含比對時不能因此丟例外。
        var defs = new[] { new DefInfo("ModX_Thing", label: null), new DefInfo("WindTurbine", "wind turbine") };

        Result<DefInfo> r = DefResolver.Resolve("turbine", defs, "buildable");

        Assert.That(r.Success, Is.True);
        Assert.That(r.Value.DefName, Is.EqualTo("WindTurbine"));
    }

    [Test]
    public void InputContainingLabelMatches()
    {
        // 玩家說「木牆」時模型可能傳 "wooden wall"：輸入包含 label "wall"。
        Result<DefInfo> r = DefResolver.Resolve("wooden wall", Defs, "buildable");
        Assert.That(r.Success, Is.True);
        Assert.That(r.Value.DefName, Is.EqualTo("Wall"));
    }

    [Test]
    public void AmbiguousListsCandidatesAndAsksForDefName()
    {
        var defs = new List<DefInfo> { new("SteelWall", "steel wall"), new("StoneWall", "stone wall") };
        Result<DefInfo> r = DefResolver.Resolve("wall", defs, "buildable");
        Assert.That(r.Success, Is.False);
        Assert.That(r.Error, Does.Contain("SteelWall").And.Contain("StoneWall").And.Contain("defName"));
    }

    [Test]
    public void NoMatchReportsKind()
    {
        Result<DefInfo> r = DefResolver.Resolve("teleporter", Defs, "buildable");
        Assert.That(r.Success, Is.False);
        Assert.That(r.Error, Does.StartWith("Error:").And.Contain("buildable").And.Contain("teleporter"));
    }

    [TestCase("research bench")]
    [TestCase("simple_research-bench")]
    public void SeparatorsAreIgnoredAgainstDefName(string input)
    {
        // 簡中遊戲的 label 是中文，英文只能對 defName。
        var defs = new[] { new DefInfo("SimpleResearchBench", "简易研究台"), new DefInfo("Wall", "墙") };

        Result<DefInfo> r = DefResolver.Resolve(input, defs, "buildable");

        Assert.That(r.Success, Is.True);
        Assert.That(r.Value.DefName, Is.EqualTo("SimpleResearchBench"));
    }

    [Test]
    public void NoMatchSuggestsClosestByWords()
    {
        var defs = new[]
        {
            new DefInfo("SimpleResearchBench", "简易研究台"), new DefInfo("HiTechResearchBench", "高级研究台"),
            new DefInfo("Wall", "墙")
        };

        Result<DefInfo> r = DefResolver.Resolve("FueledResearchBench", defs, "buildable");

        Assert.That(r.Success, Is.False);
        Assert.That(r.Error, Does.Contain("Closest: HiTechResearchBench").And.Contain("SimpleResearchBench"));
        Assert.That(r.Error, Does.Not.Contain("Wall"));
    }

    [Test]
    public void NoMatchWithoutSharedWordsListsNothing()
    {
        Result<DefInfo> r = DefResolver.Resolve("teleporter", Defs, "buildable");

        Assert.That(r.Error, Does.Not.Contain("Closest"));
    }

    [Test]
    public void EmptyInputFails()
    {
        Assert.That(DefResolver.Resolve("  ", Defs, "buildable").Success, Is.False);
    }
}

[TestFixture]
public class TargetResolverTests
{
    private List<ColonistInfo> _colonists;

    [SetUp]
    public void SetUp()
    {
        _colonists =
        [
            FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"),
            FakeGameWorld.MakeColonist(102, "Bob", "", "Jones"),
            FakeGameWorld.MakeColonist(103, "Alicia", "", "Brown")
        ];
    }

    [Test]
    public void ResolvesById()
    {
        Assert.That(TargetResolver.ResolveColonist("102", _colonists).Value.Id, Is.EqualTo(102));
    }

    [Test]
    public void ResolvesByNicknameCaseInsensitive()
    {
        Assert.That(TargetResolver.ResolveColonist("ali", _colonists).Value.Id, Is.EqualTo(101));
    }

    [Test]
    public void ResolvesByFullName()
    {
        Assert.That(TargetResolver.ResolveColonist("Bob Jones", _colonists).Value.Id, Is.EqualTo(102));
    }

    [Test]
    public void PrefixAmbiguityListsCandidatesWithIds()
    {
        Result<ColonistInfo> r = TargetResolver.ResolveColonist("Alic", _colonists);
        Assert.That(r.Success, Is.False);
        Assert.That(r.Error, Does.Contain("[101]").And.Contain("[103]"));
    }

    [Test]
    public void UnknownNameListsEveryone()
    {
        Result<ColonistInfo> r = TargetResolver.ResolveColonist("Zed", _colonists);
        Assert.That(r.Success, Is.False);
        Assert.That(r.Error, Does.Contain("[101]").And.Contain("[102]").And.Contain("[103]"));
    }

    [Test]
    public void UnknownIdFails()
    {
        Assert.That(TargetResolver.ResolveColonist("999", _colonists).Success, Is.False);
    }

    [TestCase("all")]
    [TestCase("Everyone")]
    [TestCase("*")]
    public void EveryoneKeywordsReturnAll(string keyword)
    {
        Result<List<ColonistInfo>> r = TargetResolver.ResolveColonists(keyword, _colonists);
        Assert.That(r.Success, Is.True);
        Assert.That(r.Value, Has.Count.EqualTo(3));
    }

    [Test]
    public void CommaListResolvesEachAndDeduplicates()
    {
        Result<List<ColonistInfo>> r = TargetResolver.ResolveColonists("Bob, 101, ali", _colonists);
        Assert.That(r.Success, Is.True);
        Assert.That(r.Value, Has.Count.EqualTo(2));
    }

    [Test]
    public void CommaListFailsOnFirstUnknown()
    {
        Assert.That(TargetResolver.ResolveColonists("Bob, Zed", _colonists).Success, Is.False);
    }

    [Test]
    public void RectIsNormalisedAndBoundsChecked()
    {
        var bounds = new MapBounds { Width = 50, Height = 50 };
        Result<CellRectSpec> ok = TargetResolver.ResolveRect(20, 30, 5, 7, bounds);
        Assert.That(ok.Success, Is.True);
        Assert.That(ok.Value.MinX, Is.EqualTo(5));
        Assert.That(ok.Value.MaxZ, Is.EqualTo(30));

        Result<CellRectSpec> bad = TargetResolver.ResolveRect(0, 0, 50, 10, bounds);
        Assert.That(bad.Success, Is.False);
        Assert.That(bad.Error, Does.Contain("0-49"));
    }

    [Test]
    public void CheckCellReturnsNullInsideAndMessageOutside()
    {
        var bounds = new MapBounds { Width = 10, Height = 10 };
        Assert.That(TargetResolver.CheckCell(9, 9, bounds), Is.Null);
        Assert.That(TargetResolver.CheckCell(10, 0, bounds), Does.StartWith("Error:"));
        Assert.That(TargetResolver.CheckCell(-1, 0, bounds), Does.StartWith("Error:"));
    }

    [Test]
    [SetCulture("sv-SE")]
    public void CheckCellMessageIgnoresPlayerCulture()
    {
        // 瑞典文的負號是 U+2212；送給模型的座標必須是 ASCII 的 -1，模型才能照抄回工具參數。
        var bounds = new MapBounds { Width = 10, Height = 10 };
        Assert.That(TargetResolver.CheckCell(-1, 0, bounds), Does.Contain("cell (-1,0)"));
    }

    [TestCase("simple research bench", "SimpleResearchBench")]
    [TestCase("corn", null)]
    [TestCase("research bench", null)]
    public void SameNameIgnoringSpacesBeatsPartialMatches(string input, string expected)
    {
        List<DefInfo> defs = [new("AncientSimpleResearchBench", "遠古基礎研究台"), new("SimpleResearchBench", "基礎研究台"), new("Plant_Fibercorn", "纖維稈"), new("Plant_Corn", "玉米"), new("RawCorn", "玉米粒")];
        Result<DefInfo> result = DefResolver.Resolve(input, defs, "thing");
        Assert.That(result.Success ? result.Value.DefName : null, Is.EqualTo(expected));
    }

    [Test]
    public void AmbiguousCandidatesShowTheirNote()
    {
        List<DefInfo> defs = [new("RawCorn", "玉米") { Note = "item" }, new("Plant_Corn", "玉米") { Note = "plant" }];
        Assert.That(DefResolver.Resolve("玉米", defs, "thing").Error, Does.Contain("RawCorn (玉米, item), Plant_Corn (玉米, plant)"));
    }

    [Test]
    public void DefPrefixIsIgnoredOnlyWhenAsked()
    {
        List<DefInfo> plants = [new("Plant_Fibercorn", "纖維稈"), new("Plant_Corn", "玉米")];
        Assert.That(DefResolver.Resolve("corn", plants, "growable plant", defPrefix: "Plant_").Value.DefName, Is.EqualTo("Plant_Corn"));
    }

    [Test]
    public void ParametersDescribedAsOptionalHaveDefaults()
    {
        var optional = new System.Text.RegularExpressions.Regex("^Optional|[Ee]mpty (=|keeps|buys|sells|picks|allows|disallows|lists|string matches)|leave it empty|ignored for");
        string[] missing = [.. typeof(DefResolver).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods())
            .Where(m => m.IsDefined(typeof(AgentToolAttribute), inherit: false))
            .SelectMany(m => m.GetParameters().Select(p => (m, p)))
            .Where(x => !x.p.HasDefaultValue
                && x.p.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false).Cast<System.ComponentModel.DescriptionAttribute>().Any(d => optional.IsMatch(d.Description)))
            .Select(x => x.m.Name + "." + x.p.Name)];
        Assert.That(missing, Is.Empty);
    }
}
