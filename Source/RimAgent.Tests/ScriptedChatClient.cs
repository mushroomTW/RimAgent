using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace RimAgent.Tests;

/// <summary>
/// 腳本化的假 IChatClient：每次呼叫依序吐出預先排好的回應（工具呼叫或文字），
/// 並記錄收到的訊息與 options，供整合測試斷言。腳本用完後重複最後一則。
/// </summary>
internal sealed class ScriptedChatClient : IChatClient
{
    private readonly List<Func<IList<ChatMessage>, ChatOptions, ChatResponse>> _script = [];
    private int _scriptIndex;
    private int _callIds;

    public List<IList<ChatMessage>> ReceivedMessages { get; } = [];
    public List<ChatOptions> ReceivedOptions { get; } = [];
    public int CallCount => ReceivedMessages.Count;

    public ScriptedChatClient Reply(string text)
    {
        _script.Add((_, __) => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        return this;
    }

    public ScriptedChatClient ReplyWithUsage(string text, long inputTokens)
    {
        _script.Add((_, __) => new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = inputTokens }
        });
        return this;
    }

    /// <summary>回一則要求呼叫工具的 assistant 訊息。</summary>
    public ScriptedChatClient CallTool(string name, object arguments)
    {
        _script.Add((_, __) =>
        {
            string callId = "call_" + (++_callIds);
            var args = arguments == null
                ? []
                : arguments.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(arguments));
            var message = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, name, args)]);
            return new ChatResponse(message) { FinishReason = ChatFinishReason.ToolCalls };
        });
        return this;
    }

    /// <summary>在同一則 assistant 回應中要求多個工具，用來覆蓋批次 tool calls。</summary>
    public ScriptedChatClient CallTools(params (string Name, object Arguments)[] calls)
    {
        _script.Add((_, __) =>
        {
            var contents = new List<AIContent>();
            foreach ((string name, object arguments) in calls)
            {
                string callId = "call_" + (++_callIds);
                var args = arguments == null
                    ? []
                    : arguments.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(arguments));
                contents.Add(new FunctionCallContent(callId, name, args));
            }
            var message = new ChatMessage(ChatRole.Assistant, contents);
            return new ChatResponse(message) { FinishReason = ChatFinishReason.ToolCalls };
        });
        return this;
    }

    /// <summary>上一個排好的步驟被叫到時先執行 <paramref name="action"/>（例如模擬玩家在模型思考時插話）。</summary>
    public ScriptedChatClient Do(Action action)
    {
        Func<IList<ChatMessage>, ChatOptions, ChatResponse> step = _script[_script.Count - 1];
        _script[_script.Count - 1] = (messages, options) =>
        {
            action();
            return step(messages, options);
        };
        return this;
    }

    public ScriptedChatClient Throw(Exception exception)
    {
        _script.Add((_, __) => throw exception);
        return this;
    }

    /// <summary>回一則帶「工具已被剝除」標記的文字回應。</summary>
    public ScriptedChatClient ReplyWithToolsStripped(string text)
    {
        _script.Add((_, __) => new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["rimllm_tools_stripped"] = true }
        });
        return this;
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var list = messages.ToList();
        ReceivedMessages.Add(list);
        // 存當下的複本：工具迴圈在同一輪會沿用並修改同一份 options（展開工具包）。
        ReceivedOptions.Add(options?.Clone());

        if (_script.Count == 0) throw new InvalidOperationException("ScriptedChatClient has no scripted response.");
        // 腳本用完後重複最後一則。
        Func<IList<ChatMessage>, ChatOptions, ChatResponse> step = _script[Math.Min(_scriptIndex, _script.Count - 1)];
        _scriptIndex++;
        return Task.FromResult(step(list, options));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions options = null, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("RimAgent does not stream.");
    }

    public object GetService(Type serviceType, object serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
