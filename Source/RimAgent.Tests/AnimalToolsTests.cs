using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    /// <summary>動物類別（animal issue 01）：查詢、馴養、訓練開關、區域指派。</summary>
    [TestFixture]
    public class AnimalToolsTests
    {
        private FakeGameWorld _world;
        private AnimalTools _tools;

        [SetUp]
        public void SetUp()
        {
            _world = new FakeGameWorld();
            _world.Animals.Add(new AnimalInfo { Id = 301, Name = "Muffy", SpeciesDefName = "Muffalo", Species = "muffalo", Area = "Barn", HealthPercent = 0.9f });
            _world.Animals.Add(new AnimalInfo { Id = 302, Name = "Rex", SpeciesDefName = "Husky", Species = "husky", HealthPercent = 1f, Downed = true });
            _world.Animals.Add(new AnimalInfo { Id = 303, Name = "timber wolf", SpeciesDefName = "TimberWolf", Species = "timber wolf", Wild = true, HealthPercent = 1f });
            _world.Animals.Add(new AnimalInfo { Id = 304, Name = "timber wolf", SpeciesDefName = "TimberWolf", Species = "timber wolf", Wild = true, HealthPercent = 0.7f });
            _tools = new AnimalTools(_world);
        }

        [Test]
        public void AnimalIsOffByDefault()
        {
            Assert.That(new ToolPermissions().Animal, Is.False);
            Assert.That(new RimAgentSettings().ToPermissions().Animal, Is.False);
        }

        [Test]
        public void RegistryGatesTheAnimalToolsByPermission()
        {
            var registry = new AgentToolRegistry(_world, new AgentTranscript());

            Assert.That(registry.Select(new ToolPermissions { Animal = true }).OfType<AIFunction>().Count(t => t.Name == "list_animals" || t.Name == "tame_animal" || t.Name == "train_animal" || t.Name == "set_animal_area"), Is.EqualTo(4));
            Assert.That(registry.Select(new ToolPermissions { Animal = false }).OfType<AIFunction>().Any(t => t.Name == "list_animals"), Is.False);
        }

        [Test]
        public void ListsColonyAnimalsAndWildSummary()
        {
            string text = _tools.ListAnimals();

            Assert.That(text, Does.Contain("2 colony animals").And.Contain("Muffy").And.Contain("Barn").And.Contain("Rex").And.Contain("unrestricted").And.Contain("DOWNED"));
            Assert.That(text, Does.Contain("Wild on the map: 2 timber wolf."));
        }

        [Test]
        public void TamesWildAnimalById()
        {
            string text = _tools.TameAnimal("303");

            Assert.That(text, Is.EqualTo("Marked timber wolf for taming. A handler will attempt it when available."));
            Assert.That(_world.TameMarked, Does.Contain(303));
        }

        [Test]
        public void TamingTameAnimalIsRefused()
        {
            Assert.That(_tools.TameAnimal("Muffy"), Does.StartWith("Error:").And.Contain("already tame"));
        }

        [Test]
        public void AmbiguousSpeciesNameAsksForId()
        {
            string text = _tools.TameAnimal("timber wolf");

            Assert.That(text, Does.StartWith("Error:").And.Contain("Use the id"));
            Assert.That(_world.TameMarked, Is.Empty);
        }

        [Test]
        public void TrainingTurnsOnAndOff()
        {
            Assert.That(_tools.TrainAnimal("Rex", "haul", true), Is.EqualTo("Rex will now be trained in haul."));
            Assert.That(_world.TrainingOn, Does.Contain("302:Haul"));
            Assert.That(_tools.TrainAnimal("Rex", "haul", false), Is.EqualTo("Rex will no longer be trained in haul."));
            Assert.That(_world.TrainingOn, Is.Empty);
        }

        [Test]
        public void TrainingWildAnimalIsRefused()
        {
            Assert.That(_tools.TrainAnimal("303", "obedience", true), Does.StartWith("Error:").And.Contain("tame it first"));
        }

        [Test]
        public void UnknownTrainingListsNoFabrication()
        {
            Assert.That(_tools.TrainAnimal("Rex", "quantum physics", true), Does.StartWith("Error:"));
        }

        [Test]
        public void SetsAreaAndUnrestricted()
        {
            Assert.That(_tools.SetAnimalArea("Rex", "Pasture"), Is.EqualTo("Rex's allowed area is now Pasture."));
            Assert.That(_world.Animals[1].Area, Is.EqualTo("Pasture"));
            Assert.That(_tools.SetAnimalArea("Rex", "unrestricted"), Is.EqualTo("Rex's allowed area is now unrestricted."));
            Assert.That(_world.Animals[1].Area, Is.Empty);
        }

        [Test]
        public void UnknownAreaReportsError()
        {
            Assert.That(_tools.SetAnimalArea("Rex", "moon base"), Does.StartWith("Error:"));
        }

        [Test]
        public async Task TrainAnimalIsLoggedAsAToolCall()
        {
            var client = new ScriptedChatClient();
            client.CallTool("train_animal", new { animal = "Rex", training = "haul", enable = true }).Reply("Done.");
            var session = new ColonyAgentSession(client, _world, () => new ToolPermissions { Observe = true, Animal = true }, 8);

            await session.SendAsync("讓 Rex 學搬運");

            TranscriptEntry tool = session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool);
            Assert.That(tool.ToolName, Is.EqualTo("train_animal"));
            Assert.That(tool.ToolCategory, Is.EqualTo(ToolCategory.Animal));
            Assert.That(tool.Result, Does.Contain("Rex").And.Contain("haul"));
        }
    }
}
