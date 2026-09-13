using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    /// <summary>交易類別（issue 01）：權限開關與軌道商人庫存查詢。</summary>
    [TestFixture]
    public class TradeToolsTests
    {
        private FakeGameWorld _world;
        private TradeTools _tools;

        [SetUp]
        public void SetUp()
        {
            _world = new FakeGameWorld();
            _tools = new TradeTools(_world);
        }

        private static TraderInfo MakeOrbitalShip(string name = "Starweaver (Exotic goods trader)")
        {
            return new TraderInfo
            {
                Kind = TraderInfo.OrbitalKind,
                Name = name,
                Faction = "Outlanders",
                Silver = 1200,
                Goods = new[]
                {
                    new TradeGoodsInfo { DefName = "ComponentSpacer", Label = "spacer component", Count = 8, MarketValue = 55 },
                    new TradeGoodsInfo { DefName = "MedicineUltratech", Label = "ultratech medicine", Count = 12, MarketValue = 38 }
                }
            };
        }

        private static TraderInfo MakeVisitorCaravan(string name = "Rook (Bulk goods trader)")
        {
            return new TraderInfo
            {
                Kind = TraderInfo.VisitorKind,
                Name = name,
                Faction = "Tribe",
                Silver = 350,
                Goods = new[]
                {
                    new TradeGoodsInfo { DefName = "Steel", Label = "steel", Count = 100, MarketValue = 2 },
                    new TradeGoodsInfo { DefName = "HerbalMedicine", Label = "herbal medicine", Count = 20, MarketValue = 8 }
                }
            };
        }

        [Test]
        public void TradeIsOffByDefault()
        {
            Assert.That(new ToolPermissions().Trade, Is.False);
            Assert.That(new RimAgentSettings().ToPermissions().Trade, Is.False);
        }

        [Test]
        public void RegistryGatesTheOrbitalToolByPermission()
        {
            var registry = new AgentToolRegistry(_world, new AgentTranscript());

            Assert.That(registry.Select(new ToolPermissions { Trade = true }).OfType<AIFunction>().Any(t => t.Name == "list_orbital_traders"), Is.True);
            Assert.That(registry.Select(new ToolPermissions { Trade = false }).OfType<AIFunction>().Any(t => t.Name == "list_orbital_traders"), Is.False);
        }

        [Test]
        public async Task SessionOffersTheToolOnlyWhenTradeIsAllowed()
        {
            var world = new FakeGameWorld();
            var client = new ScriptedChatClient();
            client.Reply("ok").Reply("ok");
            var permissions = new ToolPermissions { Observe = true, Work = true, Trade = false };
            var session = new ColonyAgentSession(client, world, () => permissions, 8);

            await session.SendAsync("one");
            permissions.Trade = true;
            await session.SendAsync("two");

            Assert.That(client.ReceivedOptions[0].Tools.OfType<AIFunction>().Any(t => t.Name == "list_orbital_traders"), Is.False);
            Assert.That(client.ReceivedOptions[1].Tools.OfType<AIFunction>().Any(t => t.Name == "list_orbital_traders"), Is.True);
        }

        [Test]
        public async Task OrbitalQueryIsLoggedAsAToolCall()
        {
            _world.Traders.Add(MakeOrbitalShip());
            var client = new ScriptedChatClient();
            client.CallTool("list_orbital_traders", null).Reply("Two ships in orbit.");
            var session = new ColonyAgentSession(client, _world, () => new ToolPermissions { Observe = true, Trade = true }, 8);

            await session.SendAsync("軌道上有誰在賣東西？");

            IReadOnlyList<TranscriptEntry> transcript = session.Transcript.Snapshot();
            Assert.That(transcript.Count(e => e.Kind == TranscriptKind.Tool), Is.EqualTo(1));
            TranscriptEntry tool = transcript.Single(e => e.Kind == TranscriptKind.Tool);
            Assert.That(tool.ToolName, Is.EqualTo("list_orbital_traders"));
            Assert.That(tool.ToolCategory, Is.EqualTo(ToolCategory.Trade));
            Assert.That(tool.Result, Does.Contain("Starweaver"));
        }

        [Test]
        public void ListsOrbitalTraderWithStockAndSilver()
        {
            _world.Traders.Add(MakeOrbitalShip());

            string text = _tools.ListOrbitalTraders();

            Assert.That(text, Does.Contain("Starweaver").And.Contain("Outlanders").And.Contain("1200"));
            Assert.That(text, Does.Contain("spacer component (ComponentSpacer) x8").And.Contain("ultratech medicine (MedicineUltratech) x12"));
        }

        [Test]
        public void NoOrbitalTraderIsReportedExplicitly()
        {
            Assert.That(_tools.ListOrbitalTraders(), Does.Contain("no orbital traders").IgnoreCase);
        }

        [Test]
        public void VisitorCaravanIsNeverSubstitutedForAnOrbitalTrader()
        {
            _world.Traders.Add(new TraderInfo
            {
                Kind = TraderInfo.VisitorKind,
                Name = "來訪商隊",
                Faction = "Outlanders",
                Silver = 500,
                Goods = new[] { new TradeGoodsInfo { DefName = "Steel", Label = "steel", Count = 100 } }
            });

            string text = _tools.ListOrbitalTraders();

            Assert.That(text, Does.Contain("no orbital traders").IgnoreCase);
            Assert.That(text, Does.Not.Contain("steel"));
        }

        [Test]
        public void LongStockListIsTruncatedWithACount()
        {
            var goods = Enumerable.Range(1, TradeTools.MaxGoodsListed + 5)
                .Select(i => new TradeGoodsInfo { DefName = "Good" + i, Label = "good " + i, Count = i })
                .ToList();
            _world.Traders.Add(new TraderInfo { Kind = TraderInfo.OrbitalKind, Name = "Bulk freighter", Goods = goods });

            string text = _tools.ListOrbitalTraders();

            Assert.That(text, Does.Contain("and 5 more"));
        }

        [Test]
        public void ListsVisitorCaravanWithStockAndSilver()
        {
            _world.Traders.Add(MakeVisitorCaravan());

            string text = _tools.ListVisitorCaravans();

            Assert.That(text, Does.Contain("visiting caravan").And.Contain("Rook").And.Contain("Tribe").And.Contain("350"));
            Assert.That(text, Does.Contain("steel (Steel) x100").And.Contain("herbal medicine (HerbalMedicine) x20"));
        }

        [Test]
        public void NoVisitorCaravanIsReportedExplicitly()
        {
            Assert.That(_tools.ListVisitorCaravans(), Does.Contain("no visiting caravans").IgnoreCase);
        }

        [Test]
        public void OrbitalTraderIsNeverSubstitutedForAVisitor()
        {
            _world.Traders.Add(MakeOrbitalShip());

            string text = _tools.ListVisitorCaravans();

            Assert.That(text, Does.Contain("no visiting caravans").IgnoreCase);
            Assert.That(text, Does.Not.Contain("Starweaver").And.Not.Contain("spacer component"));
        }

        [Test]
        public void BothKindsCoexistWithoutLeakingIntoEachOther()
        {
            _world.Traders.Add(MakeOrbitalShip());
            _world.Traders.Add(MakeVisitorCaravan());

            string orbital = _tools.ListOrbitalTraders();
            string visitor = _tools.ListVisitorCaravans();

            Assert.That(orbital, Does.Contain("1 orbital trader").And.Contain("Starweaver").And.Not.Contain("Rook"));
            Assert.That(visitor, Does.Contain("1 visiting caravan").And.Contain("Rook").And.Not.Contain("Starweaver"));
        }

        [Test]
        public async Task VisitorQueryIsLoggedAsAToolCall()
        {
            _world.Traders.Add(MakeVisitorCaravan());
            var client = new ScriptedChatClient();
            client.CallTool("list_visitor_caravans", null).Reply("A tribe is visiting.");
            var session = new ColonyAgentSession(client, _world, () => new ToolPermissions { Observe = true, Trade = true }, 8);

            await session.SendAsync("來訪商隊在賣什麼？");

            TranscriptEntry tool = session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool);
            Assert.That(tool.ToolName, Is.EqualTo("list_visitor_caravans"));
            Assert.That(tool.ToolCategory, Is.EqualTo(ToolCategory.Trade));
            Assert.That(tool.Result, Does.Contain("Rook"));
        }
    }
}
