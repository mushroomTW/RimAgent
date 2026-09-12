using System.Collections.Generic;
using NUnit.Framework;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    [TestFixture]
    public class DefResolverTests
    {
        private static readonly DefInfo[] Defs =
        {
            new DefInfo("Wall", "wall"),
            new DefInfo("Door", "door"),
            new DefInfo("SolarGenerator", "solar generator"),
            new DefInfo("WindTurbine", "wind turbine"),
            new DefInfo("Bed", "bed"),
            new DefInfo("DoubleBed", "double bed")
        };

        [Test]
        public void ExactDefNameWins_CaseInsensitive()
        {
            ResolveResult<DefInfo> r = DefResolver.Resolve("solargenerator", Defs, "buildable");
            Assert.That(r.Success, Is.True);
            Assert.That(r.Value.DefName, Is.EqualTo("SolarGenerator"));
        }

        [Test]
        public void ExactLabelMatches()
        {
            ResolveResult<DefInfo> r = DefResolver.Resolve("Wind Turbine", Defs, "buildable");
            Assert.That(r.Success, Is.True);
            Assert.That(r.Value.DefName, Is.EqualTo("WindTurbine"));
        }

        [Test]
        public void PrefixMatchesWhenUnique()
        {
            ResolveResult<DefInfo> r = DefResolver.Resolve("sol", Defs, "buildable");
            Assert.That(r.Success, Is.True);
            Assert.That(r.Value.DefName, Is.EqualTo("SolarGenerator"));
        }

        [Test]
        public void ExactMatchBeatsPrefixAmbiguity()
        {
            // "bed" 同時是 Bed 的 defName 與 DoubleBed 的 label 子字串；精確比對優先。
            ResolveResult<DefInfo> r = DefResolver.Resolve("bed", Defs, "buildable");
            Assert.That(r.Success, Is.True);
            Assert.That(r.Value.DefName, Is.EqualTo("Bed"));
        }

        [Test]
        public void InputContainingLabelMatches()
        {
            // 玩家說「木牆」時模型可能傳 "wooden wall"：輸入包含 label "wall"。
            ResolveResult<DefInfo> r = DefResolver.Resolve("wooden wall", Defs, "buildable");
            Assert.That(r.Success, Is.True);
            Assert.That(r.Value.DefName, Is.EqualTo("Wall"));
        }

        [Test]
        public void AmbiguousListsCandidatesAndAsksForDefName()
        {
            var defs = new List<DefInfo> { new DefInfo("SteelWall", "steel wall"), new DefInfo("StoneWall", "stone wall") };
            ResolveResult<DefInfo> r = DefResolver.Resolve("wall", defs, "buildable");
            Assert.That(r.Success, Is.False);
            Assert.That(r.Error, Does.Contain("SteelWall").And.Contain("StoneWall").And.Contain("defName"));
        }

        [Test]
        public void NoMatchReportsKind()
        {
            ResolveResult<DefInfo> r = DefResolver.Resolve("teleporter", Defs, "buildable");
            Assert.That(r.Success, Is.False);
            Assert.That(r.Error, Does.StartWith("Error:").And.Contain("buildable").And.Contain("teleporter"));
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
            _colonists = new List<ColonistInfo>
            {
                FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"),
                FakeGameWorld.MakeColonist(102, "Bob", "", "Jones"),
                FakeGameWorld.MakeColonist(103, "Alicia", "", "Brown")
            };
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
            ResolveResult<ColonistInfo> r = TargetResolver.ResolveColonist("Alic", _colonists);
            Assert.That(r.Success, Is.False);
            Assert.That(r.Error, Does.Contain("[101]").And.Contain("[103]"));
        }

        [Test]
        public void UnknownNameListsEveryone()
        {
            ResolveResult<ColonistInfo> r = TargetResolver.ResolveColonist("Zed", _colonists);
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
            ResolveResult<List<ColonistInfo>> r = TargetResolver.ResolveColonists(keyword, _colonists);
            Assert.That(r.Success, Is.True);
            Assert.That(r.Value, Has.Count.EqualTo(3));
        }

        [Test]
        public void CommaListResolvesEachAndDeduplicates()
        {
            ResolveResult<List<ColonistInfo>> r = TargetResolver.ResolveColonists("Bob, 101, ali", _colonists);
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
            ResolveResult<CellRectSpec> ok = TargetResolver.ResolveRect(20, 30, 5, 7, bounds);
            Assert.That(ok.Success, Is.True);
            Assert.That(ok.Value.MinX, Is.EqualTo(5));
            Assert.That(ok.Value.MaxZ, Is.EqualTo(30));

            ResolveResult<CellRectSpec> bad = TargetResolver.ResolveRect(0, 0, 50, 10, bounds);
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
    }
}
