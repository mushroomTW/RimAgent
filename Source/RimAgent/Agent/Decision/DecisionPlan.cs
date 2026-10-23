using System;
using System.Collections.Generic;

namespace RimAgent.Agent;

/// <summary>決策題目的型別：對應 System One 的三種問法。</summary>
public enum DecisionQuestionType
{
    /// <summary>是非題：回傳 0 到 1 的肯定機率，不另帶信心分數。</summary>
    Noul,
    /// <summary>選項題：從一組選項挑一個，回傳選項、機率與信心。</summary>
    Choice,
    /// <summary>評分題：依自訂等級評分，回傳等級、各級機率與信心。</summary>
    Score
}

/// <summary>一次決策計畫裡的一道題。判準文字由玩家可改寫的擴充包檔案提供。</summary>
public sealed class DecisionQuestion
{
    /// <summary>題目識別碼：同一個計畫內唯一，回傳的答案用它對回。</summary>
    public string Id { get; init; } = string.Empty;

    public DecisionQuestionType Type { get; init; }

    /// <summary>題目敘述：問什麼、判準是什麼。</summary>
    public string Instructions { get; init; } = string.Empty;

    /// <summary>
    /// 選項（Choice）：值 → 說明文字。Noul 用 "true" 與 "false" 兩個鍵描述兩種情況。
    /// </summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>等級（Score）：由低到高。</summary>
    public IReadOnlyList<string> Levels { get; init; } = Array.Empty<string>();

    /// <summary>是非題：肯定時的說明。</summary>
    public string WhenTrue => Option("true");

    /// <summary>是非題：否定時的說明。</summary>
    public string WhenFalse => Option("false");

    private string Option(string key)
    {
        if (Options == null) return string.Empty;
        return Options.TryGetValue(key, out string value) ? value ?? string.Empty : string.Empty;
    }

    /// <summary>建一題是非題；兩段說明可省略，省略時模型只看題目敘述。</summary>
    public static DecisionQuestion Noul(string id, string instructions, string whenTrue = null, string whenFalse = null)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(whenTrue)) options["true"] = whenTrue;
        if (!string.IsNullOrWhiteSpace(whenFalse)) options["false"] = whenFalse;
        return new DecisionQuestion
        {
            Id = id ?? string.Empty,
            Type = DecisionQuestionType.Noul,
            Instructions = instructions ?? string.Empty,
            Options = options
        };
    }

    /// <summary>建一題選項題；選項的值就是回傳時被選中的那個值。</summary>
    public static DecisionQuestion Choice(string id, string instructions, IReadOnlyDictionary<string, string> options)
    {
        return new DecisionQuestion
        {
            Id = id ?? string.Empty,
            Type = DecisionQuestionType.Choice,
            Instructions = instructions ?? string.Empty,
            Options = options ?? new Dictionary<string, string>(StringComparer.Ordinal)
        };
    }

    /// <summary>建一題評分題；等級由低到高。</summary>
    public static DecisionQuestion Score(string id, string instructions, IReadOnlyList<string> levels)
    {
        return new DecisionQuestion
        {
            Id = id ?? string.Empty,
            Type = DecisionQuestionType.Score,
            Instructions = instructions ?? string.Empty,
            Levels = levels ?? Array.Empty<string>()
        };
    }
}

/// <summary>一次決策請求要問的題目集合；同一個 state 平行評估，題目之間互不影響。</summary>
public sealed class DecisionPlan
{
    public IReadOnlyList<DecisionQuestion> Questions { get; init; } = Array.Empty<DecisionQuestion>();

    /// <summary>沒有題目時不必送出請求。</summary>
    public bool IsEmpty => Questions == null || Questions.Count == 0;

    public static DecisionPlan Of(params DecisionQuestion[] questions)
    {
        return new DecisionPlan { Questions = questions ?? Array.Empty<DecisionQuestion>() };
    }
}
