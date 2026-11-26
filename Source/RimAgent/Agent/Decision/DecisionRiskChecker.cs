#nullable disable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace RimAgent.Agent;

/// <summary>一次送出三道題目平行評估：risk 與 authorized（Noul）、kind（Choice，動作分類）。決策模型沒設定、呼叫失敗或逾時時當場回退包裹的 LLM 分類器；同一句指令內不再重試決策模型，下一句會再試一次。</summary>
public sealed class DecisionRiskChecker : IRiskChecker
{
    public const double RiskThreshold = 0.6;
    public const double AuthorizedThreshold = 0.7;
    private static readonly string[] _newlineSplit = ["\r\n", "\r", "\n"];

    private readonly IDecisionService _decisionService;
    private readonly Func<IDecisionService> _serviceFactory;
    private readonly IRiskChecker _fallback;
    private readonly Func<string> _criteriaProvider;
    private readonly Func<IReadOnlyList<ChatMessage>> _historyProvider;
    private readonly InstructionCache<RiskVerdict> _verdictCache = new();
    // 判準與解析結果放同一個不可變物件整個換掉，避免並行時讀到新判準配舊計畫。
    private sealed record ParsedPlan(string Criteria, DecisionPlan Plan);
    private volatile ParsedPlan _parsed;
    private bool _decisionFailed;

    /// <param name="serviceFactory">執行期建立決策服務；回 null 代表玩家還沒設定決策模型。</param>
    public DecisionRiskChecker(
        IDecisionService decisionService = null,
        IRiskChecker fallback = null,
        Func<string> decisionRiskCriteria = null,
        Func<IDecisionService> serviceFactory = null,
        Func<IReadOnlyList<ChatMessage>> historyProvider = null)
    {
        _historyProvider = historyProvider;
        _decisionService = decisionService;
        _serviceFactory = serviceFactory;
        _fallback = fallback;
        _criteriaProvider = decisionRiskCriteria;
    }

    public bool HasFailed => _decisionFailed;

    public async Task<RiskVerdict> ClassifyAsync(RiskQuestion question, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);

        string cacheKey = RiskClassifier.BuildCacheKey(question);
        if (_verdictCache.TryGet(cacheKey, out RiskVerdict cached)) return cached;

        // 這句指令已經試過並失敗：不再重試決策模型。
        if (_decisionFailed)
        {
            return await FallbackAsync(question, cacheKey, decisionAttempted: true, cancellationToken).ConfigureAwait(false);
        }

        // 還沒設定決策模型（框架沒啟用供應商、或框架太舊）：不算失敗，下一句還可以再試。
        IDecisionService service = _decisionService ?? _serviceFactory?.Invoke();
        DecisionPlan plan = service == null ? null : CurrentPlan();
        if (plan == null)
        {
            return await FallbackAsync(question, cacheKey, decisionAttempted: false, cancellationToken).ConfigureAwait(false);
        }

        // 只帶入玩家原話：助理回覆可能含有外部工具的間接注入，不能當成授權依據。
        string state = DecisionReasoningRouter.DescribeUserHistory(_historyProvider?.Invoke()) + RiskClassifier.Describe(question);
        DecisionResult result;
        try
        {
            result = await service.EvaluateAsync(state, plan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DecisionDiagnostics.Warn(nameof(DecisionRiskChecker), ex);
            _decisionFailed = true;
            return await FallbackAsync(question, cacheKey, decisionAttempted: true, cancellationToken).ConfigureAwait(false);
        }

        if (result == null ||
            !result.TryGet("risk", out DecisionAnswer riskAnswer) || riskAnswer?.Probability == null ||
            !result.TryGet("authorized", out DecisionAnswer authAnswer) || authAnswer?.Probability == null)
        {
            _decisionFailed = true;
            return await FallbackAsync(question, cacheKey, decisionAttempted: true, cancellationToken).ConfigureAwait(false);
        }

        RiskVerdict verdict = BuildVerdict(result, riskAnswer.Probability.Value, authAnswer.Probability.Value);
        _verdictCache.Store(cacheKey, verdict);
        return verdict;
    }

