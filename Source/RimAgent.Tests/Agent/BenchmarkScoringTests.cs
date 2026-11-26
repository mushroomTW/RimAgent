using System.Linq;
using NUnit.Framework;
using RimAgent.InGameTests;

namespace RimAgent.Tests;

/// <summary>Benchmark 判分（天數、座標）與成對 bootstrap 信賴區間。</summary>
[TestFixture]
public class BenchmarkScoringTests
{
    [TestCase("大約還能撐 3.4 天", 3.4f)]
    [TestCase("大約能撐３天", 3f)]
    [TestCase("about 5 days left", 5f)]
    [TestCase("撐 2日", 2f)]
    public void Days_ReadsNumberBeforeDayWord(string reply, float expected)
    {
        Assert.That(BenchmarkScoring.Days(reply), Is.EqualTo(new[] { expected }));
    }

    [Test]
    public void Days_IgnoresNumbersNotFollowedByDayWord()
    {
        Assert.That(BenchmarkScoring.Days("3 位殖民者，食物 12 份"), Is.Empty);
        Assert.That(BenchmarkScoring.Days(null), Is.Empty);
    }

    [Test]
    public void DistinctCells_SkipsRestatedAreaCorners()
    {
        string flat = BenchmarkScoring.FlattenCoordinates("區域 (10,20) 到 (24,34)，答案是（12，22）");
        Assert.That(BenchmarkScoring.DistinctCells(flat, "(12,22)", ["(10,20)", "(24,34)"]), Is.EqualTo(1));
        Assert.That(flat, Does.Contain("(12,22)"));
    }

    [Test]
    public void DistinctCells_CountsAnswerEvenWhenItIsACorner()
    {
        string flat = BenchmarkScoring.FlattenCoordinates("(10,20) (11,21) (12,22)");
        Assert.That(BenchmarkScoring.DistinctCells(flat, "(10,20)", ["(10,20)", "(24,34)"]), Is.EqualTo(3));
    }

    [Test]
    public void PairedBootstrap_AllSameDifferenceHasZeroWidthInterval()
    {
        (double mean, double low, double high) = BenchmarkScoring.PairedBootstrap(Enumerable.Repeat(1, 8).ToList());
        Assert.That((mean, low, high), Is.EqualTo((1d, 1d, 1d)));
    }

    [Test]
    public void PairedBootstrap_MixedDifferencesIntervalContainsZero()
    {
        (double mean, double low, double high) = BenchmarkScoring.PairedBootstrap([1, -1, 0, 1, -1, 0]);
        Assert.Multiple(() =>
        {
            Assert.That(mean, Is.Zero);
            Assert.That(low, Is.LessThan(0));
            Assert.That(high, Is.GreaterThan(0));
        });
    }
}
