using System.IO;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.UI;

namespace RimAgent.Tests;

[TestFixture]
public class TranscriptScreenshotTests
{
    [Test]
    public void AcceptsOnlySuccessfulScreenshotInsideScreenshotFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "RimAgentScreenshots");
        string path = Path.Combine(folder, "RimAgent_1.png");
        var entry = new TranscriptEntry
        {
            Kind = TranscriptKind.Tool,
            ToolName = "take_screenshot",
            Result = $"Screenshot saved: {path} (1920x1080)"
        };

        Assert.That(TranscriptScreenshot.TryGetPath(entry, folder), Is.EqualTo(Path.GetFullPath(path)));

        entry.Failed = true;
        Assert.That(TranscriptScreenshot.TryGetPath(entry, folder), Is.Null);
        entry.Failed = false;
        entry.ToolName = "other_tool";
        Assert.That(TranscriptScreenshot.TryGetPath(entry, folder), Is.Null);
        entry.ToolName = "take_screenshot";
        entry.Result = $"Screenshot saved: {Path.Combine(folder, "..", "outside.png")} (1920x1080)";
        Assert.That(TranscriptScreenshot.TryGetPath(entry, folder), Is.Null);
    }
}
