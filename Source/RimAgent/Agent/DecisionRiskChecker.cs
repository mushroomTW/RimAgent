using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RimAgent.Agent;

/// <summary>
/// 使用 TypeSafe System One / Jev 決策模型的風險檢查器。
/// 一次送出三道題目（risk, authorized, kind）平行評估：
/// - risk (Noul): 機率 >= 0.6 視為高風險
/// - authorized (Noul): 機率 >= 0.7 視為已授權
/// - kind (Choice): 動作分類（read_only / reversible_colony_change / irreversible_colony_change / configuration_change / external_side_effect / data_exfiltration）
///
/// 決策模型沒設定、呼叫失敗或逾時時，當場回退包裹的 LLM 分類器；
/// 同一句指令內不再重試決策模型，下一句指令會再試一次（網路恢復後自動回來）。
/// </summary>
public sealed class DecisionRiskChecker : IRiskChecker
{
    public const double RiskThreshold = 0.6;
    public const double AuthorizedThreshold = 0.7;

    private readonly IDecisionService _decisionService;
    private readonly Func<IDecisionService> _serviceFactory;
    private readonly IRiskChecker _fallback;
    private readonly Func<string> _criteriaProvider;
    private readonly InstructionCache<RiskVerdict> _verdictCache = new();
    private IDecisionService _service;
    private string _parsedCriteria;
    private DecisionPlan _parsedPlan;
    private bool _decisionFailed;

    /// <param name="decisionService">直接注入的決策服務（測試用）；null 則由 <paramref name="serviceFactory"/> 建立。</param>
    /// <param name="serviceFactory">執行期建立決策服務；回 null 代表玩家還沒設定決策模型。</param>
    /// <param name="fallback">決策模型不可用時接手判定的 LLM 分類器。</param>
    /// <param name="decisionRiskCriteria">判準檔內容（AgentPack/Prompts/DecisionRisk.md）。</param>
    public DecisionRiskChecker(
        IDecisionService decisionService = null,
        IRiskChecker fallback = null,
        Func<string> decisionRiskCriteria = null,
        Func<IDecisionService> serviceFactory = null)
    {
        _decisionService = decisionService;
        _serviceFactory = serviceFactory;
        _fallback = fallback;
        _criteriaProvider = decisionRiskCriteria;
    }

    /// <summary>這句指令內是否已放棄決策模型。</summary>
    public bool HasFailed => _decisionFailed;

    public async Task<RiskVerdict> ClassifyAsync(RiskQuestion question, CancellationToken cancellationToken)
    {
        if (question == null) throw new ArgumentNullException(nameof(question));

        // 鍵只用「玩家原話＋工具＋參數」：同一命令重試不重複呼叫決策模型
        string cacheKey = (question.Instruction ?? string.Empty) + "\n" +
            (question.ToolName ?? string.Empty) + "\n" + (question.Arguments ?? string.Empty);
        if (_verdictCache.TryGet(cacheKey, out RiskVerdict cached)) return cached;

        // 這句指令已經試過並失敗：不再重試決策模型。
        if (_decisionFailed)
        {
            return await FallbackAsync(question, cacheKey, true, cancellationToken).ConfigureAwait(false);
        }

        // 還沒設定決策模型（框架沒啟用供應商、或框架太舊）：不算失敗，下一句還可以再試。
        IDecisionService service = _decisionService ?? ResolveService();
        if (service == null)
        {
            return await FallbackAsync(question, cacheKey, false, cancellationToken).ConfigureAwait(false);
        }

        string criteria = _criteriaProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(criteria))
        {
            return await FallbackAsync(question, cacheKey, false, cancellationToken).ConfigureAwait(false);
        }

        DecisionPlan plan = ResolvePlan(criteria);
        if (plan.IsEmpty)
        {
            return await FallbackAsync(question, cacheKey, false, cancellationToken).ConfigureAwait(false);
        }

