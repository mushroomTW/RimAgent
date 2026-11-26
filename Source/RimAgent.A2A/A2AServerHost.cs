using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using A2A;
using A2A.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace RimAgent.A2A;

/// <summary>殖民地 agent 執行一句指令並回覆；失敗時擲出例外，訊息會原樣回給遠端 agent。</summary>
public delegate Task<string> ColonyInstructionRunner(string instruction, CancellationToken cancellationToken);

public static class A2AServerHost
{
    public const string JsonRpcPath = "/a2a";
    public const int MaxInstructionChars = 16_000;
    private const long MaxRequestBodyBytes = 256 * 1024;
    private const int MaxGrpcMessageBytes = 256 * 1024;
    private const long MaxConcurrentConnections = 32;

    public static async Task<IAsyncDisposable> StartAsync(string contentRoot, int httpPort, int grpcPort, string version, ColonyInstructionRunner runner, string? bearerToken = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        AgentCard card = CreateCard(httpPort, grpcPort, version);
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = contentRoot,
            ApplicationName = "RimAgent.A2A",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
            kestrel.Limits.MaxConcurrentConnections = MaxConcurrentConnections;
            kestrel.ListenLocalhost(httpPort, o => o.Protocols = HttpProtocols.Http1);
            kestrel.ListenLocalhost(grpcPort, o => o.Protocols = HttpProtocols.Http2);
        });
        // 只綁 loopback 還不夠：瀏覽器頁面可用 DNS rebinding 以自己的網域連進來，所以 Host 標頭也只認本機。
        builder.Services.AddHostFiltering(o => o.AllowedHosts = ["localhost", "127.0.0.1", "[::1]"]);
        builder.Services.AddSingleton(runner);
        builder.Services.AddA2AAgent<ColonyAgentHandler>(card);
        builder.Services.AddGrpc(o => o.MaxReceiveMessageSize = MaxGrpcMessageBytes);

        WebApplication app = builder.Build();
        app.UseHostFiltering();
        app.Use(RejectCrossSiteAsync);
        if (!string.IsNullOrEmpty(bearerToken))
        {
            byte[] expected = Encoding.UTF8.GetBytes("Bearer " + bearerToken);
            app.Use((context, next) => RequireBearerTokenAsync(context, next, expected));
        }
        app.MapA2A(JsonRpcPath);
        app.MapWellKnownAgentCard(card);
        app.MapGrpcService<A2AGrpcService>();
        try
        {
            await app.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return new RunningServer(app);
    }

    /// <summary>
    /// Host 檢查擋不住跨站請求：網頁可對 localhost 送 text/plain 的 POST 而不觸發 CORS 預檢，A2A.AspNetCore 1.0.0-preview4 又不檢查內容型別。
    /// 瀏覽器的跨站 POST 一定帶 Origin，agent 用戶端不帶。
    /// </summary>
    private static Task RejectCrossSiteAsync(HttpContext context, RequestDelegate next)
    {
        HttpRequest request = context.Request;
        if (!StringValues.IsNullOrEmpty(request.Headers.Origin))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
        if (HttpMethods.IsPost(request.Method) && request.Path.StartsWithSegments(JsonRpcPath, StringComparison.OrdinalIgnoreCase) && !request.HasJsonContentType())
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return Task.CompletedTask;
        }
        return next(context);
    }

    private static Task RequireBearerTokenAsync(HttpContext context, RequestDelegate next, byte[] expectedHeader)
    {
        if (context.Request.Path.StartsWithSegments("/.well-known", StringComparison.OrdinalIgnoreCase))
        {
            return next(context);
        }
        string? actual = context.Request.Headers.Authorization;
        if (string.IsNullOrEmpty(actual))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
        byte[] actualBytes = Encoding.UTF8.GetBytes(actual);
        if (!CryptographicOperations.FixedTimeEquals(actualBytes, expectedHeader))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
        return next(context);
    }

    public static AgentCard CreateCard(int httpPort, int grpcPort, string version) => new()
    {
        Name = "RimAgent",
        Description = "An AI agent that plays a RimWorld colony. Send it a natural-language instruction; it observes the map and gives orders to the colonists, then replies with what it did.",
        Version = version,
        SupportedInterfaces =
        [
            new AgentInterface { Url = LocalUrl(httpPort, JsonRpcPath), ProtocolBinding = ProtocolBindingNames.JsonRpc, ProtocolVersion = "1.0" },
            new AgentInterface { Url = LocalUrl(grpcPort, "/"), ProtocolBinding = ProtocolBindingNames.Grpc, ProtocolVersion = "1.0" },
        ],
        Capabilities = new AgentCapabilities { Streaming = true, PushNotifications = false },
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
        Skills =
        [
            new AgentSkill
            {
                Id = "colony-instruction",
                Name = "Colony instruction",
                Description = "Carry out an instruction in the running colony, such as building, assigning work, managing prisoners or reporting colony status. In RimAgent's Confirm mode, orders that change the game are only staged: the reply says how many wait for the player, and they run after the player confirms them in game (a later instruction can check the result). Instructions can take minutes of game time: send with returnImmediately and poll GetTask. CancelTask only stops tasks sent with returnImmediately (a blocking call is stopped by disconnecting it).",
                Tags = ["rimworld", "colony", "game"],
                Examples = ["How many colonists are there and what are they doing?", "Build a 5x5 bedroom next to the kitchen."],
            },
        ],
    };

    private static string LocalUrl(int port, string path) => new UriBuilder(Uri.UriSchemeHttp, "localhost", port, path).Uri.ToString();

    private sealed class RunningServer(WebApplication app) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
