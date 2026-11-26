using System;
using System.Threading;
using System.Threading.Tasks;
using A2A;

namespace RimAgent.A2A;

internal sealed class ColonyAgentHandler(ColonyInstructionRunner runner) : IAgentHandler
{
    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        var updater = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);
        await updater.SubmitAsync(metadata: null, cancellationToken).ConfigureAwait(false);
        string? text = context.UserText;
        if (string.IsNullOrWhiteSpace(text))
        {
            await updater.RejectAsync(AgentMessage(context, "The message has no text. Send the instruction as a text part."), metadata: null, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await updater.StartWorkAsync(message: null, metadata: null, cancellationToken).ConfigureAwait(false);
            string reply = await runner(text, cancellationToken).ConfigureAwait(false);
            await updater.CompleteAsync(AgentMessage(context, reply), metadata: null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A2A 1.0.0-preview4 的 A2AServer.CancelTaskAsync 先取消這裡的 token，等這次執行結束後，再另起一次執行呼叫 CancelAsync 寫入取消狀態；
            // 這裡先寫的話，那次執行會判定任務已是終態而回 TaskNotCancelable。阻塞式請求斷線時，SDK 也會取消這個 token 並拆掉這次執行。
        }
        catch (Exception ex)
        {
            await updater.FailAsync(AgentMessage(context, ex.Message), metadata: null, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        => await new TaskUpdater(eventQueue, context.TaskId, context.ContextId).CancelAsync(metadata: null, cancellationToken).ConfigureAwait(false);

    private static Message AgentMessage(RequestContext context, string text) => new()
    {
        Role = Role.Agent,
        MessageId = Guid.NewGuid().ToString("N"),
        ContextId = context.ContextId,
        TaskId = context.TaskId,
        Parts = [Part.FromText(text)],
    };
}
