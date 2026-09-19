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
                Id = "PassingShip_1",
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
                Id = "Thing_2",
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

        private void SeedColonyStock()
        {
            _world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
            _world.Colonists.Add(FakeGameWorld.MakeColonist(102, "Bob", "", "Jones"));
            _world.Resources = new ResourceSnapshot
            {
                Items = new[]
                {
                    new ResourceInfo { DefName = "Silver", Label = "silver", Count = 5000, Category = "Resources" },
                    new ResourceInfo { DefName = "Steel", Label = "steel", Count = 300, Category = "Resources" }
                }
            };
            _world.Prices["ComponentSpacer"] = 55;
            _world.Prices["MedicineUltratech"] = 38;
            _world.Prices["Steel"] = 2;
            _world.Prices["HerbalMedicine"] = 8;
        }

        private static void SetSocial(ColonistInfo colonist, int level)
        {
            var skills = colonist.Skills.ToList();
            skills.Add(new SkillInfo { DefName = "Social", Label = "social", Level = level });
            colonist.Skills = skills;
        }

        private static int ColonySilver(FakeGameWorld world)
        {
            return world.Resources.Items.Where(r => r.DefName == "Silver").Sum(r => r.Count);
        }

        private static int ColonyStock(FakeGameWorld world, string defName)
        {
            return world.Resources.Items.Where(r => r.DefName == defName).Sum(r => r.Count);
        }

        private static int TraderStock(TraderInfo trader, string defName)
        {
            return trader.Goods.Where(g => g.DefName == defName).Sum(g => g.Count);
        }

        [Test]
        public void TradePermissionOffUntilGrantedButSettingsDefaultOn()
        {
            Assert.That(new ToolPermissions().Trade, Is.False);
            Assert.That(new RimAgentSettings().ToPermissions().Trade, Is.True, "設定頁預設全開");
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

        [Test]
        public void TraderListShowsIdsForLaterDeals()
        {
            _world.Traders.Add(MakeOrbitalShip());
            _world.Traders.Add(MakeVisitorCaravan());

            Assert.That(_tools.ListOrbitalTraders(), Does.Contain("[PassingShip_1]"));
            Assert.That(_tools.ListVisitorCaravans(), Does.Contain("[Thing_2]"));
        }

        [Test]
        public void BuySucceedsAndMovesItemsAndSilver()
        {
            SeedColonyStock();
            SetSocial(_world.Colonists[0], 3);
            SetSocial(_world.Colonists[1], 10);
            TraderInfo ship = MakeOrbitalShip();
            _world.Traders.Add(ship);

            string text = _tools.ExecuteTrade("PassingShip_1", "spacer component x4", string.Empty, string.Empty);

            Assert.That(text, Does.Contain("bought spacer component (ComponentSpacer) x4").And.Contain("Starweaver").And.Contain("Bob"));
            Assert.That(TraderStock(ship, "ComponentSpacer"), Is.EqualTo(4));
            Assert.That(ship.Silver, Is.EqualTo(1200 + 4 * 55));
            Assert.That(ColonySilver(_world), Is.EqualTo(5000 - 4 * 55));
            Assert.That(ColonyStock(_world, "ComponentSpacer"), Is.EqualTo(4));
        }

        [Test]
        public void SellSucceedsAndMovesItemsAndSilver()
        {
            SeedColonyStock();
            TraderInfo ship = MakeOrbitalShip();
            _world.Traders.Add(ship);

            string text = _tools.ExecuteTrade("PassingShip_1", string.Empty, "steel x100", "Ali");

            Assert.That(text, Does.Contain("sold steel (Steel) x100").And.Contain("Ali"));
            Assert.That(ColonyStock(_world, "Steel"), Is.EqualTo(200));
            Assert.That(ColonySilver(_world), Is.EqualTo(5000 + 100 * 2));
            Assert.That(ship.Silver, Is.EqualTo(1200 - 100 * 2));
            Assert.That(TraderStock(ship, "Steel"), Is.EqualTo(100));
        }

        [Test]
        public void BuyAndSellSettleInOneDeal()
        {
            SeedColonyStock();
            TraderInfo caravan = MakeVisitorCaravan();
            _world.Traders.Add(caravan);

            string text = _tools.ExecuteTrade("Thing_2", "herbal medicine x10", "steel x50", string.Empty);

            Assert.That(text, Does.Contain("bought herbal medicine (HerbalMedicine) x10").And.Contain("sold steel (Steel) x50"));
            Assert.That(text, Does.Contain("Colony silver 5000 -> 5020"));
            Assert.That(ColonySilver(_world), Is.EqualTo(5020));
            Assert.That(caravan.Silver, Is.EqualTo(350 - 20));
        }

        [Test]
        public void BuyMoreThanStockFailsWithoutPartialTrade()
        {
            SeedColonyStock();
            TraderInfo ship = MakeOrbitalShip();
            _world.Traders.Add(ship);

            string text = _tools.ExecuteTrade("PassingShip_1", "spacer component x20", string.Empty, string.Empty);

            Assert.That(text, Does.StartWith("Error:").And.Contain("only has 8"));
            Assert.That(TraderStock(ship, "ComponentSpacer"), Is.EqualTo(8));
            Assert.That(ship.Silver, Is.EqualTo(1200));
            Assert.That(ColonySilver(_world), Is.EqualTo(5000));
        }

        [Test]
        public void ColonySilverInsufficientFailsWithoutPartialTrade()
        {
            SeedColonyStock();
            _world.Resources.Items.First(r => r.DefName == "Silver").Count = 100;
            TraderInfo ship = MakeOrbitalShip();
            _world.Traders.Add(ship);

            string text = _tools.ExecuteTrade("PassingShip_1", "ultratech medicine x12", string.Empty, string.Empty);

            Assert.That(text, Does.StartWith("Error:").And.Contain("cannot afford"));
            Assert.That(TraderStock(ship, "MedicineUltratech"), Is.EqualTo(12));
            Assert.That(ColonySilver(_world), Is.EqualTo(100));
        }

        [Test]
        public void SellMoreThanStockFailsWithoutPartialTrade()
        {
            SeedColonyStock();
            TraderInfo ship = MakeOrbitalShip();
            _world.Traders.Add(ship);

            string text = _tools.ExecuteTrade("PassingShip_1", string.Empty, "steel x500", string.Empty);

            Assert.That(text, Does.StartWith("Error:").And.Contain("only has 300"));
            Assert.That(ColonyStock(_world, "Steel"), Is.EqualTo(300));
            Assert.That(ship.Silver, Is.EqualTo(1200));
        }

        [Test]
        public void TraderSilverInsufficientFailsWithoutPartialTrade()
        {
            SeedColonyStock();
            TraderInfo caravan = MakeVisitorCaravan();
            caravan.Silver = 50;
            _world.Traders.Add(caravan);

            string text = _tools.ExecuteTrade("Thing_2", string.Empty, "steel x100", string.Empty);

            Assert.That(text, Does.StartWith("Error:").And.Contain("cannot afford"));
            Assert.That(ColonyStock(_world, "Steel"), Is.EqualTo(300));
            Assert.That(caravan.Silver, Is.EqualTo(50));
        }

        [Test]
        public void UnknownTraderIsRejected()
        {
            SeedColonyStock();
            _world.Traders.Add(MakeOrbitalShip());

            string text = _tools.ExecuteTrade("PassingShip_9", "spacer component x1", string.Empty, string.Empty);

            Assert.That(text, Does.StartWith("Error:").And.Contain("unknown trader"));
        }

        [Test]
        public void BadItemListsAreRejected()
        {
            SeedColonyStock();
            _world.Traders.Add(MakeOrbitalShip());

            Assert.That(_tools.ExecuteTrade("PassingShip_1", "lots of steel", string.Empty, string.Empty), Does.StartWith("Error:"));
            Assert.That(_tools.ExecuteTrade("PassingShip_1", string.Empty, string.Empty, string.Empty), Does.StartWith("Error:").And.Contain("nothing to buy or sell"));
            Assert.That(_tools.ExecuteTrade("PassingShip_1", "silver x10", string.Empty, string.Empty), Does.StartWith("Error:").And.Contain("silver"));
            Assert.That(_tools.ExecuteTrade("PassingShip_1", "unicorn horn x1", string.Empty, string.Empty), Does.StartWith("Error:"));
        }

        [Test]
        public async Task ExecuteTradeIsLoggedWithReceipt()
        {
            SeedColonyStock();
            _world.Traders.Add(MakeOrbitalShip());
            var client = new ScriptedChatClient();
            var classifier = new ScriptedChatClient().Reply("{\"risk\":\"low\"}");
            client.CallTool("execute_trade", new { trader = "PassingShip_1", buys = "spacer component x2", sells = "", negotiator = "" })
                  .Reply("Bought 2 spacer components.");
            var session = new ColonyAgentSession(
                client,
                _world,
                () => new ToolPermissions { Observe = true, Trade = true },
                8,
                () => AgentMode.Auto,
                classifierClient: classifier);

            string reply = await session.SendAsync("買 2 個太空元件");

            Assert.That(reply, Is.EqualTo("Bought 2 spacer components."));
            TranscriptEntry tool = session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool);
            Assert.That(tool.ToolName, Is.EqualTo("execute_trade"));
            Assert.That(tool.ToolCategory, Is.EqualTo(ToolCategory.Trade));
            Assert.That(tool.Result, Does.Contain("bought spacer component (ComponentSpacer) x2").And.Contain("Colony silver"));
        }
    }
}
