using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using RimLLM_Framework;
using TypeSafeAI;

namespace RimAgent.Agent;

/// <summary>
/// 把 RimAgent 的決策計畫轉成 TypeSafe System One 的請求，並把答案轉回 RimAgent 的形狀。
///
/// TypeSafeAI 的型別只出現在方法內（不進任何欄位）：RimWorld 啟動時會掃描組件的欄位型別
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
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.IsEmpty) return new DecisionResult();
        if (_client == null) throw new DecisionUnavailableException("decision service is not available");
        if (string.IsNullOrWhiteSpace(state)) throw new ArgumentException("decision state is empty", nameof(state));

        var questionSet = new QuestionSet();
        var nouls = new Dictionary<string, NoulHandle>(StringComparer.Ordinal);
        var choices = new Dictionary<string, ChoiceHandle>(StringComparer.Ordinal);
        var scores = new Dictionary<string, ScoreHandle>(StringComparer.Ordinal);

        foreach (DecisionQuestion question in plan.Questions)
        {
            if (question == null || string.IsNullOrWhiteSpace(question.Id)) continue;
            ProcessQuestion(question, questionSet, nouls, choices, scores);
        }

        QuestionSetResult result = await CallDecisionServiceAsync(state, questionSet, cancellationToken);

        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        ProcessNoulAnswers(result, nouls, answers);
        ProcessChoiceAnswers(result, choices, answers);
        ProcessScoreAnswers(result, scores, answers);
        return new DecisionResult(answers);
    }

    private async Task<QuestionSetResult> CallDecisionServiceAsync(string state, QuestionSet questionSet, CancellationToken cancellationToken)
    {
        try
        {
            string provider = _providerId?.Invoke();
            string model = _model?.Invoke();
            // 未指定供應商時走 EvaluateAsync，才能使用 RimLLM Framework 的決策備援鏈；
            // 呼叫端明確指定供應商時才走 EvaluateWithProviderAsync（依框架契約不換家）。
            return await (string.IsNullOrWhiteSpace(provider)
                ? _client.EvaluateAsync(state, questionSet, model, cancellationToken)
                : _client.EvaluateWithProviderAsync(state, questionSet, provider, model, cancellationToken))
                .ConfigureAwait(false);
        }
        catch (RimLLMException ex)
        {
            // 設定面的錯誤（缺金鑰／金鑰無效）重試也沒用，標記出來讓呼叫端整段放棄；
            // Unknown 無法判斷成因，視為暫時性錯誤，不因一次未知失敗就放棄整句指令的決策。
            bool configurationError = ex.Error == LLMError.InvalidKey;
            throw new DecisionUnavailableException(
                "decision call failed (" + ex.Error + "): " + ex.Message, ex)
            {
                IsConfigurationError = configurationError
            };
        }
    }

    private static void ProcessNoulAnswers(QuestionSetResult result, Dictionary<string, NoulHandle> nouls, Dictionary<string, DecisionAnswer> answers)
    {
        foreach (KeyValuePair<string, NoulHandle> entry in nouls)
        {
            NoulAnswer answer = result.Get(entry.Value);
            answers[entry.Key] = new DecisionAnswer { Probability = answer.Probability };
        }
    }

    private static void ProcessChoiceAnswers(QuestionSetResult result, Dictionary<string, ChoiceHandle> choices, Dictionary<string, DecisionAnswer> answers)
    {
        foreach (KeyValuePair<string, ChoiceHandle> entry in choices)
        {
            ChoiceAnswer answer = result.Get(entry.Value);
            var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, double> option in answer.Probabilities)
            {
                probabilities[option.Key] = option.Value;
            }
            answers[entry.Key] = new DecisionAnswer
            {
                Value = answer.Choice,
                Confidence = answer.Confidence,
                Probability = answer.ProbabilityOf(answer.Choice),
                Probabilities = probabilities
            };
        }
    }

    private static void ProcessScoreAnswers(QuestionSetResult result, Dictionary<string, ScoreHandle> scores, Dictionary<string, DecisionAnswer> answers)
    {
        foreach (KeyValuePair<string, ScoreHandle> entry in scores)
        {
            ScoreAnswer answer = result.Get(entry.Value);
            var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (ScoreLevel level in answer.Levels)
            {
                probabilities[LevelLabel(level)] = level.Probability;
            }
            answers[entry.Key] = new DecisionAnswer
            {
                Score = answer.Score,
                Confidence = answer.Confidence,
                Probabilities = probabilities
            };
        }
    }

    private static void ProcessQuestion(DecisionQuestion question, QuestionSet questionSet,
        Dictionary<string, NoulHandle> nouls, Dictionary<string, ChoiceHandle> choices,
        Dictionary<string, ScoreHandle> scores)
    {
        TypeSafeContent instructions = TypeSafeContent.FromText(question.Instructions ?? string.Empty);
        switch (question.Type)
        {
            case DecisionQuestionType.Noul:
            {
                nouls[question.Id] = questionSet.Noul(instructions,
                    OptionalText(question.WhenTrue), OptionalText(question.WhenFalse), question.Id);
                break;
            }
            case DecisionQuestionType.Choice:
            {
                var options = new Dictionary<string, TypeSafeContent?>(StringComparer.Ordinal);
                if (question.Options != null)
                {
                    foreach (KeyValuePair<string, string> option in question.Options)
                    {
                        options[option.Key] = TypeSafeContent.FromText(option.Value ?? string.Empty);
                    }
                }
                if (options.Count > 0)
                {
                    choices[question.Id] = questionSet.Choice(instructions, options, question.Id);
                }
                break;
            }
            case DecisionQuestionType.Score:
            {
                if (question.Levels != null && question.Levels.Count > 0)
                {
                    scores[question.Id] = questionSet.Score(instructions, question.Levels, question.Id);
                }
                break;
            }
        }
    }

    /// <summary>評分題的等級名稱取自等級說明；取不到就用序號代替。</summary>
    private static string LevelLabel(ScoreLevel level)
    {
        if (level.Description is TypeSafeContent description && !description.IsEmpty) return description.ToString();
        return level.Index.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>是非題的單段說明；空白時回 null，讓 SDK 視為沒帶這段判準。</summary>
    private static TypeSafeContent? OptionalText(string text)
    {
        return string.IsNullOrWhiteSpace(text) ? (TypeSafeContent?)null : TypeSafeContent.FromText(text);
    }
}