        string state = RiskClassifier.Describe(question);
        DecisionResult result;
        try
        {
            result = await service.EvaluateAsync(state, plan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _decisionFailed = true;
            return await FallbackAsync(question, cacheKey, true, cancellationToken).ConfigureAwait(false);
        }

        if (result == null ||
            !result.TryGet("risk", out DecisionAnswer riskAnswer) || riskAnswer?.Probability == null ||
            !result.TryGet("authorized", out DecisionAnswer authAnswer) || authAnswer?.Probability == null)
        {
            _decisionFailed = true;
            return await FallbackAsync(question, cacheKey, true, cancellationToken).ConfigureAwait(false);
        }

        double pRisk = riskAnswer.Probability.Value;
        double pAuth = authAnswer.Probability.Value;
        bool risky = pRisk >= RiskThreshold;
        bool authorized = pAuth >= AuthorizedThreshold;

        string kind = string.Empty;
        if (result.TryGet("kind", out DecisionAnswer kindAnswer) && !string.IsNullOrWhiteSpace(kindAnswer.Value))
        {
            kind = kindAnswer.Value.Trim();
        }

        string reason = !string.IsNullOrEmpty(kind)
            ? "action kind: " + kind
            : (risky ? "high risk action" : "unauthorized action");

        var verdict = new RiskVerdict
        {
            Risky = risky,
            Authorized = authorized,
            Reason = reason,
            Failed = false,
            Source = "decision"
        };

        _verdictCache.Store(cacheKey, verdict);
        return verdict;
    }

    /// <summary>延遲建立決策服務：玩家可能在遊戲進行中才去框架設定好決策模型。</summary>
    private IDecisionService ResolveService()
    {
        if (_service == null) _service = _serviceFactory?.Invoke();
        return _service;
    }

    /// <summary>判準檔的內容很少變，解析結果留著重用；內容變了才重解析。</summary>
    private DecisionPlan ResolvePlan(string criteria)
    {
        if (_parsedPlan == null || !string.Equals(_parsedCriteria, criteria, StringComparison.Ordinal))
        {
            _parsedCriteria = criteria;
            _parsedPlan = ParsePlan(criteria);
        }
        return _parsedPlan;
    }

    private async Task<RiskVerdict> FallbackAsync(
        RiskQuestion question, string cacheKey, bool decisionAttempted, CancellationToken cancellationToken)
    {
        if (_fallback != null)
        {
            RiskVerdict verdict = await _fallback.ClassifyAsync(question, cancellationToken).ConfigureAwait(false);
            if (verdict != null)
            {
                // 從沒試過決策模型（玩家還沒設定）就只是走 LLM 分類器，不是「決策失敗後的退路」。
                if (decisionAttempted) verdict.Source = "fallback";
                if (!verdict.Failed) _verdictCache.Store(cacheKey, verdict);
            }
            return verdict;
        }

        return new RiskVerdict
        {
            Risky = true,
            Failed = true,
            Reason = "risk check unavailable: decision model failed and no fallback",
            Source = decisionAttempted ? "fallback" : "llm"
        };
    }

    /// <summary>新的一句指令開始時清空：判定只在同一句內重用，並且可以再試一次決策模型。</summary>
    internal void ClearCache()
    {
        _verdictCache.Clear();
        _decisionFailed = false;
        if (_fallback is RiskClassifier rc) rc.ClearCache();
        else if (_fallback is DecisionRiskChecker drc) drc.ClearCache();
    }

