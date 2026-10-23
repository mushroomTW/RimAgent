using System;
using System.Collections.Generic;

namespace RimAgent.Agent;

/// <summary>一道題的答案。</summary>
public sealed class DecisionAnswer
{
    /// <summary>
    /// Noul：肯定機率（0 到 1）。Choice：被選中選項的機率。Score：被評分等級的機率。
    /// </summary>
    public double? Probability { get; init; }

    /// <summary>Choice：被選中的選項值。Score：被評的等級。</summary>
    public string Value { get; init; }

    /// <summary>Score 題目回傳的分數；其他題型沒有這個值。</summary>
    public double? Score { get; init; }

    /// <summary>Choice 與 Score 的信心（0 到 1）；Noul 不帶此值。</summary>
    public double? Confidence { get; init; }

    /// <summary>各選項或各等級的機率；Noul 沒有這份分布。</summary>
    public IReadOnlyDictionary<string, double> Probabilities { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);
}

/// <summary>一次決策請求的全部答案，依題目 Id 取用。</summary>
public sealed class DecisionResult
{
    private readonly Dictionary<string, DecisionAnswer> _answers;

    public DecisionResult(IReadOnlyDictionary<string, DecisionAnswer> answers = null)
    {
        _answers = answers == null
            ? new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal)
            : new Dictionary<string, DecisionAnswer>(answers, StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, DecisionAnswer> Answers => _answers;

    /// <summary>取某題的答案；題目沒問或模型沒答都回 false。</summary>
    public bool TryGet(string id, out DecisionAnswer answer)
    {
        if (string.IsNullOrEmpty(id))
        {
            answer = null;
            return false;
        }
        return _answers.TryGetValue(id, out answer);
    }

    /// <summary>取某題的答案；沒有就回 null。</summary>
    public DecisionAnswer Get(string id)
    {
        return TryGet(id, out DecisionAnswer answer) ? answer : null;
    }
}