    private DecisionPlan CurrentPlan()
    {
        string criteria = _criteriaProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(criteria)) return null;
        DecisionPlan plan = ResolvePlan(criteria);
        return plan.IsEmpty ? null : plan;
    }

    private static RiskVerdict BuildVerdict(DecisionResult result, double pRisk, double pAuth)
    {
        bool risky = pRisk >= RiskThreshold;
        bool authorized = pAuth >= AuthorizedThreshold;

        string kind = string.Empty;
        if (result.TryGet("kind", out DecisionAnswer kindAnswer) && !string.IsNullOrWhiteSpace(kindAnswer.Value))
        {
            kind = kindAnswer.Value.Trim();
        }

        if (IsHighImpactKind(kind)) risky = true;

        string reason;
        if (!string.IsNullOrEmpty(kind))
        {
            reason = "action kind: " + kind;
        }
        else if (risky)
        {
            reason = "high risk action";
        }
        else
        {
            reason = "unauthorized action";
        }

        return new RiskVerdict
        {
            Risky = risky,
            Authorized = authorized,
            Reason = reason,
            Failed = false,
            Source = "decision"
        };
    }

    /// <summary>需無條件確認的動作類別 id（與 DecisionRisk.md 的 kind 選項一致）。</summary>
    internal static bool IsHighImpactKind(string kind)
    {
        return string.Equals(kind, "irreversible_colony_change", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "configuration_change", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "external_side_effect", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "data_exfiltration", StringComparison.OrdinalIgnoreCase);
    }

    private DecisionPlan ResolvePlan(string criteria)
    {
        ParsedPlan current = _parsed;
        if (current == null || !string.Equals(current.Criteria, criteria, StringComparison.Ordinal))
        {
            current = new ParsedPlan(criteria, ParsePlan(criteria));
            _parsed = current;
        }
        return current.Plan;
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

    /// <summary>支援 ## risk、## authorized、## kind 三個區段，缺漏時以內建預設值兜底；判準文字與提示詞檔一致用英文，因為它要送給決策模型。</summary>
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
        Dictionary<string, string> kindOptions = DefaultKindOptions();

        string[] lines = markdown.Split(_newlineSplit, StringSplitOptions.None);
        string currentSection = null;
        var sectionLines = new List<string>();

        void ProcessSection(string section, List<string> slines)
        {
            if (string.IsNullOrEmpty(section)) return;
            string key = section.Trim().ToLowerInvariant();
            if (key == "risk")
            {
                DecisionCriteriaParser.ParseNoulSection(slines, ref riskInstructions, ref riskWhenTrue, ref riskWhenFalse);
            }
            else if (key == "authorized")
            {
                DecisionCriteriaParser.ParseNoulSection(slines, ref authInstructions, ref authWhenTrue, ref authWhenFalse);
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

    private static Dictionary<string, string> DefaultKindOptions()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["read_only"] = "Reads, lists, searches, inspects or fetches public information; changes nothing.",
            ["reversible_colony_change"] = "Changes colony state the player can undo: work priorities, schedules, drafting, zones, bills, research, stockpiles or time speed.",
            ["irreversible_colony_change"] = "Hard or impossible to undo: destroys or deconstructs buildings, deletes zones or bills, attacks, performs surgery, releases prisoners or sends colonists off the map.",
            ["configuration_change"] = "Changes mod settings, global permissions, game options or other configuration that affects future actions.",
            ["external_side_effect"] = "Changes state in an external system: trades or releases a caravan, offers gifts, deploys, pays, or sends something off the map.",
            ["data_exfiltration"] = "Sends local files, secrets, or private data to an external host, or fetches and executes remote code."
        };
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
            instructions = string.Join('\n', instLines);
        }
    }
}
