using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using A2A;
using Grpc.Core;
using Protos = A2A.Grpc.Protos;

namespace RimAgent.A2A;

/// <summary>
/// A2A 1.0.0-preview4 的 gRPC 只有客戶端與 proto 產生的服務基底，沒有伺服端實作；A2A.AspNetCore 只提供 JSON-RPC 與 HTTP+JSON。
/// 這裡把 proto 訊息轉成 SDK 的型別後，交給與 JSON-RPC 共用的 <see cref="IA2ARequestHandler"/>，兩邊的任務狀態一致。
/// 推播通知沒有宣告，維持基底的 Unimplemented。
/// </summary>
internal sealed class A2AGrpcService(IA2ARequestHandler handler, AgentCard card) : Protos.A2AService.A2AServiceBase
{
    public override Task<Protos.SendMessageResponse> SendMessage(Protos.SendMessageRequest request, ServerCallContext context)
        => Call(async () => ProtoJsonBridge.ToProto<Protos.SendMessageResponse>(
            await handler.SendMessageAsync(ProtoJsonBridge.ToDomain<SendMessageRequest>(request), requestContext: null, context.CancellationToken).ConfigureAwait(false)));

    public override Task SendStreamingMessage(Protos.SendMessageRequest request, IServerStreamWriter<Protos.StreamResponse> responseStream, ServerCallContext context)
        => Stream(() => handler.SendStreamingMessageAsync(ProtoJsonBridge.ToDomain<SendMessageRequest>(request), requestContext: null, context.CancellationToken), responseStream);

    public override Task<Protos.Task> GetTask(Protos.GetTaskRequest request, ServerCallContext context)
        => Call(async () => ProtoJsonBridge.ToProto<Protos.Task>(
            await handler.GetTaskAsync(ProtoJsonBridge.ToDomain<GetTaskRequest>(request), requestContext: null, context.CancellationToken).ConfigureAwait(false)));

    public override Task<Protos.ListTasksResponse> ListTasks(Protos.ListTasksRequest request, ServerCallContext context)
        => Call(async () => ProtoJsonBridge.ToProto<Protos.ListTasksResponse>(
            await handler.ListTasksAsync(ProtoJsonBridge.ToDomain<ListTasksRequest>(request), requestContext: null, context.CancellationToken).ConfigureAwait(false)));

    public override Task<Protos.Task> CancelTask(Protos.CancelTaskRequest request, ServerCallContext context)
        => Call(async () => ProtoJsonBridge.ToProto<Protos.Task>(
            await handler.CancelTaskAsync(ProtoJsonBridge.ToDomain<CancelTaskRequest>(request), requestContext: null, context.CancellationToken).ConfigureAwait(false)));

    public override Task SubscribeToTask(Protos.SubscribeToTaskRequest request, IServerStreamWriter<Protos.StreamResponse> responseStream, ServerCallContext context)
        => Stream(() => handler.SubscribeToTaskAsync(ProtoJsonBridge.ToDomain<SubscribeToTaskRequest>(request), requestContext: null, context.CancellationToken), responseStream);

    public override Task<Protos.AgentCard> GetExtendedAgentCard(Protos.GetExtendedAgentCardRequest request, ServerCallContext context)
        => Task.FromResult(ProtoJsonBridge.ToProto<Protos.AgentCard>(card));

    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (A2AException ex)
        {
            throw ToRpcException(ex);
        }
    }

    private static async Task Stream(Func<IAsyncEnumerable<StreamResponse>> events, IServerStreamWriter<Protos.StreamResponse> responseStream)
    {
        try
        {
            await foreach (StreamResponse e in events().ConfigureAwait(false))
            {
                await responseStream.WriteAsync(ProtoJsonBridge.ToProto<Protos.StreamResponse>(e)).ConfigureAwait(false);
            }
        }
        catch (A2AException ex)
        {
            throw ToRpcException(ex);
        }
    }

    /// <summary>對照 A2A 規格「gRPC 錯誤對應」；SDK 的 <c>GrpcErrorMapping</c> 是 internal。</summary>
    internal static RpcException ToRpcException(A2AException ex) => new(new Status(ex.ErrorCode switch
    {
        A2AErrorCode.TaskNotFound => StatusCode.NotFound,
        A2AErrorCode.TaskNotCancelable => StatusCode.FailedPrecondition,
        A2AErrorCode.PushNotificationNotSupported or A2AErrorCode.UnsupportedOperation or A2AErrorCode.MethodNotFound
            or A2AErrorCode.ExtendedAgentCardNotConfigured => StatusCode.Unimplemented,
        A2AErrorCode.ContentTypeNotSupported or A2AErrorCode.InvalidRequest or A2AErrorCode.InvalidParams
            or A2AErrorCode.ParseError or A2AErrorCode.VersionNotSupported => StatusCode.InvalidArgument,
        A2AErrorCode.ExtensionSupportRequired => StatusCode.FailedPrecondition,
        _ => StatusCode.Internal,
    }, ex.Message));
}
