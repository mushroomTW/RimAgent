using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;
using RimAgent.UI;
using RimTestRedux;
using RimWorld;
using Verse;

namespace RimAgent.InGameTests
{
    /// <summary>
    /// 不需要地圖的載入期檢查：Unity Mono 下出貨組件能否解析、翻譯鍵有沒有真的注入、
    /// 工具登錄與擴充包在真實 mod 資料夾與真實 DefDatabase 上能否建立。headless 測試跑在 .NET Framework 與假世界，這些都看不到。
    /// </summary>
    [TestSuite]
    internal static class StartupTests
    {
        private const string FrameworkPackageId = "GreenMushroom.RimLLMFramework";

        /// <summary>
        /// RimAgent 的 Assemblies/ 每顆 DLL 都已載入，且 GetTypes 與每個欄位型別都解析得到
        /// （RimWorld 啟動會掃這兩層，缺閉包時只在 log 留 AggregateException，功能到用時才壞）。
        /// </summary>
        [Test]
        public static void ShippedAssembliesResolveAllTypesAndFields()
        {
            if (TestHelpers.SkipStartupRerun(nameof(ShippedAssembliesResolveAllTypesAndFields))) return;
            var failures = new List<string>();
            ModContentPack content = TestHelpers.RimAgentContent;
            if (content == null) throw new AssertionException("RimAgentMod is not loaded");

            List<string> shipped = TestHelpers.ShippedAssemblyNames(content);
            if (shipped.Count == 0) failures.Add("no DLL found under RimAgent's Assemblies folder");
            // 只看 RimWorld 替 RimAgent 載入的那份，且檔案真的來自 RimAgent 的資料夾：
            // 同名組件若已由 Unity 或別的 mod 先載入，LoadFrom 會回傳那一份，名稱比對就會驗錯對象。
            string root = Path.GetFullPath(content.RootDir);
            foreach (string name in shipped)
            {
                Assembly assembly = content.assemblies.loadedAssemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
                if (assembly == null)
                {
                    failures.Add($"{name}: shipped but not loaded by RimWorld for RimAgent");
                    continue;
                }
                string location = assembly.Location;
                if (string.IsNullOrEmpty(location) || !Path.GetFullPath(location).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{name}: resolved to another copy ({Describe(assembly)})");
                    continue;
                }
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    foreach (string message in ex.LoaderExceptions.Where(e => e != null).Select(e => e.Message).Distinct())
                        failures.Add($"{name}: GetTypes: {message}");
                    types = ex.Types.Where(t => t != null).ToArray();
                }
                foreach (Type type in types)
                {
                    try
                    {
                        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                            _ = field.FieldType;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{name}: fields of {type.FullName}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            TestHelpers.AssertNone(failures, "shipped assemblies that do not fully resolve under Mono");
        }

        /// <summary>
        /// RimAgent 與 RimLLM Framework 出貨的 DLL 在 AppDomain 裡各只有一份：
        /// 第二份 MEAI 會讓 IChatClient 等型別轉換全部失敗（RimAgent.csproj 的出貨白名單就是為此而設）。
        /// </summary>
        [Test]
        public static void NoSecondCopyOfSharedAssemblies()
        {
            if (TestHelpers.SkipStartupRerun(nameof(NoSecondCopyOfSharedAssemblies))) return;
            var failures = new List<string>();
            ModContentPack agent = TestHelpers.RimAgentContent;
            ModContentPack framework = TestHelpers.ModByPackageId(FrameworkPackageId);
            if (agent == null || framework == null) throw new AssertionException("RimAgent or RimLLM Framework is not loaded");

            List<string> agentNames = TestHelpers.ShippedAssemblyNames(agent);
            List<string> frameworkNames = TestHelpers.ShippedAssemblyNames(framework);
            foreach (string name in agentNames.Intersect(frameworkNames, StringComparer.OrdinalIgnoreCase))
                failures.Add($"{name}: shipped by both RimAgent and RimLLM Framework");

            ILookup<string, Assembly> byName = AppDomain.CurrentDomain.GetAssemblies()
                .ToLookup(a => a.GetName().Name, StringComparer.OrdinalIgnoreCase);
            foreach (string name in agentNames.Union(frameworkNames, StringComparer.OrdinalIgnoreCase))
            {
                List<Assembly> copies = byName[name].ToList();
                if (copies.Count > 1)
                    failures.Add($"{name}: {copies.Count} copies loaded ({string.Join(", ", copies.Select(Describe))})");
            }
            TestHelpers.AssertNone(failures, "assemblies loaded more than once");
        }

        /// <summary>
        /// 用正式的 RimWorldGameWorld 建工具登錄：每個 [AgentTool] 都要在 Mono 下通過 AIFunctionFactory.Create，
        /// 名稱唯一且符合供應商的工具名規則。
        /// </summary>
        [Test]
        public static void ToolRegistryBuildsAgainstRealWorld()
        {
            if (TestHelpers.SkipStartupRerun(nameof(ToolRegistryBuildsAgainstRealWorld))) return;
            var failures = new List<string>();
            ModContentPack content = TestHelpers.RimAgentContent;
            AgentPack pack = AgentPackLoader.Load(Path.Combine(content.RootDir, "AgentPack"));
            var registry = new AgentToolRegistry(new RimWorldGameWorld(), new AgentTranscript(), null, null, skills: () => pack.Skills);

            IReadOnlyList<AgentToolDescriptor> tools = registry.Descriptors;
            if (tools.Count == 0) failures.Add("no tool was registered");
            var validName = new Regex("^[a-zA-Z0-9_-]{1,64}$");
            foreach (AgentToolDescriptor tool in tools)
            {
                if (!validName.IsMatch(tool.Name ?? string.Empty)) failures.Add($"invalid tool name \"{tool.Name}\"");
            }
            foreach (IGrouping<string, AgentToolDescriptor> dup in tools.GroupBy(t => t.Name).Where(g => g.Count() > 1))
                failures.Add($"tool name {dup.Key} registered {dup.Count()} times");
            Log.Message($"{TestHelpers.LogPrefix}registered {tools.Count} tools");
            TestHelpers.AssertNone(failures, "tool registry problems");
        }

        /// <summary>從遊戲實際載入的 mod 資料夾讀擴充包：Skills/ 與 Agents/ 下每個資料夾都成為一個技能／子代理。</summary>
        [Test]
        public static void AgentPackLoadsFromModFolder()
        {
            if (TestHelpers.SkipStartupRerun(nameof(AgentPackLoadsFromModFolder))) return;
            var failures = new List<string>();
            string root = Path.Combine(TestHelpers.RimAgentContent.RootDir, "AgentPack");
            AgentPack pack = AgentPackLoader.Load(root);

            List<string> skillDirs = SubfoldersWith(Path.Combine(root, "Skills"), "SKILL.md");
            List<string> skills = pack.Skills.Select(s => s.Name).ToList();
            foreach (string missing in skillDirs.Except(skills, StringComparer.OrdinalIgnoreCase))
                failures.Add($"skill folder {missing} was not loaded");

            List<string> agentDirs = Directory.Exists(Path.Combine(root, "Agents"))
                ? Directory.GetDirectories(Path.Combine(root, "Agents")).Select(Path.GetFileName).ToList()
                : new List<string>();
            if (agentDirs.Count != pack.Subagents.Count)
                failures.Add($"{agentDirs.Count} subagent folders ({string.Join(", ", agentDirs)}) but {pack.Subagents.Count} subagents loaded ({string.Join(", ", pack.Subagents.Select(s => s.Name))})");
            if (string.IsNullOrWhiteSpace(pack.Preamble)) failures.Add("Prompts/Briefing.md was not loaded");
            if (string.IsNullOrWhiteSpace(pack.ClassifierPrompt)) failures.Add("Prompts/RiskClassifier.md was not loaded");
            TestHelpers.AssertNone(failures, "agent pack problems");
        }

        /// <summary>
        /// RimAgent 程式碼裡每個 "RimAgent.xxx" 字面翻譯鍵，加上以列舉組出來的動態鍵，
        /// 在 RimAgent 出貨的每種語言都有 Keyed 條目（不在就退回英文、英文也沒有就顯示鍵名）。
        /// </summary>
        [Test]
        public static void EveryTranslationKeyExistsInShippedLanguages()
        {
            if (TestHelpers.SkipStartupRerun(nameof(EveryTranslationKeyExistsInShippedLanguages))) return;
            var failures = new List<string>();
            List<string> keys = LiteralTranslationKeys(typeof(RimAgentMod).Assembly, failures)
                .Concat(DynamicTranslationKeys())
                .Distinct()
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
            if (keys.Count == 0) failures.Add("no translation key literal found in RimAgent.dll");

            string languagesDir = Path.Combine(TestHelpers.RimAgentContent.RootDir, "Languages");
            HashSet<string> shipped = new HashSet<string>(
                Directory.Exists(languagesDir) ? Directory.GetDirectories(languagesDir).Select(Path.GetFileName) : Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            // Core 的語言資料夾帶在地名（如 "ChineseTraditional (繁體中文)"），mod 用去掉括號的舊名；遊戲兩者都認。
            List<LoadedLanguage> languages = LanguageDatabase.AllLoadedLanguages
                .Where(l => shipped.Contains(l.folderName) || shipped.Contains(l.LegacyFolderName))
                .ToList();
            if (languages.Count == 0) failures.Add("none of RimAgent's language folders matched a loaded language");

            foreach (LoadedLanguage language in languages)
            {
                List<string> missing;
                if (language == LanguageDatabase.activeLanguage || language == LanguageDatabase.defaultLanguage)
                {
                    // 遊戲已載入的語言：直接問遊戲，並挑出 RimAgent 自己的載入錯誤
                    // （重複鍵、壞掉的 XML、對不到 def 的 DefInjected；遊戲只在 log 留一句「N errors」）。
                    missing = keys.Where(k => !language.HaveTextForKey(k)).ToList();
                    IEnumerable<string> loadErrors = language.loadErrors.Concat(language.defInjections.SelectMany(p => p.loadErrors));
                    foreach (string error in loadErrors.Where(e => e.Contains("RimAgent")))
                        failures.Add($"{language.folderName}: {error}");
                }
                else
                {
                    // 沒啟用的語言不強迫 LoadData（會把所有 mod 的該語言資料永久留在記憶體），改讀 RimAgent 自己的 Keyed 檔。
                    string folder = shipped.Contains(language.folderName) ? language.folderName : language.LegacyFolderName;
                    HashSet<string> keyed = ReadKeyedFiles(Path.Combine(languagesDir, folder, "Keyed"), language.folderName, failures);
                    missing = keys.Where(k => !keyed.Contains(k)).ToList();
                }
                if (missing.Count > 0) failures.Add($"{language.folderName} is missing {missing.Count}: {string.Join(", ", missing)}");
            }
            Log.Message($"{TestHelpers.LogPrefix}checked {keys.Count} keys in {string.Join(", ", languages.Select(l => l.folderName))}");
            TestHelpers.AssertNone(failures, "missing translation keys");
        }

        /// <summary>Agent 主按鈕的 def 已載入，worker 類別解析到 RimAgent 的型別。</summary>
        [Test]
        public static void MainButtonDefIsLoaded()
        {
            if (TestHelpers.SkipStartupRerun(nameof(MainButtonDefIsLoaded))) return;
            MainButtonDef def = DefDatabase<MainButtonDef>.GetNamedSilentFail("RimAgent");
            Assert.That(def != null).Is.True();
            Assert.That(def.workerClass == typeof(MainButtonWorker_ToggleAgent)).Is.True();
        }

        /// <summary>
        /// 不依賴地圖的 Def 目錄在真實 DefDatabase（含 DLC）上非空、有標籤，且每一項都能用 defName 解析回自己
        /// ——模型拿到的 defName 必須打得回去。
        /// </summary>
        [Test]
        public static void DefCatalogsResolveByDefName()
        {
            if (TestHelpers.SkipStartupRerun(nameof(DefCatalogsResolveByDefName))) return;
            var failures = new List<string>();
            var world = new RimWorldGameWorld();
            var catalogs = new Dictionary<string, IReadOnlyList<DefInfo>>
            {
                ["WorkTypes"] = world.WorkTypes,
                ["Buildables"] = world.Buildables,
                ["Stuffs"] = world.Stuffs,
                ["TimeAssignments"] = world.TimeAssignments,
                ["TradeGoods"] = world.TradeGoods,
                ["MedicalCareLevels"] = world.MedicalCareLevels,
                ["SurgeryRecipes"] = world.SurgeryRecipes,
                ["PrisonerInteractionModes"] = world.PrisonerInteractionModes,
                ["AnimalTraining"] = world.AnimalTraining,
                ["StorageFilterables"] = world.StorageFilterables,
                ["GrowablePlants"] = world.GrowablePlants,
                ["TimeSpeeds"] = world.TimeSpeeds
            };
            foreach (KeyValuePair<string, IReadOnlyList<DefInfo>> catalog in catalogs)
            {
                if (catalog.Value.Count == 0)
                {
                    failures.Add($"{catalog.Key}: empty");
                    continue;
                }
                foreach (DefInfo entry in catalog.Value)
                {
                    if (string.IsNullOrEmpty(entry.Label)) failures.Add($"{catalog.Key}: {entry.DefName} has no label");
                    Result<DefInfo> resolved = DefResolver.Resolve(entry.DefName, catalog.Value, catalog.Key);
                    if (!resolved.Success)
                        failures.Add($"{catalog.Key}: {entry.DefName} does not resolve: {resolved.Error}");
                    else if (!string.Equals(resolved.Value.DefName, entry.DefName, StringComparison.Ordinal))
                        failures.Add($"{catalog.Key}: {entry.DefName} resolves to {resolved.Value.DefName}");
                }
            }
            TestHelpers.AssertNone(failures, "Def catalog problems");
        }

        private static string Describe(Assembly assembly)
        {
            string location;
            try
            {
                location = assembly.Location;
            }
            catch (NotSupportedException)
            {
                location = "dynamic";
            }
            return $"{assembly.GetName().Version} @ {(string.IsNullOrEmpty(location) ? "(loaded from bytes)" : location)}";
        }

        private static List<string> SubfoldersWith(string dir, string file)
        {
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.GetDirectories(dir).Where(d => File.Exists(Path.Combine(d, file))).Select(Path.GetFileName).ToList();
        }

        /// <summary>以列舉組出的鍵，用 RimAgentMod 同一組鍵產生器，規則改了這裡跟著變。</summary>
        private static IEnumerable<string> DynamicTranslationKeys()
        {
            foreach (ReasoningLevel level in (ReasoningLevel[])Enum.GetValues(typeof(ReasoningLevel)))
                yield return RimAgentMod.ReasoningKey(level);
            foreach (ToolCategory category in (ToolCategory[])Enum.GetValues(typeof(ToolCategory)))
            {
                string key = RimAgentMod.PermissionKey(category);
                yield return key;
                yield return key + "Tip";
            }
        }

        /// <summary>一個語言資料夾 Keyed/ 下所有條目的鍵；壞掉的 XML 與重複鍵記成失敗（對應遊戲 LoadData 會記的錯誤）。</summary>
        private static HashSet<string> ReadKeyedFiles(string keyedDir, string language, List<string> failures)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (!Directory.Exists(keyedDir)) return keys;
            foreach (string file in Directory.GetFiles(keyedDir, "*.xml", SearchOption.AllDirectories))
            {
                XDocument doc;
                try
                {
                    doc = XDocument.Load(file);
                }
                catch (XmlException ex)
                {
                    failures.Add($"{language}: {Path.GetFileName(file)}: {ex.Message}");
                    continue;
                }
                foreach (XElement element in doc.Root?.Elements() ?? Enumerable.Empty<XElement>())
                {
                    if (!keys.Add(element.Name.LocalName)) failures.Add($"{language}: duplicate key {element.Name.LocalName}");
                }
            }
            return keys;
        }

        private static readonly Regex KeyLiteral = new Regex(@"^RimAgent(\.[A-Za-z0-9_]+){2,}$");

        /// <summary>
        /// 掃描組件每個方法體的 ldstr，取出形如 RimAgent.X.Y 的字面字串（含 lambda 與 async 狀態機）。
        /// 解不開的方法記成失敗，不默默少掃。
        /// </summary>
        private static IEnumerable<string> LiteralTranslationKeys(Assembly assembly, List<string> failures)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (Type type in assembly.GetTypes())
            {
                IEnumerable<MethodBase> methods = type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all));
                foreach (MethodBase method in methods)
                {
                    byte[] il = method.GetMethodBody()?.GetILAsByteArray();
                    if (il == null) continue;
                    try
                    {
                        foreach (int token in StringTokens(il))
                        {
                            string literal = method.Module.ResolveString(token);
                            if (KeyLiteral.IsMatch(literal)) keys.Add(literal);
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        failures.Add($"IL scan of {type.FullName}.{method.Name} stopped: {ex.Message}");
                    }
                }
            }
            return keys;
        }

        private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (OpCode)f.GetValue(null))
            .ToDictionary(o => o.Value);

        /// <summary>逐一解碼 IL 指令，回傳每個 ldstr 的字串 token。</summary>
        private static IEnumerable<int> StringTokens(byte[] il)
        {
            int i = 0;
            while (i < il.Length)
            {
                short value = il[i] == 0xFE && i + 1 < il.Length ? (short)(0xFE00 | il[i + 1]) : il[i];
                if (!OpCodesByValue.TryGetValue(value, out OpCode op))
                    throw new InvalidOperationException($"unknown opcode 0x{(ushort)value:X} at IL_{i:X4}");
                i += op.Size;
                switch (op.OperandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        i += 1;
                        break;
                    case OperandType.InlineVar:
                        i += 2;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        i += 8;
                        break;
                    case OperandType.InlineSwitch:
                        int count = BitConverter.ToInt32(il, i);
                        i += 4 + count * 4;
                        break;
                    case OperandType.InlineString:
                        yield return BitConverter.ToInt32(il, i);
                        i += 4;
                        break;
                    default:
                        i += 4;
                        break;
                }
            }
        }
    }
}