    /// <summary>
    /// 從 markdown 格式判準檔解析 DecisionPlan。
    /// 支援 ## risk、## authorized、## kind 三個區段；區段缺漏時以內建預設值兜底。
    /// 判準文字與提示詞檔一致用英文：它要送給決策模型，不是給玩家看的。
    /// </summary>
    public static DecisionPlan ParsePlan(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return DecisionPlan.Of();

        string riskInstructions = "How hard is this action to undo, and how much does it affect?";
        string riskWhenTrue = "Hard or impossible to undo, affects many colonists or a large area at once, spends significant resources or silver.";
        string riskWhenFalse = "Small and reversible: work priorities, schedules, drafting, area or filter tweaks, research, time-speed changes.";

        string authInstructions = "Does the player's own instruction clearly authorize this action?";
        string authWhenTrue = "The instruction plainly covers this action, including its targets and amounts.";
        string authWhenFalse = "The action goes beyond the instruction, the assistant chose the target itself, or the instruction is ambiguous about it.";

        string kindInstructions = "What is the most significant effect of this action?";
        var kindOptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["read_only"] = "Reads, lists, searches, inspects or fetches public information; changes nothing.",
            ["reversible_colony_change"] = "Changes colony state the player can undo: work priorities, schedules, drafting, zones, bills, research, stockpiles or time speed.",
            ["irreversible_colony_change"] = "Hard or impossible to undo: destroys or deconstructs buildings, deletes zones or bills, attacks, performs surgery, releases prisoners or sends colonists off the map.",
            ["configuration_change"] = "Changes mod settings, global permissions, game options or other configuration that affects future actions.",
            ["external_side_effect"] = "Changes state in an external system: trades or releases a caravan, offers gifts, deploys, pays, or sends something off the map.",
            ["data_exfiltration"] = "Sends local files, secrets, or private data to an external host, or fetches and executes remote code."
        };

        string[] lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        string currentSection = null;
        var sectionLines = new List<string>();

        void ProcessSection(string section, List<string> slines)
        {
            if (string.IsNullOrEmpty(section)) return;
            string key = section.Trim().ToLowerInvariant();
            if (key == "risk")
            {
                ParseNoulSection(slines, ref riskInstructions, ref riskWhenTrue, ref riskWhenFalse);
            }
            else if (key == "authorized")
            {
                ParseNoulSection(slines, ref authInstructions, ref authWhenTrue, ref authWhenFalse);
            }
            else if (key == "kind")
            {
                ParseChoiceSection(slines, ref kindInstructions, kindOptions);
            }
        }

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                ProcessSection(currentSection, sectionLines);
                currentSection = trimmed.Substring(3).Trim();
                sectionLines.Clear();
            }
            else if (currentSection != null)
            {
                sectionLines.Add(line);
            }
        }
        ProcessSection(currentSection, sectionLines);

        var qRisk = DecisionQuestion.Noul("risk", riskInstructions, riskWhenTrue, riskWhenFalse);
        var qAuth = DecisionQuestion.Noul("authorized", authInstructions, authWhenTrue, authWhenFalse);
        var qKind = DecisionQuestion.Choice("kind", kindInstructions, kindOptions);

        return DecisionPlan.Of(qRisk, qAuth, qKind);
    }

    private static void ParseNoulSection(List<string> lines, ref string instructions, ref string whenTrue, ref string whenFalse)
    {
        var instLines = new List<string>();
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("true:", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("true：", StringComparison.OrdinalIgnoreCase))
            {
                whenTrue = trimmed.Substring(5).Trim();
            }
            else if (trimmed.StartsWith("false:", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("false：", StringComparison.OrdinalIgnoreCase))
            {
                whenFalse = trimmed.Substring(6).Trim();
            }
            else if (!string.IsNullOrEmpty(trimmed))
            {
                instLines.Add(trimmed);
            }
        }
        if (instLines.Count > 0)
        {
            instructions = string.Join("\n", instLines);
        }
    }

    private static void ParseChoiceSection(List<string> lines, ref string instructions, Dictionary<string, string> options)
    {
        var instLines = new List<string>();
        bool foundOptions = false;
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                foundOptions = true;
                string content = trimmed.Substring(2).Trim();
                int colonIdx = content.IndexOfAny([':', '：']);
                if (colonIdx > 0)
                {
                    string optKey = content.Substring(0, colonIdx).Trim();
                    string optVal = content.Substring(colonIdx + 1).Trim();
                    options[optKey] = optVal;
                }
            }
            else if (!foundOptions && !string.IsNullOrEmpty(trimmed))
            {
                instLines.Add(trimmed);
            }
        }
        if (instLines.Count > 0)
        {
            instructions = string.Join("\n", instLines);
        }
    }
}
