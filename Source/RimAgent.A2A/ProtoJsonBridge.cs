using System.Text.Json;
using A2A;
using Google.Protobuf;

namespace RimAgent.A2A;

/// <summary>
/// proto 訊息與 SDK 型別互轉。A2A 1.0 規格的 JSON 綁定就是 proto 的 ProtoJSON，所以經 JSON 轉一次即可；
/// SDK 自己的 <c>A2A.Grpc.ProtoMap</c> 是 internal（A2A.Grpc 1.0.0-preview4）。
/// </summary>
internal static class ProtoJsonBridge
{
    private static readonly JsonParser Parser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    public static T ToDomain<T>(IMessage proto)
        => JsonSerializer.Deserialize<T>(JsonFormatter.Default.Format(proto), A2AJsonUtilities.DefaultOptions)
           ?? throw new A2AException("Empty request.", A2AErrorCode.InvalidRequest);

    public static T ToProto<T>(object domain) where T : IMessage<T>, new()
        => Parser.Parse<T>(JsonSerializer.Serialize(domain, domain.GetType(), A2AJsonUtilities.DefaultOptions));
}
