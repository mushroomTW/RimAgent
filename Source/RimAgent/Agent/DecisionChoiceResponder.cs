using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimAgent.Game;

namespace RimAgent.Agent;

/// <summary>System One 對一個抉擇選定的選項。</summary>
public sealed class ChoiceDecision
{
    public PendingChoiceInfo Choice { get; init; }
    public ChoiceOptionInfo Option { get; init; }
    public double Confidence { get; init; }
}

/// <summary>
/// 由 System One 代答抉擇信件與對話框：每個抉擇一道選項題，一次請求平行評估。
/// 要附輸入（選世界格子、地圖目標）的抉擇它填不了，留給主 LLM；信心不足的也留給主 LLM。
/// 決策模型沒設定、判準檔缺失或呼叫失敗時回傳空清單，呼叫端照舊交給主 LLM。
/// </summary>
public sealed class DecisionChoiceResponder
{
    /// <summary>低於此信心不代答：多數抉擇是永久的（收人、付贖金、拒絕難民）。</summary>
    public const double MinConfidence = 0.7;

    private readonly IDecisionService _decisionService;
    private readonly Func<IDecisionService> _serviceFactory;
    private readonly Func<string> _criteriaProvider;
    private IDecisionService _service;

    public DecisionChoiceResponder(
        IDecisionService decisionService = null,
        Func<string> criteriaProvider = null,
        Func<IDecisionService> serviceFactory = null)
    {
        _decisionService = decisionService;
        _criteriaProvider = criteriaProvider;
        _serviceFactory = serviceFactory;
    }

    /// <summary>System One 能不能自己回答這個抉擇：不用額外輸入，且至少有一個能按的選項。</summary>
    public static bool CanAnswer(PendingChoiceInfo choice)
    {
        return choice != null
            && !string.IsNullOrWhiteSpace(choice.Id)
            && string.IsNullOrEmpty(choice.InputHint)
            && choice.Options != null
            && choice.Options.Any(o => !o.Disabled);
    }

    public async Task<IReadOnlyList<ChoiceDecision>> DecideAsync(
        IReadOnlyList<PendingChoiceInfo> choices,
        string colonyState,
        CancellationToken cancellationToken = default)
    {
        List<PendingChoiceInfo> answerable = choices?.Where(CanAnswer).ToList() ?? [];
        if (answerable.Count == 0) return Array.Empty<ChoiceDecision>();

        IDecisionService service = _decisionService ?? ResolveService();
        if (service == null) return Array.Empty<ChoiceDecision>();
        string criteria = _criteriaProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(criteria)) return Array.Empty<ChoiceDecision>();

        var questions = new List<DecisionQuestion>();
        foreach (PendingChoiceInfo choice in answerable)
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ChoiceOptionInfo option in choice.Options.Where(o => !o.Disabled))
            {
                options[option.Number.ToString(CultureInfo.InvariantCulture)] = option.Text ?? string.Empty;
            }
            questions.Add(DecisionQuestion.Choice(choice.Id, criteria.Trim() + "\n\n" + Describe(choice), options));
        }

        string state = string.IsNullOrWhiteSpace(colonyState) ? "Colony state unknown." : colonyState.Trim();
        DecisionResult result;
        try
        {
            result = await service.EvaluateAsync(state, DecisionPlan.Of([.. questions]), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Array.Empty<ChoiceDecision>();
        }

        var decisions = new List<ChoiceDecision>();
        foreach (PendingChoiceInfo choice in answerable)
        {
            if (result == null || !result.TryGet(choice.Id, out DecisionAnswer answer) || answer == null) continue;
            double confidence = answer.Confidence ?? answer.Probability ?? 0d;
            if (confidence < MinConfidence) continue;
            if (!int.TryParse(answer.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) continue;
            ChoiceOptionInfo picked = choice.Options.FirstOrDefault(o => o.Number == number && !o.Disabled);
            if (picked == null) continue;
            decisions.Add(new ChoiceDecision { Choice = choice, Option = picked, Confidence = confidence });
        }
        return decisions;
    }

    /// <summary>抉擇內容包在 &lt;choice&gt; 裡；遊戲文字可能夾帶看似指令的內容，判準檔已聲明它只是資料。</summary>
    internal static string Describe(PendingChoiceInfo choice)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<choice>");
        sb.Append("Title: ").AppendLine(Escape(choice.Title));
        if (choice.ExpiresInTicks.HasValue)
        {
            float hours = Math.Max(0, choice.ExpiresInTicks.Value) / 2500f;
            sb.Append("Expires in: ").Append(hours.ToString("0.#", CultureInfo.InvariantCulture)).AppendLine(" h");
        }
        if (!string.IsNullOrWhiteSpace(choice.Text)) sb.AppendLine(Escape(choice.Text.Trim()));
        sb.Append("</choice>");
        return sb.ToString();
    }

    private static string Escape(string value)
    {
        return (value ?? string.Empty).Replace("<", "\\<");
    }

    private IDecisionService ResolveService()
    {
        if (_service == null) _service = _serviceFactory?.Invoke();
        return _service;
    }
}
