#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace RimAgent.Agent;

/// <summary>以供應商回報的輸入 token 為錨點：兩次請求之間增加的真實 token，按估算比例分給新增的內容；下一次請求的用量 = 上次真實值 + 新增內容 − 被拿掉的內容。</summary>
/// <remarks>供應商只回報整個請求的輸入總數（MEAI <see cref="UsageDetails.InputTokenCount"/>），沒有逐則訊息的數字，只能從相鄰兩次請求的差值推回。</remarks>
internal sealed class ContextMeter
{
    private readonly System.Threading.Lock _gate = new();
    private readonly ConditionalWeakTable<AIContent, StrongBox<long>> _measured = new();
    private Dictionary<AIContent, long> _sent;
    private long? _lastReal;
    private string _model;

    public long? LastReal
    {
        get { lock (_gate) return _lastReal; }
    }

    public void Observe(IEnumerable<ChatMessage> sent, long? realInputTokens, string model)
    {
        List<AIContent> contents = Distinct(sent);
        lock (_gate)
        {
            if (!realInputTokens.HasValue || realInputTokens.Value < 0)
            {
                ClearAnchor();
                return;
            }
            long real = realInputTokens.Value;
            bool sameModel = _sent != null && _lastReal.HasValue && string.Equals(_model, model, StringComparison.OrdinalIgnoreCase);
            // 第一次請求含工具定義，工具的真實格式化開銷不明，拆不出訊息本身的大小。
            if (sameModel)
            {
                var current = new HashSet<AIContent>(contents, ReferenceEqualityComparer.Instance);
                List<AIContent> added = [.. contents.Where(c => !_sent.ContainsKey(c))];
                List<AIContent> removed = [.. _sent.Keys.Where(c => !current.Contains(c))];
                long addedReal = real - _lastReal.Value + removed.Sum(c => _sent[c]);
                Attribute(added, removed, addedReal);
            }
            _sent = contents.ToDictionary<AIContent, AIContent, long>(c => c, SizeOf, ReferenceEqualityComparer.Instance);
            _lastReal = real;
            _model = model;
        }
    }

    /// <summary>模型這次的回覆會原樣出現在下一次請求裡，用供應商回報的輸出 token 當它的大小。</summary>
    /// <remarks>供應商有回報思考 token 時，思考不會原樣送回，從總數扣掉、也不分給思考內容。</remarks>
    public void ObserveOutput(IEnumerable<ChatMessage> reply, long? outputTokens, long? reasoningTokens = null)
    {
        if (!outputTokens.HasValue || outputTokens.Value <= 0) return;
        List<AIContent> contents = Distinct(reply);
        long total = outputTokens.Value;
        if (reasoningTokens is > 0)
        {
            total -= reasoningTokens.Value;
            contents.RemoveAll(c => c is TextReasoningContent);
        }
        lock (_gate) Attribute(contents, [], total);
    }

    /// <summary>
    /// 對照上次請求檢視歷史：排在已送出內容之前、卻不在上次請求裡的，是被壓縮折掉的；
    /// 之後的是還沒送出的新內容，<c>Pending</c> 為它們的大小（沒有錨點時為 null）。
    /// </summary>
    public (HashSet<AIContent> Folded, long? Pending) Inspect(IEnumerable<ChatMessage> history)
    {
        List<AIContent> contents = Distinct(history);
        var folded = new HashSet<AIContent>(ReferenceEqualityComparer.Instance);
        lock (_gate)
        {
            if (_sent == null || !_lastReal.HasValue) return (folded, null);
            int last = contents.FindLastIndex(_sent.ContainsKey);
            long pending = 0;
            for (int i = 0; i < contents.Count; i++)
            {
                if (_sent.ContainsKey(contents[i])) continue;
                if (i < last) folded.Add(contents[i]);
                else pending += SizeOf(contents[i]);
            }
            return (folded, pending);
        }
    }

    /// <summary>依上次真實值推算這批訊息送出時的輸入 token；沒有錨點時為 null。</summary>
    public long? Project(IEnumerable<ChatMessage> next)
    {
        List<AIContent> contents = Distinct(next);
        lock (_gate)
        {
            if (_sent == null || !_lastReal.HasValue) return null;
            var current = new HashSet<AIContent>(contents, ReferenceEqualityComparer.Instance);
            long added = contents.Where(c => !_sent.ContainsKey(c)).Sum(SizeOf);
            long removed = _sent.Where(p => !current.Contains(p.Key)).Sum(p => p.Value);
            return Math.Max(0, _lastReal.Value + added - removed);
        }
    }

    /// <summary>由供應商用量推回的真實大小；沒推過的內容為 null。</summary>
    public long? Measured(AIContent content)
    {
        if (content == null) return null;
        lock (_gate) return _measured.TryGetValue(content, out StrongBox<long> box) ? box.Value : null;
    }

    public void Reset()
    {
        lock (_gate) ClearAnchor();
    }

    internal static long Estimate(AIContent content)
    {
        return content switch
        {
            TextContent text => ColonyAgentSession.EstimateTokens(text.Text),
            TextReasoningContent reasoning => ColonyAgentSession.EstimateTokens(reasoning.Text),
            FunctionCallContent call => ColonyAgentSession.EstimateTokens(call.Name)
                + (call.Arguments == null ? 0 : call.Arguments.Sum(a => ColonyAgentSession.EstimateTokens(a.Key) + ColonyAgentSession.EstimateTokens(a.Value?.ToString()))),
            FunctionResultContent result => ColonyAgentSession.EstimateTokens(result.Result?.ToString()),
            _ => 0
        };
    }

    private long SizeOf(AIContent content)
    {
        return _measured.TryGetValue(content, out StrongBox<long> box) ? box.Value : Estimate(content);
    }

    /// <summary>圖片等非文字內容估不出 token，兩邊數量不同時差值裡混著它們的真實大小，這一步不分配。</summary>
    private void Attribute(List<AIContent> added, List<AIContent> removed, long total)
    {
        if (added.Count(IsOpaque) != removed.Count(IsOpaque)) return;
        List<AIContent> targets = [.. added.Where(c => !IsOpaque(c) && Estimate(c) > 0)];
        if (targets.Count == 0 || total <= 0) return;
        long weightSum = targets.Sum(Estimate);
        long assigned = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            long share = i == targets.Count - 1
                ? total - assigned
                : total * Estimate(targets[i]) / weightSum;
            assigned += share;
            _measured.AddOrUpdate(targets[i], new StrongBox<long>(share));
        }
    }

    private static bool IsOpaque(AIContent content)
    {
        return content is DataContent or UriContent;
    }

    private void ClearAnchor()
    {
        _sent = null;
        _lastReal = null;
        _model = null;
    }

    private static List<AIContent> Distinct(IEnumerable<ChatMessage> messages)
    {
        var seen = new HashSet<AIContent>(ReferenceEqualityComparer.Instance);
        var list = new List<AIContent>();
        if (messages == null) return list;
        foreach (ChatMessage message in messages)
        {
            if (message?.Contents == null) continue;
            foreach (AIContent content in message.Contents)
            {
                if (content != null && seen.Add(content)) list.Add(content);
            }
        }
        return list;
    }
}
