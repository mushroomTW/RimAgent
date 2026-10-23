using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kkdev92.Jev;
using RimLLM_Framework;

namespace RimAgent.Agent;

/// <summary>
/// 把 RimAgent 的決策計畫轉成 TypeSafe System One 的請求，並把答案轉回 RimAgent 的形狀。
///
/// Kkdev92.Jev 的型別只出現在方法內（不進任何欄位）：RimWorld 啟動時會掃描組件的欄位型別
/// 並要求全部能解析，欄位若直接引用 SDK 型別會讓載入順序變成硬性相依。
/// </summary>
public sealed class JevDecisionService : IDecisionService
{
    private readonly RimLLMDecisionClient _client;
    private readonly Func<string> _providerId;
    private readonly Func<string> _model;

    /// <param name="client">框架的決策服務；null 代表不可用。</param>
    /// <param name="providerId">要用的決策供應商 id；null 或空字串代表沿用框架啟用的那一個。</param>
    /// <param name="model">要用的模型名稱；null 或空字串代表沿用該供應商設定的模型。</param>
    public JevDecisionService(RimLLMDecisionClient client, Func<string> providerId = null, Func<string> model = null)
    {
        _client = client;
        _providerId = providerId;
        _model = model;
    }

    public async Task<DecisionResult> EvaluateAsync(string state, DecisionPlan plan, CancellationToken cancellationToken = default)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (plan.IsEmpty) return new DecisionResult();
        if (_client == null) throw new DecisionUnavailableException("decision service is not available");
        if (string.IsNullOrWhiteSpace(state)) throw new ArgumentException("decision state is empty", nameof(state));

        var builder = new JevDecisionPlanBuilder();
        var nouls = new Dictionary<string, JevNoulHandle>(StringComparer.Ordinal);
        var choices = new Dictionary<string, JevChoiceHandle<string>>(StringComparer.Ordinal);
        var scores = new Dictionary<string, JevScoreHandle>(StringComparer.Ordinal);

        foreach (DecisionQuestion question in plan.Questions)
        {
            if (question == null || string.IsNullOrWhiteSpace(question.Id)) continue;
            JevContent instructions = JevContent.FromText(question.Instructions ?? string.Empty);
            switch (question.Type)
            {
                case DecisionQuestionType.Noul:
                {
                    JevNoulCriteria criteria = BuildNoulCriteria(question);
                    nouls[question.Id] = builder.AddNoul(question.Id, instructions, criteria);
                    break;
                }
                case DecisionQuestionType.Choice:
                {
                    var options = new List<JevChoiceOption<string>>();
                    if (question.Options != null)
                    {
                        foreach (KeyValuePair<string, string> option in question.Options)
                        {
                            options.Add(new JevChoiceOption<string>(option.Key, option.Key,
                                JevContent.FromText(option.Value ?? string.Empty)));
                        }
                    }
                    if (options.Count == 0) break;
                    choices[question.Id] = builder.AddChoice(question.Id, instructions, options.ToArray());
                    break;
                }
                case DecisionQuestionType.Score:
                {
                    if (question.Levels == null || question.Levels.Count == 0) break;
                    scores[question.Id] = builder.AddScore(question.Id, instructions, ToContentArray(question.Levels));
                    break;
                }
            }
        }

        JevDecisionPlan jevPlan = builder.Build();
        JevResult result;
        try
        {
            result = await _client
                .EvaluateWithProviderAsync(state, jevPlan, _providerId?.Invoke(), _model?.Invoke(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RimLLMException ex)
        {
            // 設定面的錯誤（供應商未啟用、缺金鑰、供應商 id 不存在）重試也沒用，標記出來讓呼叫端整段放棄。
            bool configurationError = ex.Error == LLMError.InvalidKey || ex.Error == LLMError.Unknown;
            throw new DecisionUnavailableException(
                "decision call failed (" + ex.Error + "): " + ex.Message, ex)
            {
                IsConfigurationError = configurationError
            };
        }

        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JevNoulHandle> entry in nouls)
        {
            NoulAnswer answer = result.Get(entry.Value);
            answers[entry.Key] = new DecisionAnswer { Probability = answer.Probability };
        }
        foreach (KeyValuePair<string, JevChoiceHandle<string>> entry in choices)
        {
            ChoiceAnswer<string> answer = result.Get(entry.Value);
            var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int i = 0; i < answer.OptionCount; i++)
            {
                probabilities[answer.GetLabel(i)] = answer.GetProbability(i);
            }
            answers[entry.Key] = new DecisionAnswer
            {
                Value = answer.Value,
                Confidence = answer.Confidence,
                Probability = answer.GetProbability(answer.Label),
                Probabilities = probabilities
            };
        }
        foreach (KeyValuePair<string, JevScoreHandle> entry in scores)
        {
            ScoreAnswer answer = result.Get(entry.Value);
            var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int i = 0; i < answer.LevelCount; i++)
            {
                probabilities[LevelLabel(answer.Legend, i)] = answer.GetProbability(i);
            }
            answers[entry.Key] = new DecisionAnswer
            {
                Score = answer.Value,
                Confidence = answer.Confidence,
                Probabilities = probabilities
            };
        }
        return new DecisionResult(answers);
    }

    /// <summary>評分題的等級名稱在圖例裡；取不到就用序號代替。</summary>
    private static string LevelLabel(System.Collections.Immutable.ImmutableArray<System.Text.Json.JsonElement> legend, int index)
    {
        if (index < 0 || index >= legend.Length) return index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        System.Text.Json.JsonElement element = legend[index];
        if (element.ValueKind == System.Text.Json.JsonValueKind.String) return element.GetString() ?? string.Empty;
        return element.ToString();
    }

    /// <summary>把唯讀清單複製成陣列（SDK 的簽章要 ReadOnlySpan）。</summary>
    private static JevContent[] ToContentArray(IReadOnlyList<string> values)
    {
        var array = new JevContent[values.Count];
        for (int i = 0; i < values.Count; i++) array[i] = JevContent.FromText(values[i] ?? string.Empty);
        return array;
    }

    /// <summary>
    /// 是非題的兩段說明；兩者都沒有時回 default（SDK 以 IsEmpty 判定「沒帶判準」）。
    /// </summary>
    private static JevNoulCriteria BuildNoulCriteria(DecisionQuestion question)
    {
        string whenTrue = question.WhenTrue;
        string whenFalse = question.WhenFalse;
        if (string.IsNullOrWhiteSpace(whenTrue) && string.IsNullOrWhiteSpace(whenFalse)) return default;
        return new JevNoulCriteria(
            JevContent.FromText(whenTrue ?? string.Empty),
            JevContent.FromText(whenFalse ?? string.Empty));
    }
}
