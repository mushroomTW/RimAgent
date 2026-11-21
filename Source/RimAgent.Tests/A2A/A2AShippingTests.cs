using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace RimAgent.Tests.A2A;

/// <summary>A2A/ 的出貨白名單（RimAgent.A2A.csproj 的 CopyA2AShippedAssemblies）必須剛好是執行期需要、又沒有別人提供的組件。</summary>
[TestFixture]
public class A2AShippingTests
{
    private string _a2aDir;
    private HashSet<string> _provided;
    private Dictionary<string, string> _shipped;

    [OneTimeSetUp]
    public void Locate()
    {
        string modRoot = FindModRoot();
        _a2aDir = Path.Combine(modRoot, "A2A");
        if (!File.Exists(Path.Combine(_a2aDir, "RimAgent.A2A.dll")))
        {
            Assert.Fail("A2A/RimAgent.A2A.dll not found; build Source/RimAgent.A2A in Release first.");
        }
        string gameRoot = Path.GetFullPath(Path.Combine(modRoot, "..", ".."));
        _provided = new[] { gameRoot, Path.Combine(gameRoot, "Mods", "RimLLM Framework", "Assemblies"), Path.Combine(modRoot, "Assemblies") }
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.dll"))
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _shipped = Directory.GetFiles(_a2aDir, "*.dll").ToDictionary(Path.GetFileNameWithoutExtension, f => f, StringComparer.OrdinalIgnoreCase);
    }

    [Test]
    public void ShippedAssembliesDoNotDuplicateGameFrameworkOrRimAgent()
    {
        Assert.That(_shipped.Keys.Where(_provided.Contains), Is.Empty,
            "These are already provided by the game, RimLLM Framework or RimAgent's Assemblies/; remove them from the A2A whitelist.");
    }

    [Test]
    public void EveryReferenceResolves()
    {
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in _shipped.Values)
        {
            foreach (string name in References(path))
            {
                if (!_provided.Contains(name) && !_shipped.ContainsKey(name)) missing.Add(name + " (from " + Path.GetFileName(path) + ")");
            }
        }
        Assert.That(missing, Is.Empty, "Add these to the A2A whitelist in RimAgent.A2A.csproj.");
    }

    [Test]
    public void EveryShippedAssemblyIsReachableFromRimAgentA2A()
    {
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "RimAgent.A2A" };
        var queue = new Queue<string>([_shipped["RimAgent.A2A"]]);
        while (queue.Count > 0)
        {
            foreach (string name in References(queue.Dequeue()))
            {
                if (_shipped.TryGetValue(name, out string path) && reached.Add(name)) queue.Enqueue(path);
            }
        }
        Assert.That(_shipped.Keys.Where(n => !reached.Contains(n)), Is.Empty, "Nothing references these; remove them from the A2A whitelist.");
    }

    private static IEnumerable<string> References(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        MetadataReader md = pe.GetMetadataReader();
        return [.. md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name))];
    }

    private static string FindModRoot([CallerFilePath] string testFile = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(testFile)); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Source", "RimAgent", "RimAgent.csproj"))) return dir.FullName;
        }
        throw new FileNotFoundException("Mod root not found");
    }
}
